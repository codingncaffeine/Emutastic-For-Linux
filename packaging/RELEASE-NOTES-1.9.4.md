The original Xbox arrives in Emutastic, run by Midway Verdant: our own clean-room, low-level Xbox emulator, made for Emutastic and released alongside it.

## What's New

- **Original Xbox** — The original Xbox in the sidebar, in a group of its own. Games run on [Midway Verdant](https://github.com/codingncaffeine/Midway-Verdant), which emulates the console's hardware and runs its own boot ROM, BIOS and kernel unmodified, with the CPU under KVM at full speed. Import your XISO or full-dump disc images like any other console.
- **Midway Verdant ships with the app, and updates from Preferences** — The core is bundled, and it is also in *Preferences → Cores* (and in *Download All Recommended*), downloaded from its own releases on GitHub, so new versions arrive the way other cores' updates do.
- **Xbox firmware in System Files** — *Preferences → System Files* lists the two files you supply from your own console: the MCPX boot ROM and the flash BIOS. The EEPROM and the hard disk are created on the first start.
- **Every Xbox core option in Preferences** — *Preferences → Core Options → Midway Verdant* shows all of its settings from the first run, before you have launched a game: internal resolution, audio mode, shader compiling, anisotropic filtering, disc loading, and the rest.
- **Up to 6x internal resolution, live** — Internal Resolution and Anisotropic Filtering are in the in-game overlay's Visuals and change while the game runs. Frames are handed over on the GPU: a 6x picture is drawn straight from graphics memory, with no copy through the CPU.
- **The Duke** — The Xbox controller picture in the Controls panel is the original Duke, with its buttons where they belong.

## What's Fixed

- **Large software frames** — Cores that hand over big XRGB8888 frames are copied a vector at a time and in bands, so high-resolution software frames cost far less.
- **ScreenScraper and Xbox discs** — Box art lookups also try a disc's plain `.iso` name.

## Requirements for the Xbox

An x86-64 PC with hardware virtualization (Intel VT-x or AMD-V) switched on in its firmware setup, and a GPU with a Vulkan driver. If virtualization is off, the game tells you how to switch it on.
