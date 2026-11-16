# ESP32xBenchmark implementation plan

This is the visible project plan and delivery status. It replaces the plan that was previously kept only in the assistant session.

## North star

Provide reproducible, auditable comparisons across ESP32 families by combining a shared measurement protocol with capability-aware test suites. Scores are calibrated within a target class; unsupported or unmeasured categories are never guessed.

## Score weights

| Category | Weight | Current state |
|---|---:|---|
| CPU | 20% | Implemented: integer and float workload rates |
| Memory | 18% | Implemented: SRAM, allocation, app flash, optional PSRAM |
| Connectivity | 20% | Partial: Wi-Fi scan is informational; throughput/radio peer tests remain |
| Power | 18% | External current measurement required |
| Peripherals | 10% | Partial: GPIO software transition rate; electrical fixture tests remain |
| RTOS | 10% | Implemented: task notification handoff and queue round-trip |
| AI/TinyML | 4% | Optional; model/runtime and accuracy corpus remain |

## Delivery phases

### Phase 1 — shared contract and truthful score
- [x] Define line-delimited JSON serial records and result schema.
- [x] Add five-sample medians, three-suite repetition aggregation, between-run variation, device metadata, and explicit skip reasons.
- [x] Add reference profiles, class validation, category weights, global score, and coverage.
- [x] Generate JSON, CSV, and HTML reports.

### Phase 2 — runnable ESP32 firmware
- [x] Build CPU, internal-memory, application-flash, optional-PSRAM, GPIO, and FreeRTOS tests.
- [x] Include Wi-Fi discovery as informational-only and explicitly skip throughput without a peer.
- [x] Compile and run on the connected ESP32 Dev Module; retain a real calibration profile and raw result/report.
- [x] Verify the second physical run scores against the baseline (96.13 index, 58% category-weight coverage).

### Phase 3 — broader hardware coverage
- [x] Add PlatformIO build environments for ESP32, S2, S3, C3, and C6; verify builds for all five targets.
- [ ] Flash and collect verified calibration profiles for S2, S3, C3, and C6; add H2/P4 only after framework support is confirmed.
- [ ] Add a host/peer-driven Wi-Fi TCP/UDP suite using Espressif's iperf methodology.
- [ ] Add BLE, ESP-NOW, Thread/Zigbee and controlled-distance workloads where supported.
- [x] Accept externally measured metrics (e.g. power probe or peer throughput) as validated-format JSON supplements.
- [ ] Add direct instrument adapters and deep-sleep/wake measurements.
- [ ] Add reproducible ADC/SPI/I2C fixture tests.

### Phase 4 — recognized benchmark integrations
- [x] Review CoreMark and Embench licensing and integration constraints.
- [ ] Integrate CoreMark and Embench as independently versioned upstream dependencies; Embench is GPL-3.0 and CoreMark has a separate EEMBC acceptable-use agreement.
- [ ] Add optional TinyML inference model/runtime workloads with accuracy validation.
- [ ] Preserve upstream result output and provenance; do not silently modify reference algorithms.

### Phase 5 — publication and maintenance
- [x] Add methodology and contribution-facing project documentation.
- [x] Add host-side unit tests and CI build/test workflow.
- [ ] Publish the verified implementation to the configured GitHub repository.
- [x] Publish the classic ESP32 calibration run with firmware version, SDK, board, host, and environment metadata.

## Comparability rules

1. Every firmware run reports a `comparability_class`.
2. A scored result must use a reference profile with the exact same class.
3. Category and global indices are reference-relative (`reference = 100`), not capped.
4. The global score renormalizes across measured categories and always reports weight coverage.
5. Different classes require independent calibration; a single cross-family leaderboard is not valid.
6. External instruments, network peers, and AI models must be identified in run metadata before those categories count.

## First practical baseline

The connected ESP32-D0WD-V3 can be used to create `classic_wifi_ble` calibration profile. This is a project-specific measured baseline, not a manufacturer specification. It must not be used for other SoC classes.
