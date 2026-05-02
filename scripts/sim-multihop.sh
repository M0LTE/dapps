#!/usr/bin/env bash
#
# Local 6-node multi-hop simulator. Spins up six dapps.core instances on
# loopback in a small-region mesh that exercises chained relays,
# branching, and off-spine paths — the kind of topology a real RF test
# network would produce. Drives several A→…→Z sends across the
# topology and reports each receiver's view of OriginatorCallsign so
# F1 source-tracking can be verified end-to-end at a glance.
#
# Topology
#
#       A ──G1── B ──G2── C ──G3── D                 (1, 2, 3 hops from A)
#                         │
#                         G4
#                         │
#                         E ──G5── F                 (3 hops, then 4 from A)
#
# Multicast groups G1..G5 stand in for distinct RF "broadcast domains";
# each adjacent pair shares a group so beacons reach exactly the right
# peers. Forwarding still goes unicast UDP via the per-node neighbour
# table (the same way RF works: broadcast discovery, point-to-point
# forward). Pre-B5 (learned-graph routing) the route-hints encode the
# multi-hop chains; B5 will replace them with learned routes.
#
# Usage:
#   scripts/sim-multihop.sh                       # passive-flood (default)
#   SIM_ALGO=meshcore scripts/sim-multihop.sh     # meshcore-like
#   scripts/sim-multihop.sh stop      # tear down
#   scripts/sim-multihop.sh send X Y  # send a message X → Y
#   scripts/sim-multihop.sh exercise  # run the canned non-trivial set
#   scripts/sim-multihop.sh status    # ports, PIDs, queue counts
#   scripts/sim-multihop.sh verify    # show every node's inbox
#   scripts/sim-multihop.sh learned   # dump per-node learned-routes
#   scripts/sim-multihop.sh discovered    # dump per-node discovered-paths
#   scripts/sim-multihop.sh prove-learning   # passive-flood acceptance
#   scripts/sim-multihop.sh prove-meshcore   # meshcore acceptance
#
# Requires: dotnet 8 SDK, curl, python3 (with stdlib sqlite3).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="${SIM_DIR:-/tmp/dapps-sim}"
BIN_DIR="$SIM_DIR/bin"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SIM_PWD="${SIM_PWD:-simpassw0rd}"

# Routing algorithm: passive-flood (default — AODV-flavoured passive
# learning + bounded flood) or meshcore (DSR-style source routing
# with passive discovery). Set via env: SIM_ALGO=meshcore. Picked up
# by every node via DAPPS_ROUTING_ALGORITHM at startup.
SIM_ALGO="${SIM_ALGO:-passive-flood}"

NODES=(A B C D E F)

declare -A HTTP_PORT=( [A]=15001 [B]=15002 [C]=15003 [D]=15004 [E]=15005 [F]=15006 )
declare -A MQTT_PORT=( [A]=11881 [B]=11882 [C]=11883 [D]=11884 [E]=11885 [F]=11886 )
declare -A UDP_PORT=(  [A]=50071 [B]=50072 [C]=50073 [D]=50074 [E]=50075 [F]=50076 )
declare -A AGW_PORT=(  [A]=18001 [B]=18002 [C]=18003 [D]=18004 [E]=18005 [F]=18006 )   # closed; AGW probe will fail, harmless
declare -A CALLSIGN=(  [A]=G0SIA-1 [B]=G0SIB-1 [C]=G0SIC-1 [D]=G0SID-1 [E]=G0SIE-1 [F]=G0SIF-1 )

# Discovery channels — UDP multicast groups standing in for distinct
# RF "broadcast domains". All five share the same UDP port and differ
# only in multicast group address; this used to leak across groups
# within a process (see UdpMulticastDiscoveryBearer's bind comment for
# the gory detail), now fixed by binding each recv socket to the group
# address rather than IPAddress.Any.
G1="239.0.7.1:54321"
G2="239.0.7.2:54321"
G3="239.0.7.3:54321"
G4="239.0.7.4:54321"
G5="239.0.7.5:54321"
declare -A CHANNELS=(
    [A]="$G1"
    [B]="$G1 $G2"
    [C]="$G2 $G3 $G4"
    [D]="$G3"
    [E]="$G4 $G5"
    [F]="$G5"
)

# Direct neighbours — entries are space-separated "<letter>" lookups
# into the rest of the tables. Resolves to the matching callsign + UDP
# endpoint at configuration time.
declare -A NEIGHBOURS=(
    [A]="B"
    [B]="A C"
    [C]="B D E"
    [D]="C"
    [E]="C F"
    [F]="E"
)

# Route hints — used to be pre-B5 "force the relay" entries here, but
# B5 (PR-B passive learning + PR-C bounded flood) means the network
# converges from cold-start without any explicit hints. The simulator
# is a clean validation of that: only direct neighbours are configured;
# all multi-hop routing is discovered by passive learning, with the
# bounded flood handling the very first message to a never-seen
# destination. Keep the array empty so cold-start exercises the flood
# path; future tests can add hints back in to validate operator
# overrides if needed.
declare -A ROUTE_HINTS=( [A]="" [B]="" [C]="" [D]="" [E]="" [F]="" )

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
    local d; d="$(node_dir "$n")"
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
            DAPPS_ROUTING_ALGORITHM="$SIM_ALGO" \
            ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT[$n]}" \
            DOTNET_ENVIRONMENT=Production \
            "$BIN_DIR/app/dapps.core" >"$d/dapps.log" 2>&1 &
        echo $! >"$d/pid"
    )

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
    local pid_file; pid_file="$(node_dir "$n")/pid"
    [ -f "$pid_file" ] || return 0
    local pid; pid=$(cat "$pid_file")
    if kill -0 "$pid" 2>/dev/null; then
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
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    rm -f "$cookie"

    curl -fsS -o /dev/null -X POST "$base/Setup" \
        --data-urlencode "Password=$SIM_PWD" \
        --data-urlencode "Confirm=$SIM_PWD"
    curl -fsS -o /dev/null -c "$cookie" -X POST "$base/Login" \
        --data-urlencode "Password=$SIM_PWD"

    for ch in ${CHANNELS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/DiscoveryChannels" \
            -H 'content-type: application/json' \
            -d "{\"Bearer\":\"udp\",\"ChannelKey\":\"$ch\",\"LinkClass\":\"LanMulticast\"}"
    done

    for nb in ${NEIGHBOURS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
            -H 'content-type: application/json' \
            -d "{\"Callsign\":\"${CALLSIGN[$nb]}\",\"BpqPort\":null,\"UdpEndpoint\":\"127.0.0.1:${UDP_PORT[$nb]}\"}"
    done

    if [ -n "${ROUTE_HINTS[$n]:-}" ]; then
        local db; db="$(node_dir "$n")/data/dapps.db"
        for entry in ${ROUTE_HINTS[$n]}; do
            local dest_letter="${entry%%:*}"
            local hop_letter="${entry##*:}"
            # Route hint Destination is the BASE callsign (no SSID) — that's
            # what the resolver uses as the lookup key. NextHop is the full
            # neighbour callsign with SSID.
            local dest_base="${CALLSIGN[$dest_letter]%-*}"
            local hop_full="${CALLSIGN[$hop_letter]}"
            python3 -c "
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('insert or replace into routehints (Destination, NextHop) values (?, ?)', (sys.argv[2], sys.argv[3]))
con.commit()
" "$db" "$dest_base" "$hop_full"
        done
    fi
}

# ── exercise the topology ──────────────────────────────────────────────
trigger_run() {
    local n=$1
    # /Message/dorun is admin-protected — pass the cookie set up at
    # configure_node, otherwise the request gets a 302 to /Login and
    # the forwarder silently never runs.
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    curl -fsS -o /dev/null -b "$cookie" -X POST "http://127.0.0.1:${HTTP_PORT[$n]}/Message/dorun" || true
}

# Drain queues. With the auto-forwarder hosted service on a 5-second
# tick, a 4-hop chain takes ~20s to drain on its own. To make the
# canned exercises finish quickly, we manually kick every node every
# second for 8 rounds — each round each relay forwards whatever just
# arrived from the upstream kick. The DoRun mutex makes the kicks
# benign even when they overlap the auto-forwarder's own tick.
drain_queues() {
    for _ in 1 2 3 4 5 6 7 8; do
        for n in "${NODES[@]}"; do trigger_run "$n" & done
        wait
        sleep 1
    done
}

submit_message() {
    local from=$1 to=$2 payload=$3
    local b64
    b64=$(printf '%s' "$payload" | base64 -w0 2>/dev/null || printf '%s' "$payload" | base64)
    curl -fsS -o /dev/null -X POST \
        "http://127.0.0.1:${HTTP_PORT[$from]}/AppApi/outbound" \
        -H 'content-type: application/json' \
        -d "{\"App\":\"chat\",\"DestCallsign\":\"${CALLSIGN[$to]}\",\"Payload\":\"$b64\",\"Ttl\":300}"
}

cmd_send() {
    local from=$1 to=$2
    local payload="${3:-hello-${from}-to-${to}-$(date +%s%3N)}"
    echo ">>> Submit ${CALLSIGN[$from]} → ${CALLSIGN[$to]}: '$payload'"
    submit_message "$from" "$to" "$payload"
    drain_queues
    show_inbox "$to"
}

show_inbox() {
    local n=$1
    echo "----- ${CALLSIGN[$n]} inbox (chat) -----"
    local rows
    rows=$(curl -fsS "http://127.0.0.1:${HTTP_PORT[$n]}/AppApi/inbound/chat")
    if [ -z "$rows" ] || [ "$rows" = "[]" ]; then
        echo "(empty)"
        return
    fi
    # Pretty-print each row as: id  origin=X  source=Y  payload-decoded
    python3 - "$rows" <<'PY'
import json, sys, base64
rows = json.loads(sys.argv[1])
for r in rows:
    payload = base64.b64decode(r.get("payload", "") or "").decode("utf-8", errors="replace")
    print(f"  id={r['id']}  origin={r.get('originatorCallsign') or '?'}  source={r['sourceCallsign']}  payload={payload!r}  ttl={r.get('ttl')}")
PY
}

cmd_verify() {
    echo ">>> Inboxes across the mesh:"
    for n in "${NODES[@]}"; do show_inbox "$n"; done
}

# Dump every node's learned-routes table — populated by the passive
# learning algorithm as inbound traffic arrives carrying F1 src=
# headers. After the canned exercises, these tables are the proof
# that learning actually happened.
cmd_learned() {
    echo ">>> Learned routes across the mesh:"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        echo "----- ${CALLSIGN[$n]} learned routes -----"
        if [ ! -f "$d" ]; then echo "(db not present)"; continue; fi
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
try:
    rows = list(con.execute("select DestinationBaseCallsign, NextHopCallsign, LastSeenAt, ConsecutiveFailures from learnedroutes order by DestinationBaseCallsign"))
except sqlite3.OperationalError as e:
    print(f"  (no learnedroutes table yet: {e})"); sys.exit()
if not rows:
    print("  (none)")
for dest, nh, seen, fails in rows:
    print(f"  → {dest} via {nh}  (failures={fails})")
PY
    done
}

# Dump every node's discovered-paths table — the MeshCore-flavoured
# algorithm's equivalent of learned-routes, but storing the FULL
# ordered intermediate-hop list rather than just the next hop.
# Populated as flood-discovery messages traverse the mesh.
cmd_discovered() {
    echo ">>> Discovered paths across the mesh:"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        echo "----- ${CALLSIGN[$n]} discovered paths -----"
        if [ ! -f "$d" ]; then echo "(db not present)"; continue; fi
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
try:
    rows = list(con.execute("select DestinationBaseCallsign, IntermediatesCsv, LastSeenAt, ConsecutiveFailures from discoveredpaths order by DestinationBaseCallsign"))
except sqlite3.OperationalError as e:
    print(f"  (no discoveredpaths table yet: {e})"); sys.exit()
if not rows:
    print("  (none)")
for dest, mids, seen, fails in rows:
    intermediates = mids if mids else "(direct)"
    print(f"  → {dest} via [{intermediates}]  (failures={fails})")
PY
    done
}

# MeshCore equivalent of cmd_prove_learning. Wipe route-hints and
# discovered-peers (leaving only the meshcore algorithm's discovered-
# paths as the routing source) and send A→F. If discovery has done
# its job, the message still delivers via source routing — proving
# the algorithm self-organised the mesh without operator config.
cmd_prove_meshcore() {
    echo ">>> Wiping route-hints + discovered-peers on every node…"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from routehints"); con.execute("delete from discoveredpeers")
con.commit()
PY
    done
    echo ">>> A→F using ONLY discovered paths…"
    submit_message A F "meshcore-only-$(date +%s)"
    drain_queues
    show_inbox F
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  MESHCORE ACCEPTANCE: above message arrived at F using ONLY"
    echo "  paths discovered by the meshcore algorithm. No route-hints"
    echo "  configured, no discovered peers seeded. If you see a row"
    echo "  with origin=G0SIA-1, source-routed delivery is working."
    echo "═════════════════════════════════════════════════════════════"
}

# Drop the route-hints table and the discovered-peer entries on every
# node, leaving ONLY learned routes as the routing source. Then send
# A→F again. If passive learning has done its job, the message still
# delivers — proving that after one round of bidirectional traffic
# the network can route without explicit operator config.
cmd_prove_learning() {
    echo ">>> Wiping route-hints + discovered-peers on every node…"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from routehints"); con.execute("delete from discoveredpeers")
con.commit()
PY
    done
    echo ">>> A→F using ONLY learned routes…"
    submit_message A F "learned-only-$(date +%s)"
    drain_queues
    show_inbox F
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  PR-B ACCEPTANCE: above message arrived at F using ONLY"
    echo "  routes learned passively from prior traffic. No route-hints"
    echo "  configured, no discovered peers seeded. If you see a row"
    echo "  with origin=G0SIA-1, passive learning is working."
    echo "═════════════════════════════════════════════════════════════"
}

# Canned non-trivial exercise — runs several sends across the topology
# that exercise different path lengths, branching, and parallel flows.
# After all sends complete, prints each receiver's inbox so F1 origin
# preservation can be verified at a glance.
cmd_exercise() {
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 1: longest forward path A→F (4 hops: A→B→C→E→F)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A F "long-forward-$(date +%s)"
    drain_queues
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 2: reverse longest path F→A (4 hops: F→E→C→B→A)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message F A "long-reverse-$(date +%s)"
    drain_queues
    show_inbox A

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 3: off-spine D→F (3 hops: D→C→E→F, never touches A or B)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message D F "off-spine-$(date +%s)"
    drain_queues
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 4: fan-out from A to D, E, F submitted in parallel"
    echo "  (one originator splitting across three different paths)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A D "fan-out-D-$(date +%s)" &
    submit_message A E "fan-out-E-$(date +%s)" &
    submit_message A F "fan-out-F-$(date +%s)" &
    wait
    drain_queues
    for n in D E F; do show_inbox "$n"; done

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 5: cross-traffic A↔F simultaneously"
    echo "  (both endpoints originate; relays handle fan-in)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A F "cross-A2F-$(date +%s)" &
    submit_message F A "cross-F2A-$(date +%s)" &
    wait
    drain_queues
    show_inbox A
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  F1 ACCEPTANCE CHECK"
    echo "  Every 'origin=…' above should be the *originator* callsign"
    echo "  (the from-side of the arrow), NOT the link-source/relay."
    echo "  source=UDP is fine — it's the bearer placeholder for"
    echo "  datagram-shaped backhauls; the receiver app has dapps-source"
    echo "  via MQTT for the link source separately."
    echo "═════════════════════════════════════════════════════════════"

    echo
    if [ "$SIM_ALGO" = "meshcore" ]; then
        cmd_discovered
        echo
        cmd_prove_meshcore
    else
        cmd_learned
        echo
        cmd_prove_learning
    fi
}

cmd_status() {
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")"
        local pid; pid=$(cat "$d/pid" 2>/dev/null || echo -)
        local alive=no
        kill -0 "$pid" 2>/dev/null && alive=yes
        printf "  %s  pid=%-6s alive=%s  http=:%s  udp=:%s  channels=%s\n" \
            "${CALLSIGN[$n]}" "$pid" "$alive" "${HTTP_PORT[$n]}" "${UDP_PORT[$n]}" \
            "$(echo "${CHANNELS[$n]:-(none)}" | tr ' ' ',')"
    done
}

cmd_stop() {
    for n in "${NODES[@]}"; do stop_node "$n"; done
}

cmd_up() {
    maybe_publish
    cmd_stop
    for n in "${NODES[@]}"; do start_node "$n"; done
    for n in "${NODES[@]}"; do configure_node "$n"; done
    echo
    echo ">>> Topology ready:"
    cmd_status
    echo
    echo ">>> Dashboards (login pwd: $SIM_PWD):"
    for n in "${NODES[@]}"; do
        printf "    %s  http://127.0.0.1:%s/\n" "${CALLSIGN[$n]}" "${HTTP_PORT[$n]}"
    done
    echo
    cmd_exercise
}

case "${1:-up}" in
    up)         cmd_up ;;
    stop)       cmd_stop ;;
    send)       cmd_send "$2" "$3" "${4:-}" ;;
    exercise)   cmd_exercise ;;
    status)     cmd_status ;;
    verify)     cmd_verify ;;
    learned)    cmd_learned ;;
    prove-learning) cmd_prove_learning ;;
    discovered) cmd_discovered ;;
    prove-meshcore) cmd_prove_meshcore ;;
    *)          echo "usage: $0 {up|stop|send <from> <to> [payload]|exercise|status|verify|learned|prove-learning|discovered|prove-meshcore}"; echo "       SIM_ALGO=meshcore $0 up    # run with the MeshCore-like algorithm instead of passive-flood"; exit 2 ;;
esac
