$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkDirectory = Join-Path $repoRoot "windows\.dotnet"
$localDotnet = Join-Path $sdkDirectory "dotnet.exe"
$solution = Join-Path $repoRoot "windows\ESP32xBenchmark.sln"
$desktopProject = Join-Path $repoRoot "windows\Esp32Benchmark.Desktop\Esp32Benchmark.Desktop.csproj"

if ($env:OS -ne "Windows_NT") {
    throw "The WPF application must be built on Windows."
}
Set-Location $repoRoot

if (-not (Test-Path $localDotnet)) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $hasSdk = $false
    if ($systemDotnet) {
        $sdkList = & $systemDotnet.Source --list-sdks
        $hasSdk = ($sdkList | Where-Object { $_ -match '^8\.' }).Count -gt 0
    }

    if ($hasSdk) {
        $localDotnet = $systemDotnet.Source
    } else {
        Write-Host "Installing the official .NET 8 SDK locally under windows\.dotnet ..."
        $response = Invoke-WebRequest -UseBasicParsing -Uri "https://dot.net/v1/dotnet-install.ps1"
        $installer = [System.Text.Encoding]::UTF8.GetString($response.Content)
        & ([scriptblock]::Create($installer)) -Channel 8.0 -InstallDir $sdkDirectory -NoPath
        if (-not (Test-Path $localDotnet)) {
            throw "Could not install the official .NET 8 SDK. Install the .NET 8 SDK and rerun this script."
        }
    }
}

Write-Host "Running parity and protocol tests..."
& $localDotnet test $solution --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw ".NET tests failed."
}

Write-Host "Publishing self-contained single-file win-x64 application..."
$publishDirectory = Join-Path $repoRoot "windows\Esp32Benchmark.Desktop\publish"
if (Test-Path $publishDirectory) {
    Remove-Item -Recurse -Force $publishDirectory
}
& $localDotnet publish $desktopProject --configuration Release -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) {
    throw "WPF single-file publish failed."
}

$executable = Join-Path $repoRoot "windows\Esp32Benchmark.Desktop\publish\ESP32xBenchmark.exe"
if (-not (Test-Path $executable)) {
    throw "Build succeeded but the expected executable was not produced: $executable"
}
Get-ChildItem $publishDirectory -Filter "*.pdb" -File | Remove-Item -Force
$verification = Start-Process -FilePath $executable `
    -ArgumentList "--verify-firmware-bundle" -Wait -PassThru
if ($verification.ExitCode -ne 0) {
    throw "Published EXE is missing a valid embedded firmware bundle (verification exit code $($verification.ExitCode))."
}
Write-Host "Portable application: $executable"
Write-Host "Embedded firmware sources verified and extracted to the per-user cache."
