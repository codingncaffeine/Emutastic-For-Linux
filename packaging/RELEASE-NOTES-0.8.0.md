The big one: **GameCube and Dreamcast arrive**, and the emulation loop got a
deep tune-up — smoother pacing, cleaner audio, and high internal resolutions
that no longer cost you frames. Every change below was verified with
benchmarking on real games.

## What's New

- **GameCube is here (Dolphin).** Import `.rvz`/`.iso`/`.gcm` and play.
  Tuned out of the box — dual-core emulation and fast memory access are on by
  default (the old conservative settings were a Windows-era caution that
  doesn't apply on Linux), measured taking Mario Kart: Double Dash from
  ~45fps to a locked 60. First-ever launch of a game warms a shader cache
  (brief dips); after that it boots clean. Internal resolution, anisotropic
  filtering, and anti-aliasing are adjustable live from the in-game cog.
- **Dreamcast is here (Flycast).** `.chd`/`.gdi`/`.cdi` all load, VMU saves
  work out of the box, fast GD-ROM loading is on by default, and
  RetroAchievements identify and unlock — including `.chd` dumps. Internal
  resolution and texture upscaling adjustable from Core Options.
- **DS Visuals menu** — Internal Resolution and xBrz Texture Scaling now sit
  in the in-game cog for Nintendo DS, mirroring the 3DS layout. Resolution
  choices are capped where CPU rendering stays playable.
- **DS performance defaults** — the DS core now uses its JIT recompiler and
  a multi-threaded renderer by default (previously interpreter +
  single-threaded). Measured: Mario Kart DS at 4x internal resolution went
  from ~48fps to a locked 60. Your own Core Options choices still win.

## Smoother and Faster

- **The multi-second freezes at screens and transitions are gone.** A
  long-standing pacing bug made the emulator sit idle for seconds at scene
  changes (Dreamcast menus were the worst case — bursts of frames, then
  3–4 second gaps with crackling audio). Root-caused and fixed; boot
  sequences and menus now flow at full speed. This also removed most of
  GameCube's launch stall.
- **Games that run at 30fps internally now pace perfectly.** The loop now
  follows the game's own clock (the audio it actually produces) instead of
  forcing the console's nominal rate — 30fps titles settle at a steady 30
  with clean audio, 60fps titles at 60, automatically.
- **High internal resolutions are now (almost) free.** The frame-transfer
  path used to move the full-size rendered image every frame — at 3DS 10x
  that's ~43MB a frame, and it capped games in the 40s. The transfer is now
  bounded by your window size no matter the internal resolution: 3DS at 10x
  runs a locked 60. GameCube, PSP, and N64 high-res benefit the same way.
- **3DS resolution settings now apply correctly at launch.** Saving a high
  internal resolution used to clip the picture to a corner sliver on the
  next boot (a core init quirk); the setting is now applied a frame after
  boot instead, which renders correctly at any factor.

## What's Fixed

- **Video snap previews survive Arch-style VLC packaging.** On distros that
  split VLC into per-plugin packages, a missing codec plugin used to silently
  kill every video preview; the app now recovers and logs which package to
  install (`vlc-plugin-ffmpeg` on Arch). The wiki's Other Distributions page
  has the details.
- **RetroAchievements "unrecognized dump" messages are console-aware** —
  disc systems suggest Redump dumps, cartridges suggest No-Intro, arcade
  explains ROM-set matching, instead of one generic hint.

## For Tinkerers

- `EMUTASTIC_FPS_LOG=1` logs a per-second fps + frame-cost line to
  `emulator-host.log` — the same readout used to verify everything above.
- `EMUTASTIC_FULLRES_READBACK=1` restores full-internal-resolution frame
  transfer (for maximum-quality recording at a frame-rate cost).
