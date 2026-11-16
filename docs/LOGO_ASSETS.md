# Logo asset pipeline

The two JPGs in `assets/` are the preserved originals. Final transparent assets
are generated into `assets/final/`; the scripts do not create intermediate
files in the repository.

On Windows, install the isolated tooling and run both steps from the repository
root:

```powershell
$venv = Join-Path $env:TEMP 'esp32x-logo-tools'
python -m venv $venv
$python = Join-Path $venv 'Scripts\python.exe'
& $python -m pip install -r scripts\requirements_logo.txt
& $python scripts\process_logo.py
& $python scripts\build_logo_sizes.py
```

`process_logo.py` uses the open-source U²-Net model via `rembg` to assist
foreground extraction. Since the source JPGs contain a checkerboard baked into
the pixels, the script also applies a logo-specific red/white matte to remove
that pattern cleanly, then creates a padded 4× transparent PNG master with
high-quality resampling. The first run downloads the model into the user's
`%USERPROFILE%\.rembg` cache.

`build_logo_sizes.py` creates:

- `logo-header-dark.png` and `logo-header-light.png` for the WPF header. White
  details are recolored dark in the light variant.
- `logo-header-tile-dark.png` and `logo-header-tile-light.png` for the Windows
  window icon, with rounded gray tiles matching the application theme.
- `favicon.ico` and `favicon-16.png` through `favicon-256.png` for light
  surfaces.
- `app-icon.ico` for the Windows executable, with a rounded dark-gray tile.

Pass `--source assets\ESPxBenchmark_logo_uncropped.jpg` to process the
uncropped original, or `--scale` to change the master enlargement. The resize
script accepts `--source` and `--output-dir`.
