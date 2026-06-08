Library quality-of-life and a round of "it was wired up wrong" fixes.

## What's New

- **Selection that you can see** — selected games now glow: a theme-colored
  ring around the box art (the Theme tab's selection color drives it) with a
  gently breathing halo and a ✓ badge per selected card. Shift-click selects a
  range, Ctrl-click toggles, and the **Delete key** removes the selection
  (with confirmation) — in the Screenshots tab too.
- **DS Screen Layout** — switch between Top/Bottom, Side by Side, single-screen
  and Hybrid views from the in-game cog menu, live. The display now follows
  mid-game shape changes correctly (this also benefits any core that resizes
  its output while running).
- **Achievement challenge indicators** — when a "do it without dying"-style
  achievement arms, its badge appears bottom-right until you finish (or blow
  it); measured achievements show a progress tracker ("47/100") top-right as
  you advance. A first for Emutastic — the Windows app doesn't have these yet.
- **Achievement toasts now play their notification sound** in-game (toggle and
  cooldown in Preferences → Achievements → Friends settings).

## What's Fixed

- **Recently Played was always empty** — play count, last-played and total
  play time were never recorded. Sessions now count (history starts fresh
  from this release).
- **Library refresh now refreshes metadata too** — developer/publisher/genre/
  description backfill for existing games, including ones whose earlier
  fetch came up empty.
- **The friend profile window** — couldn't be closed, outlived the app,
  Compare-tab filters and the Leaderboards game picker did nothing, the
  notification bell never turned gold: all fixed (the bell rings on hover
  now, like it should).
- **Desktop no longer bleeds through the game window** in translucent overlay
  areas (you could faintly see windows behind the game).
- **Long achievement toast messages wrap** instead of running off the toast.
- **Save States tab refreshes itself** when a session's saves land while
  you're looking at it.
- Removed a stale "coming soon" flash on the Achievements tab and the dead
  "Customize…" button on the Theme panel (theme editing lives in the panels
  themselves).
