#!/bin/sh
# Unbounded Panorama 360, container edition: installs or updates everything inside a Debian 13 LXC.
# Run as root inside the container:
#   sh install.sh [path/to/unbounded-panorama-linux-x64.tar.gz]
# Without an argument it downloads the latest release from GitHub.
set -eu
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

REPO="UnboundedTechnologies/Unbounded-Panorama-360"
APP_DIR=/opt/unbounded-panorama
DATA_DIR=/var/lib/unbounded-panorama
CONFIG_DIR=/etc/unbounded-panorama
OPENRGB_DIR=/opt/openrgb
OPENRGB_VERSION=1.1
OPENRGB_URL="https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_x86_64_8e16de2.AppImage"
OPENRGB_SHA256=d556c2273b0ef863dbee7ffdb438304b382add5d685c93c60758b42de3b0268d

say() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }

[ "$(id -u)" = 0 ] || { echo "Run as root."; exit 1; }
. /etc/os-release
[ "${ID:-}" = debian ] || echo "Warning: written for Debian 13; this is ${PRETTY_NAME:-unknown}."

say "System packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl unzip ffmpeg libusb-1.0-0 libicu76 \
    libopengl0 libgl1 libegl1 libfontconfig1 libxkbcommon0 libdbus-1-3 libharfbuzz0b >/dev/null

say "yt-dlp (latest, for YouTube imports)"
curl -fsSL -o /usr/local/bin/yt-dlp.new https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux
chmod 755 /usr/local/bin/yt-dlp.new && mv /usr/local/bin/yt-dlp.new /usr/local/bin/yt-dlp
yt-dlp --version

say "Deno (yt-dlp needs a JavaScript runtime for YouTube)"
tmp=$(mktemp -d)
curl -fsSL -o "$tmp/deno.zip" https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip
unzip -qo "$tmp/deno.zip" -d "$tmp" && install -m 755 "$tmp/deno" /usr/local/bin/deno
rm -rf "$tmp"
deno --version | head -1

if [ ! -x "$OPENRGB_DIR/squashfs-root/AppRun" ] || [ "$(cat "$OPENRGB_DIR/VERSION" 2>/dev/null)" != "$OPENRGB_VERSION" ]; then
    say "OpenRGB $OPENRGB_VERSION (RGB devices)"
    mkdir -p "$OPENRGB_DIR"
    curl -fsSL -o "$OPENRGB_DIR/OpenRGB.AppImage" "$OPENRGB_URL"
    echo "$OPENRGB_SHA256  $OPENRGB_DIR/OpenRGB.AppImage" | sha256sum -c -
    chmod 755 "$OPENRGB_DIR/OpenRGB.AppImage"
    # A container cannot mount AppImages (no FUSE): unpack it instead.
    rm -rf "$OPENRGB_DIR/squashfs-root"
    (cd "$OPENRGB_DIR" && ./OpenRGB.AppImage --appimage-extract >/dev/null)
    rm -f "$OPENRGB_DIR/OpenRGB.AppImage"
    echo "$OPENRGB_VERSION" > "$OPENRGB_DIR/VERSION"
fi
cat > /etc/systemd/system/openrgb.service <<EOF
[Unit]
Description=OpenRGB SDK server
After=network.target

[Service]
Environment=QT_QPA_PLATFORM=offscreen
ExecStart=$OPENRGB_DIR/squashfs-root/AppRun --server --server-host 127.0.0.1 --server-port 6742 --noautoconnect
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

say "Unbounded Panorama 360"
package=${1:-}
if [ -z "$package" ]; then
    package=$(mktemp --suffix=.tar.gz)
    url=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep -o '"browser_download_url": *"[^"]*linux-x64[^"]*\.tar\.gz"' | head -1 | cut -d'"' -f4)
    [ -n "$url" ] || { echo "No Linux package in the latest release."; exit 1; }
    curl -fsSL -o "$package" "$url"
fi
systemctl stop unbounded-panorama 2>/dev/null || true
rm -rf "$APP_DIR.new" && mkdir -p "$APP_DIR.new"
tar --no-same-owner -xzf "$package" -C "$APP_DIR.new"
rm -rf "$APP_DIR" && mv "$APP_DIR.new" "$APP_DIR"
chmod 755 "$APP_DIR/unbounded-panorama"
mkdir -p "$DATA_DIR" "$CONFIG_DIR"
chmod 700 "$CONFIG_DIR"

cat > /etc/systemd/system/unbounded-panorama.service <<EOF
[Unit]
Description=Unbounded Panorama 360 (cooler screen and RGB, web page on port 8360)
After=network-online.target openrgb.service
Wants=network-online.target openrgb.service

[Service]
Type=notify
ExecStart=$APP_DIR/unbounded-panorama
WorkingDirectory=$APP_DIR
Environment=UPN_DATA=$DATA_DIR
Environment=UPN_CONFIG=$CONFIG_DIR
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now openrgb.service >/dev/null 2>&1
systemctl enable --now unbounded-panorama.service >/dev/null 2>&1
sleep 3
systemctl is-active --quiet unbounded-panorama && say "Running. Open http://$(hostname -I | awk '{print $1}'):8360" \
    || { echo "The service did not start:"; journalctl -u unbounded-panorama -n 30 --no-pager; exit 1; }
