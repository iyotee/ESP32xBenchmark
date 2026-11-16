#include <Arduino.h>
#include <ESP8266WiFi.h>
#include <ESP8266WiFiGeneric.h>
#include <pgmspace.h>

#include <algorithm>
#include <math.h>
#include <stdlib.h>

namespace {
constexpr uint32_t kSampleCount = 5;
constexpr uint32_t kCpuIterations = 600000;
constexpr uint32_t kBufferBytes = 8192;
constexpr uint32_t kMemoryPasses = 64;
constexpr uint32_t kFlashBytes = 65536;
constexpr uint32_t kFlashBlockBytes = 4096;
constexpr uint32_t kAllocations = 128;
constexpr uint8_t kBenchmarkGpio = D1;

volatile uint32_t g_integerSink = 0;
volatile float g_floatSink = 0.0f;
uint32_t g_runSequence = 0;

double median(double* values) {
  std::sort(values, values + kSampleCount);
  return values[kSampleCount / 2];
}

void emitMetric(const char* category, const char* name, double value,
                const char* unit, const char* direction, const char* status,
                uint32_t samples) {
  Serial.printf(
      "{\"type\":\"metric\",\"category\":\"%s\",\"name\":\"%s\","
      "\"value\":%.4f,\"unit\":\"%s\",\"direction\":\"%s\","
      "\"status\":\"%s\",\"samples\":%lu}\n",
      category, name, value, unit, direction, status,
      static_cast<unsigned long>(samples));
}

void emitUnavailable(const char* category, const char* name,
                     const char* reason) {
  Serial.printf(
      "{\"type\":\"metric\",\"category\":\"%s\",\"name\":\"%s\","
      "\"value\":null,\"unit\":\"\",\"direction\":\"higher\","
      "\"status\":\"skipped\",\"reason\":\"%s\"}\n",
      category, name, reason);
}

void emitSamples(const char* category, const char* name, double* values,
                 const char* unit, const char* direction) {
  const double result = median(values);
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
    if ((i & 0x0FFFu) == 0) {
      ESP.wdtFeed();
    }
  }
  g_integerSink = checksum;
  return checksum;
}

__attribute__((noinline)) float runFloatWorkload() {
  float accumulator = 0.25f;
  for (uint32_t i = 1; i <= kCpuIterations / 4; ++i) {
    const float value = static_cast<float>(i & 1023u) * 0.001f + 0.1f;
    accumulator = accumulator * 1.00001f + value * 0.0001f;
    if ((i & 0x0FFFu) == 0) {
      ESP.wdtFeed();
    }
  }
  g_floatSink = accumulator;
  return accumulator;
}

void runCpuBenchmarks() {
  double integerRates[kSampleCount];
  double floatRates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    const uint32_t start = micros();
    const uint32_t checksum = runIntegerWorkload();
    const uint32_t elapsed = micros() - start;
    if (elapsed == 0 || checksum == 0) {
      emitUnavailable("cpu", "integer_mix_miter_s",
                      "invalid_timer_or_checksum");
      return;
    }
    integerRates[sample] =
        static_cast<double>(kCpuIterations) / static_cast<double>(elapsed);

    const uint32_t floatStart = micros();
    const float floatChecksum = runFloatWorkload();
    const uint32_t floatElapsed = micros() - floatStart;
    if (floatElapsed == 0 || !isfinite(floatChecksum)) {
      emitUnavailable("cpu", "float_mix_miter_s",
                      "invalid_timer_or_checksum");
      return;
    }
    floatRates[sample] =
        static_cast<double>(kCpuIterations / 4) /
        static_cast<double>(floatElapsed);
  }
  emitSamples("cpu", "integer_mix_miter_s", integerRates, "Miter/s", "higher");
  emitSamples("cpu", "float_mix_miter_s", floatRates, "Miter/s", "higher");
}

void runMemoryBenchmarks() {
  uint8_t* buffer = static_cast<uint8_t*>(malloc(kBufferBytes));
  if (buffer == nullptr) {
    emitUnavailable("memory", "sram_write_mib_s", "buffer_allocation_failed");
    emitUnavailable("memory", "sram_read_mib_s", "buffer_allocation_failed");
    emitUnavailable("memory", "allocation_latency_us",
                    "buffer_allocation_failed");
    return;
  }

  double writeRates[kSampleCount];
  double readRates[kSampleCount];
  const double mebibytes =
      (static_cast<double>(kBufferBytes) * kMemoryPasses) / (1024.0 * 1024.0);
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    uint32_t start = micros();
    for (uint32_t pass = 0; pass < kMemoryPasses; ++pass) {
      for (uint32_t i = 0; i < kBufferBytes; ++i) {
        buffer[i] = static_cast<uint8_t>((i + pass * 13u) & 0xFFu);
      }
      if ((pass & 0x0Fu) == 0) {
        ESP.wdtFeed();
      }
    }
    uint32_t elapsed = micros() - start;
    if (elapsed == 0) {
      free(buffer);
      emitUnavailable("memory", "sram_write_mib_s", "invalid_timer");
      return;
    }
    writeRates[sample] = mebibytes * 1000000.0 / elapsed;

    volatile uint32_t checksum = 0;
    start = micros();
    for (uint32_t pass = 0; pass < kMemoryPasses; ++pass) {
      for (uint32_t i = 0; i < kBufferBytes; ++i) {
        checksum += buffer[i];
      }
      if ((pass & 0x0Fu) == 0) {
        ESP.wdtFeed();
      }
    }
    elapsed = micros() - start;
    if (elapsed == 0 || checksum == 0) {
      free(buffer);
      emitUnavailable("memory", "sram_read_mib_s",
                      "invalid_timer_or_checksum");
      return;
    }
    readRates[sample] = mebibytes * 1000000.0 / elapsed;
  }
  free(buffer);
  emitSamples("memory", "sram_write_mib_s", writeRates, "MiB/s", "higher");
  emitSamples("memory", "sram_read_mib_s", readRates, "MiB/s", "higher");

  double allocationTimes[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    const uint32_t start = micros();
    uint32_t successful = 0;
    for (uint32_t i = 0; i < kAllocations; ++i) {
      void* block = malloc(512);
      if (block == nullptr) {
        break;
      }
      memset(block, static_cast<int>(i), 512);
      free(block);
      ++successful;
    }
    const uint32_t elapsed = micros() - start;
    if (elapsed == 0 || successful != kAllocations) {
      emitUnavailable("memory", "allocation_latency_us",
                      "allocation_stress_failed");
      return;
    }
    allocationTimes[sample] =
        static_cast<double>(elapsed) / kAllocations;
  }
  emitSamples("memory", "allocation_latency_us", allocationTimes, "us/op",
              "lower");
}

void runFlashBenchmark() {
  uint8_t* buffer = static_cast<uint8_t*>(malloc(kFlashBlockBytes));
  if (buffer == nullptr) {
    emitUnavailable("memory", "flash_read_mib_s", "buffer_allocation_failed");
    return;
  }

  double rates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    volatile uint32_t checksum = 0;
    const uint32_t start = micros();
    for (uint32_t offset = 0; offset < kFlashBytes;
         offset += kFlashBlockBytes) {
      if (!ESP.flashRead(
              0x1000 + offset, reinterpret_cast<uint32_t*>(buffer),
              kFlashBlockBytes)) {
        free(buffer);
        emitUnavailable("memory", "flash_read_mib_s", "flash_read_failed");
        return;
      }
      checksum += buffer[offset % kFlashBlockBytes];
      ESP.wdtFeed();
    }
    const uint32_t elapsed = micros() - start;
    if (elapsed == 0 || checksum == 0) {
      free(buffer);
      emitUnavailable("memory", "flash_read_mib_s",
                      "invalid_timer_or_checksum");
      return;
    }
    rates[sample] =
        (static_cast<double>(kFlashBytes) / (1024.0 * 1024.0)) *
        1000000.0 / elapsed;
  }
  free(buffer);
  emitSamples("memory", "flash_read_mib_s", rates, "MiB/s", "higher");
}

void runGpioBenchmark() {
  pinMode(kBenchmarkGpio, OUTPUT);
  constexpr uint32_t kTransitions = 100000;
  double rates[kSampleCount];
  for (uint32_t sample = 0; sample < kSampleCount; ++sample) {
    const uint32_t start = micros();
    for (uint32_t i = 0; i < kTransitions; ++i) {
      digitalWrite(kBenchmarkGpio, (i & 1u) != 0 ? HIGH : LOW);
      if ((i & 0x0FFFu) == 0) {
        ESP.wdtFeed();
      }
    }
    const uint32_t elapsed = micros() - start;
    if (elapsed == 0) {
      emitUnavailable("peripherals", "gpio_transitions_s", "invalid_timer");
      return;
    }
    rates[sample] =
        static_cast<double>(kTransitions) * 1000000.0 / elapsed;
  }
  digitalWrite(kBenchmarkGpio, LOW);
  emitSamples("peripherals", "gpio_transitions_s", rates, "transitions/s",
              "higher");
}

void runConnectivityProbe() {
  WiFi.mode(WIFI_STA);
  WiFi.disconnect();
  delay(100);
  const uint32_t start = micros();
  const int16_t networks = WiFi.scanNetworks(false, true);
  const uint32_t elapsed = micros() - start;
  WiFi.scanDelete();
  WiFi.mode(WIFI_OFF);
  if (networks < 0 || elapsed == 0) {
    emitUnavailable("connectivity", "wifi_scan_ms", "scan_failed");
  } else {
    emitMetric("connectivity", "wifi_scan_ms", elapsed / 1000.0, "ms",
               "lower", "informational", 1);
    emitMetric("connectivity", "wifi_visible_networks", networks,
               "networks", "higher", "informational", 1);
  }
  emitUnavailable("connectivity", "wifi_tcp_udp_mbit_s",
                  "iperf_peer_and_wifi_credentials_not_configured");
  emitUnavailable("connectivity", "ble_throughput_kbit_s",
                  "ble_not_supported");
  emitUnavailable("connectivity", "thread_zigbee_latency_ms",
                  "802154_not_supported");
}

void printRunStart() {
  const uint32_t runId = micros() + (++g_runSequence * 2654435761UL);
  Serial.printf(
      "{\"type\":\"run_start\",\"schema_version\":1,\"run_id\":\"%08lx\","
      "\"device\":{\"target\":\"ESP8266EX\",\"family\":\"esp8266\","
      "\"architecture\":\"Xtensa-LX106\",\"firmware_version\":\"0.2.0\","
      "\"comparability_class\":\"esp8266_wifi\",\"cores\":1,"
      "\"cpu_mhz\":%u,\"flash_bytes\":%lu,\"psram_bytes\":0,"
      "\"heap_free_bytes\":%lu,\"sdk_version\":\"%s\","
      "\"wifi_supported\":true,\"ble_supported\":false}}\n",
      static_cast<unsigned long>(runId), ESP.getCpuFreqMHz(),
      static_cast<unsigned long>(ESP.getFlashChipSize()),
      static_cast<unsigned long>(ESP.getFreeHeap()), ESP.getSdkVersion());
}

void runSuite() {
  Serial.println(
      "{\"type\":\"suite\",\"name\":\"ESP32xBenchmark\","
      "\"version\":\"0.2.0\",\"target\":\"ESP8266EX\"}");
  runCpuBenchmarks();
  runMemoryBenchmarks();
  runFlashBenchmark();
  runGpioBenchmark();
  emitUnavailable("rtos", "task_handoff_us", "freertos_not_available");
  emitUnavailable("rtos", "queue_roundtrip_us", "freertos_not_available");
  runConnectivityProbe();
  emitUnavailable("power", "active_current_ma",
                  "external_power_analyzer_required");
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
  printRunStart();
  runSuite();
}

void loop() {
  delay(15000);
  printRunStart();
  runSuite();
}
