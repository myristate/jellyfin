#!/usr/bin/env bash
# Build the Finly server image on the Unraid box.
#
# Publishes the server for linux-x64, takes the web client build from ../../jellyfin-web/dist,
# copies both to the Unraid host and builds finly-server:local there. It does not start or
# restart any container.
#
# Usage: finly/build-and-deploy.sh [ssh-host]   (default root@192.168.1.249)
set -euo pipefail

HOST="${1:-root@192.168.1.249}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(dirname "$HERE")"
WEB_DIST="$(dirname "$REPO")/jellyfin-web/dist"
REMOTE_DIR=/mnt/user/appdata/finly-build
TAG="finly-server:local"

echo "Publishing server..."
rm -rf "$HERE/dist"
dotnet publish "$REPO/Jellyfin.Server/Jellyfin.Server.csproj" --configuration Release --self-contained \
    --runtime linux-x64 --output "$HERE/dist/server" -p:DebugSymbols=false -p:DebugType=none >/dev/null

[ -f "$WEB_DIST/index.html" ] || { echo "Web client not built: run 'npm run build:production' in jellyfin-web" >&2; exit 1; }
cp -r "$WEB_DIST" "$HERE/dist/web"
cp "$HERE/Dockerfile" "$HERE/dist/"

echo "Copying build to $HOST:$REMOTE_DIR..."
tar -C "$HERE/dist" -czf "$HERE/finly-build.tgz" .
ssh "$HOST" "rm -rf $REMOTE_DIR && mkdir -p $REMOTE_DIR"
scp -q "$HERE/finly-build.tgz" "$HOST:$REMOTE_DIR/"
rm -f "$HERE/finly-build.tgz"

echo "Building $TAG on $HOST..."
ssh "$HOST" "cd $REMOTE_DIR && tar -xzf finly-build.tgz && rm finly-build.tgz && docker build -q -t $TAG --label finly.revision=$(git -C "$REPO" rev-parse --short HEAD) . && rm -rf $REMOTE_DIR"
echo "Built $TAG"
