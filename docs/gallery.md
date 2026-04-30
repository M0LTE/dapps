# DAPPS sample app gallery

Three small apps that demonstrate common shapes you'll build on top of DAPPS. Each one is short, runnable, and chosen to surface a specific design point. None are production-ready — they're written to be read.

If you haven't yet, read [concepts.md](concepts.md) and walk through [tutorial-hello-world.md](tutorial-hello-world.md) first. They cover the fundamentals these examples assume.

All three are Python + paho-mqtt. Setup is the same as in the tutorial:

```bash
pip install paho-mqtt
```

Run a DAPPS instance locally, replace `<your-callsign>` with your own throughout, and try the examples in any order.

## Group chat — `examples/chat.py`

[`examples/chat.py`](examples/chat.py)

A line-mode chat app. Each line you type at the prompt is broadcast to a fixed list of peer callsigns; incoming lines from anyone are printed inline. Run the same script (with appropriate args) on every node that wants to participate — there is no central server, no registry, no membership protocol.

```bash
# On node A (G7XYZ):
python chat.py M0LTE-1 GB7AAA

# On node B (M0LTE-1):
python chat.py G7XYZ GB7AAA

# On node C (GB7AAA):
python chat.py G7XYZ M0LTE-1
```

Type a line on any node; the others see it.

**What this demonstrates**:
- **One-to-many fan-out** — DAPPS doesn't have multicast at the app layer, so the app loops and publishes once per recipient. Each recipient sees the same `dapps-id`, since the payload + salt are the same per send (the salt is per-publish, not per-recipient — but that's a minor point you can refine in a real implementation).
- **Reading `dapps-source`** to display "who said what." On a single-hop network, this is the original sender; on a multi-hop network, it's the last hop. End-to-end source tracking is on the spec roadmap (see Phase F1 in `plan.md`).
- **TTL of 1 hour** — chat is more useful than a 5-minute reply window, less useful than yesterday's transcript.

**What this glosses over**:
- The `seen` set is in-memory; restart the script and you'll re-print the backlog. Persist it to SQLite for a real app.
- No formatting, no nicknames, no slash-commands — the wire format is "raw UTF-8 bytes." A real chat app would put a small JSON envelope around each line for sender display name, format, etc.
- Membership is hard-coded. A self-organising version would have nodes announce themselves on a "join" topic and others react.

## Sensor publisher — `examples/sensor.py`

[`examples/sensor.py`](examples/sensor.py)

A periodic publisher with no listening surface. Reads a (faked) sensor every N seconds and pushes a JSON reading to a list of subscriber callsigns. Pure submit-and-go — no `on_message`, no acks.

```bash
# Long-running mode (publishes every 60s):
python sensor.py --interval 60 --subscribers G7XYZ M0LTE-1

# One-shot from cron / a systemd timer:
python sensor.py --once --subscribers G7XYZ
```

A subscriber on the receiving side would look like the `hello.py` shape: subscribe to `dapps/in/sensor`, decode the JSON, do something with it (graph it, log it, raise an alarm). The example doesn't ship a subscriber because it's identical in shape to `hello.py` minus the reply.

**What this demonstrates**:
- **Submit-only apps.** Not every DAPPS app cares about responses. The shape is a normal MQTT publisher with QoS 1; DAPPS handles all the radio-side awkwardness.
- **Long TTL (24h)** — a sensor reading is *useful information* well past the next reading. Compare to chat (1h) or pager (5m). TTL isn't "freshness urgency," it's "after this point, the message is more clutter than signal."
- **`--once` mode** — DAPPS tolerates the publisher coming and going. Submit, exit, the queue takes care of delivery.

**What this glosses over**:
- The fake sensor — `read_sensor()` returns random numbers. Replace with `psutil`, a serial-port read, an I²C call — whatever you actually want to publish.
- One JSON object per message. For higher-rate sensors you'd batch readings into a single payload to reduce per-message overhead on the radio side.

## Two-way pager — `examples/pager.py`

[`examples/pager.py`](examples/pager.py)

A messenger app with two distinct CLI modes. Run with no arguments to listen for incoming pages; run `pager.py send <callsign> <message>` to fire off a single page and exit.

```bash
# Receiver — long-running, on the recipient's node:
python pager.py
[pager] listening on dapps/in/pager

# Sender — one-shot, from anywhere:
python pager.py send G7XYZ "the eagle has landed"
-> sent to G7XYZ: 'the eagle has landed'
```

The receiver prints incoming messages with `[<source>] <text>` and notes the residual TTL when one is set.

**What this demonstrates**:
- **Two lifetimes, one app slot.** Both modes use `app=pager`; the receiver owns the `dapps/in/pager` subscription, the sender publishes to `dapps/out/pager/<dest>` once and exits. Multiple processes coexist cleanly because submit and deliver are independent paths.
- **Surfacing TTL to the user.** The receiver shows residual TTL when it's set, so the operator knows whether they're looking at a fresh page or one that survived a long backlog.
- **Short TTL (5m).** A pager message that arrives an hour late is almost always noise.

**What this glosses over**:
- No reply-to / threading. A real messenger would build that on top — perhaps using a header in the payload to correlate.
- No history; restart the receiver and old pages re-arrive (because `seen` is in-memory). In production, persist `seen` and on startup also fetch the queue once via REST to display the backlog before the broker replay fires.

## When to write your own

These three apps cover the major DAPPS-shaped patterns:

| Pattern | Example | Distinguishing feature |
|---|---|---|
| Request → reply | [`hello.py`](examples/hello.py) | Both subscribes and publishes; replies on `dapps-source`. |
| Many-to-many | [`chat.py`](examples/chat.py) | Loops `publish()` per peer; participants are symmetric. |
| One-shot submit | [`sensor.py`](examples/sensor.py) (`--once`) | No subscription; submit and exit. |
| Periodic submit | [`sensor.py`](examples/sensor.py) (default) | Publisher only; no `on_message`. |
| Mixed listener + sender | [`pager.py`](examples/pager.py) | Two CLI modes share the same app name. |

If your app fits one of these shapes, copy the closest example and adapt. If it doesn't, the [reference](reference.md) has the full surface — almost any DAPPS app boils down to combinations of `subscribe`, `publish`, and `ack` against your own `<app>` slot.

## See also

- [`docs/reference.md`](reference.md) — full MQTT topic and REST endpoint reference.
- [`docs/tutorial-hello-world.md`](tutorial-hello-world.md) — start here if you haven't written one yet.
