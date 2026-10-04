#!/usr/bin/env bash
# Builds a Release AirGlass.app and packs it into dist/AirGlass-<version>.dmg
# with an /Applications shortcut for drag-and-drop installation.
#
# Signing comes from Config/Local.xcconfig (your Personal Team). Without it
# the app is ad-hoc signed, which works but makes macOS forget the Screen
# Recording permission after every update.
set -euo pipefail

cd "$(dirname "$0")/.."

APP="AirGlass"
DERIVED="build/release"
APP_PATH="$DERIVED/Build/Products/Release/$APP.app"

command -v xcodegen >/dev/null || { echo "xcodegen not found: brew install xcodegen" >&2; exit 1; }

echo "==> Generating Xcode project"
xcodegen generate --quiet

echo "==> Building $APP (Release)"
xcodebuild \
  -project "$APP.xcodeproj" \
  -scheme "$APP" \
  -configuration Release \
  -derivedDataPath "$DERIVED" \
  -allowProvisioningUpdates \
  -quiet \
  build

echo "==> Checking the signature"
codesign --verify --deep --strict "$APP_PATH"
if codesign -dv "$APP_PATH" 2>&1 | grep -q "Signature=adhoc"; then
  echo "warning: $APP is ad-hoc signed. Set DEVELOPMENT_TEAM in Config/Local.xcconfig" >&2
  echo "         so users keep their Screen Recording permission across updates." >&2
fi

VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP_PATH/Contents/Info.plist")
DMG="dist/$APP-$VERSION.dmg"

STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT
ditto "$APP_PATH" "$STAGING/$APP.app"
ln -s /Applications "$STAGING/Applications"

echo "==> Creating $DMG"
mkdir -p dist
rm -f "$DMG"
hdiutil create -quiet -volname "$APP" -srcfolder "$STAGING" -fs HFS+ -format UDZO -ov "$DMG"

echo "$DMG"
