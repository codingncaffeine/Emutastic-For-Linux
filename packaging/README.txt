================================================================================
 Emutastic for Linux — Quick Start Guide
================================================================================

REQUIREMENTS
------------
.deb install: dependencies are handled automatically by your package manager.

Tarball: you need these system libraries (most desktops already have them):
  SDL3 (libsdl3-0)        audio + controllers
  Mesa OpenGL/EGL         game rendering
  Wayland (libwayland)    game window presentation
  libpng (libpng16)       shader / overlay textures
  ffmpeg                  gameplay recording
  VLC (libvlc5)           optional — video snap previews in the library

On Debian/Ubuntu:  sudo apt install libsdl3-0 libwayland-client0 libwayland-egl1 \
                     libpng16-16 ffmpeg libvlc5 vlc-plugin-base

No .NET installation is needed — the runtime is bundled.


GETTING STARTED
---------------
1. .deb install: run "emutastic" (or find Emutastic in your app menu).
   Tarball: extract anywhere writable and run ./Emutastic

2. Open Preferences (gear icon) and go to Cores / Extras:
   - Download the cores for the systems you want to play
   - Download DAT files — these are important! Without them, disc images
     and some cartridge ROMs may be assigned to the wrong system or
     require manual selection during import. Grab all of them.
   - "Update All" updates installed cores to the latest libretro
     nightlies; run it occasionally.

3. If any system requires a BIOS (Sega CD, Saturn, PlayStation,
   PlayStation 2, etc.), go to Preferences → System Files to see what's
   needed and where to place the files. Some systems read their BIOS from
   a subfolder — System Files shows the exact location for each.

4. Drag and drop ROM, disc image, or zip files onto the library window
   to import your games, or use the Import ROMs button in the navigation
   bar below Preferences. Zips are auto-extracted into the data folder;
   the original archive is left untouched.

5. (Optional) Set up artwork and accounts:
   - Preferences -> Snaps: No account is needed to get started --
     Emutastic identifies games against OpenVGDB, a built-in local
     database, and downloads box art from the libretro thumbnail
     server (only the lookup is offline; the art itself is still
     pulled over the internet). Sign in to ScreenScraper to make it
     the primary source instead: richer, region-aware metadata with
     fuller art coverage, plus 3D box art and downloadable game
     manuals. OpenVGDB then acts as the backup that fills whatever
     ScreenScraper misses.
   - Preferences -> Achievements: Sign in to RetroAchievements
     (see RETROACHIEVEMENTS section below) to track unlocks.
   - Preferences -> Backups: Sign in to GitHub for free cloud sync of
     battery saves and your library database across PCs (see BACKUPS
     section below).


CONTROLLERS
-----------
Connect your controller before launching Emutastic. Button mappings are
configurable in Preferences → Controls. Controllers are detected
automatically — no refresh needed.


KEYBOARD SHORTCUTS
------------------
In the library:
  Ctrl+F     Focus the search box in the active tab
  Ctrl+A     Select all visible games
  Enter      Open the focused game's detail card
  Delete     Remove selected games (save states are preserved) or
             delete selected screenshots
  Esc        Clear the search box and drop focus

In a game (move the mouse to bring up the overlay):
  Print Screen / F12   Take a screenshot
  F9                   Start / stop gameplay recording
  Esc                  Exit back to the library
  Cog icon             Cheats, save/load state, notes, manual, settings


SCREENSHOTS & RECORDING
-----------------------
Press Print Screen or F12 in a game to capture the current frame.
Screenshots land in your data folder under Screenshots/<Console>/
and show up in the Screenshots tab in the library sidebar, grouped
per-game. Multi-select with Ctrl-click / Shift-click and press Delete
to remove.

Press F9 (or cog → Record) to record gameplay with audio; recordings
encode to .mp4 in Recordings/<Console>/<Game>/ when you stop. Closing
the game mid-recording finishes the encode automatically.


SAVE STATES
-----------
Open the in-game overlay (move the mouse), click the cog, then
"Save State" or "Load State". Existing states are listed in the Save
States tab in the library sidebar, grouped per-game and previewed with
thumbnails. Both tabs have their own search box at the top.

A handful of cores can't create save states reliably and the option is
hidden for them.


THEMES
------
Preferences → Themes switches between bundled themes or loads an
.emutheme file. Click "Edit" on the current theme to open the visual
editor — live color picker, per-console accent / background overrides,
preview as you go. Save edits under a new name; the bundled default
theme is read-only so you always have a known-good fallback.


BACKUPS
-------
Preferences -> Backups has two options for protecting your data:

Local Backup
~~~~~~~~~~~~
Set a folder to back up your library database, battery saves, and
save states. Click "Back Up Now" to copy everything to that folder —
drop it on a USB stick or a cloud-synced folder for safekeeping.
Restore from the same screen. Cores, BIOS files, core options, and
the ROMs themselves are not part of the backup (cores and BIOS are
easy to re-download, ROMs are easy to re-import).

Cloud Sync (GitHub)
~~~~~~~~~~~~~~~~~~~
Sync your battery saves and library database across multiple PCs
using your GitHub account. Sign in once, and a private repo called
"emutastic-saves" is created automatically on your account.

  - Battery saves upload when you close a game
  - The newer save is pulled when you launch a game on another PC
  - "Sync Now" runs a full bidirectional sync of all saves and
    the library database

Optional AES-256-GCM encryption with a passphrase you choose — saves
are encrypted before they leave your machine. The passphrase never
leaves your PC; you'll enter it once per PC.

Save states are NOT included in cloud sync — they get too large for
some consoles. Use the local backup option above for save states.

For details on encryption, GitHub storage limits, and sharing saves
with friends, see:
   https://github.com/codingncaffeine/Emutastic/wiki/Cloud-Sync


BIOS FILES
----------
Place BIOS files in:
  ~/.local/share/Emutastic/System/
  (or wherever your data directory is set; in portable mode this is
  PortableData/System/ next to the Emutastic executable)

You can also place them in the same folder as your ROMs for that system.
A few systems read their BIOS from a subfolder of System/ (for example
PlayStation 2 uses System/pcsx2/bios/) — Preferences → System Files shows
the exact filenames and location required for each system.


PORTABLE MODE  (tarball installs only)
--------------------------------------
Run Emutastic from a USB stick, take it between PCs, sync the whole
folder — everything Emutastic needs lives inside the install folder.

Either trigger works (both opt-in):

  1. Create an empty file named  portable.txt  in the same folder as
     the Emutastic executable, then launch:
       touch portable.txt
  OR
  2. Launch with the  --portable  command-line flag:
       ./Emutastic --portable
     Useful for launchers when you don't want to leave a marker file
     in the folder.

From then on, ALL data lives in a  PortableData  subfolder right next
to the executable — that includes the library database, configs, save
states, battery saves, screenshots, recordings, artwork, BIOS files,
libretro cores, and ROMs you import. Nothing is written to
~/.local/share or ~/.config.

True USB portability — what to expect:

  • Move the entire Emutastic folder to a USB stick.
  • Plug the USB into ANY Linux PC; the mount point doesn't matter.
    Library paths are stored relative to PortableData, so they don't
    break across machines.
  • ROMs you import are auto-copied into PortableData/Roms/<Console>/
    so they travel with the USB.
  • Cores download into PortableData/Cores/, so the data folder is
    fully self-contained.

Important — enable portable mode BEFORE importing ROMs:

  ROMs imported while Emutastic is running in normal mode stay at
  their original location, and the database stores the absolute path.
  Switching to portable mode afterwards does NOT reach back to copy
  those ROMs into PortableData. The cleanest path: enable portable
  mode first (touch portable.txt, launch once), then import.

To go back to normal mode, simply delete portable.txt — your
~/.local/share data (if any) becomes active again.

Notes: portable.txt must sit at the same level as the executable, and
the folder must be writable (a read-only location silently falls back
to normal mode). Portable mode is NOT available on .deb installs —
/usr/lib is root-owned; use the tarball for portable setups.

In-app updates preserve portable mode: the updater never touches
portable.txt or PortableData/.


CHEATS
------
Per-game cheats can be managed two ways:

  - In-game: open the overlay (move the mouse), click the cog, and
    choose "Cheats" -> "Add Cheat...". Each cheat has a pill-style
    toggle switch on the left -- click it to flip on/off without
    opening the editor.
  - From the library: click a game to open its detail card, then
    "..." -> "Cheats...". Same toggles and editor; changes apply
    the next time you start the game.

Cheats database
~~~~~~~~~~~~~~~
The community libretro cheats database is one click away. Open
Preferences -> Cores / Extras and download "Cheats Database" (about
37 MB, single download covering 25+ systems). After it's installed,
open any game's cheats menu and click "Import from database..." --
matching cheats are imported all-disabled, then you toggle on the
ones you want.

Cheats are matched by ROM filename, so for best results use ROMs that
match the No-Intro / Redump naming convention.

A few cores cannot apply cheats (PSP, 3DS, Vectrex, 3DO, CD-i, NeoGeo,
ColecoVision). For those systems the Cheats option is hidden.


RETROACHIEVEMENTS
-----------------
RetroAchievements (https://retroachievements.org) is a community-run
service that tracks achievement unlocks across hundreds of supported
games. Just sign in under Preferences -> Achievements -- that's all it
takes; there is no separate "enable" switch. Once you're signed in,
achievements track automatically in every supported game.

  1. Username + Password
     Sign in here -- this is the only required step. After your first
     successful login the password is replaced by a session token, so
     you only enter it once.

  2. Web API Key  (separate from your password)
     Unlocks the per-game stats on the library detail card and the
     whole Achievements tab (profile, trophy case, friends, activity
     heatmap). Without it the unlocks still work, just no in-app stats.

     Grab the key from:
        https://retroachievements.org/controlpanel.php
     Sign in, find "Keys" -> "Web API Key" near the bottom, and
     paste the value into Preferences -> Achievements -> Web API Key.

  3. Hardcore Mode  (default ON)
     Disables save state LOADING (creating states is still allowed)
     and cheat codes during gameplay. Required by RA for "hardcore"
     achievement unlocks, which are worth more points and count
     toward the mastery badge.

     Emutastic ships with Hardcore Mode ON to align with RA's
     recommendation for new accounts. Flip the toggle off in
     Preferences -> Achievements -> Hardcore Mode any time if you
     want to use save state loading or cheats -- note that any
     achievement unlocks earned with hardcore off won't count toward
     hardcore points or the mastery badge. Switching mid-session is
     not allowed by RA, so the change takes effect on the next game
     launch.

     Hardcore Mode is temporarily disabled for PSP titles regardless
     of the toggle setting -- see the Hardcore Compliance wiki page
     for the technical reason.

     NOTE: With Hardcore Mode on, RetroAchievements pops a toast that
     reads "Unknown emulator" at game start. This is EXPECTED, not a
     bug -- RA only approves an emulator for hardcore after it has been
     publicly available for six months, and Emutastic's earliest
     application date is October 14, 2026, so the server doesn't yet
     recognize it as an approved hardcore client.

     If you want to unlock standard achievements and stop the message,
     turn Hardcore Mode OFF (Preferences -> Achievements). We'll let
     everyone know once Emutastic is approved for hardcore.

     For the full line-by-line compliance audit:
        https://github.com/codingncaffeine/Emutastic/wiki/Hardcore-Compliance

In-game, achievements appear as toast notifications the moment you
unlock them — the toast's look is fully customizable in Preferences →
Achievements → Toast Appearance. The Achievements tab has You / Friends
sub-tabs mirroring your RA follow graph; click a friend for their
recently-played games, unlocks, and a compare view.


UPDATES
-------
Preferences -> About shows the current version and checks GitHub for
the latest release. When a new version is available, click "Update Now":

  Tarball installs   download, swap in place, and relaunch — your data
                     (and portable.txt, if present) is untouched.
  .deb installs      download the package and install it through your
                     system's authorization prompt, then relaunch.

Downloads are verified against the release's published SHA-256 checksum
before anything is installed; a mismatch cancels the update.


LINUX NOTES
-----------
The game window uses native Wayland when available (X11 works too).


MORE INFORMATION
----------------
GitHub:  https://github.com/codingncaffeine/Emutastic-For-Linux
Upstream (Windows): https://github.com/codingncaffeine/Emutastic

================================================================================
