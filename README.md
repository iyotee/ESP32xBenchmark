# ESP32xBenchmark

ESP32xBenchmark is a reproducible, hardware-measured benchmark suite for ESP32-family and ESP8266 development boards. The firmware emits structured serial records; the host collector aggregates three complete suite repetitions by default, creates JSON/CSV/HTML reports, and calculates a weighted global index against a same-class calibration profile.

> **This is not a simulated benchmark.** The firmware reports real measurements. Categories that need unavailable hardware or peers are marked skipped and excluded from the score.

## What is measured today

| Category | Measurements | Notes |
|---|---|---|
| CPU | Integer and floating-point workload iterations/second | Fixed workloads; five samples; median, range, and variation reported |
| Memory | Internal SRAM read/write throughput, allocation latency, app-flash read throughput, PSRAM write throughput when present | PSRAM reports skipped when the chip has none |
| Peripherals | GPIO2 transitions/second | Pin 2 is toggled; connect an oscilloscope/logic analyzer for signal-integrity measurements |
| RTOS | FreeRTOS task notification handoff and queue round-trip latency | Five samples; median, range, and variation reported |
| Connectivity | Wi-Fi scan duration and visible AP count | Informational only: scan time is environment-dependent and is not scored as throughput |
| Power | Active/deep-sleep current and wake latency | Skipped honestly until an external power analyzer is connected |
| AI/TinyML | Inference latency | Skipped until a model/runtime and accuracy test are configured |

Wi-Fi TCP/UDP throughput, BLE, ESP-NOW, Thread/Zigbee, ADC, SPI, and I2C are explicitly reported as unavailable until their peer, fixture, or credentials are configured. This avoids presenting a Wi-Fi scan as a throughput result or treating a software GPIO loop as an electrical measurement.

## Quick start (Windows PowerShell)

```powershell
python -m pip install -e ".[hardware,test]"
python -m pytest
Set-Location .\platformio_test
pio run
pio run -t upload
Set-Location ..
python -m esp32_benchmark.cli capture --port COM6 --project-dir platformio_test --timeout 120 --calibrate esp32-classic-v1
```

The capture command writes:

- `artifacts/results.json` — raw run, metrics, and scores
- `artifacts/results.csv` — one row per metric
- `artifacts/results.html` — human-readable report
- `artifacts/reference_profile.json` — created only when `--calibrate` is used

For a Wemos D1 mini (ESP8266), select its separate firmware environment and its own calibration class:

```powershell
python -m esp32_benchmark.cli capture --port COM6 --project-dir platformio_test --environment esp8266 --upload --calibrate esp8266-d1-mini-v1 --profile-output samples\reference_profiles\esp8266-d1-mini-v1.json --output-dir samples\reference_profiles\esp8266-d1-mini-v1-run
```

For later runs in the same comparability class:

```powershell
python -m esp32_benchmark.cli capture --port COM6 --reference artifacts/reference_profile.json --output-dir artifacts\candidate
```

To score a previously captured run:

```powershell
python -m esp32_benchmark.cli score artifacts\candidate\results.json --reference artifacts\reference_profile.json --output-dir artifacts\scored
```

External probes and peer tests can add real measurements with a JSON supplement such as:

```json
{
  "metrics": [
    {
      "category": "power",
      "name": "active_current_ma",
      "value": 82.4,
      "unit": "mA",
      "direction": "lower",
      "status": "ok"
    }
  ]
}
```

Pass it with `--supplement measurements.json` to `capture` or `score`. The supplement must contain measured values, not estimates; a matching same-class reference metric is also required before the value affects the score.

The host can build and upload as part of capture with `--upload`; use `--environment` to select a PlatformIO environment.

## Global score

The score is a **reference index**, not a fabricated universal percentage:

- CPU 20%
- Memory 18%
- Connectivity 20%
- Power 18%
- Peripherals 10%
- RTOS 10%
- AI/TinyML 4%

Each measured metric is compared to the same metric in a calibration run. The host aggregates repeated suite medians from one firmware session and computes variation across repetitions. For higher-is-better metrics, its index is `observed / reference × 100`; for lower-is-better metrics, it is `reference / observed × 100`. Category scores are the arithmetic mean of their included metric indices. The global index is the weighted mean of available category scores, renormalized over available weights. The reference run scores 100; scores can exceed 100 and are intentionally not capped.

Every report includes **coverage**. Metrics with more than 25% within-run variation are excluded and listed as unstable; missing categories are excluded rather than guessed. Comparisons are rejected when the target's `comparability_class` differs from the profile's class. Calibrate a new reference per class; do not compare a classic ESP32 profile against an ESP32-C6 or P4.

Calibration is only as good as the reference run. Use stable power, clock, build flags, ambient conditions, and test fixtures, and preserve the profile with the raw run. A calibration run is a project baseline, not a manufacturer specification or an industry-certified result.

## PlatformIO targets

PlatformIO build environments are provided for ESP32, S2, S3, C3, C6, the C3 Super Mini, and the ESP8266-based Wemos D1 mini. The Super Mini profile enables its native USB-Serial/JTAG CDC console; other C3 boards can use the regular `esp32c3` environment. ESP8266 has a separate firmware because it does not share the ESP32 SDK or FreeRTOS capabilities. Physical calibration profiles currently cover the classic ESP32 Dev Module (`ESP32-D0WD-V3`), the ESP32-C3 Super Mini, and the Wemos D1 mini. The firmware identifies the SoC and assigns a comparability class at runtime. Scores from different comparability classes must not be compared as one global index. Additional radio/peripheral fixtures are part of the expansion plan in [PLAN.md](PLAN.md).

## Reference projects and attribution

The suite uses its own measurement and result protocol. It does not copy source code from the following references:

- [ESP32ShadowStackBenchmark](https://github.com/LehnigerKai/ESP32ShadowStackBenchmark) — modular benchmark and automation patterns
- [CoreMark](https://github.com/eembc/coremark) and [Embench-IoT](https://github.com/embench/embench-iot) — CPU workload integration candidates; their licenses and exact integration requirements must be preserved
- [Espressif Wi-Fi performance guide](https://github.com/espressif/esp-idf/blob/master/docs/en/api-guides/wifi-driver/wifi-performance-and-power-save.rst) and [iperf example](https://github.com/espressif/esp-idf/tree/master/examples/wifi/iperf) — network methodology
- [EdgeBench ESP32](https://github.com/irajPatel/EdgeBench-ESP32--TinyML-Inference-Benchmarking-Suite) — optional TinyML methodology
- [ESP32-C6 Wireless Performance Evaluation](https://github.com/Anton-Johansson-it/performance-evaluation-esp32-c6) — controlled radio test methodology
- [ESP32 Language Lab](https://github.com/georgik/esp32-lang-lab) — future framework/runtime comparison

See [docs/METHODOLOGY.md](docs/METHODOLOGY.md) for measurement limitations and reproducibility details.
