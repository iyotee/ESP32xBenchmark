# Reference profiles

Calibration profiles are created from physical benchmark runs and must be kept separate by `comparability_class`.

The `esp32-classic-v1.json` profile in this directory is measured on the connected ESP32-D0WD-V3 development module using PlatformIO Arduino-ESP32 / ESP-IDF 5.5.5. It is a project-specific baseline only. It is not a vendor specification, and its score cannot be compared to profiles from another class.

Generate or replace a calibration profile and retain its matching raw run/report with:

```powershell
python -m esp32_benchmark.cli capture --port COM6 --project-dir platformio_test --upload --calibrate esp32-classic-v1 --profile-output samples/reference_profiles/esp32-classic-v1.json --output-dir samples/reference_profiles/esp32-classic-v1-run
```

Only publish a new profile after checking its device metadata, environment, firmware revision, and measurement stability.
