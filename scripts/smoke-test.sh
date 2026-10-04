#!/usr/bin/env bash
# Launches an app's main binary for a few seconds to prove it actually
# starts. Catches dyld and code-signing failures (such as an embedded
# framework failing library validation) before anything is published.
#
# usage: scripts/smoke-test.sh path/to/AirGlass.app [seconds]
set -euo pipefail

APP="${1:?usage: smoke-test.sh path/to/App.app [seconds]}"
SECONDS_ALIVE="${2:-5}"

EXECUTABLE=$(/usr/libexec/PlistBuddy -c "Print CFBundleExecutable" "$APP/Contents/Info.plist")
OUTPUT=$(mktemp)
trap 'rm -f "$OUTPUT"' EXIT

"$APP/Contents/MacOS/$EXECUTABLE" >"$OUTPUT" 2>&1 &
PID=$!
sleep "$SECONDS_ALIVE"

if ! kill -0 "$PID" 2>/dev/null; then
  status=0
  wait "$PID" || status=$?
  echo "smoke test FAILED: $EXECUTABLE exited within ${SECONDS_ALIVE}s (status $status)" >&2
  cat "$OUTPUT" >&2
  exit 1
fi

kill "$PID"
wait "$PID" 2>/dev/null || true

if grep -E "dyld|Library not loaded|code signature" "$OUTPUT" >&2; then
  echo "smoke test FAILED: dyld or code-signing messages in the output" >&2
  exit 1
fi
echo "smoke test passed: $EXECUTABLE stayed up for ${SECONDS_ALIVE}s"
