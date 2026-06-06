# rcheevos vendoring notes — READ BEFORE TOUCHING THE PIN

## Current pin (2026-06-06)

`../rcheevos-src` = **rcheevos PR #517 head** (`f17027a`) = **v12.3.0 + .neo content hashing**.
Source: https://github.com/RetroAchievements/rcheevos/pull/517 (authored by the project owner,
adds Geolith Neo Geo `.neo` cart hashing: validate `NEO\1` magic, skip the 4096-byte header
whose text fields differ between conversion tools, MD5 the ROM data after it, registered as
RC_CONSOLE_ARCADE).

## Why this exact pin

- The Windows app's committed `Libraries/rcheevos.dll` identifies as **rcheevos/12.3** — the
  Linux pin had silently sat at **v11.6.0** (the version drift hid in the reference-clone
  baseline; see memory note "diff-watching-blind-spots"). This bump restores version parity.
- Pinning the PR head (instead of the bare v12.3.0 tag) also gets the `.neo` hashing the owner
  is landing upstream, so this build hashes Neo Geo carts the way the RA server will expect.

## When PR #517 merges upstream

Re-pin to the actual merge commit (or the next tagged release containing it) and update this
file. If review changes the hashing (header handling, console registration), the merged version
WINS — re-vendor, rebuild, and re-verify the synthetic test below.

## What the 11.6.0 → 12.3.0 bump changed for us (all handled, 2026-06-06)

1. **Source layout**: `src/rurl/` removed (folded into rapi); `src/rhash/` split into
   `hash_*.c` files; `rc_version.c` added. `build.sh`'s globs were updated — note the
   `set -euo pipefail` + `ls` failure mode: a glob matching nothing kills the script with
   exit 2 and ZERO output.
2. **ABI**: four structs grew BY APPENDING fields (no existing offset moved):
   `rc_client_event_t` 48→56 (+subset*), `rc_client_achievement_t` 88→104
   (+badge_url*, +badge_locked_url*), `rc_client_user_t` 40→48 (+avatar_url*),
   `rc_client_game_t` 32→40 (+badge_url*). The C# mirrors in
   `src/Emutastic/Services/RcheevosInterop.cs` and the `VerifyAbi()` constants were updated to
   match. `rcheevos-abi.txt` is the regenerated native truth (build.sh refreshes it).
3. **P/Invoke surface**: all 28 imported functions verified present in the 12.3 headers AND
   exported by the rebuilt `librcheevos.so` (`nm -D`).

## Verification done at pin time

Synthetic test: two `.neo` files with NEO\1 magic, DIFFERENT 4096-byte headers, identical 8KB
payload → both hashed `6556112372898c69e1de0bf689d8db26` = `md5(payload)` exactly. The header
is provably excluded; conversion-tool metadata can no longer fork the hash.

Cross-platform check (PENDING at write time): hash a real `.neo` ROM on the Windows build (with
its PR-rebuilt rcheevos.dll — NOTE: the COMMITTED dll probes as stock 12.3 WITHOUT the .neo
strings; the owner's patched rebuild may be local-only) and on this build — hashes must match.

## Known version-gap notes carried from the 11.6.0 era

The two rcheevos-version gaps documented during the RA port (see docs / memory
"retroachievements-port") were written against 11.6.0 — re-evaluate them against 12.3.0; the
12.x additions (badge_url, avatar_url fields) may close one or both.
