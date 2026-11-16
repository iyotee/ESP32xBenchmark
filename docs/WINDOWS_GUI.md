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

- **COM ports and identity:** `System.IO.Ports` provides the primary COM-port
  list. Windows SetupAPI adds device instance IDs for board-specific USB
  detection; it uses stable device-class GUIDs and is not required for COM
  names to appear. If SetupAPI metadata lookup fails, the COM list remains
  available and the app reports the warning. The list refreshes automatically
  while the app is open; use **Refresh** to query immediately. If no port is
  listed, reconnect a data-capable USB cable and check Device Manager for the
  board's USB-serial driver.
  Existing benchmark firmware provides its target metadata directly; otherwise
  the ROM probe identifies the silicon even when the carrier uses a generic
  CH340/CP210x/FTDI USB bridge.
- **Environment selection:** Known SoCs map to the corresponding existing
  PlatformIO environment. On a new connection, the app first checks for
  benchmark firmware identity; if none is available, it makes a read-only
  `esptool` ROM chip-ID probe and selects the matching SoC environment. The
  probe briefly resets the board but does not write flash. Carrier-board
  variants that use the same SoC can still have different USB/serial wiring;
  the target remains manually overridable for those cases.
- **Build and upload:** Build invokes `pio run -e <environment>`. Upload invokes
  `pio run -e <environment> -t upload --upload-port <selected COM port>`.
  Upload always uses the explicit COM port selected in the app. PlatformIO Core,
  the board platform, and toolchains are external prerequisites and are
  intentionally not bundled; the firmware source files needed by this project
  are embedded in the EXE and materialized into the per-user cache when needed.
- **Capture:** Capture consumes the existing line-delimited JSON protocol and
  aggregates the requested number of complete runs with the same median,
  min/max, run-variation and stability behavior as the Python collector.
- **Advanced settings:** Reference profile, serial rate, run count, timeout,
  external PlatformIO project, build-only, and capture-without-upload controls
  are grouped under the collapsed **Advanced settings** expander. The default
  workflow keeps only port/target selection, upload-and-capture, and firmware
  identification visible.
- **Serial console:** The console supports live receive and line sends. Close it
  before identify, build, upload, or capture because only one process can own a
  COM port at a time.

If no COM port is listed, connect the board, install its USB-serial driver if
needed, and refresh. Unsupported firmware identity and COM-in-use errors are
shown directly. Capture timeouts report the number of complete runs received.

## Appearance and language

- The interface offers **System**, **Dark**, and **Light** themes; System is the
  first-launch default. Theme colors are monochrome and follow Windows when
  System is selected. A single animated header icon cycles System (monitor),
  Light (sun), and Dark (moon). Rounded input and drop-down templates use the
  same theme-aware foreground, background, hover, and selection colors.
  Theme and language choices are stored in
  `%LOCALAPPDATA%\ESP32xBenchmark\preferences.json`.
- The language selector lists .NET regional cultures and can follow the Windows
  UI language automatically. English and French catalogs are currently
  translated; other cultures are listed with an explicit English-fallback
  marker. This provides culture-aware number/date formatting and RTL layout
  where applicable, but does not mean every listed culture has translated
  interface text yet.
- Page changes use a brief fade/slide transition and respect the Windows
  client-area animation setting. Double-clicking a history row reopens that
  saved result on the benchmark page; single-click selection remains available
  for comparison, calibration, and deletion.

## Scores, history, and reports

- History and imported profiles live under
  `%LOCALAPPDATA%\ESP32xBenchmark\history` and
  `%LOCALAPPDATA%\ESP32xBenchmark\profiles`. Each saved run is tagged by
  target, comparability class, and selected reference profile.
- The measured ESP32 classic reference profile is bundled with the app and
  installed on first launch without overwriting local profiles. It remains the
  fixed default anchor for every board, so cross-class scores stay on one scale.
- Results and comparisons are exported as JSON, CSV, and HTML under
  `%LOCALAPPDATA%\ESP32xBenchmark\artifacts`.
- Scoring uses the existing weighted categories, higher/lower metric
  directions, the 25% stability cutoff, and coverage calculation. The ESP32
  classic reference is anchored at 20/100; metric points scale linearly from
  that reference and are capped at 100. Other board classes use matching
  metrics and units against the same anchor and are labeled cross-class.
  Category coverage is the share of total category weights represented by
  stable, matching measurements; omitted categories do not contribute to the
  score. It helps show when a score is based on only part of the benchmark.
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

## Portable releases and updates

- Stable Windows releases use tags in `VYYYY.MM.DD.N` form, for example
  `V2026.11.16.2`. Pushing a tag runs the Windows test/publish workflow and
  attaches the self-contained `ESP32xBenchmark-win-x64.exe` to a GitHub Release.
- The release workflow publishes `latest.json` on `main` with the newest
  release tag and the executable's SHA-256 digest. The app checks that public
  GitHub-hosted manifest every 10 seconds using conditional HTTP requests; no
  private key or user token is required.
- A newer binary is downloaded to the per-user update cache and its digest is
  verified before a separate helper waits for the app to close, replaces the
  portable EXE, and restarts it. The target folder must be writable by the
  current Windows user for automatic replacement to succeed.
