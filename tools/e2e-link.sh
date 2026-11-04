#!/usr/bin/env bash
# End to end: the reference export server publishes samples/event-fixture, and the Unity link
# downloader (tests/Zimension.Core.Tests --fetch) resolves, downloads, verifies and re-uses it.
set -euo pipefail
cd "$(dirname "$0")/.."
PORT=$((19000 + RANDOM % 1000))
DATA=$(mktemp -d)
node tools/dev-export-server.mjs --port $PORT --data "$DATA" --publish samples/event-fixture --world event-fixture > "$DATA/server.log" &
SERVER=$!
trap 'kill $SERVER 2>/dev/null; rm -rf "$DATA"' EXIT
for i in $(seq 1 50); do grep -q "dev server on" "$DATA/server.log" && break; sleep 0.2; done
CODE=$(sed -n 's/.*Code: //p' "$DATA/server.log")
echo "Code-only fetch:"
dotnet run --project tests/Zimension.Core.Tests -- --fetch "$CODE" --api "http://localhost:$PORT/api/v1"
echo "Full-link fetch:"
dotnet run --no-build --project tests/Zimension.Core.Tests -- --fetch "http://localhost:$PORT/api/v1/world/unity-export/link/$CODE"
