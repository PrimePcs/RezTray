# RezTray

Tray app for NVIDIA GPUs. It lives in the up arrow next to the clock. Left or right click the icon.

## Download

Get `RezTray-share.zip` from the [latest release](https://github.com/PrimePcs/RezTray/releases/latest), unzip it and run `RezTray.exe`. There is nothing to install.

You need Windows 10 or 11 (64-bit) and an NVIDIA GPU.

The app is not code-signed, so Windows may show "Windows protected your PC". Click **More info**, then **Run anyway**.

## What it does

- **Switch resolution:** each monitor lists its native mode plus every custom resolution saved in the NVIDIA driver.
- **Create resolution:** type a size and refresh rate. The driver shows it for 15 seconds. Click Keep to save it, otherwise it goes back by itself.
- **Delete resolution:** in the same window.
- **Color profiles:** digital vibrance, brightness, contrast and gamma, set per monitor.
- **Start with Windows:** off until you tick it in the menu.

Sizes up to the monitor's own can be created. The monitor stays on its usual signal and the GPU scales the picture, the same way the NVIDIA Control Panel does it.
Whether a smaller resolution stretches or shows black bars follows "Adjust desktop size and position" in NVIDIA Control Panel.

Color profiles are saved in `%APPDATA%\RezTray\settings.json`. Resolutions are saved in the NVIDIA driver.

## Build

```bash
dotnet build -c Release
```

Output is `bin\Release\net48\RezTray.exe`. It needs nothing beyond Windows 10/11 and an NVIDIA driver.

`RezTray.exe --dump report.txt` writes what the app sees without changing anything.
