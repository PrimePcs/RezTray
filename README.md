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
- **Hotkeys:** bind keys to a resolution, native, a color profile or a setup. They work system-wide, including while a game has focus.
- **Refresh rate:** change Hz from the menu without creating a resolution.
- **Setups:** save every monitor's resolution and color under a name like "Gaming", then apply it in one click.
- **Import and export:** share color profiles and a resolution list as a `.reztray` file. Importing changes no display. Profiles join your list, and resolutions fill in the Create window, where the 15-second test still applies. Files leave out hotkeys, setups and anything that identifies your monitors.
- **Update alerts:** tells you when a new version is on GitHub. It only opens the download page and never installs anything. Turn it off under More.
- **Start with Windows:** off until you tick it under More.

Sizes up to the monitor's own can be created. The monitor stays on its usual signal and the GPU scales the picture, the same way the NVIDIA Control Panel does it.
Whether a smaller resolution stretches or shows black bars follows "Adjust desktop size and position" in NVIDIA Control Panel.

In a game running in exclusive fullscreen, a resolution hotkey changes the desktop, and the game may keep its own resolution until you leave it. Borderless games follow the change, though some need to be told the new size in their settings.

On keyboards with an AltGr key, Windows treats AltGr as Ctrl+Alt, so Ctrl+Alt hotkeys can block characters such as `@` on some layouts. If that happens, use an F key or Ctrl+Shift+Alt instead.

Color profiles, hotkeys and setups are saved in `%APPDATA%\RezTray\settings.json`. Resolutions are saved in the NVIDIA driver.

## Build

```bash
dotnet build -c Release
```

Output is `bin\Release\net48\RezTray.exe`. It needs nothing beyond Windows 10/11 and an NVIDIA driver.

`RezTray.exe --dump report.txt` writes what the app sees without changing anything.
