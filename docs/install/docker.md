# Install with Docker

A published image is available on Docker Hub. Useful if you already orchestrate with compose, run a homelab stack, or just don't want to manage another systemd unit. The image runs the same binary as the bare-metal install - same configuration story, same dashboard.

## Image

```
m0lte/dapps:latest
```

Tagged versions (`m0lte/dapps:v0.24.0`) match the GitHub Release tags. `latest` is the most recent published release.

## Quick start

```bash
docker run -d \
  --name dapps \
  -p 5000:5000 \
  -p 1883:1883 \
  -v dapps-data:/var/lib/dapps \
  -e DAPPS_CALLSIGN=M0LTE-1 \
  -e DAPPS_NODE_HOST=192.168.1.10 \
  -e DAPPS_AGW_PORT=8000 \
  m0lte/dapps:latest
```

Then `http://localhost:5000/` for the dashboard. First request goes to `/Setup` to set an admin password.

## docker compose

For a more permanent setup:

```yaml
services:
  dapps:
    image: m0lte/dapps:latest
    container_name: dapps
    restart: unless-stopped
    ports:
      - "5000:5000"   # dashboard / REST / MCP
      - "1883:1883"   # MQTT broker
    volumes:
      - dapps-data:/var/lib/dapps
    environment:
      DAPPS_CALLSIGN: M0LTE-1
      DAPPS_NODE_HOST: 192.168.1.10   # your packet node's IP
      DAPPS_AGW_PORT: 8000

volumes:
  dapps-data:
```

`docker compose up -d`.

## Connecting to a packet node on the host

If the node (BPQ, etc.) runs on the Docker host itself, the daemon needs to reach it across the container boundary:

- **Linux**: use the host's LAN IP (`hostname -I`), or run with `--network host` to share the host network namespace.
- **Docker Desktop on macOS / Windows**: `host.docker.internal` resolves to the host.

Set `DAPPS_NODE_HOST` accordingly.

## Updates

The supervised in-place update path (the `/Update/apply` button on the dashboard, the `trigger_update` MCP tool) does not work inside the standard image - the privileged updater wants to swap the binary on disk and restart its own service, which Docker doesn't model the same way.

Instead, update by pulling a new image tag and restarting the container:

```bash
docker compose pull
docker compose up -d
```

The dashboard's update banner still surfaces "v0.X.Y available" so you know when to do this.

## Persistence

Everything DAPPS needs to remember between restarts is in the SQLite database at `/var/lib/dapps/dapps.db` inside the container. Mount that path to a named volume (or a host bind mount) and you can recreate the container freely without losing state.

## Logs

```bash
docker logs -f dapps
```

Same content as the bare-metal install's stdout - start-up events, decision events, errors.
