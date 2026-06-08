A focused N64 release: correct speed, clean audio, and working internal-resolution
controls.

## What's Fixed

- **Nintendo 64 no longer runs too fast.** A pacing bug let some N64 games run
  ~20% above full speed (≈72fps instead of 60). The loop now never paces a game
  faster than its content rate.
- **Clean N64 audio out of the box.** The default N64 core is now
  **Mupen64Plus-Next**, which produces correct-rate audio on Linux's SDL3 audio
  path — the previous default (Parallel N64) under-produced audio at correct
  speed, causing a rough/garbled sound. Parallel N64 is still available in
  Preferences → Cores if you prefer it.
- **N64 internal resolution actually changes now.** The in-game Visuals menu
  drives the resolution that the renderer outputs (sharper N64, default 960×720),
  and adjusting it takes effect. The setting is applied when the game starts, so
  change it and relaunch the game.

## Improvements

- **In-game core-option changes now persist.** Tweaks you make in the cog →
  Visuals menu are saved per-core and survive a restart (previously they were
  lost when you closed the game). This is what makes "restart to apply" options
  like N64 resolution work at all.
- **In-game menu fits its box.** Long option values (resolutions, texture-filter
  names) no longer spill past the edge of the cog menu, and a missing-glyph box
  no longer appears on labels.

## Install

Tarball and `.deb` on the [releases page](https://github.com/codingncaffeine/Emutastic-For-Linux/releases),
or on Arch via the AUR: `yay -S emutastic-bin`. Existing installs update in-app
from Preferences → About.
