================================================================================
  EMUTASTIC FOR LINUX
  A multi-system libretro emulator frontend — Linux port (.NET 10 + Avalonia)
================================================================================

A native Linux port of Emutastic, inspired by OpenEmu. Organize your games by
console in a clean library; emulation is handled by libretro cores loaded at
runtime. No cores, ROMs, or BIOS files are bundled.

LEGAL: This is a frontend only. It does not include or facilitate acquiring any
copyrighted ROMs, BIOS files, or system software. You are responsible for the
legal right to use anything you load.

--------------------------------------------------------------------------------
REQUIREMENTS
--------------------------------------------------------------------------------
  * A modern 64-bit Linux desktop (developed on Debian 13 / KDE Plasma).
  * Runtime libs (the .deb declares them as dependencies; most desktops have
    them already): libsdl3-0, libvulkan1 + Mesa drivers, libvlc, ffmpeg,
    libx11-6, libice6, libsm6, libfontconfig1.
  * libretro core .so files — download them in-app: Preferences -> Cores.
  * Optional: DAT files for ROM identification (Preferences -> Cores / Extras).

  The .deb bundles the .NET 10 runtime (self-contained) — no separate install.

--------------------------------------------------------------------------------
FIRST RUN
--------------------------------------------------------------------------------
  1. Open Preferences -> Cores and download the cores for the systems you want.
  2. Drag-and-drop ROMs onto the library (or use Import ROMs). The console is
     detected automatically; ambiguous disc images are matched against DAT
     files (download those first, in Preferences -> Cores / Extras).
  3. Double-click a game to play. Press Escape (or the in-game menu) to exit.

--------------------------------------------------------------------------------
BIOS FILES
--------------------------------------------------------------------------------
  Place BIOS files in:  ~/.local/share/Emutastic/System/
  (or  PortableData/System/  next to the executable in portable mode).
  The app also checks each system's ROM folder.

  Common ones: Sega CD (bios_CD_U/E/J.bin), PlayStation (scph5501/5502/5500.bin),
  Saturn (sega_101.bin / mpr-17933 / mpr-17941), FDS (disksys.rom),
  TurboGrafx-CD (syscard3.pce), 3DO (panafz10.bin).

--------------------------------------------------------------------------------
CONTROLLERS
--------------------------------------------------------------------------------
  Plug in a controller — Xbox, DualSense/DualShock, and most others are
  detected via SDL3. The left analog stick also acts as the D-pad on classic
  systems. A keyboard fallback is available for player 1 (arrows + Z/X/A/S,
  Enter = Start, Right-Shift = Select).

--------------------------------------------------------------------------------
FEATURES
--------------------------------------------------------------------------------
  Themes (Dark / Light / OLED / Midnight + editor) · automatic artwork &
  metadata (OpenVGDB + libretro thumbnails, optional ScreenScraper) ·
  RetroAchievements · GitHub cloud sync of saves + library · disk swapping
  (L3 + Start) · per-game notes · game manuals · cheats · ROM-hack patching
  (IPS/BPS/UPS) · per-core options · play-time tracking.

--------------------------------------------------------------------------------
WHERE THINGS LIVE (XDG layout)
--------------------------------------------------------------------------------
  ~/.config/Emutastic/             config.json
  ~/.local/share/Emutastic/        library.db, Cores/, System/, DATs/, Saves/,
                                   Screenshots/, Recordings/, Artwork/, Themes/
  ~/.cache/Emutastic/              transient caches

  PORTABLE MODE: drop an empty "portable.txt" next to the executable (or launch
  with --portable) and EVERYTHING lives in PortableData/ beside the executable
  — config, library, saves, cores, BIOS, ROMs — so it travels on a USB stick.

--------------------------------------------------------------------------------
  This is a community Linux port of Emutastic (github.com/codingncaffeine/Emutastic).
  Licensed under the GNU General Public License v3.0. See NOTICES.txt for the
  licenses of bundled/used libraries.
================================================================================
