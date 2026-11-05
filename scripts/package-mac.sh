#!/usr/bin/env bash
# Builds "ScreenRec Trainer.app" + a .dmg (drag to Applications) + the screenrec CLI zip.
# Usage: scripts/package-mac.sh [osx-arm64|osx-x64]
set -euo pipefail
RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/$RID"
APP="$OUT/ScreenRec Trainer.app"
DMG="$OUT/ScreenRecTrainer-$RID.dmg"

rm -rf "$OUT" && mkdir -p "$APP/Contents/MacOS" "$OUT/cli"

dotnet publish "$ROOT/src/ScreenRec.App" -c Release -r "$RID" --self-contained -o "$APP/Contents/MacOS"
dotnet publish "$ROOT/src/ScreenRec.Cli" -c Release -r "$RID" --self-contained -o "$OUT/cli"
cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"

# Ad-hoc signature so macOS can grant Screen Recording / Input Monitoring to the app bundle itself.
codesign --force --deep --sign - "$APP" || echo "codesign failed (the app still runs; permissions may attach to the terminal instead)"

# Drag-to-Applications DMG: app + /Applications symlink in one window.
STAGING="$OUT/dmg"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
rm -f "$DMG"
hdiutil create -volname "ScreenRec Trainer" -srcfolder "$STAGING" -ov -format UDZO "$DMG" | tail -1
rm -rf "$STAGING"

(cd "$OUT" && ditto -c -k cli "screenrec-cli-$RID.zip")

echo "Built $DMG"
