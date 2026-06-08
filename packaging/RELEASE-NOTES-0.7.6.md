Polish, hardening, and project housekeeping.

## What's New

- **Controller status in the game window** — plugging in or unplugging a
  controller during gameplay now shows a named "Controller connected" /
  "Controller disconnected" message in the game window's status line,
  matching the message in the main app.
- **"Show in Files" for screenshots** — right-click any screenshot card to open
  your file manager with that file selected (Dolphin and Nautilus highlight it;
  other file managers open the containing folder).
- **Smarter status bar** — the app now tells you when save states created
  outside the app are discovered at startup, when core updates are available
  (with a pointer to Preferences → Cores), and when a controller connects or
  disconnects — by name.
- **Cores & Extras panel overhaul** — expanded sections are properly indented
  with guide lines so the Category → Console → Core hierarchy reads at a
  glance; sections remember whether you left them open; and downloading a core
  no longer collapses everything and throws the view somewhere else. The
  download buttons also moved clear of the scrollbar so you can't grab one
  while aiming for the other.
- **RetroAchievements hardcore hardening** — audited line-by-line against
  RA's official hardcore compliance requirements (the full writeup is on the
  project wiki). Renaming or deleting a save state now keeps its achievement
  progress side-car with it, and an rcheevos-requested reset now resets the
  game itself, not just the achievement runtime.

## What's Fixed

- **Controls mapping dead for controllers connected after startup** — if you
  started the app without a controller and plugged one in later, the Controls
  panel listed it but button capture never responded until an app restart.
  The same root cause also swallowed controller hot-plug status messages
  while a game was running.
- **Achievement toast text rendering as boxes** — the toast font picker
  offered every installed font, including symbol fonts with no actual letters.
  The picker now only lists fonts that can render text, and a bad font saved
  in your config falls back to the default instead of drawing tofu.

## Project

- **License published**: Emutastic for Linux is GPL-3.0, same as the Windows
  app, with the LICENSE now in the repository and in every release artifact.
- **The wiki is live** — 30 pages covering per-console notes, features,
  emulation timing, logs, hardcore compliance, privacy policy, and a guide to
  running the tarball on Arch, Fedora, openSUSE and other non-Debian distros:
  https://github.com/codingncaffeine/Emutastic-For-Linux/wiki
- **Store-distribution groundwork** — AppStream metadata ships in the .deb,
  and an AUR package definition is staged in the repository.
