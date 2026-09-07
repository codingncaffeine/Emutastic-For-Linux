#!/usr/bin/env bash
# Smoke-test a built Emutastic artifact: unpack it, verify the payload inventory,
# then LAUNCH it and require it to stay running.
#
# Why this exists: emutastic-bin 0.8.0 through 0.9.3-1 shipped to the AUR with all
# 218 managed assemblies missing. makepkg exited 0, pacman installed 54 files without
# a complaint, and the app died instantly with
#   "The application to execute does not exist: '/usr/lib/emutastic/Emutastic.dll'"
# No build-time or install-time signal exists for that failure. Only running the app
# reveals it, so the release process has to run the app.
#
# Usage: packaging/smoke-test.sh <artifact> [...]
#   <artifact>: a release .tar.gz, a .deb, or an Arch .pkg.tar.zst
# Exit: 0 all checks passed | 1 a check failed | 2 could not run the launch test
set -uo pipefail

MIN_ASSEMBLIES=150
LAUNCH_SECONDS=15

fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILED=1; }
ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }

smoke_one() {
    local artifact="$1"
    local tmp payload
    FAILED=0
    printf '\n== %s ==\n' "$(basename "$artifact")"

    [[ -f $artifact ]] || { fail "no such file"; return 1; }
    tmp=$(mktemp -d) || return 1
    trap 'rm -rf "$tmp"' RETURN

    case $artifact in
        *.tar.gz|*.tgz|*.pkg.tar.zst|*.pkg.tar.xz)
            bsdtar -xf "$artifact" -C "$tmp" 2>/dev/null ;;
        *.deb)
            if command -v dpkg-deb >/dev/null; then
                dpkg-deb -x "$artifact" "$tmp"
            else
                bsdtar -xOf "$artifact" 'data.tar.*' | bsdtar -xf - -C "$tmp"
            fi ;;
        *)  fail "unrecognised artifact type"; return 1 ;;
    esac
    [[ $? -eq 0 ]] || { fail "could not unpack"; return 1; }

    # The apphost anchors the payload wherever the artifact happens to root it.
    payload=$(find "$tmp" -name Emutastic -type f -perm -u+x -printf '%h\n' 2>/dev/null | head -1)
    [[ -n $payload ]] || { fail "no Emutastic apphost found in the artifact"; return 1; }
    ok "payload at ${payload#$tmp}"

    # --- inventory -------------------------------------------------------------
    local n
    n=$(find "$payload" -maxdepth 1 -name '*.dll' | wc -l)
    if (( n < MIN_ASSEMBLIES )); then
        fail "only $n managed assemblies (expected >= $MIN_ASSEMBLIES)"
    else
        ok "$n managed assemblies"
    fi

    local f
    for f in Emutastic.dll Emutastic.runtimeconfig.json Emutastic.deps.json \
             System.Private.CoreLib.dll Avalonia.Base.dll \
             libcoreclr.so libhostfxr.so libhostpolicy.so; do
        [[ -f "$payload/$f" ]] && ok "$f" || fail "$f is missing"
    done

    # --- launch ----------------------------------------------------------------
    # The check that would have caught the AUR bug. Everything above is inference;
    # this is the only step that observes the app actually working.
    if [[ -z ${DISPLAY:-} && -z ${WAYLAND_DISPLAY:-} ]]; then
        printf '  \033[33mSKIPPED\033[0m  launch test: no DISPLAY/WAYLAND_DISPLAY.\n'
        printf '           Inventory alone is NOT a pass. Re-run on a graphical session.\n'
        return 2
    fi

    local log=$tmp/launch.log pid i
    ( cd "$payload" && ./Emutastic ) > "$log" 2>&1 &
    pid=$!
    for ((i = 1; i <= LAUNCH_SECONDS; i++)); do
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done

    if kill -0 "$pid" 2>/dev/null; then
        ok "still running after ${LAUNCH_SECONDS}s"
        kill "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
    else
        wait "$pid" 2>/dev/null
        fail "exited after ${i}s (code $?)"
        sed 's/^/           | /' "$log" | head -20
    fi

    # A .NET host failure prints to stdout and can still exit 0 in some paths.
    if grep -qiE 'does not exist|A fatal error|Failed to load|FileNotFoundException' "$log"; then
        fail "host/runtime error in output:"
        grep -iE 'does not exist|A fatal error|Failed to load|FileNotFoundException' "$log" |
            sed 's/^/           | /' | head -5
    fi

    return $FAILED
}

(( $# )) || { echo "usage: $(basename "$0") <artifact> [...]" >&2; exit 2; }

rc=0
for a in "$@"; do
    smoke_one "$a" || rc=$?
done

printf '\n'
case $rc in
    0) printf '\033[32mSMOKE TEST PASSED\033[0m — artifact launches.\n' ;;
    2) printf '\033[33mSMOKE TEST INCOMPLETE\033[0m — inventory only, app never launched.\n' ;;
    *) printf '\033[31mSMOKE TEST FAILED\033[0m — do NOT release this artifact.\n' ;;
esac
exit $rc
