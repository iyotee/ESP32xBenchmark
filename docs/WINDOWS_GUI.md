# Windows desktop application

The desktop application is a native C#/.NET 8 WPF app with Fluent styling from
[WPF UI](https://github.com/lepoco/wpfui) (MIT, pinned at 4.3.0). It is not a
Python launcher or a wrapper around the command line. The existing Python CLI
and firmware remain available and unchanged. The C# core reads the same
line-delimited serial JSON, result JSON, reference profiles, category weights,
stability threshold, and comparison rules.

## Run and publish

The build machine needs Windows x64, internet access for package restore, and a
.NET 8 SDK. The repository script uses an installed SDK if it has one; when it
does not, it downloads and installs the official Microsoft .NET 8 SDK locally
under `windows\.dotnet`:

```powershell
.\scripts\build_windows_desktop.ps1
```

The script runs the .NET parity/protocol tests and publishes a self-contained,
single-file executable:

```text
windows\Esp32Benchmark.Desktop\publish\ESP32xBenchmark.exe
```

Equivalent direct commands when the SDK is installed:

```powershell
dotnet test windows\ESP32xBenchmark.sln --configuration Release
dotnet publish windows\Esp32Benchmark.Desktop\Esp32Benchmark.Desktop.csproj `
  --configuration Release -p:PublishProfile=win-x64
```

The EXE bundles the .NET runtime and WPF UI library; the target PC does not need
Python, .NET, or an installer. The binary is unsigned, so Windows SmartScreen
may warn when launching a locally built release.

The EXE also embeds `platformio.ini`, `src/main.cpp`, and
`src/main_esp8266.cpp`. If the optional **External PlatformIO project** field is
left blank, the app verifies/extracts the matching firmware sources to a
content-versioned writable folder under
`%LOCALAPPDATA%\ESP32xBenchmark\firmware-projects`. This lets the EXE be copied
and run outside the checkout while keeping PlatformIO build/upload available.
Selecting an external project folder uses that folder instead of the bundled
copy. The build script launches the published EXE in a verification mode and
fails if the embedded files are absent or do not extract byte-for-byte.

## Hardware workflow

- **COM ports and identity:** Ports are discovered through `System.IO.Ports`.
  Identify listens for the firmware's JSON `run_start` device metadata. This
  identifies the supported SoC/family when benchmark firmware is running.
  Opening a port may reset the board. USB bridge hardware IDs alone are not
  considered a reliable board model.
- **Environment selection:** Known SoCs map to the corresponding existing
  PlatformIO environment. Firmware metadata cannot distinguish every carrier
  board variant (for example, generic ESP32-C3 versus a C3 Super Mini), so the
  environment remains editable and is never silently forced for an unknown
  target.
- **Build and upload:** Build invokes `pio run -e <environment>`. Upload invokes
  `pio run -e <environment> -t upload --upload-port <selected COM port>`.
  Upload always uses the explicit COM port selected in the app. PlatformIO Core,
  the board platform, and toolchains are external prerequisites and are
  intentionally not bundled; the firmware source files needed by this project
  are embedded in the EXE and materialized into the per-user cache when needed.
- **Capture:** Capture consumes the existing line-delimited JSON protocol and
  aggregates the requested number of complete runs with the same median,
  min/max, run-variation and stability behavior as the Python collector.
- **Serial console:** The console supports live receive and line sends. Close it
  before identify, build, upload, or capture because only one process can own a
  COM port at a time.

If no COM port is listed, connect the board, install its USB-serial driver if
needed, and refresh. Unsupported firmware identity and COM-in-use errors are
shown directly. Capture timeouts report the number of complete runs received.

## Scores, history, and reports

- History and imported profiles live under
  `%LOCALAPPDATA%\ESP32xBenchmark\history` and
  `%LOCALAPPDATA%\ESP32xBenchmark\profiles`. Each saved run is tagged by
  target, comparability class, and selected reference profile.
- Results and comparisons are exported as JSON, CSV, and HTML under
  `%LOCALAPPDATA%\ESP32xBenchmark\artifacts`.
- Scoring follows the existing weighted categories, higher/lower metric
  directions, the 25% stability cutoff, reference class check, uncapped
  reference index, and coverage calculation.
- Cross-board comparison calls the C# port of the shared-reference semantics:
  one reference is applied to the stable metric intersection present in every
  selected board result. It does not imply unsupported categories are
  equivalent.
- Calibration profiles are created only from measured, valid result metrics.

`windows\Esp32Benchmark.Tests` includes parity tests against the published
Python comparison JSON and serial-protocol tests. The Python CLI has its own
unchanged tests under `tests/`.

## Layout

- `windows\Esp32Benchmark.Desktop` — WPF shell, Fluent UI, serial/PlatformIO
  orchestration, local history, and operator workflows.
- `windows\Esp32Benchmark.Core` — JSON protocol aggregation, environment
  recommendation, scoring/comparison rules, and report writers.
- `windows\Esp32Benchmark.Tests` — .NET parity and protocol tests using checked
  in benchmark samples.
- `scripts\build_windows_desktop.ps1` — .NET SDK bootstrap, tests and single-file
  publish workflow.

Physical upload/capture still requires a supported board and its USB driver;
unit tests validate protocol and scoring but do not simulate hardware.
