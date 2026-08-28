#!/usr/bin/env bash
# Packages a NativeAOT `dotnet publish` output into the two Linux installer
# formats: an .AppImage (run it anywhere, no install) and a .deb (apt/dpkg,
# with a desktop entry and icons). The plain .tar.gz is not built here — the
# release workflow already stages one with LICENSE/README/CHANGELOG beside the
# binary, and duplicating it would give two tarballs of the same bytes.
#
# Linux-only: dpkg-deb ships with any Debian-family distro and runner image,
# and appimagetool is downloaded on demand and run with
# --appimage-extract-and-run because GitHub's runners have no FUSE.
#
# Usage: build-packages.sh <publish-dir> <version> <rid> <out-dir>
#   publish-dir  output of `dotnet publish -r <rid> ...`
#   version      e.g. 1.2.3 (no leading v)
#   rid          linux-x64 | linux-arm64
#   out-dir      where to write the packages
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
OUT_DIR="$4"

case "$RID" in
  linux-x64)   DEB_ARCH="amd64"; APPIMAGE_ARCH="x86_64"  ;;
  linux-arm64) DEB_ARCH="arm64"; APPIMAGE_ARCH="aarch64" ;;
  *) echo "Unknown RID: $RID (expected linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

# The same base name the release workflow gives the .tar.gz, so every Linux
# asset on a release page sorts together and reads as one set.
BASE_NAME="kubeNimbus-$VERSION-$RID"
MASTER_DIR="$REPO_ROOT/design/masters/icon"
DESKTOP_TEMPLATE="$REPO_ROOT/installer/linux/kubenimbus.desktop.template"

# Stage what actually ships: the publish output minus the .dbg side file
# NativeAOT strips its debug symbols into, which is larger than the binary.
STAGE_DIR="$WORK_DIR/stage"
mkdir -p "$STAGE_DIR"
cp -R "$PUBLISH_DIR/." "$STAGE_DIR/"
rm -f "$STAGE_DIR"/*.dbg
chmod +x "$STAGE_DIR/kubeNimbus"

# One desktop entry, two Exec lines: inside an AppImage the binary is invoked
# by its in-bundle name, while the .deb exposes a /usr/bin/kubenimbus symlink.
emit_desktop() { # <exec-line> <dest>
  sed "s|__EXEC__|$1|" "$DESKTOP_TEMPLATE" > "$2"
}

# ---- AppImage ---------------------------------------------------------------
APPDIR="$WORK_DIR/AppDir"
mkdir -p "$APPDIR/usr/bin"
cp -R "$STAGE_DIR/." "$APPDIR/usr/bin/"
emit_desktop "kubeNimbus" "$APPDIR/kubenimbus.desktop"
cp "$MASTER_DIR/icon-256.png" "$APPDIR/kubenimbus.png"
cp "$MASTER_DIR/icon-256.png" "$APPDIR/.DirIcon"
# AppRun as a symlink rather than a wrapper script: NativeAOT resolves its
# side-car .so files relative to /proc/self/exe, so nothing has to be exported.
ln -s usr/bin/kubeNimbus "$APPDIR/AppRun"

APPIMAGETOOL="$WORK_DIR/appimagetool"
curl -fsSL --retry 3 -o "$APPIMAGETOOL" \
  "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$APPIMAGE_ARCH.AppImage"
chmod +x "$APPIMAGETOOL"
ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" --appimage-extract-and-run \
  "$APPDIR" "$OUT_DIR/$BASE_NAME.AppImage"
echo "Built $OUT_DIR/$BASE_NAME.AppImage"

# ---- .deb -------------------------------------------------------------------
# Debian spells a prerelease with ~ (which sorts *before* the release), where
# semver uses -. Only reachable through a workflow_dispatch test version today,
# but a 1.0.0-rc.1 tag would hit it for real.
#
# Through `tr` rather than ${VERSION/-/~}: bash performs tilde expansion on the
# replacement word, so the substitution form turns 0.4.0-rc.1 into a version
# carrying the invoking user's home directory, and dpkg-deb refuses it as an
# invalid character in a version number. Observed, not theorised.
DEB_VERSION="$(printf '%s' "$VERSION" | tr '-' '~')"
DEB_ROOT="$WORK_DIR/deb"
mkdir -p "$DEB_ROOT/DEBIAN" \
         "$DEB_ROOT/usr/lib/kubenimbus" \
         "$DEB_ROOT/usr/bin" \
         "$DEB_ROOT/usr/share/applications"
cp -R "$STAGE_DIR/." "$DEB_ROOT/usr/lib/kubenimbus/"
ln -s ../lib/kubenimbus/kubeNimbus "$DEB_ROOT/usr/bin/kubenimbus"
emit_desktop "kubenimbus" "$DEB_ROOT/usr/share/applications/kubenimbus.desktop"
for px in 16 24 32 48 256; do
  dest="$DEB_ROOT/usr/share/icons/hicolor/${px}x${px}/apps"
  mkdir -p "$dest"
  cp "$MASTER_DIR/icon-$px.png" "$dest/kubenimbus.png"
done

INSTALLED_SIZE_KB=$(du -sk "$DEB_ROOT/usr" | cut -f1)
# Depends: the seven X11-family libraries Avalonia's X11 backend dlopens (see
# CLAUDE.md, "The launch check") plus fontconfig and freetype, which Skia goes
# through for font lookup. Skia and HarfBuzz themselves are bundled side-car
# .so files, not system packages. The point of listing them is the .deb smoke
# test in release.yml: apt resolves this list on a runner, so a library the app
# loads and this file forgot fails there rather than on somebody's machine.
cat > "$DEB_ROOT/DEBIAN/control" <<CONTROL
Package: kubenimbus
Version: $DEB_VERSION
Section: admin
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $INSTALLED_SIZE_KB
Maintainer: Dmitrii Shmanev <shman4ik@gmail.com>
Homepage: https://github.com/Shman4ik/kubeNimbus
Depends: libx11-6, libice6, libsm6, libfontconfig1, libfreetype6, libxext6, libxi6, libxcursor1, libxrandr2
Description: Fast, open-source Kubernetes desktop client
 A Kubernetes client built with .NET and Avalonia and compiled to a NativeAOT
 binary for instant startup. Live resource lists over watch, pod logs, exec,
 port-forward, YAML apply with a server-side dry-run preview. No telemetry, and
 no credentials are ever copied out of your kubeconfig. MIT licensed.
CONTROL
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$OUT_DIR/$BASE_NAME.deb"
echo "Built $OUT_DIR/$BASE_NAME.deb"
