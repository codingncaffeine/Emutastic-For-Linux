#!/usr/bin/env bash
# Builds the three Linux release artifacts the in-app updater consumes:
#   Emutastic-<ver>-linux-x64.tar.gz            self-contained, extract anywhere
#   Emutastic-<ver>-linux-x64-portable.tar.gz   same + portable.txt pre-dropped
#   emutastic_<ver>_amd64.deb                   system install (/usr/lib/emutastic)
# Asset names are a CONTRACT with Services/UpdateService.cs — change both together.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' src/Emutastic/Emutastic.csproj)
OUT=packaging/out
PUB=$OUT/publish
rm -rf "$OUT" && mkdir -p "$PUB"

echo "── publish v$VER (self-contained linux-x64)"
~/.dotnet/dotnet publish src/Emutastic/Emutastic.csproj -c Release -r linux-x64 \
    --self-contained true -o "$PUB" -v q

# Native libs + loose assets ride OutDir during build; make sure they're in the publish set.
for so in native/wlpresent/libwlpresent.so native/rcheevos/librcheevos.so native/libchdr/libchdr.so; do
    cp -f "$so" "$PUB/"
done
mkdir -p "$PUB/Assets/Sounds"
cp -f src/Emutastic/Assets/Sounds/Notification1.mp3 "$PUB/Assets/Sounds/"
cp -f "src/Emutastic/Assets/buttons/powerbutton.png" "$PUB/" 2>/dev/null || true

echo "── tarballs"
tar -C "$PUB" -czf "$OUT/Emutastic-$VER-linux-x64.tar.gz" .
touch "$PUB/portable.txt"
tar -C "$PUB" -czf "$OUT/Emutastic-$VER-linux-x64-portable.tar.gz" .
rm "$PUB/portable.txt"

echo "── deb"
DEB=$OUT/debroot
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/emutastic" "$DEB/usr/bin" \
         "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/512x512/apps"
cp -a "$PUB/." "$DEB/usr/lib/emutastic/"
cat > "$DEB/usr/bin/emutastic" <<'WRAP'
#!/bin/sh
exec /usr/lib/emutastic/Emutastic "$@"
WRAP
chmod 755 "$DEB/usr/bin/emutastic"
cp "src/Emutastic/Assets/banners and icons/emutastic-logo.png" \
   "$DEB/usr/share/icons/hicolor/512x512/apps/emutastic.png"
cat > "$DEB/usr/share/applications/emutastic.desktop" <<DESK
[Desktop Entry]
Name=Emutastic
Comment=Retro game library and emulator frontend
Exec=emutastic
Icon=emutastic
Terminal=false
Type=Application
Categories=Game;Emulator;
DESK
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: emutastic
Version: $VER
Section: games
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libegl1, libgl1, ffmpeg
Recommends: libvlc5, vlc-plugin-base
Maintainer: Emutastic for Linux <stragee@gmail.com>
Description: Retro game library and emulator frontend
 Linux port of the Emutastic libretro frontend: game library, save states,
 screenshots, recordings, cheats, and RetroAchievements.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/emutastic_${VER}_amd64.deb" > /dev/null

rm -rf "$DEB"
echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
