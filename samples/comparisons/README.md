# Cross-board comparisons

The comparison reports in this directory use one measured reference profile for every device and score only the stable metric intersection shared by all inputs. This keeps the metric set, category weights, and coverage fixed across the table.

`esp32-classic-anchor-v1` currently uses the physical ESP32-D0WD-V3 profile as a provisional anchor. It compares CPU, SRAM/flash memory, and GPIO measurements across the ESP32 classic, ESP32-C3 Super Mini, and ESP8266 Wemos D1 mini. Coverage is 48%; connectivity, power, RTOS, and AI/TinyML do not contribute to this common score.

The scores are useful for this project's workloads, but they are not manufacturer specifications or industry-standard scores. Internet results from unrelated benchmarks are not interchangeable. Once an ESP32-S3 N16R8 is available, capture a profile with the same firmware and run `compare` against that shared reference to produce an S3-anchored table.
