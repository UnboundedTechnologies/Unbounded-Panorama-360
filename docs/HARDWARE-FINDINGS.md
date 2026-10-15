# Hardware findings: Panorama `391a:1011`

Verified on a retail Panorama (firmware `v2.0.6.20260713`, app `v2.0.4.20260804`) on Windows 11, 2026-10-15, with the cooler mounted in portrait. Each result was confirmed by device readback and by a photo of the screen.

## Transport

- Windows binds the cooler to the in-box `usbprint.sys`; it registers `GUID_DEVINTERFACE_USBPRINT` (`{28d78fad-5a12-11d1-ae5b-0000f803a8c2}`). Overlapped `ReadFile`/`WriteFile` on that interface works; no driver replacement is needed.
- `RandomAccess` in .NET refuses device handles ("Stream does not support seeking"); use overlapped I/O through a `ThreadPoolBoundHandle`.
- Responses arrive in 1–100 ms. A read is kept pending at all times.
- KANALI holds the device while it runs; close it first.

## Device answers

| Query | Result |
|---|---|
| SystemConfiguration | product `PANO`; decoder, field 11 and output all 2240×1080. **No `usb_auto_keepalive` flag** and no board summary are sent, so the host always pings. |
| DeviceAuthentication (key 1) | `auth = "ok"` |
| MediaCatalog | Entries carry the full path (`/userdata/user/<name>`, presets under `/userdata/default/`) plus the extension separately. |
| MetricBatch | Answered with an `Acknowledgement` (600). |
| Ping | Answered with `Pong`. |

## Overlay (text layer drawn by the firmware)

- **Arbitrary placement works.** 11 groups at arbitrary x/y rendered exactly where placed (KANALI uses at most 3).
- **Fonts:** `roboto-regular`, `roboto-bold`, `roboto-medium`, `roboto-light` all exist and render distinctly. An unknown font is rejected with `ProtocolError FAILURE "missing font label skipped: group_id=… label_id=… font=…"`, which can be used to discover fonts.
- **Backgrounds:** `BACKGROUND_NONE` gives clean text over video; `BACKGROUND_GRADIENT_HORIZONTAL` gives a gradient badge pill.
- **Live values:** a MetricBatch updating one label every second (a clock) renders smoothly.

## Orientation

| `ui_rotation` | Text canvas | Seen from a portrait-mounted cooler |
|---|---|---|
| 0 | 2240 × 1080 landscape | Sideways |
| 90 | 1080 × 2240 portrait | Upside down |
| **270** | **1080 × 2240 portrait** | **Upright** |

`ui_rotation=270` is not used by KANALI or the reference project, but the firmware accepts it and renders it correctly.

**Video:** with `ui_rotation=270`, a portrait 1080×2240 source rotated with FFmpeg `transpose=cclock` into 2240×1080 plays upright. `transpose=clock` plays upside down. The far end of the portrait canvas (high y) wraps onto the curved front face.

## Upload

`TransferBegin` → `TransferChunk` (256 KiB, stop-and-wait) → `TransferEnd {file_type "media", checksum 0}`, with header version 0 and one shared track id, is accepted. A 350 KB file uploaded in about 0.1–0.2 s and appeared in the catalog with the exact size. `FileRemoval` deletes it.

Media format that plays: raw Annex-B H.264 (`-f h264`), libx264 `veryfast` CRF 23, High profile, yuv420p, 30 fps, 2240×1080.

## Restore

A protobuf parse and re-serialize of the device's `UserConfiguration` is byte-identical (unknown fields are preserved), and writing the saved original back gives an identical readback.
