# Install on Linux (systemd)

The recommended deployment. Two systemd units: one runs DAPPS itself; a second, privileged one applies in-place updates so the dashboard's "Apply update" button can swap the binary without any SSH dance.

## 1. Drop the binary

Pick the right binary for your architecture from the [latest release](https://github.com/M0LTE/dapps/releases/latest):

```bash
sudo mkdir -p /opt/dapps /var/lib/dapps
sudo curl -L \
  https://github.com/M0LTE/dapps/releases/latest/download/dapps-linux-x64 \
  -o /opt/dapps/dapps
sudo chmod +x /opt/dapps/dapps
```

Substitute `dapps-linux-arm64` (Pi 4/5, Apple Silicon Linux) or `dapps-linux-arm` (Pi Zero, Cubie) as needed.

## 2. Set up the runtime user

DAPPS runs as a non-privileged user. The updater service runs as root because it needs to swap a binary that's currently executing - but the daemon itself does not.

```bash
sudo useradd --system --home /var/lib/dapps --shell /usr/sbin/nologin dapps
sudo chown -R dapps:dapps /var/lib/dapps
```

## 3. Install the systemd units

There are two: `dapps.service` (the daemon) and `dapps-updater.service` plus `dapps-updater.timer` (the privileged updater). The repository ships both under `scripts/`; if you've cloned the repo, copy them in. If not, here's the contents:

### dapps.service

```ini
[Unit]
Description=DAPPS daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=dapps
Group=dapps
WorkingDirectory=/var/lib/dapps
ExecStart=/opt/dapps/dapps
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=DAPPS_CALLSIGN=N0CALL
Restart=on-failure
RestartSec=5s
# Exit code 78 = fatal config error - don't restart in a tight
# loop, leave it down so the journal message is actionable.
RestartPreventExitStatus=78

[Install]
WantedBy=multi-user.target
```

Edit the `DAPPS_CALLSIGN` line to your real callsign with SSID, e.g. `M0LTE-1`. DAPPS will refuse to start with the placeholder.

If you bind to a port below 1024 (e.g. you want the dashboard on `:80`), either run as root (not recommended) or grant the binary `CAP_NET_BIND_SERVICE`:

```bash
sudo setcap 'cap_net_bind_service=+ep' /opt/dapps/dapps
```

### dapps-updater.service + .timer

Privileged. Polls a marker file (`/var/lib/dapps/update-requested`) every 60 seconds; when present, runs `dapps --apply-update`, which downloads the latest release for this architecture, swaps `/opt/dapps/dapps`, restarts `dapps.service`, verifies the new daemon stays up for 60 seconds, and rolls back to `/opt/dapps/dapps.previous` on any failure.

```ini
# /etc/systemd/system/dapps-updater.service
[Unit]
Description=DAPPS supervised updater
After=network-online.target

[Service]
Type=oneshot
ExecStart=/opt/dapps/dapps --apply-update
```

```ini
# /etc/systemd/system/dapps-updater.timer
[Unit]
Description=Poll for DAPPS update requests

[Timer]
OnBootSec=2min
OnUnitActiveSec=1min
Unit=dapps-updater.service

[Install]
WantedBy=timers.target
```

The timer fires the service every minute; the service does nothing if no marker file exists, so the steady state is a no-op heartbeat.

## 4. Enable and start

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now dapps.service
sudo systemctl enable --now dapps-updater.timer
sudo systemctl status dapps.service
```

You should see `Active: active (running)` and a journal line `Now listening on: http://0.0.0.0:5000`.

```bash
sudo journalctl -u dapps.service -f
```

## 5. First-use setup

Open the dashboard in a browser at `http://<node>:5000/`. The first request lands on `/Setup` to set an admin password (cookie-based; one password for the whole node). After that, the dashboard.

The `/Health` and `/Operational` endpoints are intentionally not behind that cookie - they're designed to be scraped by watchdogs and your own monitoring. The MCP endpoint at `/mcp` is also open for the same reason.

## Operator customisations

If you tweak the unit file (e.g. binding the dashboard to localhost only, increasing log verbosity, adding env vars), use a **systemd drop-in** rather than editing the unit file in place - the dashboard's update flow won't overwrite drop-ins, but a future install recipe might overwrite the unit:

```bash
sudo systemctl edit dapps.service
```

That opens an editor on `/etc/systemd/system/dapps.service.d/override.conf`. Add only the bits you want to change:

```ini
[Service]
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=DAPPS_MQTT_PORT=1884
```

`systemctl daemon-reload && systemctl restart dapps.service` to pick up changes.

## Logs

DAPPS logs to stdout, which systemd captures into the journal:

```bash
# Tail the live stream
sudo journalctl -u dapps.service -f

# Last 24 hours
sudo journalctl -u dapps.service --since '24h ago'

# Search a single message id end-to-end
sudo journalctl -u dapps.service | grep abc1234
```

The journal also captures the structured decision-events that the `/Operational` endpoint surfaces - so an "what happened to message X two weeks ago" investigation is a `journalctl --grep` away.

## Backups

The state worth backing up is `/var/lib/dapps/dapps.db` - the SQLite database. The binary is recoverable from GitHub Releases; everything else is derived from defaults. Stop DAPPS before copying for a consistent snapshot, or use SQLite's online backup API.

```bash
sudo systemctl stop dapps.service
sudo cp /var/lib/dapps/dapps.db /backup/dapps.db
sudo systemctl start dapps.service
```

## Uninstall

```bash
sudo systemctl disable --now dapps.service dapps-updater.timer
sudo rm /etc/systemd/system/dapps.service /etc/systemd/system/dapps-updater.{service,timer}
sudo systemctl daemon-reload
sudo rm -rf /opt/dapps /var/lib/dapps
sudo userdel dapps
```
