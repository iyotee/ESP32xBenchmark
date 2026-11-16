# Measurement methodology

## Result protocol

Firmware output is newline-delimited JSON. A run contains:

1. `run_start`, with schema version, run ID and detected SoC metadata.
2. One `metric` record for each measured or attempted test.
3. `run_complete`.

Metric records use stable names, units, direction (`higher` or `lower`), status (`ok`, `informational`, or `skipped`), and sample count. A skipped record includes a machine-readable reason. The host collector ignores bootloader text and accepts only complete run sequences.

## Sampling and scoring

- Current firmware tests collect five samples and use the median.
- Sampled measurements report minimum, maximum, and population standard deviation as a percentage of the mean.
- The host collector combines three complete suite repetitions from one firmware session by default, using the median of repetition-level metric values and recording variation across repetitions.
- Metrics with more than 25% within-run or run-to-run variation are excluded from calibration and scoring; reports list excluded unstable metrics.
- Calibration profiles are captured from completed physical runs and store the exact metric values, units, target class, and reference device metadata.
- The regular score command requires a matching `comparability_class` and matching metric name, category, direction, and unit.
- The explicit cross-board compare command uses one measured profile and only the stable metric intersection shared by every supplied run. All compared boards therefore have the same scoring categories and coverage.
- Higher-is-better metric index: `observed / reference × 100`.
- Lower-is-better metric index: `reference / observed × 100`.
- Category score: arithmetic mean of scored metric indices in that category.
- Global score: weighted mean of available category scores, normalized by the sum of available category weights.
- Coverage is the sum of available category weights divided by 100%.

Indices are intentionally not capped at 100. A score of 100 means the device matches the selected measured profile, not that it is at a theoretical or industry maximum. A cross-board index is meaningful only for its declared common workloads and reference. Published internet results from a different benchmark, compiler configuration, or workload are not interchangeable with this suite's profile.

## Environmental controls

For defensible comparisons, record and hold constant wherever possible:

- board/module revision, USB supply, clock configuration, CPU frequency, SDK/framework, compiler and build flags
- ambient temperature and thermal stabilization
- other tasks, serial logging, Wi-Fi environment, access point, channel, distance, and antenna orientation
- memory placement, PSRAM configuration, partition layout, and test input sizes
- GPIO pin wiring and connected instruments
- power analyzer model, sampling rate, shunt, and measurement point

Serial logging and Wi-Fi scans affect power and timing. They are reported separately and are not treated as isolated radio throughput measurements.

## What current firmware does not claim

- ESP8266 uses a separate Arduino-core firmware and `esp8266_wifi` comparability class. Its RTOS tests are skipped because this target does not expose the same FreeRTOS interface as ESP32; ESP8266 scores must not be compared to ESP32 class indices.
- A Wi-Fi scan is not TCP/UDP throughput. It is informational only.
- GPIO API toggles per second are not pin rise/fall time, signal integrity, or a full peripheral benchmark.
- CPU workload iteration rates are project-local workloads; they are not CoreMark or Embench results.
- Current draw, deep-sleep current, and wake energy cannot be inferred from software. They require a physical power instrument.
- TinyML performance requires a fixed model, input corpus, runtime, quantization, and accuracy checks.

## Upstream integrations

CoreMark, Embench, Espressif iperf, and TinyML should be integrated as versioned upstream dependencies with their original licenses, attribution, configuration, and provenance. Do not copy or alter their workloads while claiming standard results. Until those integrations are built and verified, reports must continue to label the matching categories unavailable.

License review found important differences that affect integration: Embench-IoT is GPL-3.0, while CoreMark's repository includes an EEMBC acceptable-use agreement governing both the software and trademark use. ESP32ShadowStackBenchmark is Apache-2.0 at the repository level but contains separately sourced benchmark projects. Each upstream component therefore needs its own pinned revision, license/notice review, and distribution decision; the root MIT license must not be presented as covering those third-party components.
