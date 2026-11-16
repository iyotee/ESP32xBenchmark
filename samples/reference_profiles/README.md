# Reference profiles

Calibration profiles are created from physical benchmark runs and must be kept separate by `comparability_class`.

The `esp32-classic-v1.json` profile in this directory is measured on the connected ESP32-D0WD-V3 development module using PlatformIO Arduino-ESP32 / ESP-IDF 5.5.5. It is a project-specific baseline only. It is not a vendor specification, and its score cannot be compared to profiles from another class.

Generate or replace a calibration profile and retain its matching raw run/report with:

```powershell
python -m esp32_benchmark.cli capture --port COM6 --project-dir platformio_test --upload --calibrate esp32-classic-v1 --profile-output samples/reference_profiles/esp32-classic-v1.json --output-dir samples/reference_profiles/esp32-classic-v1-run
```

Only publish a new profile after checking its device metadata, environment, firmware revision, and measurement stability.

The adjacent `esp32-classic-v1-verification` report is a separate three-repetition capture against this calibration. It scored 100.0 with 58% category-weight coverage on the same board. That index is relative to this project's own reference, not a universal performance score; connectivity, power, and AI/TinyML were excluded because the required peer/instrument/model was not configured.

The `esp32-c3-super-mini-v1.json` profile and `esp32-c3-super-mini-v1-run` report are a separate calibration for the connected ESP32-C3 Super Mini. Its `esp32c3_supermini` PlatformIO environment enables the board's native USB-Serial/JTAG CDC console. The separate `esp32-c3-super-mini-v1-verification` run scored 100.03 against its own reference, with 58% category-weight coverage. This score is not directly comparable to the classic ESP32 profile.

The `esp8266-d1-mini-v1.json` profile is calibrated on the connected Wemos D1 mini (ESP8266EX). Its separate firmware measures CPU, SRAM, flash reads, and GPIO, while reporting RTOS as unavailable because the ESP8266 Arduino core does not provide the ESP32 FreeRTOS test interface. The independent verification run is under `esp8266-d1-mini-v1-verification`; its 100.0 index has 48% category-weight coverage and is comparable only with an `esp8266_wifi` reference.
