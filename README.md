<p align="center">
  <img src="docs/images/banner.svg" alt="Unbounded Panorama 360: anything, anywhere, on your TRYX Panorama" width="100%">
</p>

<p align="center">
  <a href="https://github.com/UnboundedTechnologies/Unbounded-Panorama-360/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/UnboundedTechnologies/Unbounded-Panorama-360?label=download&color=00E5FF&style=for-the-badge"></a>
  <img alt="Open source" src="https://img.shields.io/badge/open%20source-free%20forever-8B5CF6?style=for-the-badge">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-3DDC97?style=for-the-badge"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-4F8CFF?style=for-the-badge&logo=windows11&logoColor=white">
  <a href="docs/CONTAINER.md"><img alt="Proxmox and Linux" src="https://img.shields.io/badge/Proxmox%20%7C%20Linux-container-E57000?style=for-the-badge&logo=proxmox&logoColor=white"></a>
</p>

<h3 align="center">Your cooler has a gorgeous screen. Use all of it.</h3>

<p align="center">
  <b>Unbounded Panorama 360</b> is the free, open source studio for cooler screens:<br>
  the curved AMOLED of <b>TRYX Panorama</b> coolers on Windows, and <b>Thermalright</b> screens on a home server.<br>
  Put live temperatures, clocks, your own text, photos and videos <b>anywhere</b> on the screen,<br>
  and control every RGB device in the machine from the same place.
</p>

<p align="center">
  <a href="https://github.com/UnboundedTechnologies/Unbounded-Panorama-360/releases/latest"><b>⬇ Download the installer</b></a>
  &nbsp;·&nbsp; <a href="#container-edition">Run it on a server</a>
  &nbsp;·&nbsp; <a href="#everything-it-does">See what it does</a>
  &nbsp;·&nbsp; <a href="#open-source">It is open source</a>
</p>

---

## Why you will love it

The official KANALI app gives you **three metrics in fixed slots, horizontally only**. Mount your cooler vertically and everything reads sideways. The screen can do far more than that, and now it does:

| | KANALI | **Unbounded Panorama 360** |
|---|:---:|:---:|
| Where values can go | 3 fixed slots | **Anywhere, up to 24 elements** |
| Vertical (portrait) cooler | Horizontal only | **Upright, as mounted** |

And on top of that: control of every RGB device in your PC, a different photo or video on the top and on the curved front, your own text with badges in any colour, YouTube import in the best available quality, free drawing, presets, a live video preview and a media library. **All free and open source.**

Cooler in a home server instead? The **container edition** runs on Proxmox (or any Linux), drives **Thermalright screens**, and gives you the studio **in your browser**, with live statistics for the whole server.

<p align="center">
  <img src="docs/images/features.svg" alt="Anything anywhere, two media at once, upright as mounted" width="100%">
</p>

## Everything it does

### 🎛️ A real design studio
- **Drag anything anywhere**: CPU and GPU temperature, load, clocks, power, fans, RAM, drives, network, plus clock, date and your own text.
- **Smart guides** like your favourite design tool: magnetic snapping, alignment, equal spacing, centring, multi-select, undo and redo.
- **Style it your way**: four font weights, any size, a full colour picker with quick and recent colours, gradient badges.
- **Free drawing** with pen, lines, rectangles and ellipses, right on the screen.
- **Presets**: save your designs, switch between them in one click, start from ready-made templates.

### 🖼️ Photos and videos, done right
- **Two media at once**: a video on the flat top face and a photo on the curved front, or one image across the whole screen.
- **Drop it where it goes**: drag a file over the window and the screen lights up its top, middle or bottom, like snapping a window in Windows.
- **Exact placement**: move and zoom the background with the mouse, snap it to the edges or the centre, or type exact pixels.
- **YouTube import**: paste a link, pick the moment and the length, and it downloads in the best quality YouTube offers.
- **See it move**: the editor plays your video exactly as the cooler will, and the library previews every clip.

### 🌈 All your RGB in one place
- **Every RGB device of every major brand** in one ARGB tab: motherboard and its ARGB headers, RAM, graphics card, fans, keyboards and more, detected through the free OpenRGB service.
- **Sync everything to one look** in one click, or give each device and zone its own effect, colour, speed and brightness.
- **Animated effects everywhere**: Rainbow, Spectrum Cycle, Breathing and Flashing also run on devices without built-in effects (such as Wooting keyboards), drawn live by the app.
- **Unplug and replug freely**: a keyboard or any device plugged back in gets your lighting again within seconds.
- **Checked, not assumed**: after start-up, sleep or a device change, the app reads every device back to make sure it really shows your settings, and repairs OpenRGB by itself when one does not. The page never shows "synced" for a device that is not.
- **Every effect really applies**: Static, Off, Breathing and the rest reach the device even where OpenRGB 1.0 used to ignore them silently.
- **Fan hubs and LED strips on ARGB headers**: set how many LEDs are connected, name the header, and they join in.
- **Profiles** to switch looks, restored automatically at start-up and after sleep, and saved into devices that have memory for it.
- **Replaces Armoury Crate's lighting** with one click (the rest of Armoury Crate stays).

### ⚡ Fast and invisible
- **Opens in about a second**, with no UAC prompt, and can start with Windows straight into the tray.
- **Prepares your design in the background** while you edit, so pressing Apply usually only uploads.
- **Uses your NVIDIA GPU** to convert video when you have one, at higher quality and about twice as fast.
- **Live values update every second** on the cooler, without re-sending your design.
- **Cleans up after itself**: old conversions are deleted automatically, only what is useful is kept.

### 🖥️ On your home server
- **The studio in any browser**, on a computer, tablet or phone: a **live preview** of exactly what the screen shows, updated as you drag.
- **Text and live values anywhere**, badges, any colour (black included), alignment, four font weights, and **free drawing** right on the screen.
- **Pictures and videos as background**: drop a file on the screen, move it and zoom with the mouse, pick the part of a video to loop.
- **YouTube import with a real section picker**: paste a link and the video appears on a timeline. Drag the highlighted section to move it, drag its edges to change the start or the end, or use one-click lengths (5, 10, 15, 30, 60 s) and ±1 s nudges, with a clear readout such as `1:00 → 1:10 · 10 s of 10:35 · ≈ 20 MB`.
- **See the loop before you download it**: the first and last frames of your section, cropped square as on the screen, and **▶ Preview the loop**, which plays exactly that section over and over. Then import it in the best available quality, to the library or **straight onto the screen**, with progress, size, speed and time left.
- **Exact loops**: the screen plays precisely the section you chose, frame for frame, never a second early or late.
- **A library** of everything you added, with videos that play their own loop when you point at them.
- **Server statistics** for your widgets: CPU, NVMe and RAM temperatures, plus host CPU, memory, load, uptime, network speed, storage and **every VM and container** through a read-only Proxmox token.
- **ARGB for the server** too: motherboard headers, fan hubs, **RGB RAM** (through the motherboard's SMBus) and every USB RGB device OpenRGB supports, with sync, profiles and the automatic check and repair.
- **Runs by itself**: starts with the server, restores the design and the lighting, and uses about 6 % of one CPU core.
- **Light on memory**: about 190 MB for the app, so a 1 GB container is plenty, even while you scrub through a YouTube video (only the frames you are looking at are fetched; the ones you moved past are cancelled).
- **Optional password** for the page.

## Get it

### 💻 On Windows (TRYX Panorama)

1. **Download** `UnboundedPanorama360-Setup-<version>.exe` from the [latest release](https://github.com/UnboundedTechnologies/Unbounded-Panorama-360/releases/latest).
2. **Close KANALI** (only one program can talk to the cooler at a time).
3. **Run the setup.** It installs everything it needs, only if missing: FFmpeg for video, yt-dlp and Deno for YouTube, the PawnIO driver for CPU temperature, clocks and power, and OpenRGB for the ARGB tab. .NET is included.

Windows SmartScreen may say the publisher is unknown, because the installer is not code-signed. Click **More info**, then **Run anyway**.

Updating is just as easy: run a newer setup and it updates in place, keeping your designs, presets and library. Uninstall any time from Windows Settings, Apps.

<a id="container-edition"></a>

### 🖥️ On a home server: the container edition

For **Thermalright screens** (Frozen Warframe Pro, GrandVision, Mjolnir Vision: USB `87ad:70db`) in a Proxmox or Linux machine.

1. **Create a small Debian 13 container** (unprivileged, 2 cores, 1 GB of memory) and pass the screen and your USB RGB controller through. A tiny host service keeps the passthrough right after every reboot.
2. **Install inside the container**:
   ```sh
   curl -fsSLO https://raw.githubusercontent.com/UnboundedTechnologies/Unbounded-Panorama-360/main/deploy/container/install.sh
   sh install.sh
   ```
   It installs FFmpeg, yt-dlp and Deno, OpenRGB 1.1 and the app, with services that start at boot.
3. **Open** `http://<container address>:8360` from any browser on your network.

The [container guide](docs/CONTAINER.md) has every command, including the optional read-only Proxmox token for server statistics. Running `install.sh` again updates everything and keeps your designs, library and lighting.

## Open source

Unbounded Panorama 360 is **free and open source** under the [MIT license](LICENSE). Every line of code is right here: read it, build it, change it, share it.

- **No account, no telemetry, no ads.** The app talks to your cooler over USB and to nothing else (except YouTube and winget when you ask it to, and on a server, the Proxmox API if you give it a read-only token).
- **Contributions are welcome.** Found a bug, have an idea, own a Panorama SE or WB? Open an [issue](https://github.com/UnboundedTechnologies/Unbounded-Panorama-360/issues) or a pull request.
- **Tested**: 83 automated tests cover layout, snapping, video conversion, the two-media composition, lighting and the container edition's screen protocol.

### Build it yourself

You need Windows and the .NET 10 SDK.

```powershell
winget install Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
dotnet build -c Release              # the app
dotnet test -c Release               # the tests (FFmpeg needed for the video ones)
.\build-installer.ps1                # the setup, in dist\

# the container edition (any OS with the .NET 10 SDK)
dotnet publish src/Unbounded.Panorama.Server -c Release -r linux-x64 --self-contained -o dist/server
```

The code is shared: `src/Unbounded.Panorama.Core` builds for both Windows and Linux (media, YouTube, layout, lighting), the Windows app is `src/Unbounded.Panorama.App` and the container edition `src/Unbounded.Panorama.Server`.

A read-only hardware probe is included for curious minds (close KANALI first):

```powershell
dotnet run -c Release --project tools/Unbounded.Panorama.Probe -- .\probe-output
```

## Under the hood

The cooler appears to Windows as a USB printer-class device bound to the in-box `usbprint.sys` driver. The app opens its `GUID_DEVINTERFACE_USBPRINT` interface with overlapped `ReadFile`/`WriteFile`: **no driver replacement, no Zadig.** Messages are `"TRYX"` + a little-endian `uint32` length + a protobuf message (see `src/Unbounded.Panorama.Core/Protocol/panorama.proto`).

Your design becomes two things: an **overlay** of text groups the cooler's firmware draws itself (so live values are cheap to update) and **one H.264 video** for the background, which FFmpeg composes from your photos, videos and drawing.

RGB goes through the OpenRGB service on `127.0.0.1:6742`. The app works around three OpenRGB 1.0 problems, all confirmed on hardware:
- **The same RAM listed twice.** AMD's SMBus (PIIX4) has several ports, and OpenRGB can list the same sticks on two of them (on an X870 board: 4 "sticks" for 2; a colour sent only to the second port lit both real ones). Writing both paths at once collides on the bus and leaves the RAM dark, which looked like settings flashing and never applying. The app keeps one path per stick: the same chip at the same address, with the same firmware and LED count, on another port of the same controller is the same stick.
- **Modes without a direction** (Static, Off, Breathing…) are reported with direction `0xFFFFFFFF` but only accepted with `0`, and a request with the reported value is dropped without an error. The app sends mode changes itself with the value OpenRGB accepts.
- **A device detected again while OpenRGB runs** (unplugged and plugged back in, or after a rescan) gets a new internal ID that OpenRGB then checks as a list position, so every command for it is dropped while it stays listed (fixed in OpenRGB 1.1). The app listens for OpenRGB's device-list notice, reads every device back after re-applying, and restarts the service when a device ignored its settings: again after each replug, otherwise at most once every 10 minutes and 3 times an hour. Each step is written to `lighting.log`.

The app needs administrator rights because CPU sensors are read through a kernel driver (like HWiNFO). It still never shows a UAC prompt once installed: setup registers two scheduled tasks with highest privileges, one the shortcuts use and one for "Start with Windows".

### The container edition

Thermalright screens do not draw anything themselves: they show whatever picture they receive, and go back to their logo after 2 to 3 seconds without one. The server therefore composes every frame (the background's current video frame, the text and values, the drawing) with SkiaSharp and streams it at 24 frames per second through its own libusb driver: raw bulk transfers on endpoint `0x01`, a 64-byte handshake (`12 34 56 78`, command 1) whose reply names the panel, then frames made of a 64-byte header (magic, width, height, command 2, pixel count) and RGB565 pixels, high byte first. A video background is first rendered once as a small clip of exactly the visible part, then looped in real time.

A YouTube section can only be downloaded from the keyframe before its start (cutting anywhere else would mean re-encoding the original), and it keeps YouTube's timestamps. The library therefore records where the chosen section starts inside the file and how long it is, and every player uses exactly that: the clip for the screen (a 10 s section is exactly 240 frames at 24 fps, starting on the chosen frame), the loop preview, which the server cuts to start at zero, and the library's hover previews.

Choosing a section asks YouTube for frames and previews, and each request runs yt-dlp, Deno and FFmpeg (about 130 MB together). Only the newest request per picture is kept: the page aborts the one it replaces and the server kills that download, with at most two running at once (measured during a 12-move drag and a 22 s preview: 397 MB at the peak in a 1 GB container). The tools keep their temporary files on disk rather than in `/tmp`, which is in memory in a container, and the server uses .NET's workstation garbage collector (one compact heap instead of one per core). Frames, previews and imports use direct (https) streams, because a section cut out of an HLS stream comes out empty.

Inside a container, live values come from the host's `hwmon` sensors, and whole-server figures from the Proxmox API with a token that only has the read-only PVEAuditor role. OpenRGB runs as a service in the container, listening only on `127.0.0.1`.

| USB ID | Cooler | App | Status |
|---|---|---|---|
| `391a:1011` | TRYX Panorama | Windows | Verified on hardware |
| `391a:1021` | TRYX Panorama SE | Windows | Same protocol family, untested |
| `391a:1031` | TRYX Panorama WB | Windows | Same protocol family, untested |
| `87ad:70db` | Thermalright Frozen Warframe Pro (320×320) | Container | Verified on hardware |
| `87ad:70db` | Thermalright GrandVision, Mjolnir Vision (480×480) | Container | Same protocol, untested |

## Credits

TRYX protocol knowledge comes from the clean-room research in [DXVSI/Tryx-Linux-GUI](https://github.com/DXVSI/Tryx-Linux-GUI) (MIT). The Thermalright handshake is documented in the protocol notes of [thermalright-trcc-linux](https://github.com/Lexonight1/thermalright-trcc-linux); the frame format was read from what the device does on the wire, and the driver here is written from scratch (no code is shared). RGB devices are driven through [OpenRGB](https://openrgb.org) (GPL-2.0, installed separately as its own service, not bundled) using [OpenRGB.NET](https://github.com/diogotr7/OpenRGB.NET) (MIT). Frames in the container edition are drawn with [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT). Unbounded Panorama 360 is an independent implementation and is not affiliated with TRYX, KANALI or Thermalright.

## License

[MIT](LICENSE) © 2026 Unbounded Technologies. Free to use, modify and share.

<p align="center"><img src="docs/images/logo.svg" alt="Unbounded Panorama 360 logo" width="72"></p>
