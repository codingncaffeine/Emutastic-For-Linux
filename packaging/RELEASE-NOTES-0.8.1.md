A polish release on top of 0.8.0: smoother pause effects in fullscreen, a
simpler RetroAchievements sign-in, faster box-art downloads for paid
ScreenScraper accounts, and a clearer in-game FPS readout.

## What's Fixed

- **Pause effects are smooth in fullscreen again.** The animated pause
  overlay rendered on the CPU at full window resolution every frame, which
  bogged down at fullscreen (especially 1440p/4K) and made the animation
  crawl in slow motion. It now composites on the GPU at a fixed internal
  resolution, so it runs at full speed at any window size.
- **RetroAchievements: signing in is the only step.** The separate "Enable
  RetroAchievements" toggle is gone — it was redundant with entering your
  credentials and easy to forget (signed in, but achievements silently off).
  Now achievements simply work whenever you're signed in. *(Heads-up: with
  Hardcore Mode on, RetroAchievements shows an "Unknown emulator" message at
  game start — this is expected. RA only approves an emulator for hardcore
  after it's been public for six months; until then, turn Hardcore Mode off
  if you'd rather not see it. See Preferences → Achievements.)*
- **Faster 2D box-art downloads on paid ScreenScraper accounts.** Cover-art
  fetching was running one image at a time regardless of your account tier.
  It now uses your account's allowed thread count (up to 6 for paid
  supporters), matching the metadata and 3D-art paths.

## Improvements

- **The in-game FPS readout now distinguishes display from emulation.** The
  number is the frames actually shown on screen; when the core runs faster
  than the screen can present, it appends `emu N` — telling you the
  bottleneck is presentation (GPU/compositor), not emulation.
- **System Files tab tidied** — dropped the dead MT-32 ROM and SoundFont
  drag-drop references (leftovers from a system that isn't supported).

## Install

Available as a tarball and `.deb` on the [releases page](https://github.com/codingncaffeine/Emutastic-For-Linux/releases),
or on Arch via the AUR: `yay -S emutastic-bin`. Existing installs update
in-app from Preferences → About.
