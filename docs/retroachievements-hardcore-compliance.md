# RetroAchievements Hardcore Compliance — Requirements & Linux Audit

Source: https://docs.retroachievements.org/general/hardcore-compliance-requirements.html
(fetched 2026-06-06). Audit of Emutastic for Linux against each section follows the
requirement text. Status: ✅ compliant · ⚪ N/A · 📋 application-time item.

## A. RetroAchievements Features

| Requirement | Status | How |
|---|---|---|
| UI visibility — achievements accessible in the emulator | ✅ | Achievements dashboard tab in the main window; in-game unlock toasts + challenge/progress indicators in the GL OSD |
| Achievements trigger correctly; Measured/Trigger flags work | ✅ | rc_client (rcheevos) does all evaluation; Measured progress + challenge indicators rendered in-game |
| Rich Presence + Leaderboards operate correctly | ✅ | rc_client's built-in RP ping runs via DoFrame/Idle; leaderboard scoreboard events surface as in-game toasts (triumph logic in the library) |
| Offline queueing — unlocks cache + sync on reconnect | ✅ | rc_client's in-session disconnect/reconnect queue (DISCONNECTED/RECONNECTED events handled); same level as the Windows app |
| Save state hit storage (recommended) | ✅ | `.cheevos` side-car: `SerializeProgress` written with every state, `DeserializeProgress` restored on successful load only; stale side-cars deleted on overwrite; side-car renamed/deleted with its state |
| Toolkit (RAIntegration DLL) — Windows builds | ⚪ | Windows-only by definition; not required |
| Save file compatibility | ✅ | Standard libretro `.srm`; `.state` files stay binary-compatible (progress kept in the side-car, not embedded) |

## B. Hardcore Rules Enforcement

| Requirement | Status | How |
|---|---|---|
| Cheats disabled in hardcore | ✅ | Single chokepoint (`ExecuteCheatsApplyOnEmuThread`) gates every path: toggle, import, live re-apply, session-start apply, parent-sent reload |
| Rewind disabled in hardcore | ✅ | No rewind exists at all (config flag present, defaults off, no implementation) |
| Slowdown / frame advance disabled in hardcore | ✅ | Neither feature exists. (Fast-forward — allowed by RA — is also not yet implemented; when added, only speed-up will be offered) |
| Loading save states ALWAYS blocked in hardcore | ✅ | Single gate in `RequestLoadState` (covers F7, the status-bar picker, detail-card launches) **plus** a belt at `ExecuteLoadOnEmuThread` for the boot-time `--load-state` race where the pending load queues before the async RA login resolves |
| RP/Leaderboards can't be disabled; popups configurable | ✅ | No toggle disables RP or LB submission; only popup/toast/sound settings exist |
| Resume/quick-resume drops to softcore | ⚪ | No auto-resume feature; launch-into-state goes through the hardcore load gates above |
| Softcore→hardcore requires full game reset | ✅ | Hardcore is fixed at session start (mid-session toggle takes effect next launch = full reload); additionally rcheevos's RESET event now performs `retro_reset` + runtime reset (stricter than Windows) |
| State creation allowed, loading blocked | ✅ | Save paths never gate on hardcore; only loads do |
| Memory editors / debuggers / TAS playback prohibited | ✅ | None exist in the application |

## C. Identity and Integrity

| Requirement | Status | How |
|---|---|---|
| User agent `EmulatorName/v1.0.0 (OS) core/v0.5.0` | ✅ | `EmutasticUserAgent` builds exactly this (assembly version, `/etc/os-release` OS segment, per-session core name+version); sent on all RA traffic |
| Prior identification disclosure | 📋 | Identifies as **Emutastic** — the same product as the Windows app by the same author; to be stated in the application |

## D. Eligibility Timeline

First public Linux release: **v0.5.0 on 2026-06-05** → eligible to apply **on or after 2026-12-05**.
(If RA accepts the Windows app's public history as the parent project, eligibility may be earlier —
worth asking at application time.)

## E. Defaults and UX

| Requirement | Status | How |
|---|---|---|
| Hardcore on by default (recommended) | ✅ | `HardcoreMode` defaults `true` |
| Hardcore state visibly indicated during gameplay | ✅ | Persistent **HARDCORE** chip in the in-game status bar |

## F. Transparency and Legality

| Requirement | Status | How |
|---|---|---|
| Monetization disclosure | ✅ | None exists — free, GPL-3.0 |
| FOSS core licensing info + upstream links | 📋 | Cores download from the official libretro buildbot; the Cores panel should link each core's upstream/license before application (TODO) |
| GPL/LGPL/MPL obligations | ✅ | App is GPL-3.0 (same as upstream Windows); full source public; LGPL deps (libvlc) dynamically linked system packages |
| Privacy policy (data retention, servers, GDPR) | 📋 | TODO before application: publish a short policy (locally-stored credentials, no telemetry, third-party endpoints: retroachievements.org, ScreenScraper, libretro buildbot, GitHub for sync/updates) |

## G. Auto-fail Criteria — all clear

Save-state loads in hardcore blocked (two layers) · no rewind/slow-mo/frame-advance ·
cheats hard-gated · hardcore entry forces reset · unique well-formed user agent ·
no prior foreign identification · no commercial use of NC cores · (privacy policy pending, see F).

## Outstanding before application (target ≥ 2026-12-05)

1. Privacy policy page (F).
2. Core licensing/upstream links in the Cores panel (F).
3. Disclose shared identity with the Windows Emutastic (C).
4. Re-run this audit against the then-current requirement text.
