The Commodore 64 arrives in Emutastic, run by Kernal64: our own cycle-exact C64 emulator, written from scratch and released alongside it.

## What's New

- **Commodore 64** — The C64 in the sidebar's Other group. Games run on [Kernal64](https://github.com/codingncaffeine/Kernal64), which emulates the whole machine, a 1541 disk drive running its own DOS, the datasette and a CMD hard drive. Import cartridges (`.crt`), programs (`.prg`, `.p00`, `.t64`), disks (`.d64`, `.g64`, `.nib`, multi-disk `.m3u`) and tapes (`.tap`) like any other system. The core downloads from Kernal64's own releases (*Preferences → Cores*, also in *Download All Recommended*).
- **Commodore 64 BASIC** — Once the C64's ROMs are in System Files, a *Commodore 64 BASIC* entry appears in the library: the machine as it powers on, at the READY prompt, for writing your own programs. Its right-click menu downloads the C64's original **User's Guide** and **Programmer's Reference Guide**.
- **A real keyboard** — In a C64 game your keyboard is the C64's keyboard: every key goes to the machine (F10 leaves, F11 is fullscreen). Joysticks, paddles and the on-screen keyboard work from a controller.
- **Blank floppies, tapes and hard drives** — A C64 game's right-click menu makes a **New Blank Floppy Disk**, **New Blank Tape** or **New Blank Hard Drive**; floppies and tapes can also be made from the in-game overlay. A hard drive (20 MB to 1 GB) is a CMD HD set up by the drive's own programs, ready to use as device 12.
- **Device 10, unlimited** — Every C64 game can save to a folder on your computer as device 10 (`SAVE"NAME",10`), with no size limit.
- **Fast Loading** — In the in-game overlay's options: tapes and disks load faster with the sound muted, back to real speed when loading stops.
- **C64 system files** — *Preferences → System Files* lists the C64's BASIC, KERNAL and character ROMs, the 1541's ROMs for disks, and the CMD HD's boot ROM and utilities disk for hard drives. They are found by their contents, whatever their names.

## What's Fixed

- **Covers** — When the libretro thumbnail server does not serve, box art comes from the libretro-thumbnails repositories on GitHub instead.
- **Right-click menus** — A menu no longer closes by itself while covers download.
- **Controllers without a mapping** — A device whose axes rest at an end of their range (some tablet keypads) no longer holds a player's stick in a corner.
- **Cloud sync** — C64 hard drives stay on your computer: they are too big for the backup.

## Requirements for the Commodore 64

The C64's own ROMs, which you supply: BASIC, KERNAL and the character set (and the 1541's two ROMs for disk images). See the [Kernal64 wiki](https://github.com/codingncaffeine/Kernal64/wiki/System-files).
