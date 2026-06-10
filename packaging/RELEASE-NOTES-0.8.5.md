**PlayStation 2 arrives.** Import your PS2 games and play them, hardware-accelerated,
with adjustable internal resolution — plus a clearer BIOS setup flow and fixes across
import and controller input.

## What's New

- **PlayStation 2 is here (PCSX2).** Import `.iso`/`.chd`/`.bin`/`.m3u` and play.
  Rendered through the OpenGL hardware path, with **Internal Resolution** and
  **Texture Filtering** adjustable live from the in-game cog → Visuals. Box art and
  metadata scrape automatically, RetroAchievements identify and unlock, and the
  DualShock 2 is mapped out of the box. Grab the core from Preferences → Cores.
- **PS2 needs a BIOS.** PlayStation 2 now appears in Preferences → System Files with
  the common known-good dumps listed — drop a valid BIOS into the PS2 BIOS folder (or
  next to your ROMs) and it's detected automatically.

## What's Fixed

- **A missing BIOS now tells you so.** Launching a game that needs a BIOS you don't
  have shows a clear "BIOS required" dialog pointing you to System Files, instead of
  failing silently or with a cryptic core error. Applies to every system that needs a
  BIOS (PS2, PS1, Saturn, Sega CD, and more).
- **Controller input on more cores.** Some emulator cores read the whole controller in
  a single combined poll rather than button-by-button; those reads weren't being
  answered, so input didn't register at all. Now handled — affected cores respond to
  the pad correctly.
- **DAT downloads from redump.org work.** Redump serves its DAT databases zipped; they
  are now unwrapped on download, fixing a silent failure where the saved file couldn't
  be read for ROM identification.

## Improvements

- **Cleaner BIOS panel.** Preferences → System Files now groups BIOS files in a
  two-level layout (manufacturer → console → files), each with its own present/missing
  badge, so multi-console sections read clearly at a glance.
- **Download All for DAT files.** A single button fetches every reference DAT in turn,
  with per-system progress.

## Install

Tarball and `.deb` on the [releases page](https://github.com/codingncaffeine/Emutastic-For-Linux/releases),
or on Arch via the AUR: `yay -S emutastic-bin`. Existing installs update in-app
from Preferences → About.
