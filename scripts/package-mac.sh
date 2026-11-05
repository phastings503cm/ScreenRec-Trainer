#!/usr/bin/env bash
# Builds "ScreenRec Trainer.app" (arm64 or x64) and a zip, ad-hoc signed so macOS can grant Screen Recording and
# Input Monitoring to the app itself. Usage: scripts/package-mac.sh [osx-arm64|osx-x64]
set -euo pipefail
RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/$RID"
APP="$OUT/ScreenRec Trainer.app"
rm -rf "$OUT" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
dotnet publish "$ROOT/src/ScreenRec.App" -c Release -r "$RID" --self-contained -o "$APP/Contents/MacOS"
dotnet publish "$ROOT/src/ScreenRec.Cli" -c Release -r "$RID" --self-contained -o "$OUT/cli"
cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"
codesign --force --deep --sign - "$APP" || echo "codesign failed (the app still runs; permissions may attach to Terminal instead)"
(cd "$OUT" && ditto -c -k --keepParent "ScreenRec Trainer.app" "ScreenRecTrainer-$RID.zip")
echo "Built $APP"
