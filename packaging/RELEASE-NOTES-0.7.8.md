A focused release: full in-game UI on X11 sessions, plus library polish.

## What's New

- **Play touch-based DS games entirely on a controller.** The right analog
  stick now moves an on-screen crosshair and taps with the right trigger —
  so games that require the touchscreen are fully playable from the couch.
  A bindable **Touch** button also appears under Preferences → Controls →
  Nintendo DS ("Touch Screen" section). Mouse taps keep working as before.
- **Full in-game experience on X11** — if your desktop runs X11 (XFCE, MATE,
  Cinnamon on Xorg, etc.), the game window previously showed just the game:
  no status bar, no hover controls, no settings menu. X11 now gets the
  complete in-game UI — the status line, the Power/Pause/Reset/Save/Record
  pill, the full cog menu, achievement toasts and indicators, pause effects —
  plus **bezels and Vectrex screen overlays**, and games now render at the
  correct display aspect ratio on X11 too. (Your window manager keeps its own
  title bar; shader presets and full-resolution screenshots remain
  Wayland-only for now.)
- **Sharper power button** in the in-game controls — the new art is crisp at
  any size, where the old one rendered soft on large or high-scale displays.

## What's Fixed

- **The "ghost card" is gone** — after closing a game, the card you launched
  could linger over the next library you opened, following you across views
  until enough clicking dislodged it. Root-caused to a view-virtualization
  quirk (a keyboard-navigation bookmark pinned the old card's container) and
  fixed at the source — verified gone.
- **Far less console noise** — a binding in the library card template
  produced tens of thousands of harmless-but-noisy errors per session while
  scrolling and switching views; it now resolves without the noise.
- **Controller bindings that can't be resolved are now reported** in
  `controller-diag.log` instead of being silently ignored, making "this
  button does nothing" problems diagnosable.
- **Library hygiene on view switches** — selection and focus are released
  when you switch libraries, so a card from the previous view can't hold
  state into the next one.

