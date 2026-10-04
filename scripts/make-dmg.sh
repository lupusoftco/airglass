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

# Make sure the app and everything embedded in it carry one signature, so
# library validation never sees a framework signed differently from the
# app (a mismatch makes dyld refuse it: "different Team IDs"). Re-sign
# inside out with the identity and options the app was built with.
echo "==> Re-signing embedded frameworks and the app"
SIGNATURE=$(codesign -dvv "$APP_PATH" 2>&1)
if grep -q "Signature=adhoc" <<<"$SIGNATURE"; then
  IDENTITY="-"
  echo "warning: $APP is ad-hoc signed. Set DEVELOPMENT_TEAM in Config/Local.xcconfig" >&2
  echo "         so users keep their Screen Recording permission across updates." >&2
else
  AUTHORITY=$(sed -n 's/^Authority=//p' <<<"$SIGNATURE" | head -n 1)
  # Prefer the certificate's hash: names can be ambiguous in the keychain.
  IDENTITY=$(security find-identity -v -p codesigning | grep -F "\"$AUTHORITY\"" | awk '{print $2}' | head -n 1)
  IDENTITY=${IDENTITY:-$AUTHORITY}
fi
RUNTIME_FLAG=""
grep -Eq "flags=.*runtime" <<<"$SIGNATURE" && RUNTIME_FLAG="--options=runtime"

find "$APP_PATH/Contents/Frameworks" -depth \( -name "*.framework" -o -name "*.dylib" \) -print0 2>/dev/null |
  while IFS= read -r -d '' code; do
    codesign --force --timestamp=none --sign "$IDENTITY" $RUNTIME_FLAG "$code"
  done
codesign --force --timestamp=none --sign "$IDENTITY" \
  --preserve-metadata=identifier,entitlements,flags "$APP_PATH"

echo "==> Checking the signature"
codesign --verify --deep --strict "$APP_PATH"

VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP_PATH/Contents/Info.plist")
DMG="dist/$APP-$VERSION.dmg"

STAGING=$(mktemp -d)
MOUNT=$(mktemp -d)
cleanup() {
  hdiutil detach -quiet "$MOUNT" 2>/dev/null || true
  rm -rf "$STAGING" "$MOUNT"
}
trap cleanup EXIT
ditto "$APP_PATH" "$STAGING/$APP.app"
ln -s /Applications "$STAGING/Applications"

echo "==> Creating $DMG"
mkdir -p dist
rm -f "$DMG"
hdiutil create -quiet -volname "$APP" -srcfolder "$STAGING" -fs HFS+ -format UDZO -ov "$DMG"

# Test exactly what users get: the app as it sits inside the DMG.
echo "==> Smoke-testing the app inside $DMG"
hdiutil attach -quiet -nobrowse -readonly -mountpoint "$MOUNT" "$DMG"
if ! scripts/smoke-test.sh "$MOUNT/$APP.app"; then
  rm -f "$DMG"
  exit 1
fi

echo "$DMG"
