# rcheevos vendoring notes — READ BEFORE TOUCHING THE PIN

## Current pin

`../rcheevos-src` = **v12.5.0** (tagged upstream 2026-09-14), vendored 2026-09-23 together with
the Windows app's `rcheevos.dll` rebuild from the same tag and the macOS port — the coordinated
bump described below, done once both of its conditions held: PR #517 (.neo content hashing)
merged upstream on 2026-06-15 and shipped in v12.4.0, and the Windows app had been identifying
Neo Geo games by that content hash since 2026-06-16. `.neo` files therefore identify by content
hash on every platform. Verified at bump time: `checkabi` numbers match `VerifyAbi`, all 29
P/Invoke imports export from the new `.so`, `--ra-selftest` passes, and two synthetic `.neo`
files with different headers and the same payload hash identically to `md5(payload)`.

Layout changes 11.6 → 12.5 are exactly the appended fields listed in the dress-rehearsal notes
below plus `rc_client_user_t.avatar_last_updated` (time_t, 12.4). `src/rurl/` is gone and
`build.sh`'s glob list no longer mentions it. The 11.6 `rc_client_begin_change_media` (path
based) is `rc_client_begin_identify_and_change_media` in 12.x, and is what the disc-swap
path now calls.

## ⛔ DO NOT bump this as part of routine "port today's upstream diffs"

The owner authors rcheevos changes UPSTREAM (e.g. PR #517 — .neo content hashing, on the
`codingncaffeine/rcheevos` fork, branch `add-neo-extension`). Those pushes are public review
material that is supposed to execute NOWHERE until RetroAchievements completes their side.
On 2026-06-06 a routine diff sweep vendored the PR head here, making this port the only build
on earth running it — and breaking Neo Geo identification (content hashes unknown to the
server → `Load failed (-29): Unknown game`). Reverted same day; no release shipped with it.

## When TO bump (the coordinated cycle — BOTH conditions, then BOTH ports together)

1. PR #517 (or successor) is MERGED upstream, AND
2. RA's database is SEEDED with the .neo content hashes (carmiker's canonical set and/or ours —
   we can generate a manifest: see "tooling" below).

Then: re-vendor the merge commit on Linux while Windows rebuilds its dll from the same commit,
verify ONE real Neo Geo game identifies on each platform with MATCHING hashes, ship both.

## Dress-rehearsal knowledge from the 2026-06-06 attempt (so the real bump is an hour, not a day)

The bump to 12.3-era source was fully built and verified before the revert. Everything learned:

1. **Source layout** (11.6.0 → 12.3): `src/rurl/` removed (folded into rapi); `src/rhash/`
   split into `hash_*.c`; `rc_version.c` added. `build.sh`'s SOURCES globs need updating —
   beware: under `set -euo pipefail`, a glob matching nothing makes the script die with
   exit 2 and ZERO output.
2. **ABI** (all growth by APPENDED fields, no existing offset moved):
   `rc_client_event_t` 48→56 (+subset*), `rc_client_achievement_t` 88→104 (+badge_url*,
   +badge_locked_url*), `rc_client_user_t` 40→48 (+avatar_url*), `rc_client_game_t` 32→40
   (+badge_url*). Update the C# mirrors + `VerifyAbi()` in
   `src/Emutastic/Services/RcheevosInterop.cs`; build.sh regenerates `rcheevos-abi.txt`.
3. **P/Invoke surface**: all 28 imports existed in 12.3 headers and exports — re-verify with
   `nm -D librcheevos.so` against the DllImport list.
4. **Verification recipe**: (a) synthetic — two `.neo` files, NEO\1 magic, different 4096-byte
   headers, identical payload → identical hash == `md5(payload)`; (b) real — launch a Neo Geo
   game, hash in the RA log must match the same ROM on Windows; (c) the LunaGarlic-set
   reference value at attempt time: `bstars2.neo → 97c926d6474a75b250a4c72ad878bc9a`.

## Tooling

A trivial harness (pattern preserved here) links `librcheevos.so` and prints a ROM's hash:
`rc_hash_initialize_iterator` + `rc_hash_generate(hash, RC_CONSOLE_ARCADE, &it)`. Useful to
generate the full `game → content-hash` manifest from the owner's romset for RA's seeding.
