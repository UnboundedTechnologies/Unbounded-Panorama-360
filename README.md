# Unbounded Panorama 360

A Windows app for TRYX Panorama coolers (curved 2240 × 1080 AMOLED). Put anything anywhere on the screen, in landscape or portrait: live hardware values (CPU/GPU temperature, load, clocks), clock, custom text, images and video.

> Status: early development. The device link works on real hardware (Panorama `391a:1011`); the editor app is in progress.

## Why

KANALI, the official app, places at most three metrics in fixed slots and only works horizontally. The display firmware itself accepts text groups at arbitrary positions with free text, size, colour and background. Unbounded Panorama 360 exposes that.

## How it talks to the cooler

The cooler enumerates as a USB printer-class device and Windows binds it to the in-box `usbprint.sys` driver. The app opens the `GUID_DEVINTERFACE_USBPRINT` interface and uses overlapped `ReadFile`/`WriteFile`. **No driver replacement, no Zadig.** Close KANALI first; only one program can own the device.

Frames are `"TRYX"` + little-endian `uint32` length + a protobuf message (`src/Unbounded.Panorama.Core/Protocol/panorama.proto`).

| USB ID | Model | Status |
|---|---|---|
| `391a:1011` | Panorama | Verified on hardware (read-only probe) |
| `391a:1021` | Panorama SE | Same protocol family, untested here |
| `391a:1031` | Panorama WB | Same protocol family, untested here |

## Install

Download `UnboundedPanorama360-Setup-<version>.exe` from the [latest release](https://github.com/UnboundedTechnologies/Unbounded-Panorama-360/releases/latest) and run it. Windows SmartScreen may warn that the publisher is unknown because the setup is not code-signed: choose More info, then Run anyway. It installs to `Program Files\Unbounded Panorama 360` (.NET is included), asks whether to start with Windows, and installs what the app needs through winget only when it is missing: FFmpeg (video conversion), yt-dlp and Deno (YouTube import) and the PawnIO driver (CPU temperature, clocks and power). Running a newer setup closes the running app and updates it in place. Uninstall from Windows Settings, Apps.

The app needs administrator rights because CPU sensors are read through a kernel driver (like HWiNFO), but it never shows a UAC prompt once installed: setup registers two Task Scheduler tasks with highest privileges, one the shortcuts use to open the app and one for "Start with Windows". Only the setup itself asks for administrator rights.

To build the setup yourself (needs the .NET 10 SDK and Inno Setup 6):

```powershell
winget install Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
.\build-installer.ps1        # writes dist\UnboundedPanorama360-Setup-<version>.exe; -Run starts it
```

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
# Read-only hardware probe (close KANALI first):
dotnet run -c Release --project tools/Unbounded.Panorama.Probe -- .\probe-output
```

## Credits

Protocol knowledge comes from the clean-room research in [DXVSI/Tryx-Linux-GUI](https://github.com/DXVSI/Tryx-Linux-GUI) (MIT). This project is an independent Windows implementation and is not affiliated with TRYX or KANALI.

## License

MIT. See [LICENSE](LICENSE).
