# Emutastic for Linux

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A native **Linux** port of [Emutastic](https://github.com/codingncaffeine/Emutastic) — a multi-system
emulator frontend inspired by [OpenEmu](https://openemu.org/), rebuilt on **.NET 10 + Avalonia** (the
original is Windows/WPF/.NET 8). Games are organized by console in a clean library interface. Emulation
is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

The goal is a **1:1 clone**: aesthetically and functionally identical to the Windows app, with only the
platform plumbing swapped underneath (WPF → Avalonia, Direct3D/Vulkan → OpenGL/Vulkan, WASAPI → SDL3,
XInput → SDL3 gamepad, Win32 core loading → `dlopen`).

> **Status:** active development — feature parity with upstream is in progress. The emulation core
> (run a ROM with video/audio/input), the ROM library + import pipeline, and the main-window UI are
> in place; remaining subsystems (hardware rendering, achievements, recording, packaging) are landing
> incrementally.

> **Legal notice:** This project is a frontend only. It does not include, distribute, or facilitate the
> acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files.
> You are solely responsible for ensuring you have the legal right to use any software you load.

---

## Requirements

- A modern 64-bit Linux desktop (developed on **Debian 13 / KDE Plasma**, X11 or Wayland)
- Runtime libraries (most desktops already have these; the `.deb` declares them as dependencies):
  `libsdl3-0` (audio + controllers), `libvulkan1` + Mesa drivers (hardware rendering),
  `libvlc` (in-app video), `ffmpeg` (recording), plus the usual `libx11-6`/`libice6`/`libsm6`/`libfontconfig1`
- libretro core `.so` files (downloadable in-app — Preferences → Cores)
- Optional: DAT files for ROM identification (Preferences → Cores / Extras)

The published `.deb` bundles the .NET 10 runtime (self-contained), so no separate .NET install is needed.

---

## Supported Systems

<details>
<summary><strong>33 systems across 11 manufacturers</strong> (click to expand)</summary>

| System | Tag | Core (priority order) | BIOS |
|---|---|---|---|
| NES | NES | nestopia → quicknes → fceumm | No |
| Famicom Disk System | FDS | nestopia | `disksys.rom` |
| SNES | SNES | snes9x → bsnes | No |
| Nintendo 64 | N64 | parallel_n64 → mupen64plus_next | No |
| Game Boy | GB | mgba → gambatte → sameboy | No |
| Game Boy Color | GBC | mgba → gambatte → sameboy | No |
| Game Boy Advance | GBA | mgba | Optional |
| Nintendo 3DS | 3DS | azahar | No |
| Nintendo DS | NDS | desmume → melonds | No |
| Virtual Boy | VirtualBoy | mednafen_vb | No |
| Genesis / Mega Drive | Genesis | genesis_plus_gx → picodrive | No |
| Sega CD / Mega CD | SegaCD | genesis_plus_gx | Region BIOS |
| Sega 32X | Sega32X | picodrive | No |
| Sega Saturn | Saturn | mednafen_saturn → kronos → yabause | Region BIOS |
| Master System | SMS | genesis_plus_gx → picodrive | No |
| Game Gear | GameGear | genesis_plus_gx | No |
| SG-1000 | SG1000 | genesis_plus_gx | No |
| PlayStation | PS1 | mednafen_psx_hw → mednafen_psx | Region BIOS |
| PSP | PSP | ppsspp | No |
| TurboGrafx-16 | TG16 | mednafen_pce → mednafen_pce_fast | No |
| TurboGrafx-CD | TGCD | mednafen_pce → mednafen_pce_fast | `syscard3.pce` |
| Neo Geo Pocket | NGP | mednafen_ngp | No |
| Neo Geo Pocket Color | NGPC | mednafen_ngp | No |
| Neo Geo | NeoGeo | geolith | `neogeo.zip` + `aes.zip` |
| Neo Geo CD | NeoCD | geolith | `neogeo.zip` + `aes.zip` + `neocdz.zip` |
| Arcade | Arcade | fbneo + mame2003-plus | No |
| Atari 2600 | Atari2600 | stella | No |
| Atari 7800 | Atari7800 | prosystem | No |
| Atari Jaguar | Jaguar | virtualjaguar | No |
| ColecoVision | ColecoVision | gearcoleco → bluemsx | No |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | `panafz10.bin` |
| Philips CD-i | CDi | same_cdi | No |

</details>

Cores are downloaded from the **Linux** libretro build servers (`buildbot.libretro.com/nightly/linux/x86_64`)
on demand — same core lineup as upstream, as `.so` instead of `.dll`.

---

## BIOS Files

Place BIOS files in `~/.local/share/Emutastic/System/` (or `PortableData/System/` next to the executable
in portable mode). The app also checks each system's ROM folder.

<details>
<summary><strong>BIOS file details by system</strong></summary>

**Sega CD** — `bios_CD_U.bin` (USA), `bios_CD_E.bin` (Europe), `bios_CD_J.bin` (Japan)

**Sega Saturn** — Beetle Saturn: `sega_101.bin` (JP v1.00), `mpr-17933.bin` (JP v1.01),
`mpr-17941.bin` (USA/EU v1.01).

**PlayStation** — USA: `scph5501.bin`, `scph1001.bin`, `scph7001.bin`. Europe: `scph5502.bin`. Japan: `scph5500.bin`

**TurboGrafx-CD** — Any of: `syscard3.pce`, `syscard2.pce`, `syscard1.pce`

**3DO** — Any of: `panafz10.bin`, `panafz1j.bin`, `goldstar.bin`

**Famicom Disk System** — `disksys.rom`

</details>

---

## ROM Import

Drag and drop ROMs onto the library or use **Import ROMs**. The app detects the console from file
extension, cleans the title, and hashes the ROM. For ambiguous formats (`.chd`, `.iso`, `.cue`, `.bin`),
a SHA1 lookup against DAT files is attempted first — if no match, a console picker is shown. `.zip` is
handled by the .NET BCL; `.7z`/`.rar`/`.tar`/`.gz` via SharpCompress (no native dependency).

**Multi-disc games** are auto-bundled into a single library entry via an `.m3u` playlist.

---

## Features

Themes (Dark / Light / OLED / Midnight + a visual editor) · automatic artwork & metadata (OpenVGDB +
libretro thumbnails, optional ScreenScraper) · **SDL3** controller support with analog-stick-as-D-pad ·
RetroAchievements · GitHub cloud sync of saves + library · disk swapping (L3 + Start) · per-game notes ·
game manuals · cheats · ROM-hack patching (IPS/BPS/UPS) · core options · play-time tracking.

(See the upstream [Emutastic wiki](https://github.com/codingncaffeine/Emutastic/wiki) for per-feature
detail — behavior is intended to match.)

---

## Folder Layout

Follows the XDG Base Directory spec:

```
~/.config/Emutastic/             config.json
~/.local/share/Emutastic/        (or your custom data folder)
    library.db
    DATs/                        (No-Intro / Redump DATs — downloadable in-app)
    Cores/                       (libretro core .so files — downloadable in-app)
    System/                      (BIOS files)
    Saves/ / Screenshots/ / Recordings/ / Artwork/ / Themes/ / ...
~/.cache/Emutastic/              (transient caches)
```

### Installing & updating
Three release artifacts per version (built by `packaging/build-release.sh`):
- `emutastic_<ver>_amd64.deb` — system install (`/usr/lib/emutastic`, `emutastic` on PATH,
  desktop entry). **Portable mode is not available on a .deb install** (the install dir is
  root-owned); data lives in `~/.local/share/Emutastic`.
- `Emutastic-<ver>-linux-x64.tar.gz` — self-contained; extract anywhere writable and run
  `./Emutastic`. Data in `~/.local/share/Emutastic` unless you opt into portable mode.
- `Emutastic-<ver>-linux-x64-portable.tar.gz` — same, with `portable.txt` pre-dropped:
  fully self-contained out of the box.

**In-app updates** (Preferences → About): the app checks the latest GitHub release and,
when newer, offers **Update Now** — tarball installs self-replace and relaunch (your
`portable.txt` and `PortableData/` are untouched); .deb installs download the package and
install it via a system authorization prompt (`pkexec dpkg -i`), then relaunch.
Development builds (run from `bin/Release`) update via `git pull` instead.

### Portable mode

Drop an empty `portable.txt` next to the executable **or** launch with `--portable`, and **everything**
lives in `PortableData/` beside the executable — config, library, saves, screenshots, recordings,
artwork, BIOS, libretro cores, and imported ROMs. Move the folder to a USB stick and run it on any
Linux PC; paths are stored relative to `PortableData/` so the install travels intact.

---

## Building

Requires the **.NET 10 SDK** and **Avalonia 12**.

The Wayland game window is presented through a small native shim (`native/wlpresent/`, an own
`xdg_toplevel` + EGL/GL presenter — the path that hits a clean windowed 60 fps). The build compiles it
automatically (an MSBuild target invokes `native/wlpresent/build.sh` and copies `libwlpresent.so` beside
the app), so building from source needs the C toolchain + Wayland/OpenGL **development** packages:

```sh
sudo apt install build-essential pkg-config libwayland-dev libegl-dev libgl-dev
```

```sh
git clone git@github.com:codingncaffeine/Emutastic-For-Linux.git
cd Emutastic-For-Linux
dotnet build src/Emutastic.slnx -c Release
```

> These `-dev` packages are **only needed to build from source** — they ship the headers the shim is
> compiled against. End users running a packaged release (`.deb`/AppImage/Flatpak) don't need them: the
> compiled `libwlpresent.so` is bundled in the package. If the dev packages are missing the managed build
> still succeeds, but the native game window won't be produced.

---

## Credits

**Emulation** is handled by libretro cores maintained by their upstream authors — Emutastic bundles none
of them; the in-app core manager downloads them from the libretro build servers on demand. The lineup is
unchanged from upstream (Nestopia, snes9x, mGBA, Genesis Plus GX, Mednafen/Beetle, Dolphin, PPSSPP,
Flycast, FBNeo, MAME 2003-Plus, and more) — see the upstream
[Emutastic credits](https://github.com/codingncaffeine/Emutastic#credits) for the full per-core author
list. Please support those projects directly.

**Frameworks & libraries** (the Linux port swaps several of the Windows ones):

| Library | Purpose | License |
|---|---|---|
| [Avalonia](https://avaloniaui.net/) | Cross-platform UI (replaces WPF) | MIT |
| [SDL3](https://www.libsdl.org/) | Audio output + controllers (replaces NAudio/WASAPI + XInput) | Zlib |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | `.7z`/`.rar`/`.tar`/`.gz` import (replaces SevenZipExtractor) | MIT |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | Library database | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |
| [rcheevos](https://github.com/RetroAchievements/rcheevos) | RetroAchievements client | MIT |
| [libchdr](https://github.com/rtissera/libchdr) | CHD format reader | BSD 3-Clause |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) | In-app video playback | LGPL-2.1 |
| [librashader](https://github.com/SnowflakePowered/librashader) | slang shader presets (optional) | MPL-2.0 / MIT |

Controller illustrations from [OpenEmuControllerArt](https://github.com/kodi-game/OpenEmuControllerArt)
(BSD 3-Clause; not affiliated with OpenEmu). Bezels from [The Bezel Project](https://github.com/thebezelproject).
Inspired by [OpenEmu](https://openemu.org/) for macOS. Full license texts in `NOTICES.txt`.

This is a community Linux port of [Emutastic](https://github.com/codingncaffeine/Emutastic) by the same author.

---

## License

[GNU General Public License v3.0](LICENSE)
