# Finly server image: hotio's Jellyfin image with the Finly server and web client swapped in.
# hotio supplies the s6 init, jellyfin-ffmpeg, NVIDIA support and the PUID/PGID/UMASK handling.
# Pinned to the hotio 12.1 image this fork is based on, so nightly auto-updates never change it.
ARG BASE_IMAGE=ghcr.io/hotio/jellyfin@sha256:a748831be2af7c8d2574622659702b7ff260f0b7171cc6d1d8e7128777a1b185
FROM ${BASE_IMAGE}

RUN rm -rf /usr/lib/jellyfin/bin /usr/share/jellyfin/web
COPY --chown=root:root --chmod=755 server/ /usr/lib/jellyfin/bin/
COPY --chown=hotio:users web/ /usr/share/jellyfin/web/

LABEL org.opencontainers.image.title="Finly Server" \
      org.opencontainers.image.description="A family fork of the Jellyfin media server" \
      org.opencontainers.image.source="https://github.com/myristate/jellyfin" \
      org.opencontainers.image.licenses="GPL-2.0-only"
