<p align="center"><img src="docs/logo.png" width="280" alt="LiteClip"></p>

<p align="center">A free, lightweight game clipper for Windows. Press a key and the last 30 seconds are saved, at full quality, in about a second.</p>

---

LiteClip records in the background using your graphics card's built-in encoder, so games barely lose any FPS. It only keeps a short rolling buffer, so nothing fills your drive. There's no account, no cloud, no ads: just one `.exe`.

## Download

Grab `LiteClip.exe` from [Releases](https://github.com/ItsSleepy/LiteClip/releases) and run it. Nothing to install.
The newest test build is always under [Nightly](https://github.com/ItsSleepy/LiteClip/releases/tag/nightly).

Needs Windows 10 (2004) or newer. Windows 11 is best: on Windows 10, Windows draws a yellow border around whatever is being captured.

## What it does

- **Clip key** (`F8`): saves the last 15s, 30s, 1m, 3m or 5m (you choose).
- **Long clip key** (`Shift+F8`): saves the last 3 minutes, for whole rounds.
- **Only while gaming**: starts when a game opens, stops when it closes, idle the rest of the time.
- **Just the game, or the whole screen**, with a choice of monitor.
- **Quality presets** from Low to Max (up to 4K) at 30, 60, 120 or 144 fps, in H.264, HEVC or AV1.
- **NVIDIA, AMD and Intel** encoders, picked automatically, with a CPU fallback.
- **Separate audio tracks**: game, mic, and the two mixed, so you can fix the mix later. Pick your mic, set volumes, watch live meters.
- **Library**: thumbnails grouped by day and game, search, favourites.
- **Lossless trim**: drag the handles, save a trimmed copy or trim the original.
- **Copy to clipboard** to paste straight into Discord, or make a **Discord-size copy** that fits the 10 MB limit.
- **Tray app** that can start with Windows. The window closes completely when you close it, so LiteClip idles at a few MB of memory while you play.

## How it works

```
game window ──Windows.Graphics.Capture──▶ GPU resize + colour convert ──▶ NVENC / AMF / Media Foundation
                                                                                   │
game sound (WASAPI loopback) ─┐                                                    ▼
                              ├─ named pipes, clock-aligned ───────────▶ 1-second MPEG-TS segments (rolling)
mic (WASAPI) ─────────────────┘                                                    │
                                                                    key press: stream-copy the last N seconds
                                                                                   ▼
                                                                         Downloads\LiteClip\*.mp4
```

- Video frames never leave the graphics card on the hardware paths.
- Audio is stamped with the same clock Windows stamps captured frames with, and each device's sample rate is trimmed by a few ppm to stop drift, so sound stays in sync over long sessions.
- Saving a clip is a stream copy (no re-encode), including the segment still being written, so the clip ends at the key press.
- LiteClip ships a slimmed-down ffmpeg (about 15 MB instead of 165 MB) with one small patch: `gfxcapture` keeps absolute capture timestamps. See [`ffmpeg/build.sh`](ffmpeg/build.sh).

## Building

Everything builds on GitHub Actions ([`.github/workflows/build.yml`](.github/workflows/build.yml)): ffmpeg in MSYS2, then `dotnet publish` into a single self-contained exe. Push a `v*` tag to make a release.

Locally (Windows, .NET 8 SDK): put `ffmpeg.exe` and `ffprobe.exe` from the CI `ffmpeg` artifact in `src/LiteClip/Embedded/`, then

```
dotnet publish src/LiteClip/LiteClip.csproj -c Release -o out
```

For quick UI work, drop a `wwwroot` folder and `ffmpeg.exe`/`ffprobe.exe` next to a built `LiteClip.exe`; it uses those instead of the built-in copies.

Files: settings in `%APPDATA%\LiteClip`, buffer/thumbnails/logs in `%LOCALAPPDATA%\LiteClip`, clips in `Downloads\LiteClip`.

## Licence

The bundled ffmpeg is LGPL 2.1+, built from FFmpeg sources with only the timestamp patch in `ffmpeg/build.sh`.
