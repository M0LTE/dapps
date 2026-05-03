# Getting started

This page gives you the shortest path from "I've heard of DAPPS" to "my node is forwarding messages." The other sections of this manual are deep dives; this is the tour.

## What DAPPS is

A DAPPS node is a small daemon you run alongside your packet node. It exposes two things:

1. **An app interface** — local applications publish and subscribe over MQTT (or REST). They name their destination as `app@CALLSIGN` and DAPPS handles the rest: routing, forwarding, fragmenting, retrying, and acking when the message lands.
2. **A backhaul** — DAPPS opens sessions to other DAPPS nodes (today over AGW; tomorrow over MeshCore and RHPv2) to move messages towards their destination, hop by hop, with TTL.

The wire protocol is small and human-readable on the line: a peer connects, gets a `DAPPSv1>` prompt, offers `ihave id=<sha1> dst=<callsign> sz=<bytes> ttl=<seconds>`, the receiver responds `send` or `?` (already have it), the sender ships the bytes, the receiver acks the SHA-1 hash. That's the heart of it. Multi-part messages, source tracking, polling, and source-routing are all opt-in additions on top.

## What DAPPS is not

- **Not real-time.** A DAPPS message is a queued unit of work, not a live stream. If your application needs sub-second latency, this isn't the layer.
- **Not packet mail.** DAPPS doesn't replace BPQ Mail or any existing BBS — it complements them. Apps that want mail-shaped persistence + addressability use DAPPS; apps that want a BBS use a BBS.
- **Not a routing protocol replacement for the AX.25 layer.** DAPPS routes its own messages over whichever bearer is available, but it doesn't replace what your packet node does for connecting users.
- **Not BPQ-specific.** BPQ is the first supported bearer because that's where the ecosystem is, but DAPPS only needs an AGW-style session interface. Anything that speaks AGW (or, soon, RHPv2) works the same.

## 10-minute tour

Assuming you already have a packet node up (BPQ, most likely), here's the shortest install-to-message path. Each step has a more detailed page elsewhere; treat this section as the bird's-eye view.

### 1. Install

Pick a binary for your platform from the [latest release](https://github.com/M0LTE/dapps/releases/latest), drop it on disk, make it executable. On Linux there's a one-liner systemd recipe in the [Linux install page](install/linux.md); on Windows or macOS it runs the same way as a console app.

```bash
curl -L https://github.com/M0LTE/dapps/releases/latest/download/dapps-linux-x64 -o /opt/dapps/dapps
chmod +x /opt/dapps/dapps
```

### 2. Tell BPQ to talk to it

Add an `APPLICATION` line to your `bpq32.cfg` so BPQ dispatches DAPPS-bound sessions over AGW to the DAPPS daemon. The full recipe is on the [BPQ connect page](connect/bpq.md); the line itself is:

```
APPLICATION 1,DAPPS,,,,, 0
```

This advertises a `DAPPS` command at the BPQ node prompt and routes inbound L2 connects over AGW.

### 3. Configure DAPPS

DAPPS reads its config from the SQLite database it owns. The first time you run it, it seeds defaults; environment variables override those defaults at first start.

The minimum you must set is your callsign:

```bash
DAPPS_CALLSIGN=M0LTE-1 /opt/dapps/dapps
```

DAPPS refuses to start with the placeholder `N0CALL` — that's the safety net.

The full list of knobs is on the [Configure](configure.md) page.

### 4. Run it

Run the binary directly (it logs to stdout) or wire up the [systemd unit](install/linux.md#3-install-the-systemd-units). On a successful start you'll see lines like:

```
DB: /var/lib/dapps/dapps.db
MQTT broker listening on :1883
HTTP listener: http://0.0.0.0:5000
```

Open `http://<node>:5000/` in a browser. The first request lands on `/Setup` to set an admin password (one-time), then the dashboard.

### 5. Add a neighbour

DAPPS knows nothing about other DAPPS nodes until you tell it about one. The simplest way is the dashboard's **Neighbours** panel — enter the remote callsign and the BPQ port byte your AGW link goes out on. There's a REST API and a [discovery system](discovery-and-routing.md) that auto-finds peers if you turn on beaconing, but a single manual neighbour gets you to "first message" fastest.

### 6. Send a test message

Use the dashboard's **Send a test message** form, or the `/IHave` page for a hand-crafted submit, or just publish to MQTT:

```bash
mosquitto_pub -h localhost -p 1883 \
  -t dapps/out/hello \
  -m 'hello from M0LTE' \
  -D PUBLISH user-property dapps-dst hello@G7XYZ-1
```

The dashboard's outbound queue should show the message; once forwarded, it disappears from the queue. On the receiving node, an app subscribed to `dapps/in/hello` will get it.

That's the full loop. From here:

- [**Configure**](configure.md) walks every knob — what to leave alone, what to tune for your bearer.
- [**Discovery & routing**](discovery-and-routing.md) explains how DAPPS finds peers and remembers paths.
- [**App developers**](app-developers/index.md) is the manual for someone writing software that *uses* a DAPPS node.

## Why DAPPS?

If you're an app author who wants two callsigns to exchange application-level messages without writing your own packet-mail, your own retry loop, your own deduplication, your own routing — DAPPS is the layer that handles those. You write a small handler, subscribe to one MQTT topic, and the rest is plumbing somebody else maintains.

If you're a sysop, DAPPS gives apps on your node a way to ship messages to apps on other nodes without you brokering anything. It runs alongside your existing setup; it doesn't replace it.
