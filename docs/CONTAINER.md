# Container edition (Proxmox / Linux)

Run Unbounded Panorama 360 on a home server: a small Debian container drives the cooler screen and every
RGB device, and you design everything from a web page on any computer or phone.

It is the same app as on Windows, rebuilt for a server:

- **Studio** in the browser: live preview of exactly what the screen shows, drag text and values, badges,
  colours (black included), drawing, pictures and videos as background (drop a file on the screen).
- **Library** with uploads and **YouTube import** in the best available quality (yt-dlp + Deno).
- **Live values**: CPU, NVMe and RAM temperatures from the host, and, with a read-only Proxmox token,
  server CPU, memory, load, uptime, network speed, storage and every guest's CPU, memory and network.
- **ARGB**: every device OpenRGB detects, sync, effects, ARGB header LED counts, profiles, and the same
  automatic check and repair as the Windows app.
- **Starts with the server** and comes back by itself after a reboot.

## Supported screens

| USB ID | Screens | Status |
|---|---|---|
| `87ad:70db` | Thermalright / ChiZhu bulk screens: Frozen Warframe Pro (320×320), GrandVision, Mjolnir Vision (480×480) | Frozen Warframe Pro verified on hardware |

The TRYX Panorama (`391a:1011`) is driven by the Windows app.

## 1. Create the container

A Debian 13 container, unprivileged, 2 cores, 1 GB of memory and 8 GB of disk is plenty (the app uses
about 6 % of one core and about 190 MB of memory, and stays well under 1 GB while you pick YouTube sections). On the Proxmox host:

```sh
pveam download local debian-13-standard_13.1-2_amd64.tar.zst
pct create 116 local:vztmpl/debian-13-standard_13.1-2_amd64.tar.zst --hostname rgb-display \
  --unprivileged 1 --features nesting=1,keyctl=1 --cores 2 --memory 1024 --swap 512 \
  --rootfs local-zfs:8 --net0 name=eth0,bridge=vmbr0,ip=dhcp --onboot 1 --timezone host
```

`--timezone host` keeps the clock on the screen in your time zone.

## 2. Pass the devices through

Find your devices with `lsusb` on the host (the screen, and the motherboard's RGB controller if it is a
USB device, for example `1462:7d98` for MSI Mystic Light). Their numbers can change between boots, so a
small host service updates the container before the guests start:

```sh
cp deploy/proxmox/upn360-devices /usr/local/sbin/ && chmod 755 /usr/local/sbin/upn360-devices
cp deploy/proxmox/upn360-devices.service /etc/systemd/system/
printf 'CT=116\nUSB="87ad:70db"\nHID="1462:7d98"\n' > /etc/default/upn360-devices
systemctl daemon-reload && systemctl enable --now upn360-devices
pct start 116
```

Motherboard RGB on an SMBus (most ASUS boards, RAM sticks) cannot be reached from a container; USB
controllers (MSI Mystic Light, fan hubs, keyboards) can.

## 3. Optional: server statistics

A read-only token lets widgets show the whole server, not just the container:

```sh
pveum user add upn360@pve --comment "Unbounded Panorama 360 statistics"
pveum acl modify / --users upn360@pve --roles PVEAuditor
pveum user token add upn360@pve display --privsep 0
```

Put what the last command prints into the container, in `/etc/unbounded-panorama/proxmox.env`
(readable by root only):

```sh
PVE_API_URL=https://<host address>:8006/api2/json
PVE_TOKEN_ID=upn360@pve!display
PVE_TOKEN_SECRET=<the value>
```

PVEAuditor can only read: the token cannot start, stop or change anything.

## 4. Install

Inside the container (`pct enter 116`):

```sh
curl -fsSLO https://raw.githubusercontent.com/UnboundedTechnologies/Unbounded-Panorama-360/main/deploy/container/install.sh
sh install.sh
```

It installs FFmpeg, the latest yt-dlp and Deno, OpenRGB 1.1 and the app, with services that start at boot,
then prints the address of the page: `http://<container address>:8360`. Running it again updates
everything and keeps your designs, library and lighting.

Anyone on your network can open the page until you set a password in **Settings**.

## Files

| Path | What |
|---|---|
| `/opt/unbounded-panorama` | The app |
| `/var/lib/unbounded-panorama` | Your design, settings, drawing, library, lighting and profiles |
| `/etc/unbounded-panorama/proxmox.env` | The optional Proxmox token |
| `journalctl -u unbounded-panorama` | The log |
