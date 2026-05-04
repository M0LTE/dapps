#!/usr/bin/env bash
#
# DAPPS one-liner installer.
#
#   curl -sSL https://m0lte.github.io/dapps/install.sh | sudo bash
#
# Detects arch, downloads the latest binary from the GitHub Release,
# creates a system user, drops the systemd units, enables and starts.
# Configuration (callsign, bearer, ports, ...) lives in the dashboard's
# /Setup wizard - this script sets no env vars and leaves no callsign-
# specific state behind.
#
# Idempotent: re-running upgrades the binary in place. (Operators with
# the dashboard up should usually use the in-app "Apply update" button
# instead, which goes through the supervised dapps-updater.)
#
# Linux + systemd only. macOS, Windows, and non-systemd Linux distros
# can install manually - see https://m0lte.github.io/dapps/install/

set -euo pipefail

# ── config ─────────────────────────────────────────────────────────────
PREFIX="${PREFIX:-/opt/dapps}"
STATE_DIR="${STATE_DIR:-/var/lib/dapps}"
SERVICE_USER="${SERVICE_USER:-dapps}"
HTTP_BIND="${HTTP_BIND:-0.0.0.0:5000}"
RELEASE_BASE="https://github.com/M0LTE/dapps/releases/latest/download"

# ── helpers ────────────────────────────────────────────────────────────
say()  { printf '\033[1;36m::\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m!!\033[0m %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ ${EUID:-$(id -u)} -eq 0 ]] || die "This installer needs root. Re-run with sudo, e.g. 'curl -sSL https://m0lte.github.io/dapps/install.sh | sudo bash'."
}

require_systemd() {
    command -v systemctl >/dev/null 2>&1 || die "systemd not found. This installer is Linux+systemd only. See https://m0lte.github.io/dapps/install/ for manual install on other platforms."
    [[ -d /run/systemd/system ]] || die "systemd not running. Are you in a container or chroot?"
}

detect_rid() {
    local arch
    arch=$(uname -m)
    case "$arch" in
        x86_64|amd64) echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        armv7l|armv7|armhf) echo "linux-arm" ;;
        *) die "Unsupported architecture: $arch. Build from source or open an issue at https://github.com/M0LTE/dapps/issues." ;;
    esac
}

ensure_user() {
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then
        say "User $SERVICE_USER already exists; reusing."
    else
        say "Creating system user $SERVICE_USER (home=$STATE_DIR, no shell)."
        useradd --system --home "$STATE_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
}

ensure_dirs() {
    mkdir -p "$PREFIX" "$STATE_DIR"
    chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_DIR"
}

download_binary() {
    local rid="$1"
    local url="$RELEASE_BASE/dapps-$rid"
    local target="$PREFIX/dapps"

    say "Downloading $url"
    if [[ -x "$target" ]]; then
        # Re-running: keep a sidecar copy of the previous binary so an
        # operator who realises the new release doesn't work for them
        # has a one-step rollback to the predecessor. This is the same
        # naming the dapps-updater uses.
        cp -f "$target" "$target.previous"
    fi

    # curl with -fsSL: fail on 4xx/5xx, silent, follow redirects.
    if ! curl -fsSL "$url" -o "$target.tmp"; then
        die "Download failed. Check network connectivity and the release page at https://github.com/M0LTE/dapps/releases/latest"
    fi

    chmod +x "$target.tmp"
    mv "$target.tmp" "$target"
}

write_unit() {
    local target="/etc/systemd/system/dapps.service"
    say "Writing $target"
    cat > "$target" <<EOF
[Unit]
Description=DAPPS daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$STATE_DIR
ExecStart=$PREFIX/dapps
Environment=ASPNETCORE_URLS=http://$HTTP_BIND
Restart=on-failure
RestartSec=5s
# Exit code 78 = fatal config error; don't restart in a tight loop,
# leave it down so the journal message is actionable.
RestartPreventExitStatus=78

[Install]
WantedBy=multi-user.target
EOF
}

write_updater_units() {
    local svc="/etc/systemd/system/dapps-updater.service"
    local timer="/etc/systemd/system/dapps-updater.timer"
    say "Writing $svc"
    cat > "$svc" <<EOF
[Unit]
Description=DAPPS supervised updater
After=network-online.target

[Service]
Type=oneshot
ExecStart=$PREFIX/dapps --apply-update
EOF
    say "Writing $timer"
    cat > "$timer" <<EOF
[Unit]
Description=Poll for DAPPS update requests

[Timer]
OnBootSec=2min
OnUnitActiveSec=1min
Unit=dapps-updater.service

[Install]
WantedBy=timers.target
EOF
}

start_service() {
    say "systemctl daemon-reload"
    systemctl daemon-reload
    say "Enabling and starting dapps.service"
    systemctl enable --now dapps.service
    say "Enabling and starting dapps-updater.timer"
    systemctl enable --now dapps-updater.timer
}

print_next_steps() {
    local host
    host=$(hostname -f 2>/dev/null || hostname)
    local port="${HTTP_BIND##*:}"
    cat <<EOF

╭───────────────────────────────────────────────────────────────╮
│  DAPPS is running.                                            │
│                                                               │
│    Open  http://$host:$port/  in a browser.                   │
│                                                               │
│  The first request lands on /Setup - a two-step wizard that   │
│  asks for an admin password, then your callsign and which     │
│  packet-node bearer (BPQ AGW or XRouter RHPv2) to use. The    │
│  daemon picks up your config without a restart.               │
│                                                               │
│  Logs:    journalctl -u dapps.service -f                      │
│  Update:  click "Apply update" on the dashboard, or wait for  │
│           the dapps-updater.timer to pick up future releases. │
╰───────────────────────────────────────────────────────────────╯
EOF
}

# ── main ───────────────────────────────────────────────────────────────
require_root
require_systemd

rid=$(detect_rid)
say "Architecture: $rid"

ensure_user
ensure_dirs
download_binary "$rid"
write_unit
write_updater_units
start_service
print_next_steps
