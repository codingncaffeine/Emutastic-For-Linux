#!/usr/bin/env bash
# Builds the two Linux release artifacts the in-app updater consumes:
#   Emutastic-<ver>-linux-x64.tar.gz   self-contained, extract anywhere (portable
#                                      mode = `touch portable.txt`, see README.txt)
#   emutastic_<ver>_amd64.deb          system install (/usr/lib/emutastic)
# Asset names are a CONTRACT with Services/UpdateService.cs — change both together.
# README.txt (the bundled quick-start guide) ships in the tarball root and the
# deb's /usr/share/doc/emutastic/.
set -euo pipefail
cd "$(dirname "$0")/.."

# ── preflight: ScreenScraper developer registration ──────────────────────────
# src/Emutastic/Secrets.cs is gitignored, so any fresh clone builds from the
# empty template. ScreenScraper answers a request carrying no devid/devpassword
# with its *user* credential error, so a build made that way tells every user
# their own account password is wrong. 0.9.3 shipped exactly like that — fail
# the release here instead of finding out from a bug report.
SECRETS=src/Emutastic/Secrets.cs
if grep -qF 'ScreenScraperDevId = ""' "$SECRETS" ||
   grep -qF 'ScreenScraperDevPass = ""' "$SECRETS" ||
   ! grep -qF 'ScreenScraperDevId = "' "$SECRETS" ||
   ! grep -qF 'ScreenScraperDevPass = "' "$SECRETS"; then
    echo "ERROR: $SECRETS carries no ScreenScraper developer registration." >&2
    echo "       Scraping would be dead in every artifact this run produces," >&2
    echo "       and the app would blame each user's own password. Fill it in first." >&2
    exit 1
fi
echo "── preflight: ScreenScraper developer registration present"

# ── preflight: GitHub OAuth client id (cloud sync) ───────────────────────────
# Same trap: an empty GitHubOAuthClientId compiles fine, and Preferences → Backups
# then greys out "Sign in with GitHub". Cloud sync was unusable in every release
# through 0.9.6 for exactly this reason. The id is the Windows app's OAuth App
# (device flow enabled), so both apps share one consent screen and one repository.
if ! grep -qE 'GitHubOAuthClientId = "[^"]+"' "$SECRETS"; then
    echo "ERROR: $SECRETS carries no GitHub OAuth client id." >&2
    echo "       Cloud sync sign-in would be disabled in every artifact this run produces." >&2
    exit 1
fi
echo "── preflight: GitHub OAuth client id present"

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' src/Emutastic/Emutastic.csproj)
OUT=packaging/out
PUB=$OUT/publish
rm -rf "$OUT" && mkdir -p "$PUB"

# ⛔ Clean the INTERMEDIATES too, not just our own output. A publish that reuses an obj/
# left by an earlier `dotnet build` can emit an assembly whose XAML was never compiled:
# the build exits 0 with no warnings, all 218 assemblies are present, the app still runs
# fine from bin/ — and the ARTIFACT aborts on launch with
#   Avalonia.Markup.Xaml.XamlLoadException: No precompiled XAML found for Emutastic.App
# v0.9.8 hit exactly that; smoke-test.sh caught it. Releases were safe before only because
# they came from fresh clones. This makes the script independent of the tree it runs in.
# Verify with: strings -a Emutastic.dll | grep -c CompiledAvaloniaXaml   (1 good, 0 stale)
rm -rf src/Emutastic/obj src/Emutastic/bin

echo "── publish v$VER (self-contained linux-x64)"
dotnet publish src/Emutastic/Emutastic.csproj -c Release -r linux-x64 \
    --self-contained true -o "$PUB" -v q

# Native libs + loose assets ride OutDir during build; make sure they're in the publish set.
for so in native/wlpresent/libwlpresent.so native/rcheevos/librcheevos.so native/libchdr/libchdr.so; do
    cp -f "$so" "$PUB/"
done
mkdir -p "$PUB/Assets/Sounds"
cp -f src/Emutastic/Assets/Sounds/Notification1.mp3 "$PUB/Assets/Sounds/"
cp -f "src/Emutastic/Assets/buttons/powerbutton.png" "$PUB/" 2>/dev/null || true

cp packaging/README.txt "$PUB/README.txt"
cp LICENSE "$PUB/LICENSE"
cp LICENSE-CONTROLLER-ART.txt "$PUB/LICENSE-CONTROLLER-ART.txt"
cp NOTICES.txt "$PUB/NOTICES.txt"   # BSD-3/MIT attribution for the bundled native libs

echo "── tarball"
tar -C "$PUB" -czf "$OUT/Emutastic-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/emutastic" "$DEB/usr/bin" \
         "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/512x512/apps" \
         "$DEB/usr/share/doc/emutastic"
cp -a "$PUB/." "$DEB/usr/lib/emutastic/"
rm -f "$DEB/usr/lib/emutastic/README.txt"
cp packaging/README.txt "$DEB/usr/share/doc/emutastic/README.txt"
cp LICENSE "$DEB/usr/share/doc/emutastic/copyright"
cp NOTICES.txt "$DEB/usr/share/doc/emutastic/NOTICES.txt"
mkdir -p "$DEB/usr/share/metainfo"
cp packaging/io.github.codingncaffeine.Emutastic.metainfo.xml "$DEB/usr/share/metainfo/"
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
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libxfixes3, libice6, libsm6, libfontconfig1, libegl1, libgl1, libsdl3-0, libwayland-client0, libwayland-egl1, libpng16-16t64 | libpng16-16, ffmpeg
Recommends: libvlc5, vlc-plugin-base, libsecret-1-0
Maintainer: codingncaffeine <codingncaffeine@users.noreply.github.com>
Description: Retro game library and emulator frontend
 Linux port of the Emutastic libretro frontend: game library, save states,
 screenshots, recordings, cheats, and RetroAchievements.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/emutastic_${VER}_amd64.deb" > /dev/null

rm -rf "$DEB"
echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
