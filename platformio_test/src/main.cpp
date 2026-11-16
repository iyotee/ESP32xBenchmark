#include <Arduino.h>
#include <WiFi.h>
#include <driver/gpio.h>
#include <esp_chip_info.h>
#include <esp_heap_caps.h>
#include <esp_partition.h>
#include <esp_timer.h>
#include <soc/soc_caps.h>

#include <algorithm>
#include <math.h>
#include <stdlib.h>

namespace {
constexpr uint32_t kSampleCount = 5;
constexpr uint32_t kCpuIterations = 600000;
constexpr uint32_t kBufferBytes = 16384;
constexpr uint32_t kMemoryPasses = 64;
constexpr uint32_t kFlashBytes = 65536;
constexpr uint32_t kRtosHandoffs = 5000;
constexpr gpio_num_t kBenchmarkGpio = GPIO_NUM_2;

volatile uint32_t g_integerSink = 0;
volatile float g_floatSink = 0.0f;
TaskHandle_t g_rtosWorker = nullptr;
TaskHandle_t g_rtosMain = nullptr;

#if CONFIG_IDF_TARGET_ESP32
constexpr const char* kComparabilityClass = "classic_wifi_ble";
constexpr const char* kArchitecture = "Xtensa";
#elif CONFIG_IDF_TARGET_ESP32S2
constexpr const char* kComparabilityClass = "wifi_only";
constexpr const char* kArchitecture = "Xtensa";
#elif CONFIG_IDF_TARGET_ESP32S3
constexpr const char* kComparabilityClass = "high_performance_wifi_ble";
constexpr const char* kArchitecture = "Xtensa";
#elif CONFIG_IDF_TARGET_ESP32C2 || CONFIG_IDF_TARGET_ESP32C3
constexpr const char* kComparabilityClass = "low_cost_wifi_ble";
constexpr const char* kArchitecture = "RISC-V";
#elif CONFIG_IDF_TARGET_ESP32C5 || CONFIG_IDF_TARGET_ESP32C61
constexpr const char* kComparabilityClass = "wifi6_ble";
constexpr const char* kArchitecture = "RISC-V";
#elif CONFIG_IDF_TARGET_ESP32C6
constexpr const char* kComparabilityClass = "wifi6_802154";
constexpr const char* kArchitecture = "RISC-V";
#elif CONFIG_IDF_TARGET_ESP32H2 || CONFIG_IDF_TARGET_ESP32H4 || CONFIG_IDF_TARGET_ESP32H21
constexpr const char* kComparabilityClass = "low_power_802154";
constexpr const char* kArchitecture = "RISC-V";
#elif CONFIG_IDF_TARGET_ESP32P4
constexpr const char* kComparabilityClass = "high_performance_no_radio";
constexpr const char* kArchitecture = "RISC-V";
#else
constexpr const char* kComparabilityClass = "unclassified";
constexpr const char* kArchitecture = "unknown";
#endif

void emitMetric(const char* category, const char* name, double value, const char* unit,
                const char* direction, const char* status, uint32_t samples) {
  Serial.printf(
      "{\"type\":\"metric\",\"category\":\"%s\",\"name\":\"%s\","
      "\"value\":%.4f,\"unit\":\"%s\",\"direction\":\"%s\","
      "\"status\":\"%s\",\"samples\":%lu}\n",
      category, name, value, unit, direction, status,
      static_cast<unsigned long>(samples));
}

void emitUnavailable(const char* category, const char* name, const char* reason) {
  Serial.printf(
      "{\"type\":\"metric\",\"category\":\"%s\",\"name\":\"%s\","
      "\"value\":null,\"unit\":\"\",\"direction\":\"higher\","
      "\"status\":\"skipped\",\"reason\":\"%s\"}\n",
      category, name, reason);
}

double median(double* values, size_t count) {
  std::sort(values, values + count);
  if (count % 2 == 0) {
    return (values[count / 2 - 1] + values[count / 2]) / 2.0;
  }
  return values[count / 2];
}

void emitSamples(const char* category, const char* name, double* values,
                 const char* unit, const char* direction) {
  const double result = median(values, kSampleCount);
  double mean = 0.0;
  for (uint32_t i = 0; i < kSampleCount; ++i) {
    mean += values[i];
  }
  mean /= kSampleCount;
  double variance = 0.0;
  for (uint32_t i = 0; i < kSampleCount; ++i) {
    const double delta = values[i] - mean;
    variance += delta * delta;
  }
  variance /= kSampleCount;
  const double stdevPercent =
      mean == 0.0 ? 0.0 : sqrt(variance) * 100.0 / fabs(mean);
  Serial.printf(
      "{\"type\":\"metric\",\"category\":\"%s\",\"name\":\"%s\","
      "\"value\":%.4f,\"median\":%.4f,\"min\":%.4f,\"max\":%.4f,"
      "\"stdev_pct\":%.2f,\"unit\":\"%s\",\"direction\":\"%s\","
      "\"status\":\"ok\",\"samples\":%lu}\n",
      category, name, result, result, values[0], values[kSampleCount - 1],
      stdevPercent, unit, direction, static_cast<unsigned long>(kSampleCount));
}

__attribute__((noinline)) uint32_t runIntegerWorkload() {
  uint32_t checksum = 0x12345678u;
  for (uint32_t i = 1; i <= kCpuIterations; ++i) {
    checksum ^= i * 1664525u + 1013904223u;
    checksum = (checksum << 7) | (checksum >> 25);
    checksum += i ^ (checksum >> 11);
  }
  g_integerSink = checksum;
  return checksum;
}

__attribute__((noinline)) float runFloatWorkload() {
  float accumulator = 0.25f;
  for (uint32_t i = 1; i <= kCpuIterations / 4; ++i) {
    const float value = static_cast<float>(i & 1023u) * 0.001f + 0.1f;
    accumulator = accumulator * 1.00001f + value * 0.0001f;
  }
  g_floatSink = accumulator;
  return accumulator;
}

void runCpuBenchmarks() {
  double integerRates[kSampleCount];
  double floatRates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    const int64_t start = esp_timer_get_time();
    const uint32_t checksum = runIntegerWorkload();
    const int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0 || checksum == 0) {
      emitUnavailable("cpu", "integer_mix_mops", "invalid_timer_or_checksum");
      return;
    }
    integerRates[sample] =
        (static_cast<double>(kCpuIterations) / static_cast<double>(elapsed));

    const int64_t floatStart = esp_timer_get_time();
    const float floatChecksum = runFloatWorkload();
    const int64_t floatElapsed = esp_timer_get_time() - floatStart;
    if (floatElapsed <= 0 || !isfinite(floatChecksum)) {
      emitUnavailable("cpu", "float_mix_mops", "invalid_timer_or_checksum");
      return;
    }
    floatRates[sample] =
        (static_cast<double>(kCpuIterations / 4) / static_cast<double>(floatElapsed));
  }
  emitSamples("cpu", "integer_mix_miter_s", integerRates, "Miter/s", "higher");
  emitSamples("cpu", "float_mix_miter_s", floatRates, "Miter/s", "higher");
}

void runMemoryBenchmarks() {
  uint8_t* buffer = static_cast<uint8_t*>(
      heap_caps_malloc(kBufferBytes, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));
  if (buffer == nullptr) {
    emitUnavailable("memory", "sram_write_mib_s", "buffer_allocation_failed");
    emitUnavailable("memory", "sram_read_mib_s", "buffer_allocation_failed");
    emitUnavailable("memory", "allocation_latency_us", "buffer_allocation_failed");
    return;
  }

  double writeRates[kSampleCount];
  double readRates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    int64_t start = esp_timer_get_time();
    for (uint32_t pass = 0; pass < kMemoryPasses; ++pass) {
      for (uint32_t i = 0; i < kBufferBytes; ++i) {
        buffer[i] = static_cast<uint8_t>((i + pass * 13u) & 0xFFu);
      }
    }
    int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0) {
      free(buffer);
      emitUnavailable("memory", "sram_write_mib_s", "invalid_timer");
      return;
    }
    const double mebibytes =
        (static_cast<double>(kBufferBytes) * kMemoryPasses) / (1024.0 * 1024.0);
    writeRates[sample] = mebibytes * 1000000.0 / static_cast<double>(elapsed);

    volatile uint32_t checksum = 0;
    start = esp_timer_get_time();
    for (uint32_t pass = 0; pass < kMemoryPasses; ++pass) {
      for (uint32_t i = 0; i < kBufferBytes; ++i) {
        checksum += buffer[i];
      }
    }
    elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0 || checksum == 0) {
      free(buffer);
      emitUnavailable("memory", "sram_read_mib_s", "invalid_timer_or_checksum");
      return;
    }
    readRates[sample] = mebibytes * 1000000.0 / static_cast<double>(elapsed);
  }
  free(buffer);
  emitSamples("memory", "sram_write_mib_s", writeRates, "MiB/s", "higher");
  emitSamples("memory", "sram_read_mib_s", readRates, "MiB/s", "higher");

  double allocationTimes[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    constexpr uint32_t kAllocations = 128;
    const int64_t start = esp_timer_get_time();
    uint32_t successful = 0;
    for (uint32_t i = 0; i < kAllocations; ++i) {
      void* block = heap_caps_malloc(512, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
      if (block == nullptr) {
        break;
      }
      memset(block, static_cast<int>(i), 512);
      free(block);
      ++successful;
    }
    const int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0 || successful != kAllocations) {
      emitUnavailable("memory", "allocation_latency_us", "allocation_stress_failed");
      return;
    }
    allocationTimes[sample] = static_cast<double>(elapsed) / kAllocations;
  }
  emitSamples("memory", "allocation_latency_us", allocationTimes, "us/op", "lower");

  if (ESP.getFreePsram() > 0) {
    uint8_t* psram = static_cast<uint8_t*>(
        heap_caps_malloc(kBufferBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    if (psram != nullptr) {
      double psramRates[kSampleCount];
      for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
        const int64_t start = esp_timer_get_time();
        for (uint32_t pass = 0; pass < kMemoryPasses; ++pass) {
          memset(psram, static_cast<int>(pass), kBufferBytes);
        }
        const int64_t elapsed = esp_timer_get_time() - start;
        if (elapsed <= 0) {
          free(psram);
          emitUnavailable("memory", "psram_write_mib_s", "invalid_timer");
          return;
        }
        const double mebibytes =
            (static_cast<double>(kBufferBytes) * kMemoryPasses) / (1024.0 * 1024.0);
        psramRates[sample] = mebibytes * 1000000.0 / static_cast<double>(elapsed);
      }
      free(psram);
      emitSamples("memory", "psram_write_mib_s", psramRates, "MiB/s", "higher");
    }
  } else {
    emitUnavailable("memory", "psram_write_mib_s", "psram_not_present");
  }
}

void runFlashBenchmark() {
  const esp_partition_t* partition =
      esp_partition_find_first(ESP_PARTITION_TYPE_APP, ESP_PARTITION_SUBTYPE_ANY, nullptr);
  if (partition == nullptr || partition->size < kFlashBytes) {
    emitUnavailable("memory", "flash_read_mib_s", "running_app_partition_unavailable");
    return;
  }
  uint8_t* buffer = static_cast<uint8_t*>(
      heap_caps_malloc(4096, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));
  if (buffer == nullptr) {
    emitUnavailable("memory", "flash_read_mib_s", "buffer_allocation_failed");
    return;
  }
  double rates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    volatile uint32_t checksum = 0;
    const int64_t start = esp_timer_get_time();
    for (uint32_t offset = 0; offset < kFlashBytes; offset += 4096) {
      if (esp_partition_read(partition, offset, buffer, 4096) != ESP_OK) {
        free(buffer);
        emitUnavailable("memory", "flash_read_mib_s", "partition_read_failed");
        return;
      }
      checksum += buffer[offset & 4095u];
    }
    const int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0 || checksum == 0) {
      free(buffer);
      emitUnavailable("memory", "flash_read_mib_s", "invalid_timer_or_checksum");
      return;
    }
    rates[sample] = (static_cast<double>(kFlashBytes) / (1024.0 * 1024.0)) *
                    1000000.0 / static_cast<double>(elapsed);
  }
  free(buffer);
  emitSamples("memory", "flash_read_mib_s", rates, "MiB/s", "higher");
}

void runGpioBenchmark() {
  if (gpio_set_direction(kBenchmarkGpio, GPIO_MODE_OUTPUT) != ESP_OK) {
    emitUnavailable("peripherals", "gpio_transitions_s", "gpio2_unavailable");
    return;
  }
  constexpr uint32_t kTransitions = 100000;
  double rates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    const int64_t start = esp_timer_get_time();
    for (uint32_t i = 0; i < kTransitions; ++i) {
      gpio_set_level(kBenchmarkGpio, (i & 1u) != 0);
    }
    const int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0) {
      emitUnavailable("peripherals", "gpio_transitions_s", "invalid_timer");
      return;
    }
    rates[sample] = static_cast<double>(kTransitions) * 1000000.0 / elapsed;
  }
  gpio_set_level(kBenchmarkGpio, 0);
  emitSamples("peripherals", "gpio_transitions_s", rates, "transitions/s", "higher");
}

void rtosWorker(void*) {
  for (uint32_t i = 0; i < kRtosHandoffs; ++i) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    xTaskNotifyGive(g_rtosMain);
  }
  vTaskDelete(nullptr);
}

void runRtosBenchmark() {
  double handoffTimes[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    g_rtosWorker = nullptr;
    const BaseType_t core = portNUM_PROCESSORS > 1 ? 0 : tskNO_AFFINITY;
    if (xTaskCreatePinnedToCore(rtosWorker, "bench_handoff", 2048, nullptr, 3,
                                &g_rtosWorker, core) != pdPASS) {
      emitUnavailable("rtos", "task_handoff_us", "task_creation_failed");
      return;
    }
    g_rtosMain = xTaskGetCurrentTaskHandle();
    const int64_t start = esp_timer_get_time();
    for (uint32_t i = 0; i < kRtosHandoffs; ++i) {
      xTaskNotifyGive(g_rtosWorker);
      if (ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000)) != 1) {
        vTaskDelete(g_rtosWorker);
        emitUnavailable("rtos", "task_handoff_us", "task_notification_timeout");
        return;
      }
    }
    const int64_t elapsed = esp_timer_get_time() - start;
    handoffTimes[sample] = static_cast<double>(elapsed) / kRtosHandoffs;
    delay(1);
  }
  emitSamples("rtos", "task_handoff_us", handoffTimes, "us/round_trip", "lower");

  QueueHandle_t queue = xQueueCreate(1, sizeof(uint32_t));
  if (queue == nullptr) {
    emitUnavailable("rtos", "queue_roundtrip_us", "queue_creation_failed");
    return;
  }
  double queueTimes[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    constexpr uint32_t kQueueIterations = 10000;
    uint32_t value = 0;
    const int64_t start = esp_timer_get_time();
    for (uint32_t i = 0; i < kQueueIterations; ++i) {
      value = i;
      if (xQueueSend(queue, &value, 0) != pdTRUE ||
          xQueueReceive(queue, &value, 0) != pdTRUE) {
        vQueueDelete(queue);
        emitUnavailable("rtos", "queue_roundtrip_us", "queue_operation_failed");
        return;
      }
    }
    const int64_t elapsed = esp_timer_get_time() - start;
    if (elapsed <= 0 || value != kQueueIterations - 1) {
      vQueueDelete(queue);
      emitUnavailable("rtos", "queue_roundtrip_us", "invalid_timer_or_result");
      return;
    }
    queueTimes[sample] = static_cast<double>(elapsed) / kQueueIterations;
  }
  vQueueDelete(queue);
  emitSamples("rtos", "queue_roundtrip_us", queueTimes, "us/op", "lower");
}

void runConnectivityProbe() {
#if SOC_WIFI_SUPPORTED
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(false, true);
  delay(100);
  const int64_t start = esp_timer_get_time();
  const int16_t networks = WiFi.scanNetworks(false, true, false);
  const int64_t elapsed = esp_timer_get_time() - start;
  WiFi.scanDelete();
  WiFi.mode(WIFI_OFF);
  if (networks < 0 || elapsed <= 0) {
    emitUnavailable("connectivity", "wifi_scan_ms", "scan_failed");
    return;
  }
  emitMetric("connectivity", "wifi_scan_ms", elapsed / 1000.0, "ms",
             "lower", "informational", 1);
  emitMetric("connectivity", "wifi_visible_networks", networks, "networks",
             "higher", "informational", 1);
#else
  emitUnavailable("connectivity", "wifi_scan_ms", "wifi_not_supported");
#endif
  emitUnavailable("connectivity", "wifi_tcp_udp_mbit_s",
                  "iperf_peer_and_wifi_credentials_not_configured");
  emitUnavailable("connectivity", "ble_throughput_kbit_s",
                  "ble_peer_test_not_configured");
  emitUnavailable("connectivity", "thread_zigbee_latency_ms",
                  "802154_peer_test_not_configured");
}

void printRunStart() {
  esp_chip_info_t chipInfo;
  esp_chip_info(&chipInfo);
  const uint32_t runId = esp_random();
  Serial.printf(
      "{\"type\":\"run_start\",\"schema_version\":1,\"run_id\":\"%08lx\","
      "\"device\":{\"target\":\"%s\",\"family\":\"%s\",\"architecture\":\"%s\","
      "\"firmware_version\":\"0.2.0\",\"comparability_class\":\"%s\","
      "\"cores\":%d,\"cpu_mhz\":%lu,"
      "\"flash_bytes\":%lu,\"psram_bytes\":%lu,\"heap_free_bytes\":%lu,"
      "\"sdk_version\":\"%s\",\"wifi_supported\":%s,\"ble_supported\":%s}}\n",
      static_cast<unsigned long>(runId), ESP.getChipModel(),
      CONFIG_IDF_TARGET, kArchitecture, kComparabilityClass, chipInfo.cores,
      static_cast<unsigned long>(ESP.getCpuFreqMHz()),
      static_cast<unsigned long>(ESP.getFlashChipSize()),
      static_cast<unsigned long>(ESP.getFreePsram()),
      static_cast<unsigned long>(ESP.getFreeHeap()), ESP.getSdkVersion(),
#if SOC_WIFI_SUPPORTED
      "true",
#else
      "false",
#endif
#if SOC_BT_SUPPORTED
      "true"
#else
      "false"
#endif
  );
}

void runSuite() {
  Serial.println("{\"type\":\"suite\",\"name\":\"ESP32xBenchmark\",\"version\":\"0.2.0\"}");
  runCpuBenchmarks();
  runMemoryBenchmarks();
  runFlashBenchmark();
  runGpioBenchmark();
  runRtosBenchmark();
  runConnectivityProbe();
  emitUnavailable("power", "active_current_ma",
                  "external_power_analyzer_required; current_is_not_estimated");
  emitUnavailable("power", "deep_sleep_current_ua",
                  "external_power_analyzer_required");
  emitUnavailable("power", "wake_latency_ms",
                  "external_power_analyzer_required");
  emitUnavailable("ai", "inference_latency_ms",
                  "model_and_inference_runtime_not_configured");
  Serial.println("{\"type\":\"run_complete\"}");
}
}  // namespace

void setup() {
  Serial.begin(115200);
  delay(1200);
  WiFi.mode(WIFI_OFF);
#if !CONFIG_IDF_TARGET_ESP32S2
  btStop();
#endif
  delay(100);
  printRunStart();
  runSuite();
}

void loop() {
  delay(15000);
  printRunStart();
  runSuite();
}
