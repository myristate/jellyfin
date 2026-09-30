# Finly

Finly is a family fork of Jellyfin 12.1. It runs alongside the official Jellyfin server on the same Unraid box and shares its media folders read-only.

## Changes from Jellyfin 12.1

- Live TV programmes follow their channel's parental tags, so profiles limited to tags see a TV guide.
- Tasks that run every N hours keep their schedule when the server restarts, for example after a nightly backup.
- Probing a stream that reports no frame size no longer crashes. Some Freeview SD channels do this.
- The server, web client and TV app are branded Finly.

## Layout

- `branding/`: the SVG logo and `render.py`, which renders every logo size.
- `Dockerfile`: builds on the pinned hotio Jellyfin image and swaps in the Finly server and web client.
- `build-and-deploy.sh`: publishes the server, copies it and the web build to Unraid, and builds `finly-server:local` there. It doesn't restart anything.
- `unraid/my-finly.xml`: the Unraid template.
  - Port 8097.
  - `/mnt/user/appdata/finly` for settings.
  - Read-only media folders.
  - A private tmpfs for transcodes.

## Rebuild and update

1. In `jellyfin-web`, build the web client: `npm run build:production`.
2. Build the image: `finly/build-and-deploy.sh`.
3. Restart the `finly` container from the Unraid Docker tab.

Finly is licensed under GPL-2.0, like Jellyfin. The source is at https://github.com/myristate/jellyfin (branch `custom-12.1`) and https://github.com/myristate/jellyfin-web.
