First public release of the Linux port — a native Avalonia frontend with the
RetroArch-style game host, ported feature-for-feature from the Windows app.

## Highlights
- **Game library** — import, box art (ScreenScraper + libretro thumbnails),
  collections, favorites, ratings, search, video snap previews
- **Play** — libretro cores with downloadable core manager, save states with
  screenshots, quick save/load, disc swapping, per-game cheats (+ cheat database),
  core options, per-console controls
- **RetroAchievements** — full integration: unlocks with customizable in-game
  toasts, hardcore mode, the Achievements tab (profile, trophy case, library
  spotlight, activity heatmap), friends with unlock feeds and leaderboard toasts,
  CHD identification, achievement progress in save states
- **Capture** — screenshots and gameplay recording (software x264 encode)
- **Extras** — game manuals (auto-download), per-game notes, dark theme with
  pause effects, smooth 60fps presentation (audio-master clock + dynamic rate control)

## Install
| Artifact | For |
|---|---|
| `emutastic_0.5.0_amd64.deb` | Debian/Ubuntu system install (`emutastic` on PATH + desktop entry) |
| `Emutastic-0.5.0-linux-x64.tar.gz` | Self-contained — extract anywhere and run `./Emutastic` |

**Portable setup:** extract the tarball, `touch portable.txt` beside the executable, launch —
everything then lives in `PortableData/` next to the app. Full details in the bundled `README.txt`.

Needs: `ffmpeg` and SDL3 (`libsdl3-0`); `libvlc` recommended for video snaps.
The .deb declares all of it.

**In-app updates start with this release** — Preferences → About → Update Now
handles future versions automatically (portable installs self-update in place;
.deb installs update through the system authorization prompt).

## Known notes
- Hardcore unlocks count as softcore on RA's servers until the emulator passes
  RetroAchievements' approval window.
- 3D cores (N64/PSP) require a working GPU driver stack; GameCube/Dreamcast
  are in progress.
