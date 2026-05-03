# For app developers

This section is for someone *writing software* against a DAPPS node — not running one. (If you're running one, [start with Getting started](../getting-started.md).)

## What you can build with DAPPS

A DAPPS node gives your app a queue addressed by `app@CALLSIGN`. You publish a message; DAPPS finds a path; it eventually arrives at the destination's queue; the app there gets it. No real-time guarantees, but at-least-once delivery, content-addressed deduplication, and a stable id you can key idempotency off.

This shape suits any app where:

- Two parties want to exchange application-level messages over a slow, lossy, possibly-partitioned link.
- "Eventually delivered" is acceptable. Anything from chat that tolerates seconds, to bulletin-shaped publishes that tolerate hours.
- The sender doesn't necessarily care whether the recipient is online right now.
- You want one app's traffic isolated from another app's traffic on the same node.

It does **not** suit:

- RPC. There's no built-in "call and wait." If you want a reply, send a separate message.
- Real-time anything. Latency from "submit" to "destination app sees it" can be seconds, minutes, or longer.
- Data plane bulk transfer. The bearer is radio; payloads are KB-sized, not MB-sized.

## Pick your interface

DAPPS exposes two parallel interfaces for local apps. They share the same queue; pick whichever suits your language and runtime.

- **MQTT** (embedded broker, default port 1883). Subscribe to `dapps/in/<app>` for inbound, publish to `dapps/out/<app>/<dest>` for outbound, publish the message id to `dapps/ack/<app>` to acknowledge. Push model, real-time delivery.
- **REST** (`/AppApi/*` on the dashboard's HTTP listener). `POST /AppApi/outbound` to submit, `GET /AppApi/inbound/{app}` to poll, `POST /AppApi/inbound/{app}/{id}/ack` to acknowledge. Pull model, easy to test with `curl`.

A single app picks one. Different apps on the same node can pick different ones independently.

## How to read this section

- [**Concepts**](concepts.md) — the mental model. Read first.
- [**Tutorial**](tutorial.md) — a hello-world app in Python, end-to-end.
- [**Reference**](reference.md) — every MQTT topic, every REST endpoint, every user property, with semantics.
- [**Sample gallery**](gallery.md) — a small set of runnable examples covering the major DAPPS-shaped patterns.

If you've never used DAPPS as a developer before, walk these in order. If you have, the reference is the page you'll come back to.
