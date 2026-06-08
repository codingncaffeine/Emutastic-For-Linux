#!/usr/bin/env bash
# Build libwlpresent.so — the own-xdg_toplevel Wayland presenter shim (window + EGL/GL present + OSD
# overlay + themed title bar + rounded corners + interactive move/resize + cursor-shape feedback).
#
# The *-protocol.c / *-client-protocol.h files are generated from the bundled *.xml via wayland-scanner
# (xdg-shell, xdg-decoration-unstable-v1, cursor-shape-v1, tablet-unstable-v2 — the last is only a
# dependency of cursor-shape's get_tablet_tool). Regenerate with regen-protocols below if the XML changes.
set -euo pipefail
cd "$(dirname "$0")"

regen() {
  for x in xdg-shell xdg-decoration-unstable-v1 cursor-shape-v1 tablet-unstable-v2; do
    wayland-scanner client-header "$x.xml" "$x-client-protocol.h"
    wayland-scanner private-code  "$x.xml" "$x-protocol.c"
  done
}
[ "${1:-}" = "regen" ] && regen

gcc -shared -fPIC -O2 \
    wl_shader.c \
    wl_present.c \
    wl_hwgl.c \
    xdg-shell-protocol.c \
    xdg-decoration-unstable-v1-protocol.c \
    cursor-shape-v1-protocol.c \
    tablet-unstable-v2-protocol.c \
    -o libwlpresent.so \
    $(pkg-config --cflags --libs wayland-client wayland-egl egl gl x11 libpng) -lm

echo "built libwlpresent.so ($(stat -c %s libwlpresent.so) bytes)"

# Standalone visual test (optional): ./build.sh && ./wltest
# gcc -O2 wlptest.c -o wlptest -L. -lwlpresent -Wl,-rpath,'$ORIGIN' $(pkg-config --cflags wayland-client)
