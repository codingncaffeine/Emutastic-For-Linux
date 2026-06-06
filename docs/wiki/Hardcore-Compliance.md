# Hardcore Compliance

This page tracks how **Emutastic for Linux** implements every item in [RetroAchievements' hardcore-mode compliance requirements](https://docs.retroachievements.org/general/hardcore-compliance-requirements.html). Sections below mirror RA's own structure (A–H) and address each bullet on the official list. It is the Linux counterpart of the Windows app's [Hardcore Compliance](https://github.com/codingncaffeine/Emutastic/wiki/Hardcore-Compliance) page — same author, same product family, independently audited against this codebase.

**Status**: Code implementation and documentation complete as of v0.7.5. The eligibility timeline (Section D) gates formal application until December 5, 2026 (six-month rule), unless RA accepts the Windows Emutastic (public since April 14, 2026) as the parent project. Nothing on the auto-fail list is outstanding.

**Architecture note (Linux-specific):** games run in a separate `--game-host` process (the library window and the game window are different OS processes). All hardcore enforcement lives in the game-host process ([`Emulator/EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs)), which reads configuration once at session start and never writes it — referenced throughout below.

---

## A. RetroAchievements Features

> *User must be able to conveniently access the list of achievements within the emulator.*

✅ The achievements list for the active game appears on the game detail card, accessible from the library by clicking any title. A dedicated Achievements tab in the main window shows account-wide stats, recent unlocks, friends, leaderboards, and activity.

**Code reference:** Detail-card RA section — [`GameDetailWindow.axaml.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Views/GameDetailWindow.axaml.cs). Achievements tab — [`MainWindow.RaTab.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Views/MainWindow.RaTab.cs).

> *Triggers must evaluate correctly. Measured and Trigger flags must work properly.*

✅ Trigger evaluation is fully delegated to the rcheevos `rc_client` C library (built from source as `librcheevos.so`), the upstream reference implementation. The frontend exposes the standard libretro `RETRO_MEMORY_*` regions and replays `SET_MEMORY_MAPS` descriptors that arrive during `retro_load_game` to the client once it exists.

**Code reference:** P/Invoke layer — [`RcheevosInterop.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RcheevosInterop.cs). `rc_client` wrapper — [`RetroAchievementsClient.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RetroAchievementsClient.cs). Descriptor replay — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`InitRetroAchievements`).

> *Both flags must be visible in the achievement list as well as displayed during gameplay when appropriate conditions are met.*

✅ During gameplay, the GL overlay renders RetroArch-style indicators directly over the game: primed challenge achievements show their badges bottom-right for as long as the condition is armed (`RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW/HIDE`), and measured achievements show a transient progress tracker ("47/100") top-right on `PROGRESS_INDICATOR_SHOW/UPDATE/HIDE`. Live measured progress is also captured per session and persisted for the detail card's next library visit.

**Code reference:** Event surfacing — [`RetroAchievementsClient.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RetroAchievementsClient.cs) (`ChallengeIndicatorChanged`, `ProgressIndicatorChanged`, `_liveProgress`). In-game rendering — [`GlOsd.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Platform/GlOsd.cs) (`DrawRaIndicators`); state plumbing — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`SetRaChallenge`, `SetRaProgress`).

> *Rich Presence and Leaderboards: Must function correctly.*

✅ Both flow through rcheevos. Rich Presence pings run inside `rc_client`'s `DoFrame`/`Idle` cycle on the emulation thread. Leaderboard scoreboard events are shipped from the game host to the library process (which owns the friends/rank cache), where triumph/proximity toasts are decided and sent back for in-game display; submissions themselves are entirely `rc_client`'s.

**Code reference:** Frame/idle pump — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`RaDoFrame`, `RaIdle`). Scoreboard cross-process path — same file (`LeaderboardScoreboardReceived` → `lb-scoreboard` command) and [`MainWindow.RaTab.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Views/MainWindow.RaTab.cs) (`OnHostCommandForGame`).

> *Unlocks created while offline must be securely cached and sync to RetroAchievements when connectivity returns.*

✅ rcheevos manages the unlock queue and resync internally; the frontend surfaces the `DISCONNECTED`/`RECONNECTED` events as status text and does not intervene.

**Code reference:** Upstream rcheevos source at [github.com/RetroAchievements/rcheevos](https://github.com/RetroAchievements/rcheevos). Event handling — [`RetroAchievementsClient.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RetroAchievementsClient.cs).

> *Hit counts should be stored in save states (highly recommended, not strictly required).*

✅ Met — same side-car design as the Windows app. Every `.state` is paired with a `.cheevos` file containing the `rc_client_serialize_progress_sized` blob; on load, the side-car is applied via `rc_client_deserialize_progress_sized` only after a **successful** `retro_unserialize` (restoring hits onto a failed load would mis-credit). Stale side-cars are deleted when a state is overwritten without RA active; renaming or deleting a state moves/removes its side-car. Older states without a side-car load with current rcheevos state untouched.

**Code reference:** Serialize/deserialize wrappers — [`RetroAchievementsClient.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RetroAchievementsClient.cs) (`SerializeProgress`, `DeserializeProgress`). Side-car write — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`FinalizeSave`). Read + apply — same file (`RequestLoadState`, `ExecuteLoadOnEmuThread`). Rename/delete handling — [`MainWindow.axaml.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Views/MainWindow.axaml.cs) (save-state card context menu).

> *If you ship a Windows version of the emulator, please investigate adding RAIntegration DLL support.*

➖ Not applicable to this build — RAIntegration is Windows-only tooling for set authors. (The Windows Emutastic addresses this on its own compliance page.)

> *Save files should use standard formats compatible with other emulators of the same system.*

✅ SRAM is read from the libretro `RETRO_MEMORY_SAVE_RAM` region and written as a plain `.srm` (auto-saved periodically and flushed on exit). Save states are the per-core libretro serialized format; the `.cheevos` side-car keeps the `.state` itself byte-compatible with any other libretro frontend.

**Code reference:** [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`SaveSram`, the `_srmAutoSaveTick` cadence, `FinalizeSave`).

---

## B. Hardcore Rules Enforcement

> *Cheats are disabled in hardcore, including: Built-in cheat engines, Mountable cheat devices, External cheat files…*

✅ All three categories are blocked.

* **Built-in cheat engine**: every apply path funnels through a single chokepoint that short-circuits when hardcore is active — session-start apply, in-game toggles, database import, the live re-apply queued by the library's cheat editor (`reload-cheats` over the process boundary), and post-state-load re-apply. The in-game Cheats menu row is additionally hidden/disabled in hardcore.
* **Mountable cheat devices**: none are emulated. Cheat codes apply via the libretro `retro_cheat_set` interface and frontend Action Replay RAM writes — both behind the same gate.
* **External cheat files**: per-game JSON, blocked by the gate above. The one path the frontend cannot reach is PPSSPP's own `cheats/<DiscID>.ini`, which the core reads directly with no libretro signal for hardcore. As on Windows, Emutastic therefore **refuses hardcore on PSP entirely**: launching a PSP game with hardcore enabled drops that session to softcore with a visible message ("Hardcore Mode is disabled for PSP titles — achievements still track").

**Code reference:** Chokepoint — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`ExecuteCheatsApplyOnEmuThread`, with the explicit RA-compliance comment). Cog-row gating — same file (`buildCogMain`, the `cheatable` predicate). PSP carve-out — same file (`InitRetroAchievements`, the `effectiveHardcore` demotion).

> *Rewind is disabled in hardcore.*

✅ Emutastic for Linux does not implement rewind. A `RewindEnabled` config field exists as a placeholder (default `false`) with no consumers — no rewind buffer, hotkey, or UI exists.

**Code reference:** Dead field — [`ConfigurationModels.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Configuration/ConfigurationModels.cs) (`RewindEnabled`). Search the repo to confirm no consumers.

> *Slowdown and frame advance are disabled in hardcore.*

✅ Neither slow-motion nor frame-stepping exists. The emulation loop is audio-clocked at the core's native rate; the only pacing controls are internal (DRC/backpressure) and cannot slow gameplay below real time for the player's benefit.

**Code reference:** Emulation loop — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (the decoupled run loop).

> *Loading save states is ALWAYS blocked in hardcore.*

✅ Gated at every entry point, two layers deep:

* **Chokepoint**: `RequestLoadState` — covers the F7 quick-load hotkey, the in-game status-bar Load State picker, and library-initiated loads. The Load State button is also hidden from the in-game UI in hardcore (and its hit-test region is gated, so a stale click cannot land).
* **Belt**: `ExecuteLoadOnEmuThread` re-checks at execution time. This closes a Linux-specific race: a boot-time `--load-state` (launching a game directly from a save-state card) queues its pending load *before* the asynchronous RA login resolves hardcore — so the gate is re-evaluated on the emulation thread the moment the load would actually run.

State **creation** stays fully functional in hardcore — only loading is blocked, per the requirement.

**Code reference:** [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`RequestLoadState`, `ExecuteLoadOnEmuThread`, both with RA-compliance comments; status-bar hit-test gate in the present loop). HUD hiding — [`GlOsd.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Platform/GlOsd.cs) (status-bar buttons render conditionally on hardcore).

> *Rich Presence and Leaderboards cannot be disabled in hardcore. Disabling leaderboard popups is okay…*

✅ No setting disables RP or leaderboard submission. The only related settings are popup/toast presentation (visibility, sound, cooldown), which the requirement explicitly permits.

**Code reference:** Settings surface — [`ConfigurationModels.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Configuration/ConfigurationModels.cs) (only `ToastOnUnlock`, `HardcoreOnlyToast`, `LbToastSoundEnabled`-class fields exist).

> *If the emulator supports a resume/quick resume feature, the resumed session must drop to Softcore.*

➖ Not applicable. There is no resume-on-launch; every launch is a fresh session. (Launching from a save-state card routes through the blocked load path above when hardcore is on.)

> *Switching from softcore to hardcore is not allowed mid-session… must result in a full reset…*

✅ Doubly enforced by the process architecture: the hardcore flag is snapshotted into the game-host process at `InitRetroAchievements` and the host **never re-reads configuration** — a Preferences toggle flipped mid-session lives in the library process and physically cannot reach the running game. It takes effect on the next launch, which is a fresh process and a fresh session. Additionally, if rcheevos itself demands a reset (`RC_CLIENT_EVENT_RESET`, e.g. hardcore being enabled at game load), the handler resets **both** the achievement runtime and the game (`retro_reset`) — stricter than required.

**Code reference:** Snapshot — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`_raHardcoreActive`, assigned once in `InitRetroAchievements`; `RaHardcoreActive` property). Reset handler — same file (`ResetRequested` subscription). Host-never-writes-config convention — [`GameHost.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/GameHost.cs).

> *Users should be allowed to create save states while in hardcore for debugging…*

✅ Explicit design: F5 and the in-game Save State button work in hardcore; only the load paths are blocked.

**Code reference:** [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`RequestSaveState` — no hardcore guard, by design).

> *Memory editors, debuggers, and/or scripting/TAS/recorded input playback are strictly prohibited.*

✅ None exist: no memory editor, RAM watch, debugger, scripting host, TAS recorder, or input replay anywhere in the codebase.

**Code reference:** Verification by negative — search the [repo](https://github.com/codingncaffeine/Emutastic-For-Linux) for `RamWatch`, `MemoryEditor`, `Debugger`, `TAS`, `InputReplay`, `MovieRecord`. No matches.

---

## C. Identity and Integrity of the Client

> *Format: `EmulatorName/v1.0.0 (OSName 10.0) core_name/v0.5.0`*

Emutastic for Linux sends, on both the rcheevos HTTP client and the public Web API client:

```
Emutastic/<version> (<os-release name + version>) <core_name>/<core_version>
```

The OS segment comes from `/etc/os-release` (e.g. `Debian GNU/Linux 13`). The core segment is stamped per session from `retro_get_system_info()` **before** the login/identify HTTP fires, so even the first request carries it.

**Code reference:** UA builder — [`EmutasticUserAgent.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/EmutasticUserAgent.cs). Core stamping — [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) (`InitRetroAchievements` → `RetroAchievementsClient.SetCoreContext`).

> *Segment A (Required): numeric, incrementing version…*

✅ Semantic versioning (0.7.5 as of this page), numeric and incrementing across every release tag.

> *Segment B (Optional): OS name and version.* — ✅ From `/etc/os-release`.

> *Segment C (Optional but strongly advised): core name and version.* — ✅ Per-session, stamped before first request.

> *Emulators discovered to have previously identified as another client must disclose…*

📋 To be stated at application time: this build identifies as **Emutastic** — the same product identity as the Windows Emulator by the same author, never as any third-party client. The shared identity across the two platforms will be disclosed explicitly.

---

## D. Eligibility Timeline

> *The emulator, or the parent emulator it is forked from, must have been publicly available for at least 6 months…*

⏳ Pending. First public Linux release: **v0.5.0 on June 5, 2026** → earliest standalone application date: **December 5, 2026**. The Windows Emutastic — same author, same product, the codebase this port mirrors — has been public since **April 14, 2026**; whether RA treats it as the parent project (making the effective date October 14, 2026) will be raised at application time.

---

## E. Defaults and UX

> *Enabling hardcore by default is recommended, but not required…*

✅ Fresh installs default to hardcore (`HardcoreMode = true`). The toggle is one click away in Preferences → Achievements, clearly labelled; existing users who chose softcore keep their preference.

**Code reference:** [`ConfigurationModels.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Configuration/ConfigurationModels.cs) (`HardcoreMode`).

> *Hardcore state must be visibly indicated in the UI during play.*

✅ A red **HARDCORE** chip renders in the game window's persistent status bar — drawn by the GL overlay itself, outside the auto-hiding HUD, so it is on screen for the entire hardcore session.

**Code reference:** [`GlOsd.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Platform/GlOsd.cs) (`DrawStatus`, the `hardcore` parameter).

---

## F. Transparency and Legality

> *Monetization must include a features matrix…*

✅ Fully free, open-source software — no paid tiers, purchases, or quotas. The features matrix is trivially "everything, for everyone." Source: [github.com/codingncaffeine/Emutastic-For-Linux](https://github.com/codingncaffeine/Emutastic-For-Linux).

> *Publish a listing of every shipped FOSS core, its license, and upstream links.*

✅ Met by the product-family [Cores](https://github.com/codingncaffeine/Emutastic/wiki/Cores) page — the Linux build surfaces the same core catalog with the same distribution model: **Emutastic ships no core binaries**; the in-app Core Manager downloads directly from libretro's buildbot (Linux `.so` builds).

> *Non-commercial licenses may not be shipped if there is any commercialization…*

✅ Not applicable — non-commercial product, and core binaries are not redistributed by Emutastic (users fetch them from libretro's buildbot directly).

> *GPL/LGPL/MPL obligations…*

✅ Met. Emutastic for Linux is GPL-3.0 (same license as the Windows app) with full source public and [LICENSE](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/LICENSE) at the repository root; the OpenEmu controller-art BSD-3 notice ships alongside. LGPL dependencies (libVLC) are dynamically-linked system packages.

> *Privacy policy with data retention periods, server locations, GDPR compliance details.*

✅ Met by the product-family [Privacy Policy](https://github.com/codingncaffeine/Emutastic/wiki/Privacy-Policy): no personal data is collected, stored, transmitted, or sold; no servers are operated; no telemetry, analytics, or crash reporting exist. The Linux build's outbound connections are the same set (RetroAchievements, ScreenScraper, ArcadeDatabase, libretro buildbot + thumbnails, GitHub Releases for updates) plus the user's **own** GitHub repository when the optional cloud-save sync is signed in (data goes to an account the user controls; nothing flows to the author).

---

## G. Auto-fail Criteria

* **Loading save states in hardcore mode** — ✅ Blocked at two layers. See Section B ([`RequestLoadState` + `ExecuteLoadOnEmuThread`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs)).
* **Allowing rewind / slo-mo / frame advance in hardcore** — ✅ N/A; the features do not exist.
* **Allowing gameplay-altering cheats in hardcore** — ✅ Blocked at the [`ExecuteCheatsApplyOnEmuThread`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) chokepoint; PSP carved out entirely.
* **Switching to hardcore without a reset** — ✅ Impossible by process isolation; rcheevos-demanded resets also reset the game.
* **Non-unique user agent** — ✅ Unique `Emutastic/<version>` with OS + core segments.
* **Undisclosed prior identification as another emulator** — ✅ None; shared Windows/Linux product identity to be disclosed proactively.
* **Shipping non-commercial cores with commercialization** — ✅ N/A; non-commercial, cores not redistributed.
* **Privacy policy with placeholders/contradictions** — ✅ See Section F.

---

## H. Banned Emulators

➖ Not applicable. Emutastic is not on the banned list and has no history of shipping cheat tools to hardcore players or evading RA enforcement.

---

## Implementation reference index

Quick lookup for the hardcore-specific code. All links are to the live `main` branch.

| Concern | Location | Symbol |
|---|---|---|
| Hardcore session snapshot | [`EmulatorSession.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Emulator/EmulatorSession.cs) | `_raHardcoreActive` (set in `InitRetroAchievements`), `RaHardcoreActive` |
| Save-state load gate (chokepoint) | `EmulatorSession.cs` | `RequestLoadState` |
| Save-state load gate (boot-race belt) | `EmulatorSession.cs` | `ExecuteLoadOnEmuThread` |
| Load-button hide + hit-test gate | `EmulatorSession.cs` / [`GlOsd.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Platform/GlOsd.cs) | present-loop status hit-test / `DrawStatus` |
| Cheat apply gate (chokepoint) | `EmulatorSession.cs` | `ExecuteCheatsApplyOnEmuThread` |
| Cheat menu gating | `EmulatorSession.cs` | `buildCogMain` `cheatable` predicate |
| PSP hardcore carve-out | `EmulatorSession.cs` | `InitRetroAchievements` (`effectiveHardcore`) |
| rcheevos reset → game reset | `EmulatorSession.cs` | `ResetRequested` subscription |
| `.cheevos` progress side-car | `EmulatorSession.cs` | `FinalizeSave` / `RequestLoadState` / `ExecuteLoadOnEmuThread` |
| Side-car rename/delete | [`MainWindow.axaml.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Views/MainWindow.axaml.cs) | save-state card context menu |
| HARDCORE status chip | `GlOsd.cs` | `DrawStatus` (`hardcore` parameter) |
| User-Agent builder | [`EmutasticUserAgent.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/EmutasticUserAgent.cs) | `Build()` |
| Core segment stamping | [`RetroAchievementsClient.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Services/RetroAchievementsClient.cs) | `SetCoreContext` |
| Hardcore default | [`ConfigurationModels.cs`](https://github.com/codingncaffeine/Emutastic-For-Linux/blob/main/src/Emutastic/Configuration/ConfigurationModels.cs) | `HardcoreMode = true` |
| In-game challenge/progress indicators | `GlOsd.cs` / `EmulatorSession.cs` | `DrawRaIndicators` / `SetRaChallenge`, `SetRaProgress` |
