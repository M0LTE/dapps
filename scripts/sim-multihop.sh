#!/usr/bin/env bash
#
# Local 3-node multi-hop simulator. Spins up three dapps.core instances on
# loopback — A and C in disjoint UDP-multicast "broadcast domains," with B
# straddling both as a relay — and drives an A→B→C send so you can watch
# F1 source-tracking propagate end-to-end without RF.
#
# Topology
#   G0SIA-1  ─ udp:239.0.7.1:54321 ─  G0SIB-1  ─ udp:239.0.7.2:54321 ─  G0SIC-1
#                                       │
#                                       └── reachable on both groups → relay
#
# Routing today is route-hint driven (no Phase B5 learned routing yet);
# the multicast channels exist so each node's discovered-peers table
# gets populated and you can see the discovery side working alongside.
# Once B5 lands, this script can drop the explicit route-hints and rely
# on flooded routes instead.
#
# Usage:
#   scripts/sim-multihop.sh           # build, start, configure, send-test, tail
#   scripts/sim-multihop.sh stop      # tear down
#   scripts/sim-multihop.sh send      # send another A→C message
#   scripts/sim-multihop.sh status    # ports, PIDs, queue counts
#
# Requires: dotnet 8 SDK, curl, python3 (with stdlib sqlite3 — present
# on every distro python).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="${SIM_DIR:-/tmp/dapps-sim}"
BIN_DIR="$SIM_DIR/bin"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SIM_PWD="${SIM_PWD:-simpassw0rd}"

# Per-node port + listen-port allocations. Loopback only — these MUST
# NOT overlap with anything the host actually uses.
declare -A HTTP_PORT=( [A]=15001 [B]=15002 [C]=15003 )
declare -A MQTT_PORT=( [A]=11881 [B]=11882 [C]=11883 )
declare -A UDP_PORT=(  [A]=50071 [B]=50072 [C]=50073 )
declare -A AGW_PORT=(  [A]=18001 [B]=18002 [C]=18003 )   # closed; AGW probe will fail, harmless
declare -A CALLSIGN=(  [A]=G0SIA-1 [B]=G0SIB-1 [C]=G0SIC-1 )

# Multicast groups: A subscribes to G1 only, C to G2 only, B to both.
G1="239.0.7.1:54321"
G2="239.0.7.2:54321"
declare -A CHANNELS=(
    [A]="$G1"
    [B]="$G1 $G2"
    [C]="$G2"
)

# Manual neighbour wiring. Each entry is "callsign,udpEndpoint" pairs.
# A knows B; B knows A and C; C knows B.
declare -A NEIGHBOURS=(
    [A]="G0SIB-1,127.0.0.1:${UDP_PORT[B]}"
    [B]="G0SIA-1,127.0.0.1:${UDP_PORT[A]} G0SIC-1,127.0.0.1:${UDP_PORT[C]}"
    [C]="G0SIB-1,127.0.0.1:${UDP_PORT[B]}"
)

# Route hints: pre-B5 force-the-relay. Each entry is "destBaseCall,nextHopCall".
# Destination is the BASE callsign (no SSID) — that's what the route
# resolver uses as the lookup key.
declare -A ROUTE_HINTS=(
    [A]="G0SIC,G0SIB-1"
    [B]=""
    [C]="G0SIA,G0SIB-1"
)

mkdir -p "$SIM_DIR" "$BIN_DIR"

# ── one-time publish ───────────────────────────────────────────────────
maybe_publish() {
    local stamp="$BIN_DIR/.built"
    local src_changed=0
    if [ ! -f "$stamp" ]; then src_changed=1; fi
    if [ -f "$stamp" ] && \
       find "$REPO/src/dapps" -name '*.cs' -newer "$stamp" -print -quit | grep -q .; then
        src_changed=1
    fi
    if [ "$src_changed" -eq 1 ]; then
        echo ">>> Publishing dapps.core into $BIN_DIR"
        rm -rf "$BIN_DIR/app"
        dotnet publish "$PROJ" -c Release -o "$BIN_DIR/app" --nologo --verbosity quiet >/dev/null
        touch "$stamp"
    fi
}

# ── per-node start/stop ────────────────────────────────────────────────
node_dir() { echo "$SIM_DIR/${1,,}"; }

start_node() {
    local n=$1
    local d="$(node_dir "$n")"
    mkdir -p "$d/data"
    rm -f "$d/data/dapps.db" "$d/dapps.log"

    echo ">>> Starting $n (${CALLSIGN[$n]}) http=${HTTP_PORT[$n]} udp=${UDP_PORT[$n]} mqtt=${MQTT_PORT[$n]}"
    (
        cd "$d"
        env \
            DAPPS_CALLSIGN="${CALLSIGN[$n]}" \
            DAPPS_NODE_HOST=127.0.0.1 \
            DAPPS_AGW_PORT="${AGW_PORT[$n]}" \
            DAPPS_DEFAULT_BPQ_PORT=0 \
            DAPPS_MQTT_PORT="${MQTT_PORT[$n]}" \
            DAPPS_UDP_LISTEN_PORT="${UDP_PORT[$n]}" \
            DAPPS_AUTH_REQUIRED=false \
            DAPPS_UPDATE_CHECK_ENABLED=false \
            ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT[$n]}" \
            DOTNET_ENVIRONMENT=Production \
            "$BIN_DIR/app/dapps.core" >"$d/dapps.log" 2>&1 &
        echo $! >"$d/pid"
    )

    # Wait for HTTP up.
    for _ in $(seq 1 60); do
        if curl -fs "http://127.0.0.1:${HTTP_PORT[$n]}/Setup" -o /dev/null 2>/dev/null; then
            return 0
        fi
        sleep 0.5
    done
    echo "!!! $n did not come up; tail of log:"
    tail -40 "$d/dapps.log" || true
    return 1
}

stop_node() {
    local n=$1
    local pid_file="$(node_dir "$n")/pid"
    [ -f "$pid_file" ] || return 0
    local pid; pid=$(cat "$pid_file")
    if kill -0 "$pid" 2>/dev/null; then
        echo ">>> Stopping $n (pid $pid)"
        kill "$pid" 2>/dev/null || true
        for _ in $(seq 1 20); do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.2
        done
        kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$pid_file"
}

# ── auth / configure ───────────────────────────────────────────────────
configure_node() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie="$(node_dir "$n")/cookie.txt"
    rm -f "$cookie"

    echo ">>> Configuring $n"
    # First-use: set the admin password.
    curl -fsS -o /dev/null -X POST "$base/Setup" \
        --data-urlencode "Password=$SIM_PWD" \
        --data-urlencode "Confirm=$SIM_PWD"
    # Login → cookie.
    curl -fsS -o /dev/null -c "$cookie" -X POST "$base/Login" \
        --data-urlencode "Password=$SIM_PWD"

    # Discovery channels.
    for ch in ${CHANNELS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/DiscoveryChannels" \
            -H 'content-type: application/json' \
            -d "{\"Bearer\":\"udp\",\"ChannelKey\":\"$ch\",\"LinkClass\":\"LanMulticast\"}"
    done

    # Neighbours.
    for entry in ${NEIGHBOURS[$n]}; do
        local call="${entry%%,*}"
        local udp="${entry#*,}"
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
            -H 'content-type: application/json' \
            -d "{\"Callsign\":\"$call\",\"BpqPort\":null,\"UdpEndpoint\":\"$udp\"}"
    done

    # Route hints — pre-B5 force-the-relay. There's no public POST for
    # routehints today, so this script seeds them via Python's stdlib
    # sqlite3 module (present everywhere; no extra packages needed).
    if [ -n "${ROUTE_HINTS[$n]:-}" ]; then
        local db="$(node_dir "$n")/data/dapps.db"
        for entry in ${ROUTE_HINTS[$n]}; do
            local dest="${entry%%,*}"
            local hop="${entry#*,}"
            python3 -c "
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('insert or replace into routehints (Destination, NextHop) values (?, ?)', (sys.argv[2], sys.argv[3]))
con.commit()
" "$db" "$dest" "$hop"
        done
    fi
}

# ── exercise the topology ──────────────────────────────────────────────
trigger_run() {
    local n=$1
    local cookie="$(node_dir "$n")/cookie.txt"
    curl -fsS -o /dev/null -b "$cookie" -X POST "http://127.0.0.1:${HTTP_PORT[$n]}/Message/dorun" || true
}

send_a_to_c() {
    local payload="hello-from-A-$(date +%s)"
    # Base64-encode payload bytes for /AppApi/outbound. Anything stable
    # works; using `hello-from-A-<unix-ts>` so the receiver can eyeball
    # which run produced which message.
    local b64
    b64=$(printf '%s' "$payload" | base64 -w0 2>/dev/null || printf '%s' "$payload" | base64)
    echo ">>> A submitting message for C: '$payload'"
    # /AppApi is open when AuthRequired=false (sim default) — no token,
    # no cookie. SubmitOutboundMessage stamps OriginatorCallsign with
    # A's own callsign, which is what F1 needs to propagate end-to-end.
    curl -fsS -o /dev/null -X POST \
        "http://127.0.0.1:${HTTP_PORT[A]}/AppApi/outbound" \
        -H 'content-type: application/json' \
        -d "{\"App\":\"chat\",\"DestCallsign\":\"${CALLSIGN[C]}\",\"Payload\":\"$b64\",\"Ttl\":300}"
    echo ">>> Forwarder run on A → expect message to land at B"
    trigger_run A
    sleep 1
    echo ">>> Forwarder run on B → expect message to land at C"
    trigger_run B
    sleep 1
    echo ">>> A→C send done. Inspect via:"
    for n in A B C; do
        echo "    http://127.0.0.1:${HTTP_PORT[$n]}/   (${CALLSIGN[$n]}, login pwd: $SIM_PWD)"
    done
    # Show what C's app sees — this is the F1 acceptance criterion:
    # OriginatorCallsign should be A's callsign (NOT B's, the link source).
    echo ">>> C's inbox for app 'chat' (expect OriginatorCallsign=${CALLSIGN[A]}):"
    curl -fsS "http://127.0.0.1:${HTTP_PORT[C]}/AppApi/inbound/chat" || true
    echo
}

cmd_status() {
    for n in A B C; do
        local d="$(node_dir "$n")"
        local pid; pid=$(cat "$d/pid" 2>/dev/null || echo -)
        local alive=no
        kill -0 "$pid" 2>/dev/null && alive=yes
        printf "  %s  pid=%-6s alive=%s  dashboard=http://127.0.0.1:%s/\n" \
            "${CALLSIGN[$n]}" "$pid" "$alive" "${HTTP_PORT[$n]}"
    done
}

cmd_stop() {
    for n in A B C; do stop_node "$n"; done
}

cmd_send() { send_a_to_c; }

cmd_up() {
    maybe_publish
    cmd_stop
    for n in A B C; do start_node "$n"; done
    for n in A B C; do configure_node "$n"; done
    echo ">>> Topology ready. Status:"
    cmd_status
    send_a_to_c
}

case "${1:-up}" in
    up)     cmd_up ;;
    stop)   cmd_stop ;;
    send)   cmd_send ;;
    status) cmd_status ;;
    *)      echo "usage: $0 {up|stop|send|status}"; exit 2 ;;
esac
