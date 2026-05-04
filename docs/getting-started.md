# Getting started

The shortest path from "I've heard of DAPPS" to "my node is forwarding messages":

```bash
curl -sSL https://m0lte.github.io/dapps/install.sh | sudo bash
```

Then open `http://<your-host>:5000/` in a browser. The setup wizard asks for an admin password, then your callsign and which packet-node bearer to use - everything else is editable from the dashboard. No env vars, no config files to hand-edit.

The rest of this page explains what just happened and walks the loop end-to-end.

## What DAPPS is

A DAPPS node is a small daemon you run alongside your packet node. It exposes two things:

1. **An app interface** - local applications publish and subscribe over MQTT (or REST). They name their destination as `app@CALLSIGN` and DAPPS handles the rest: routing, forwarding, fragmenting, retrying, and acking when the message lands.
2. **A backhaul** - DAPPS opens sessions to other DAPPS nodes (today over AGW for BPQ or RHPv2 for XRouter; tomorrow over MeshCore) to move messages towards their destination, hop by hop, with TTL.

The wire protocol is small and human-readable on the line: a peer connects, gets a `DAPPSv1>` prompt, offers `ihave id=<sha1> dst=<callsign> sz=<bytes> ttl=<seconds>`, the receiver responds `send` or `?` (already have it), the sender ships the bytes, the receiver acks the SHA-1 hash. That's the heart of it. Multi-part messages, source tracking, polling, and source-routing are all opt-in additions on top.

## What DAPPS is not

- **Not real-time.** A DAPPS message is a queued unit of work, not a live stream. If your application needs sub-second latency, this isn't the layer.
- **Not packet mail.** DAPPS doesn't replace BPQ Mail or any existing BBS - it complements them. Apps that want mail-shaped persistence + addressability use DAPPS; apps that want a BBS use a BBS.
- **Not a routing protocol replacement for the AX.25 layer.** DAPPS routes its own messages over whichever bearer is available, but it doesn't replace what your packet node does for connecting users.
- **Not BPQ-specific.** BPQ is one supported packet node; XRouter is another (via RHPv2). Anything that speaks AGW or RHPv2 works the same; MeshCore is in flight.

## The journey

### 1. Install

The one-liner above does this on Linux+systemd:

- Detects your architecture (`x86_64` / `aarch64` / `armv7l`).
- Downloads the matching binary from [the latest GitHub Release](https://github.com/M0LTE/dapps/releases/latest) to `/opt/dapps/dapps`.
- Creates a system user `dapps` and a state directory `/var/lib/dapps` (where the SQLite DB lives).
- Drops two systemd units: `dapps.service` (the daemon) and `dapps-updater.service` + `.timer` (the supervised in-place updater that powers the dashboard's "Apply update" button).
- Enables and starts both.

No env vars, no callsign yet, no bearer choice. Those all happen in step 2.

For Docker, Windows, or non-systemd Linux, see the [install pages](install/index.md).

### 2. Open the dashboard

`http://<your-host>:5000/`. On a fresh install the first request lands on `/Setup`, a two-step wizard:

1. **Admin password.** One password for the whole node - there are no user accounts. Used to gate the admin endpoints (`/Config`, `/Neighbours`, etc.) behind a cookie.
2. **Callsign + packet node.** Type your callsign with SSID (e.g. `M0LTE-1`) and pick a bearer. Click **Detect packet node** and the dashboard probes `localhost:8000` (AGW) and `localhost:9000` (RHPv2) and pre-fills the choice.

Submit; the daemon picks up the new callsign and bearer within a few seconds (no restart). The dashboard appears.

### 3. Tell your packet node about DAPPS

The DAPPS side is now configured; your packet node needs to know to dispatch sessions to DAPPS. This is the part that depends on your node software.

**For BPQ**, add an `APPLICATION` line to `bpq32.cfg`:

```
APPLICATION 1,DAPPS,,,,, 0
```

This advertises a `DAPPS` command at the BPQ node prompt and routes inbound L2 connects over AGW. Restart BPQ to pick it up. The [BPQ connect page](connect/bpq.md) has the full recipe.

**For XRouter**, add `RHPPORT=9000` to `XROUTER.CFG` and restart XRouter. No `APPL` block is needed - DAPPS binds the callsign over RHPv2 dynamically. The [XRouter connect page](connect/xrouter.md) has the details.

### 4. Add a neighbour

DAPPS knows nothing about other DAPPS nodes until you tell it about one. From the dashboard's **Neighbours** panel, enter:

- A peer's callsign with SSID.
- The bearer port your link goes out on (the AGW port byte for BPQ, or the 0-indexed `PORT=N - 1` for XRouter).

There's a REST API and a [discovery system](discovery-and-routing.md) that auto-finds peers if you turn on beaconing - but a single manual neighbour gets you to "first message" fastest.

### 5. Send a test message

Use the dashboard's **Send a test message** form, or the `/IHave` page for a hand-crafted submit, or just publish to MQTT:

```bash
mosquitto_pub -h localhost -p 1883 \
  -t dapps/out/hello \
  -m 'hello from M0LTE' \
  -D PUBLISH user-property dapps-dst hello@G7XYZ-1
```

The dashboard's outbound queue should show the message; once forwarded, it disappears from the queue. On the receiving node, an app subscribed to `dapps/in/hello` will get it.

That's the full loop. From here:

- [**Configure**](configure.md) walks every knob - what to leave alone, what to tune for your bearer.
- [**Discovery & routing**](discovery-and-routing.md) explains how DAPPS finds peers and remembers paths.
- [**App developers**](app-developers/index.md) is the manual for someone writing software that *uses* a DAPPS node.

## Why DAPPS?

If you're an app author who wants two callsigns to exchange application-level messages without writing your own packet-mail, your own retry loop, your own deduplication, your own routing - DAPPS is the layer that handles those. You write a small handler, subscribe to one MQTT topic, and the rest is plumbing somebody else maintains.

If you're a sysop, DAPPS gives apps on your node a way to ship messages to apps on other nodes without you brokering anything. It runs alongside your existing setup; it doesn't replace it.
