#!/usr/bin/env bash
# Packages a NativeAOT `dotnet publish` output into an ad-hoc signed
# kubeNimbus.app bundle and wraps it in a drag-to-Applications .dmg.
#
# macOS-only: sips, iconutil, hdiutil and codesign are all stock tools, present
# on GitHub's macos-* runner images with nothing to install.
#
# Usage: build-app-bundle.sh <publish-dir> <version> <rid> <out-dir>
#   publish-dir  output of `dotnet publish -r <rid> ...`
#   version      e.g. 1.2.3 (no leading v)
#   rid          osx-arm64 | osx-x64
#   out-dir      where to write the .dmg
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
OUT_DIR="$4"

case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "Unknown RID: $RID (expected osx-arm64 or osx-x64)" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="$(mktemp -d)"
# The .dmg's volume root rather than just the bundle: it also carries the
# drag-to-install /Applications symlink beside the app (see below).
DMG_ROOT="$WORK_DIR/dmg"
APP_DIR="$DMG_ROOT/kubeNimbus.app"

mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

sed "s/__VERSION__/$VERSION/g" "$REPO_ROOT/installer/macos/Info.plist.template" \
  > "$APP_DIR/Contents/Info.plist"

# App icon: build a .iconset from the prepared masters and compile it to .icns.
# Each slot is filled from the hand-drawn master at that exact pixel size when
# one exists (design/masters/icon/icon-<px>.png) and downscaled from the 1024
# master otherwise, so the small sizes stay crisp instead of being resampled
# from one mid-size PNG. Apple applies its own rounded-rect mask, so the
# masters are square and full-bleed with no pre-rounding.
ICONSET_DIR="$WORK_DIR/app.iconset"
mkdir -p "$ICONSET_DIR"
MASTER_DIR="$REPO_ROOT/design/masters/icon"
ICON_1024="$MASTER_DIR/icon-1024.png"

emit_icon() {  # <pixels> <dest-filename>
  local px="$1" dest="$2" exact="$MASTER_DIR/icon-$1.png"
  if [ -f "$exact" ]; then
    cp "$exact" "$ICONSET_DIR/$dest"
  else
    sips -z "$px" "$px" "$ICON_1024" --out "$ICONSET_DIR/$dest" >/dev/null
  fi
}
for size in 16 32 64 128 256 512; do
  emit_icon "$size" "icon_${size}x${size}.png"
  emit_icon "$((size * 2))" "icon_${size}x${size}@2x.png"
done
iconutil -c icns "$ICONSET_DIR" -o "$APP_DIR/Contents/Resources/app.icns"

# Publish output -> Contents/MacOS, minus the debug symbols. A .dsym is itself
# a bundle directory, which is the one shape `codesign --deep` below refuses to
# seal inside Contents/MacOS — and nobody debugging a release build is doing it
# from the .dmg anyway.
cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
rm -rf "$APP_DIR"/Contents/MacOS/*.dsym "$APP_DIR"/Contents/MacOS/*.dSYM
chmod +x "$APP_DIR/Contents/MacOS/kubeNimbus"

# Ad-hoc code signature (`--sign -`), signed inside out: nested Mach-O objects
# first, then the bundle, which seals everything else into
# _CodeSignature/CodeResources.
#
# This is not about trusting the build, it is about which Gatekeeper dialog a
# user gets. A quarantined bundle carrying NO signature is reported as
# "kubeNimbus is damaged and can't be opened. You should eject the disk image",
# which reads as a corrupt download, sends people back to Releases to fetch the
# same file again, and cannot be cleared by right-click -> Open. An ad-hoc
# signature fails the same Gatekeeper check but fails it as "Apple cannot check
# it for malicious software", which is both true and clearable the normal way
# (right-click -> Open, or System Settings -> Privacy & Security -> Open
# Anyway). Ad-hoc signing is also what makes an arm64 binary loadable at all:
# Apple Silicon refuses to execute unsigned code.
#
# It is not a substitute for a Developer ID signature plus notarization, which
# would remove the warning outright and needs a paid Apple account.
while IFS= read -r lib; do
  codesign --force --timestamp=none --sign - "$lib"
done < <(find "$APP_DIR/Contents/MacOS" -type f -name '*.dylib')

codesign --force --deep --timestamp=none --sign - "$APP_DIR"
codesign --verify --deep --strict --verbose=2 "$APP_DIR"

# Drag-to-Applications. Without the symlink the .dmg holds the app alone, so
# the obvious gesture is to double-click it where it sits — which runs it from
# a read-only disk image, and leaves an app that is gone at the next launch.
ln -s /Applications "$DMG_ROOT/Applications"

mkdir -p "$OUT_DIR"
DMG_NAME="kubeNimbus-$VERSION-$RID.dmg"
hdiutil create -volname "kubeNimbus $VERSION" \
  -srcfolder "$DMG_ROOT" \
  -ov -format UDZO \
  "$OUT_DIR/$DMG_NAME"

echo "Built $OUT_DIR/$DMG_NAME"
