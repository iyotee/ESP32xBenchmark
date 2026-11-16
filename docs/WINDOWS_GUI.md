# Windows desktop app

ESP32xBenchmark includes a native-feeling Tkinter desktop app and a PyInstaller
single-file Windows executable workflow. The app uses the project's existing
collector, scoring/comparison functions, and JSON/CSV/HTML report writers; the
command-line interface remains unchanged.

## Run from the source checkout

Python 3.10+ is required. Install serial support and launch the GUI from the
repository root:

```powershell
python -m pip install -e ".[desktop]"
python -m esp32_benchmark.gui
```

Or install the console entry point and run `esp32-benchmark-gui`. Tkinter is
included with standard Windows Python distributions. If Python reports that
Tkinter is unavailable, install/repair Python with its Tcl/Tk component.

## Portable one-file executable (no installer)

Build on Windows with Python 3.12 and the Python launcher (`py`). From the
repository root:

```powershell
.\scripts\build_windows_exe.ps1
```

The script creates an isolated `.venv-windows-build`, installs the exact
versions in `requirements-windows-build.txt`, and builds:

```text
dist\ESP32xBenchmark.exe
```

The executable is a single portable file; it does not require Python or an
installer on the target PC. The PlatformIO project configuration and benchmark
firmware sources are bundled in the EXE. Copy it to a writable folder and run
it. Windows may show its normal SmartScreen warning for an unsigned locally
built binary. The source build is not code-signed. PyInstaller extracts its
bundled runtime and firmware sources to a temporary directory while running.

The GUI's history database is stored in
`%LOCALAPPDATA%\ESP32xBenchmark\history.sqlite3`; it is not stored alongside
the executable. Reports and generated reference profiles are written to the
folder configured in the app.

## Hardware and firmware workflow

1. Connect the board by USB and choose **Refresh**. The serial-port list comes
   from pyserial; USB VID/PID data is not treated as conclusive board identity.
2. With benchmark firmware already installed, choose the COM port and
   **Identify board**. The app listens for the firmware's JSON `run_start`
   record and selects a compatible PlatformIO environment when its SoC is
   recognized. Opening the port can reset the board. A non-benchmark firmware,
   bootloader-only connection, or closed serial port may not be identifiable.
3. Use **Build firmware** or select **Upload before capture**. Build/upload
   uses the bundled `platformio_test/platformio.ini` and firmware sources with
   the external PlatformIO Core CLI (`pio`). PlatformIO and its platform/
   toolchain packages are intentionally not bundled; install them separately.
   Upload explicitly targets the selected COM port.
4. Choose a reference profile if scoring is desired, then capture. The default
   is three complete runs, matching the existing CLI collector behavior.
   JSON, CSV, and HTML reports are written under `artifacts\desktop\latest`.
5. To use the serial console, connect in its tab, enter a line and send it.
   Disconnect before build, upload, identify, or capture; only one application
   can own a COM port at a time.

If no COM port is listed, confirm the board is connected, install the
manufacturer's USB-serial driver if required, and refresh. If pyserial is not
present in a source run, install `.[desktop]`. Capture timeouts explain how
many complete runs arrived; select the correct baud rate, reset the board,
and retry. Unsupported or unknown SoCs remain selectable manually, but no
unsupported firmware target is silently guessed.

## Results, calibration, and comparison

- Captures and imported results are stored in the user's local SQLite history,
  tagged by target, comparability class, and selected profile.
- Import a measured reference JSON profile from the run tab. **Calibrate
  selected** creates a profile from a selected measured run using the existing
  `make_reference_profile` validation rules.
- Select a profile and open **History & compare**. Select two or more runs and
  compare. The app calls the existing `compare_results` API, which permits
  cross-class comparison only over stable metrics shared by all entries and
  the one selected profile. It does not create its own scoring formula.
- A regular score still requires a matching `comparability_class`; mismatches
  are surfaced as errors. Reference scores are indices (reference = 100), not
  universal percentages. Coverage and exclusions remain visible in reports.

## Developer notes

- GUI orchestration: `src/esp32_benchmark/gui.py`
- Testable serial/profile/history/report helpers:
  `src/esp32_benchmark/gui_backend.py`
- PyInstaller entry point: `desktop_entry.py`
- Pinned Windows build dependencies: `requirements-windows-build.txt`
- One-file build script: `scripts/build_windows_exe.ps1`

Backend/GUI-focused tests can be run with:

```powershell
python -m pip install -e ".[test,desktop]"
python -m pytest tests\test_gui_backend.py tests\test_scoring.py
```

Tk widget tests require a Windows desktop session. Hardware capture, flashing,
and physical-board identification additionally require a connected supported
board and are not simulated by the unit tests.
