The in-game menu grows up: shaders, bezels, turbo, and real screenshots.

## What's New

- **Video shaders** — the in-game cog now has a Shader picker:
  - Seven built-in presets, always available: CRT Scanlines, Game Boy (DMG),
    Game Boy (DMG LCD), Game Boy Pocket, LCD Grid, and Smooth.
  - The full libretro community shader pack (CRT, NTSC, handheld LCD, scalers
    and more) is one click away: Preferences → Extras → Video Shaders →
    Download. Picks apply instantly and are remembered per game.
- **Arcade & Neo Geo bezels** — per-game bezel frames from The Bezel Project.
  Enable in Preferences → Extras (each game's bezel downloads on first launch
  and caches), or grab the whole set for offline use. Toggle per game from the
  cog.
- **Vectrex overlays** — the classic color screen overlays, downloadable in
  Preferences → Extras and matched to your games automatically. On by default
  when present; toggle per game from the cog.
- **Turbo buttons** — per-game, per-player autofire from the cog menu. Pick a
  button, hold it, and it fires at the classic ~10 presses/sec. Settings stick
  with each game.
- **Flip Display** — rotate the picture 180° from the cog (for that one
  arcade cabinet setup).
- **In-game screenshots** — F12 (or PrintScreen, or your own key from
  Preferences → Media) captures exactly what's on screen — at displayed
  resolution, shaders and bezel included — straight into the Screenshots tab.
- **Game windows remember their size** — resize a game's window once and that
  game reopens at that size next time, independent of the main app and every
  other game.

## What's Fixed

- **Game video could hard-freeze the whole desktop** on some setups while
  browsing the library — video previews now use software decoding, which
  sidesteps a GPU driver hang entirely.
- **F5 quick save, F7 quick load, and F9 record hotkeys were dead** in-game
  (the whole function row never reached the game window). All revived, along
  with the new F12 screenshot key.
- **Friend profile window couldn't be closed** — the ✕ did nothing, and the
  window even outlived the app. Both fixed; the ✕ is properly centered now
  too.
- **The friend notification bell** now actually turns gold (and rings!) on
  hover, and shows its on/off state correctly — on both the profile window
  and the friends list popup.
- **Turbo menu cleanup** — long button lists no longer overflow the menu
  (they paginate), and cores that expose their own built-in "Turbo A/B"
  buttons no longer show confusing duplicates.
- **Bezels size correctly** — the game keeps its own aspect ratio and sits
  inside the bezel's window, at any window size.
- **ROM hacks** — soft-patching (IPS/BPS/UPS) is wired end to end.

## Notes

- The shader pack and bezel/overlay downloads live in **Preferences → Extras**.
- A small number of pack shaders use features not yet supported (LUT textures,
  feedback passes) — those fail safely back to the unshaded picture.
- Shaders apply to software-rendered cores (2D systems); 3D cores render
  through their own enhanced pipeline, same as the Windows app.
