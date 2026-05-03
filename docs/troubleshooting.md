# Troubleshooting

Failure modes, in rough order of how often you'll hit them.

## DAPPS won't start

### "Refusing to start: callsign is N0CALL"

You haven't set `DAPPS_CALLSIGN`. Set it in the systemd unit / docker compose / shell environment:

```
DAPPS_CALLSIGN=M0LTE-1
```

The placeholder is a deliberate safety net — DAPPS won't transmit frames stamped with `N0CALL` because that would propagate garbage onto the air.

### Exit code 78

Operationally-fatal config error: a port is already in use, or another configured resource isn't available. The journal (or stdout, if you're running interactively) will have the actual error one line above. Common causes:

- **MQTT port (1883) already in use** by another broker on the same host. Either stop the other broker or set `DAPPS_MQTT_PORT` to a free port.
- **Dashboard port (5000) already in use** — set `ASPNETCORE_URLS=http://0.0.0.0:5001` (or whatever).
- **UDP listen port already in use** — disable the UDP datagram bearer with `DAPPS_UDP_LISTEN_PORT=0`, or pick a different port.

The unit file's `RestartPreventExitStatus=78` keeps systemd from hot-looping on a problem that won't fix itself; the journal message tells you what to fix.

### Database errors on first start

DAPPS expects to be able to create a SQLite file at `data/dapps.db` (relative to the working directory) or wherever you've pointed it. If the working directory isn't writable, startup fails. On Linux/systemd the recommended unit uses `WorkingDirectory=/var/lib/dapps`; make sure that exists and is owned by the runtime user.

## AGW connection problems

### "AGW connection refused"

DAPPS can't reach the AGW listener on `DAPPS_NODE_HOST:DAPPS_AGW_PORT`. Check:

- The packet node is actually running and AGW is enabled. For BPQ: `AGWPORT 8000` in `bpq32.cfg`, no other `#` commenting it out, BPQ restarted since.
- Network connectivity from where DAPPS runs to where the node runs. `nc -vz <bpq-host> 8000` from the DAPPS host should connect.
- No firewall in the way.

### "AGW connection drops repeatedly"

DAPPS reconnects automatically (with backoff). If it's flapping every few seconds:

- Check the packet node's logs for whether it's actively closing the connection. Some BPQ misconfigurations cause AGW to disconnect clients on every L2 event.
- Check for two DAPPS instances accidentally sharing a callsign — AGW will silently dispatch to whichever client registered last, and the older one sees its inbound dispatch evaporate.

### Inbound sessions never arrive

A remote node can `c <your-callsign>` and lands at the BPQ node prompt, but not at the `DAPPSv1>` prompt. Check:

- The `APPLICATION 1,DAPPS,,,,, 0` line is in `bpq32.cfg` and BPQ has been restarted since adding it.
- The CMD field on the `APPLICATION` line is **empty** (the `,,,,,`). Older recipes had `C N HOST K TRANS S` here — for DAPPS, leave it empty so BPQ doesn't run a node command on inbound, just dispatches the L2 'C' frame to the registered AGW client.
- DAPPS is registering the right callsign. The startup log shows `AGW: registered <callsign> for inbound dispatch`. If the callsign there doesn't match what the remote is connecting to, it won't route.
- AGW exact-match is by call+SSID. `M0LTE-1` is different from `M0LTE-7`.

## Discovery / routing problems

### "list_discovered_peers / list_neighbours empty, no traffic moving"

A fresh DAPPS install has **no discovery channels configured by default** — discovery is opt-in. Without a channel and an enabled beacon, you don't hear other nodes and they don't hear you. Two ways forward:

- **Manual neighbour**: dashboard → Neighbours → add a row for the peer you want to talk to. This is the fastest path to "first message" — no discovery needed.
- **Discovery channel**: dashboard → Discovery channels → add a channel for the BPQ port DAPPS should beacon on. Set a sensible cadence (10 minutes for VHF FM is a reasonable starting point) and a per-channel airtime budget if you're on a shared band.

### "I added a discovery channel but I'm not hearing anyone"

Check, in order:

- Channel is **enabled** (the row's enabled flag).
- Beacon cadence isn't unreasonably long (one beacon per hour means you hear nothing for an hour after start).
- Other DAPPS nodes are actually transmitting on the same channel-key (same BPQ port byte). A node beaconing on port 1 won't be heard by a node listening on port 0.
- Airtime budget isn't exhausted. Dashboard → Discovery channels heading shows trailing-hour consumption; if it's ≥ 100 % of the global cap, beacons will defer. Either raise the cap or wait.

### "Probes failing"

The probed-nodes table shows the recent failure reason. Common ones:

- **`RETRYOUT`**: AGW retried the connect to the remote callsign but never got a response. The remote isn't reachable on the link, or your AGW port byte is wrong for that peer.
- **Connect timeout**: the connect went out but the protocol parser never saw the `DAPPSv1>` prompt. The peer either isn't running DAPPS, or you're connecting to the wrong application (their `APPLICATION` line might use a different command name).
- **TIMEOUT after `DAPPSv1>`**: probe got the prompt but the session hung. Network is dropping packets mid-session, or one side has a serious clock skew.

### "Forwarding loops"

The default `passive-flood` algorithm has a hop-count cap and won't loop indefinitely, but if you have a complex topology where it's mis-deciding routes, switch to the `meshcore` algorithm (DSR-style source routing) for more deterministic forwarding. `DAPPS_ROUTING_ALGORITHM=meshcore` + restart.

## App-interface problems

### "MQTT subscriber not receiving messages"

Check, in order:

- The subscriber is connected to the right port (default 1883, configurable).
- The subscriber is subscribed to the right topic (`dapps/in/<app>` for incoming).
- Your MQTT client supports MQTT 5 user properties. DAPPS publishes `dapps-id`, `dapps-source`, `dapps-ttl` etc. as user properties — MQTT 3.1.1 clients will get the payload but not the metadata.
- The subscriber is acking (`dapps/ack/<app>`). Without acks the messages stay in the local-inbox queue (dashboard → Local inbox panel) and re-deliver on the next subscription.

### "REST submit returns 200 but message never arrives"

Look at the dashboard:

- Outbound queue panel: is the message there? If yes, it's queued but the forwarder hasn't shipped it yet (next tick is at most 5 s). If no, the submit didn't actually write to the messages table — check the response body, it'll have an error.
- Recently dropped panel: did it get dropped? If yes, reason will be there (usually TTL too short, or no route to destination).
- Per-link state panel: is the link to the destination's first-hop neighbour actually working?

### "TTL expired" drops

The default TTL on a submit is open-ended (no expiry). If you're seeing TTL drops, you're explicitly setting one. Either raise it on submit, or accept that mail-style multi-day delivery won't work with a sub-hour TTL.

## Update problems

### "Apply update button does nothing"

Three possibilities:

- The `dapps-updater.service` / `.timer` unit isn't installed. `systemctl status dapps-updater.timer` should show `active (waiting)`. If not, install per the [Linux install page](install/linux.md).
- The marker file write succeeded but the timer hasn't ticked yet. Wait up to 60 s.
- The updater ran but failed. `journalctl -u dapps-updater.service` will have the error.

### "Update applied, but rolled back"

The new binary started but didn't pass the 60 s verify. Look at:

- `journalctl -u dapps.service --since '5 minutes ago'` for the new binary's startup messages — there'll be a clear error.
- The dashboard's update card — if rollback succeeded, the phase pill shows "rolled back" with the reason.

The previous binary is restored automatically; you don't need to do anything to recover. Investigate the new version's failure mode and either wait for a fix or stay on the previous version.

### "Update banner shows the same version as I'm running"

The poll cadence is hourly. To force an immediate re-poll, click **Check now** on the dashboard, or `POST /Update/check`, or call the `check_for_updates` MCP tool.

## Investigating "where did message X go?"

A worked path:

1. **Dashboard → Recently dropped panel.** If the message id is there, the reason is the answer.
2. **`journalctl -u dapps.service | grep <message-id>`** — the decision-events ring is mirrored to the journal as structured log lines. Every step the daemon took with this id will be there: submit, queue insert, forward attempt, ack received / failed, etc.
3. **MCP `explain_why_message_failed`** if you have an assistant connected — it walks the same trail and produces a narrative.

If nothing turns up, the message was never submitted (typo on the topic / endpoint). Check the submitting application's logs.

## Getting help

- **Dashboard not showing what you expect** → screenshot it and [open an issue](https://github.com/M0LTE/dapps/issues). The UI is dense, behaviour-vs-expectation reports are useful.
- **On-air protocol question** → see the [protocol reference](app-developers/reference.md), then open an issue if it's not answered.
- **Bearer-specific weirdness** (BPQ doing something odd, AGW edge case) → likely either a config issue documented above or a real bug; an issue with the BPQ logs + DAPPS journal is the right next step.
