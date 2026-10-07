#!/usr/bin/env bash
# Packages a NativeAOT `dotnet publish` output into the two Linux installer
# formats: an .AppImage (run it anywhere, no install) and a .deb (apt/dpkg,
# with a desktop entry and icons). The plain .tar.gz is not built here — the
# release workflow already stages one with LICENSE/README/CHANGELOG beside the
# binary, and duplicating it would give two tarballs of the same bytes.
#
# Linux-only: dpkg-deb ships with any Debian-family distro and runner image,
# and appimagetool is downloaded on demand and run with
# --appimage-extract-and-run because GitHub's runners have no FUSE. Both
# downloads (appimagetool and the AppImage runtime it embeds) are pinned by
# version and SHA-256; see "Pinned tools" below.
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

# ---- Pinned tools -----------------------------------------------------------
# This script runs on a release runner before the Checksum step, so anything it
# downloads and executes could rewrite every package of that runner and have
# SHA256SUMS.txt vouch for the result. Nothing is therefore fetched from a
# moving URL or run unchecked: each download names a tagged release and is
# compared with the SHA-256 written here before it is made executable or used.
#
# Two downloads, not one. appimagetool 1.9.x fetches the AppImage *runtime* (the
# ELF stub a user's machine executes first when the AppImage is started) from
# type2-runtime's `continuous` release at build time unless --runtime-file names
# one, so pinning the tool alone would still embed whatever that release held
# on the day.
#
# Updating a pin:
#   1. Pick a tagged release (never `continuous`) of AppImage/appimagetool or
#      AppImage/type2-runtime.
#   2. Download each architecture's file and run `sha256sum` on it.
#   3. Cross-check against the digest GitHub recorded for the asset:
#        gh api repos/AppImage/<repo>/releases/tags/<tag> \
#          --jq '.assets[] | "\(.name) \(.digest)"'
#      and, for the runtime, against its detached signature:
#        gpg --import signing-pubkey.asc   # the copy in the type2-runtime repo
#        gpg --verify runtime-<arch>.sig runtime-<arch>
#   4. Replace the tag and both hashes below in one commit, then run a release
#      dry run (CONTRIBUTING.md), whose Linux legs build and launch the AppImage.
APPIMAGETOOL_TAG="1.9.1"
APPIMAGETOOL_SHA256_X86_64="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGETOOL_SHA256_AARCH64="f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158"
RUNTIME_TAG="20251108"
RUNTIME_SHA256_X86_64="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"
RUNTIME_SHA256_AARCH64="00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444"

case "$RID" in
  linux-x64)
    DEB_ARCH="amd64"; APPIMAGE_ARCH="x86_64"
    APPIMAGETOOL_SHA256="$APPIMAGETOOL_SHA256_X86_64"
    RUNTIME_SHA256="$RUNTIME_SHA256_X86_64" ;;
  linux-arm64)
    DEB_ARCH="arm64"; APPIMAGE_ARCH="aarch64"
    APPIMAGETOOL_SHA256="$APPIMAGETOOL_SHA256_AARCH64"
    RUNTIME_SHA256="$RUNTIME_SHA256_AARCH64" ;;
  *) echo "Unknown RID: $RID (expected linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

# Downloads <url> to <dest> and fails unless the file's SHA-256 is <sha256>. On
# a mismatch the file is deleted, so nothing later can pick it up by accident.
fetch_verified() { # <url> <sha256> <dest>
  local url="$1" expected="$2" dest="$3" actual
  curl -fsSL --retry 3 -o "$dest" "$url"
  if printf '%s  %s\n' "$expected" "$dest" | sha256sum -c --quiet - >/dev/null 2>&1; then
    echo "Verified $(basename "$dest") against its pinned SHA-256 ($expected)"
    return 0
  fi
  actual="$(sha256sum "$dest" | cut -d' ' -f1)"
  rm -f "$dest"
  echo "::error::SHA-256 mismatch for $url" >&2
  echo "  expected $expected" >&2
  echo "  actual   $actual" >&2
  echo "The pinned file has changed upstream, or the download was tampered with." >&2
  echo "Do not update the hash before finding out which ('Updating a pin' in $0)." >&2
  exit 1
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

# Fetched first, so a pin that no longer matches fails before any packaging.
APPIMAGETOOL="$WORK_DIR/appimagetool"
APPIMAGE_RUNTIME="$WORK_DIR/runtime-$APPIMAGE_ARCH"
fetch_verified \
  "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_TAG/appimagetool-$APPIMAGE_ARCH.AppImage" \
  "$APPIMAGETOOL_SHA256" "$APPIMAGETOOL"
fetch_verified \
  "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_TAG/runtime-$APPIMAGE_ARCH" \
  "$RUNTIME_SHA256" "$APPIMAGE_RUNTIME"
chmod +x "$APPIMAGETOOL"

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

ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" --appimage-extract-and-run \
  --runtime-file "$APPIMAGE_RUNTIME" \
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
