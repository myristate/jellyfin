#!/usr/bin/env bash
# Recreate the finly container on the Unraid host from the current finly-server:local image, with the same
# settings as my-finly.xml. Run on the Unraid host (or pipe over ssh). Only touches the finly container.
#
# Roll back to the image before the last build with: recreate-container.sh previous
set -euo pipefail

IMAGE="finly-server:${1:-local}"
# Transcodes and Live TV buffers are temporary: keep them on the cache SSD, outside appdata so they're
# never backed up (Finly)
TRANSCODE=/mnt/cache/finly-transcode

# Don't remove the running container unless the image to replace it with exists
docker image inspect "$IMAGE" >/dev/null 2>&1 || { echo "No image $IMAGE, leaving finly as it is" >&2; exit 1; }

mkdir -p "$TRANSCODE"
chown nobody:users "$TRANSCODE"

docker rm -f finly >/dev/null 2>&1 || true
docker run -d --name=finly --net=bridge --pids-limit 2048 --log-opt max-size=50m --log-opt max-file=1 \
	-e TZ="Europe/London" -e HOST_OS="Unraid" -e HOST_HOSTNAME="TheBeast" -e HOST_CONTAINERNAME="finly" \
	-e NVIDIA_VISIBLE_DEVICES="GPU-303f9dba-550b-c8c6-6c97-969dd2150bb6" -e NVIDIA_DRIVER_CAPABILITIES="all" \
	-e PUID=99 -e PGID=100 -e UMASK=002 \
	-l net.unraid.docker.managed=dockerman -l "net.unraid.docker.webui=http://[IP]:[PORT:8096]" \
	-l "net.unraid.docker.icon=https://raw.githubusercontent.com/myristate/jellyfin/custom-12.1/finly/unraid/finly-icon.png" \
	-p 8097:8096/tcp \
	-v /mnt/user/appdata/finly:/config:rw \
	-v /mnt/user/media/tv/:/tv:ro -v /mnt/user/media/movies/:/movies:ro \
	-v "$TRANSCODE":/transcode:rw \
	--runtime=nvidia "$IMAGE" >/dev/null

# The old transcode folder inside appdata is no longer used
rm -rf /mnt/user/appdata/finly/transcode

for _ in $(seq 1 60); do
	if docker logs finly 2>&1 | grep -q "Startup complete"; then
		echo "finly started: $(docker logs finly 2>&1 | grep -m1 'Startup complete')"
		exit 0
	fi
	sleep 2
done
echo "finly did not report startup within 2 minutes" >&2
exit 1
