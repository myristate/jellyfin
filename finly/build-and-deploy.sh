#!/usr/bin/env bash
# Build the Finly server image on the Unraid box.
#
# Publishes the server for linux-x64, takes the web client build from ../../jellyfin-web/dist,
# copies both to the Unraid host and builds finly-server:local there. It does not start or
# restart any container.
#
# Every build is also tagged finly-server:<commit>, the image it replaces is kept as
# finly-server:previous (roll back with: recreate-container.sh previous), and only the newest
# three commit tags are kept.
#
# Usage: finly/build-and-deploy.sh [ssh-host]   (default root@192.168.1.249)
set -euo pipefail

HOST="${1:-root@192.168.1.249}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(dirname "$HERE")"
WEB_DIST="$(dirname "$REPO")/jellyfin-web/dist"
REMOTE_DIR=/mnt/user/appdata/finly-build
TAG="finly-server:local"

REVISION="$(git -C "$REPO" describe --always --dirty --exclude '*')"
SHA_TAG="finly-server:$(git -C "$REPO" rev-parse --short HEAD)"

# Check the web client first, before the slow server publish
[ -f "$WEB_DIST/index.html" ] || { echo "Web client not built: run 'npm run build:production' in jellyfin-web" >&2; exit 1; }

echo "Publishing server ($REVISION)..."
rm -rf "$HERE/dist"
# Quiet, but compile errors still show
dotnet publish "$REPO/Jellyfin.Server/Jellyfin.Server.csproj" --configuration Release --self-contained \
    --runtime linux-x64 --output "$HERE/dist/server" -p:DebugSymbols=false -p:DebugType=none -v quiet -nologo

cp -r "$WEB_DIST" "$HERE/dist/web"
cp "$HERE/Dockerfile" "$HERE/dist/"

echo "Copying build to $HOST:$REMOTE_DIR..."
tar -C "$HERE/dist" -czf "$HERE/finly-build.tgz" .
ssh "$HOST" "rm -rf $REMOTE_DIR && mkdir -p $REMOTE_DIR"
scp -q "$HERE/finly-build.tgz" "$HOST:$REMOTE_DIR/"
rm -f "$HERE/finly-build.tgz"

echo "Building $TAG on $HOST..."
ssh "$HOST" "set -e
cd $REMOTE_DIR && tar -xzf finly-build.tgz && rm finly-build.tgz
# Keep the image this build replaces, for rolling back
if docker image inspect $TAG >/dev/null 2>&1; then docker tag $TAG finly-server:previous; fi
docker build -q -t $TAG -t $SHA_TAG --label finly.revision=$REVISION . >/dev/null
cd / && rm -rf $REMOTE_DIR
# Keep the newest three commit tags; untagged layers go with them
docker images finly-server --format '{{.CreatedAt}}|{{.Tag}}' | grep -v -e '|local$' -e '|previous$' | sort -r | tail -n +4 | cut -d'|' -f2 | while read -r t; do docker rmi finly-server:\$t >/dev/null 2>&1 || true; done
docker image prune -f >/dev/null"
echo "Built $TAG ($REVISION, also $SHA_TAG)"
