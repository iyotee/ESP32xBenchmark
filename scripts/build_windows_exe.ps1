$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (-not (Test-Path "desktop_entry.py")) {
        throw "Run this script from the repository checkout."
    }
    if ($env:OS -ne "Windows_NT") {
        throw "Build the portable Windows executable on Windows."
    }

    $launcher = Get-Command py -ErrorAction SilentlyContinue
    if ($launcher) {
        & py -3.12 -m venv .venv-windows-build
    } else {
        python -m venv .venv-windows-build
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not create the isolated Python build environment." }
    $python = Join-Path $repoRoot ".venv-windows-build\Scripts\python.exe"
    & $python -c "import sys; assert sys.version_info[:2] == (3, 12), 'Use Python 3.12 for the pinned Windows build.'"
    if ($LASTEXITCODE -ne 0) { throw "The build environment must use Python 3.12." }
    & $python -m pip install -r requirements-windows-build.txt
    if ($LASTEXITCODE -ne 0) { throw "Could not install pinned Windows build dependencies." }
    & $python -m pip install --no-build-isolation -e .
    if ($LASTEXITCODE -ne 0) { throw "Could not install ESP32xBenchmark into the build environment." }

    & $python -m PyInstaller --noconfirm --clean --onefile --windowed `
        --name ESP32xBenchmark --paths src `
        --collect-submodules serial --collect-submodules esp32_benchmark `
        --add-data "platformio_test\platformio.ini;platformio_test" `
        --add-data "platformio_test\src\main.cpp;platformio_test\src" `
        --add-data "platformio_test\src\main_esp8266.cpp;platformio_test\src" `
        desktop_entry.py
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed to create the executable." }

    Write-Host "Portable executable: $repoRoot\dist\ESP32xBenchmark.exe"
}
finally {
    Pop-Location
}
