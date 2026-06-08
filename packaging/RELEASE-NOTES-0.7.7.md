A big batch: controller rumble, in-app update notifications, a grouped
Favorites view, and an important stability fix for context menus.

## What's New

- **Update notifications** — the app now checks GitHub at startup and shows
  "Emutastic vX available — click to install" in the status bar. Clicking it
  downloads the update and restarts the app for you. (Disable by setting
  `CheckForUpdates` to false in the config file.)
- **Controller rumble** — games that use vibration now actually shake your
  controller, on all four player ports. This also lets the Dreamcast core
  initialize VMU and Purupuru peripherals properly.
- **Rebindable Disk Swap combo** — Preferences → Controls grows a FRONTEND
  section for disk-capable consoles (FDS, PS1, Saturn, Sega CD, Amiga). Click
  the box and press any two buttons — or two keys — to set your own disc-swap
  chord. L3 + Start remains the default.
- **Favorites, grouped** — the Favorites view now shows your games grouped
  under per-console headings like the Windows app, instead of one mixed grid.
- **List view polish** — alternating row shading, proper hover and selection
  colors, the two-layer "indented" rating stars, and column sorting that
  remembers your choice across restarts.
- **Live 2D/3D toggle** — the box-art style toggle now appears as soon as the
  first 3D box art lands during a download, not only after the whole console
  finishes.
- **Smarter status banner** — it's clickable now: click to install a pending
  app update, to jump to Preferences → Cores when core updates are announced,
  or to stop a running metadata refresh (it resumes where it left off next
  time you hit Refresh Library).
- **"Game reset" confirmation** — resetting from the in-game HUD now flashes a
  confirmation in the status line.

## What's Fixed

- **Context-menu actions could freeze the desktop** (reported on Arch — thank
  you!). Rating a game while the app was busy importing could block the UI
  thread while the open menu held the X11 input grab, locking up the entire
  desktop until a hard reboot. A full sweep followed the report: every menu,
  button, and dialog action across the app (library, game detail card, save
  states, achievements) now saves in the background, database busy-waits are
  capped, and the Save States tab no longer decodes every thumbnail on the UI
  thread — clicks stay instant no matter what's running.
- **Startup CPU burst tamed** — artwork pre-warming used to spawn one decode
  worker per console at launch (~dozens at once), which could starve the UI on
  busy systems. It's now a single background worker.

## Heads up for testers

Rumble and the disc-swap chord are new ports — if your controller doesn't
vibrate where it should (or vibrates where it shouldn't), please open an
issue with the core and game.
