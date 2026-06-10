![Emutastic](src/Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic for Linux

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A native **Linux** port of [Emutastic](https://github.com/codingncaffeine/Emutastic) — a multi-system
emulator frontend inspired by [OpenEmu](https://openemu.org/), rebuilt on **.NET 10 + Avalonia** (the
original is Windows/WPF/.NET 8). Games are organized by console in a clean library interface. Emulation
is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

The goal is a **1:1 clone**: aesthetically and functionally identical to the Windows app, with only the
platform plumbing swapped underneath (WPF → Avalonia, Direct3D/Vulkan → OpenGL/Vulkan, WASAPI → SDL3,
XInput → SDL3 gamepad, Win32 core loading → `dlopen`).

**[Visit emutastic.com →](https://www.emutastic.com/emutasticapp.html)** for a visual tour of the app, or grab the
[latest release](https://github.com/codingncaffeine/Emutastic-For-Linux/releases) directly.

For current status and per-release changes, see the
[releases page](https://github.com/codingncaffeine/Emutastic-For-Linux/releases).

> **Legal notice:** This project is a frontend only. It does not include, distribute, or facilitate the
> acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files.
> You are solely responsible for ensuring you have the legal right to use any software you load.

---

## Requirements

- A modern 64-bit Linux desktop (developed on **Debian 13 / KDE Plasma**, X11 or Wayland)
- Runtime libraries (most desktops already have these; the `.deb` declares them):
  `libsdl3-0` (audio + controllers), `libegl1`/`libgl1` + Mesa drivers (game rendering),
  `ffmpeg` (recording encodes), `libx11-6` + `libfontconfig1` (UI), ICU (`libicu7x`).
  Recommended: `libvlc5` + `vlc-plugin-base` (video snap previews). Optional: `libvulkan1`
  (only the legacy in-process presenter uses it; the default OpenGL path doesn't)
- libretro core `.so` files (downloadable in-app — Preferences → Cores)
- Optional: DAT files for ROM identification (Preferences → Cores / Extras)

The published `.deb` bundles the .NET 10 runtime (self-contained), so no separate .NET install is needed.

---

## Supported Systems

<details>
<summary><strong>36 systems across 11 manufacturers</strong> (click to expand)</summary>

| System | Tag | Core (priority order) | BIOS |
|---|---|---|---|
| NES | NES | nestopia → quicknes → fceumm | No |
| Famicom Disk System | FDS | nestopia | `disksys.rom` |
| SNES | SNES | snes9x → bsnes | No |
| Nintendo 64 | N64 | mupen64plus_next → parallel_n64 | No |
| GameCube | GameCube | dolphin | No |
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
| Dreamcast | Dreamcast | flycast | No |
| PlayStation | PS1 | mednafen_psx_hw → mednafen_psx | Region BIOS |
| PlayStation 2 | PS2 | pcsx2 | Required |
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

**PlayStation 2** — any valid 4 MB dump in the `pcsx2/bios/` subfolder (e.g. `ps2-0230a-20080220.bin` USA, `ps2-0230e-20080220.bin` Europe, `ps2-0230j-20080220.bin` Japan)

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
**RetroAchievements** (unlock toasts with a full appearance editor, hardcore mode, Achievements tab
with trophy case + activity heatmap, friends with unlock feeds + leaderboard toasts, CHD support) ·
screenshots & **gameplay recording** (x264) · **GitHub cloud sync** (below) · disc swapping
(L3 + Start) · per-game notes · game manuals (auto-download) · cheats (+ cheat database import) ·
core options · save states with screenshots · play-time tracking · **ROM hacks**
(IPS/BPS/UPS soft-patching — original ROM untouched) · **video shaders** (7 built-ins + the
downloadable libretro GLSL pack) · **bezels & Vectrex overlays** · **turbo buttons** ·
**in-app updates**.

(Shader note: the Windows app runs the libretro *slang* pack through librashader; librashader
ships no Linux binaries, so this port runs the libretro *GLSL* pack — the same shader library —
through a built-in OpenGL preset chain.)

(See the upstream [Emutastic wiki](https://github.com/codingncaffeine/Emutastic/wiki) for per-feature
detail — behavior is intended to match. Diagnostic logs live in the `Logs/` folder of the data
directory and match the wiki's [Log Files](https://github.com/codingncaffeine/Emutastic/wiki/Log-Files)
guide, with one delta: `controller-diag.log` is in `Logs/` too, not next to the executable.)

---

## Cloud Sync

Sign in with your GitHub account (**Preferences → Backups** — device flow, no password stored) and
your battery saves + game library sync through a private repository on your account.

<details>
<summary><strong>How it works</strong> (click to expand)</summary>

Saves pull automatically before a game launches and upload when the session ends (configurable:
on game close / every 15 minutes / manual), or sync everything on demand with **Sync Now**.

- **Cross-platform** — the same repository serves the Windows app and this port: save on one
  machine, pick up on the other. Battery saves are keyed by ROM hash, so both installs must import
  the same ROM files.
- **Shared or per-PC** — by default every PC shares one `emutastic-saves` repository, so your saves
  and library follow you between machines. Toggle *"Make this PC unique"* and that machine backs up
  to its own `emutastic-saves-<hostname>` repository instead — other machines never read or write it.
- **Optional encryption** — AES-256-GCM with a passphrase you choose; the same passphrase is
  required on every PC that shares the repository.
- The library database syncs last-writer-wins. If you run established libraries on two machines,
  take a backup (**Back Up Now**) before your first sync on each.

Sync activity is logged to `Logs/cloudsync.log`.

</details>

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
    Saves/ (SRAM) / Save States/ / Screenshots/ / Recordings/ / Artwork/ / Manuals/ / Cheats/ / Themes/ / Logs/ / ...
```

### Installing & updating
Two release artifacts per version (built by `packaging/build-release.sh`), each bundling
a quick-start `README.txt`:
- `emutastic_<ver>_amd64.deb` — system install (`/usr/lib/emutastic`, `emutastic` on PATH,
  desktop entry). **Portable mode is not available on a .deb install** (the install dir is
  root-owned); data lives in `~/.local/share/Emutastic`.
- `Emutastic-<ver>-linux-x64.tar.gz` — self-contained; extract anywhere writable and run
  `./Emutastic`. For a fully portable setup, `touch portable.txt` beside the executable
  (details in the bundled README).

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

Three native libraries build automatically from vendored sources via MSBuild targets:
`libwlpresent.so` (the Wayland game window — an own `xdg_toplevel` + EGL/GL presenter, the path
that hits a clean windowed 60 fps), `librcheevos.so` (RetroAchievements, pinned v11.6.0 with an
ABI-check harness), and `libchdr.so` (CHD disc images, built with CMake). Building from source
therefore needs the C toolchain + CMake + Wayland/OpenGL **development** packages:

```sh
sudo apt install build-essential pkg-config cmake libwayland-dev libegl-dev libgl-dev libx11-dev libpng-dev
```

```sh
git clone git@github.com:codingncaffeine/Emutastic-For-Linux.git
cd Emutastic-For-Linux
dotnet build src/Emutastic.slnx -c Release
```

> These `-dev` packages are **only needed to build from source** — they ship the headers the shim is
> compiled against. End users running a packaged release (`.deb` or tarball) don't need them: the
> compiled `libwlpresent.so` is bundled in the package. If the dev packages are missing the managed build
> still succeeds, but the native game window won't be produced.

---

## Credits

**Emulation** is handled by libretro cores maintained by their upstream authors — Emutastic bundles none
of them; the in-app core manager downloads them from the libretro build servers on demand. The lineup is
unchanged from upstream (Nestopia, snes9x, mGBA, Genesis Plus GX, Mednafen/Beetle, Dolphin, PPSSPP,
PCSX2 (LRPS2), Flycast, FBNeo, MAME 2003-Plus, and more) — see the upstream
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
| [libretro shaders](https://github.com/libretro/glsl-shaders) | downloadable GLSL shader presets (run by a built-in GL chain) | per-shader (see repo) |

Controller illustrations from [OpenEmuControllerArt](https://github.com/kodi-game/OpenEmuControllerArt)
(BSD 3-Clause; not affiliated with OpenEmu). Bezels from [The Bezel Project](https://github.com/thebezelproject).
Inspired by [OpenEmu](https://openemu.org/) for macOS. Full license texts in `NOTICES.txt`.

This is a community Linux port of [Emutastic](https://github.com/codingncaffeine/Emutastic) by the same author.

---

## License

[GNU General Public License v3.0](LICENSE)
