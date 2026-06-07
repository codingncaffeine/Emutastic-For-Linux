---
name: run-emutastic
description: Build, launch, and visually verify the Emutastic desktop app on this machine (EndeavourOS, KDE Wayland session, app UI on Xwayland)
---

# Running Emutastic

## Prerequisites (one-time, already done on this box)

- Local Avalonia 12.1.999 feed at `~/avalonia-build/Avalonia/artifacts/nuget`
  (built from Avalonia PR #20926 — see memory `avalonia-x11-dnd-gap` for rebuild steps).
  Restore fails with NU1301 if missing.
- `src/Emutastic/Secrets.cs` must exist — copy from `Secrets.cs.template` if not
  (gitignored; empty values build fine, cloud sync/ScreenScraper stay inert).

## Build

```sh
cd ~/Emutastic-For-Linux
dotnet build src/Emutastic.slnx -c Release
```

~13s warm. Also compiles three native libs into the output dir:
`libwlpresent.so`, `librcheevos.so`, `libchdr.so`. If `libwlpresent.so` is missing
from the output, the wayland/EGL headers weren't found (pacman: `wayland`, `libglvnd`, `mesa`).

## Launch

```sh
cd ~/Emutastic-For-Linux/src/Emutastic/bin/Release/net10.0
./Emutastic > /tmp/emutastic-stdout.log 2>&1 &
```

- Launch on the real session (DISPLAY=:1 / wayland-0) — no xvfb needed.
- Main window appears in ~1.6s. Verify via `~/.local/share/Emutastic/Logs/startup_timings.log`
  — look for `mark=main_window_shown` then `mark=deferred_startup_work_done`.
- Other logs: `Logs/ui_freezes.log`, `Logs/controller-diag.log`, `Logs/cloudsync.log`.
- stdout carries `Trace.WriteLine` diagnostics (e.g. `[VideoPlayback] ...`).

## Verify / drive

The main UI is **Avalonia.X11** → it's an X11 window on Xwayland, so X11 tools work.
This box has **no xdotool/wmctrl/kdotool**; ImageMagick `import` is the capture tool:

```sh
DISPLAY=:1 import -window "Emutastic" /tmp/emutastic-win.png
```

Read the PNG and look at it: expect dark-themed window, console sidebar
(Arcade/Atari/Nintendo/...), Library/Save States/Screenshots/Achievements tabs.
A blank or missing capture = launch failure.

Full-screen fallback (KDE Wayland): `spectacle -b -f -n -o /tmp/screen.png`
(captures everything, app may be occluded — prefer `import`).

## Known quirks

- `vlc-plugin-ffmpeg` missing → LibVLC falls back without `--avcodec-hw=none`
  (logged to stdout); h264 snap previews need that package installed.
- Game-window rendering (`libwlpresent.so`) is a separate own-toplevel Wayland
  surface — it does NOT show up under the X11 window capture; screenshot the
  screen via spectacle when verifying in-game video.

## Stop

```sh
pkill -x Emutastic
```
