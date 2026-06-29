# EmuTV — Linux port plan

Porting the upstream **EmuTV** feature (controller-first "TV mode": fullscreen, gamepad-driven
carousel/grid frontend with an ES-DE-compatible theme engine, video snaps, SteamGridDB cover
art, save-state browser, favorites/badges/ratings) from the Windows/WPF source to this
Avalonia/Linux port.

Upstream is the spec (github.com/codingncaffeine/Emutastic). Aesthetic fidelity is a
first-class requirement, equal to functionality — match upstream look, spacing, fonts, and
animations exactly. Each phase must leave the build green (`dotnet build src/Emutastic.slnx -c
Release`). Commit + push per phase. Complications inside a phase become tracked splinters that
are finished before the next phase starts.

## Upstream footprint (origin/main, v1.8.x)

| File | Lines | Port target |
|---|---|---|
| `Views/EmuTvWindow.xaml` | 447 | `Views/EmuTvWindow.axaml` |
| `Views/EmuTvWindow.xaml.cs` | 1286 | `Views/EmuTvWindow.axaml.cs` |
| `Services/EmuTvThemeRenderer.cs` | 1312 | Avalonia visual tree + `RenderTargetBitmap` |
| `Services/EmuTvThemeParser.cs` | 679 | mostly platform-agnostic |
| `Models/EmuTvTheme.cs` | 411 | map WPF geometry/color → Avalonia |
| `Services/EmuTvLayout.cs` / `EmuTvThemeCatalog.cs` / `EmuTvThemeService.cs` | small | logic |
| Hooks in `MainWindow`, `ControllerManager`, `Ps2Handler`, `EmulatorWindow`, `IConsoleHandler` | — | adapt to Linux launch model |
| Assets: banners, `images/emutv/*`, `emutv-themes/default/**` (Press Start 2P font) | — | `src/Emutastic/Assets/**` |

## Already present in this port (reused, not rebuilt)

- **Video snaps** — `LibVLCSharp 3.9.4` + `Services/VideoPlaybackService.cs` (byte-identical API:
  `GetLibVLCAsync`, `StartWarmup`). LibVLC is cross-platform.
- **Game launching** — `Views/EmulatorWindow.axaml` + `Services/GameHostLauncher.cs` +
  `Emulator/EmulatorSession.cs`.
- **Controller** — SDL3 `Services/ControllerManager.cs` (event-based; needs a raw-poll adapter).
- **Avalonia 12.1** UI stack.

## Phases

- **Phase 0 — Scaffolding + assets.** Branch, this plan, import all binary/theme/font assets,
  wire `AvaloniaResource`. Front-loads the binary/asset blind spot.
- **Phase 1 — Models + parser.** `EmuTvTheme`, `EmuTvThemeParser`, `EmuTvLayout`,
  `EmuTvThemeCatalog`, `EmuTvThemeService`. Pure logic; map geometry/color types.
- **Phase 2 — Theme renderer.** `EmuTvThemeRenderer` WPF visual tree → Avalonia, incl. tiling,
  gradients, and `RenderTargetBitmap` rasterization. Fidelity-critical.
- **Phase 3 — Controller input adapter.** Raw-poll getter + XInput→SDL button-index mapping so
  the EmuTV input loop works against the SDL `ControllerManager`.
- **Phase 4 — EmuTvWindow host.** `.axaml` + code-behind; animations, video, renderer, input,
  folder picker via `IStorageProvider`. UI thread never blocks.
- **Phase 5 — Launch wiring + console hooks.** Entry point from `MainWindow`; adapt
  `IConsoleHandler`/`Ps2Handler`/`EmulatorWindow` hooks to `GameHostLauncher`. Verify call sites.
- **Phase 6 — SteamGridDB.** Cover-art fallback + token verify/log + Preferences token field and
  EmuTV preferences (nav hotkeys, controls reference).
- **Phase 7 — Aesthetic fidelity + verification.** Side-by-side polish pass vs upstream, golden-rule
  artifact grep, `.deb` dependency check, Release build. Hand off for testing.
