# DAPPS concepts

If you've never used DAPPS before, read this first. It's the shortest path to a working mental model — what DAPPS *is*, what it deliberately *isn't*, and what those choices imply for an app you're about to write.

## What DAPPS is

**A queue, addressed by `app@callsign`, that survives radio.**

Your app submits a message to "the `chat` app at `G7XYZ`." DAPPS — running on your node and on theirs — gets it there eventually, over whatever path is reachable: a direct VHF link, a multi-hop AX.25 chain via someone else's BPQ, an HF skip that closes overnight and reopens at sunup, or an AXIP tunnel between two clubs on opposite continents. The queue persists across reboots, network partitions, and apps that aren't running yet. When the destination app finally connects to its local DAPPS instance, the backlog is delivered.

The queue is **content-addressed**: every message has an id derived from its bytes (`sha1(salt ++ payload)[:7]`). Two attempts to deliver the same message arrive with the same id, which means apps can detect and ignore duplicates without any coordination layer above DAPPS.

## What DAPPS is not

- **Not real-time.** Latency from "submit" to "destination app sees it" can be seconds, minutes, hours, or days. If you need an answer back inside a TCP timeout, DAPPS is the wrong tool.
- **Not RPC.** There's no built-in "call and wait for reply." If your app wants a response, it submits a separate message and the other side replies the same way. The two messages are independent — a reply might arrive before the original sender's app has even reconnected to read its own outbox-confirmation.
- **Not a packet mail system.** Mail is for humans reading inboxes. DAPPS is for programs reading queues. The two could coexist on the same node and even share routing arrangements, but they solve different problems.
- **Not transport-coupled.** DAPPS speaks a small text protocol (DAPPSv1) that rides on top of whatever bearer is available — AX.25 today, MeshCore or UDP datagrams or something else tomorrow. An app sees the same queue regardless.
- **Not a replacement for AX.25 or packet nodes.** It's an overlay. Your node still needs BPQ (or something playing the same role) to connect outbound and accept inbound.

## The destination model: `app@callsign`

Every queued message has a destination of the form `app@callsign` — for example `chat@G7XYZ` or `weather@M0LTE-1`.

The **callsign** identifies the *node* the message is for. It's the same callsign the operator has registered with BPQ, and it's how the radio network already routes traffic.

The **app name** identifies which queue on that node. It's a free-form ASCII label chosen by the developer — `chat`, `weather`, `apt-update`, whatever. Apps subscribe to their own slot (`dapps/in/chat`) and ignore everything else.

This means **one node can host many apps simultaneously**, each with its own private queue. It also means **the same app can run on many nodes** — `chat@G7XYZ` and `chat@M0LTE-1` are different conversations between different people.

There is no central registry of app names. If you pick `weather` and someone else also picks `weather`, you're both running an app called `weather` and you'll talk only to the corresponding `weather` instance on whichever node a message is addressed to. Pick descriptive names if you want to interop; pick whatever you like if you don't.

## Idempotency: at-least-once, content-addressed

DAPPS guarantees **at-least-once** delivery. If the local DAPPS crashes after handing a message to your app but before recording the ack, your app will see the same message again on next subscribe.

The way out is to lean on the message id. The id is a hash of the payload (plus an optional salt to disambiguate identical-content messages submitted seconds apart), so a redelivered message arrives with **the same id**. An app that records "ids I've already processed" handles duplicates correctly with no further coordination — no monotonic sequence numbers, no per-sender state machine, no transactions across the DAPPS boundary.

In practice this is a one-line check at the top of your message handler:

```python
if message_id in already_processed:
    ack(message_id)
    continue
already_processed.add(message_id)
# ... do the actual work ...
ack(message_id)
```

The set can be a SQLite table, a Redis key with a TTL, a file, whatever fits the app. The point is that DAPPS doesn't impose a storage decision on you; it just gives you a stable id to key off.

## TTL: how long is this still useful?

When an app submits a message, it can include a **TTL** in seconds — "this is still worth delivering for the next N seconds; after that, drop it." The TTL travels on the wire and is decremented at every hop by the time the message spent queued on that hop. A message with TTL 300 that sat in two intermediate queues for 60s each arrives at the destination with about 180s left.

On delivery, DAPPS surfaces the **residual** TTL to the receiving app — both REST and MQTT report the same number. An app reading `ttl=2` knows the message is nearly stale; an app reading `ttl=null` knows the sender didn't bother to specify one (in which case the message is kept until forwarded or manually deleted).

Pick a TTL that matches the app:

- **Chat / presence**: short (60–300s). Stale chat is annoying.
- **Bulletins / news**: medium (hours).
- **Configuration updates / apt-mirror sync**: long (a day or more) — the receiver wants the latest, but yesterday's update is still useful if today's hasn't arrived yet.
- **No TTL at all**: only if the message will *always* be useful no matter how late it arrives.

## Why DAPPS, not raw AX.25?

Raw AX.25 connections are point-to-point, session-oriented, and ephemeral. If you write your app directly against AX.25, you end up writing — *every time* — the same ten things: connection retries, partial-write handling, "what if the other end isn't listening right now," "what if the link drops mid-message," idempotent reprocessing on reconnect, framing for application-level messages, and so on.

DAPPS does that work once, in one place, so the app gets to be just an app:

```python
on_message("hello")  →  reply("world")
```

vs.

```python
loop:
    try connect to G7XYZ
    if connected: send "hello"
    else: schedule retry, persist to disk, hope BPQ is up
    on partial response: figure out how much arrived, reconnect, ask again
    on duplicate: was that the original or a redelivery? ...
```

## Why DAPPS, not packet mail?

Packet mail (BBS, FBB, Winlink) is built around humans: subjects, bodies, threads, read/unread, forwarding rules between BBSs. The mental model is an inbox you eventually read.

A program reading from a "queue named `weather`" wants none of that. It doesn't want subject lines, it doesn't want to be parsed out of an SMTP-shaped envelope, it doesn't want to route around BBS forwarding policy. It wants `byte[] payload` arriving on a socket.

DAPPS uses the *peering and routing patterns* mail has worked out — store-and-forward with eventual delivery — without inheriting the mail-shaped surface area.

## App interface: MQTT or REST

DAPPS exposes two parallel interfaces for local apps. They share the same queue; pick whichever suits your language and runtime.

- **MQTT** (embedded broker, default port 1883). Subscribe to `dapps/in/<app>` for inbound, publish to `dapps/out/<app>/<dest>` for outbound, publish a message id to `dapps/ack/<app>` to acknowledge. Real-time delivery, clean session, push model.
- **REST** (`/AppApi/*`). `POST /AppApi/outbound` to submit, `GET /AppApi/inbound/{app}` to poll, `POST /AppApi/inbound/{app}/{id}/ack` to acknowledge. Pull model, easy to test with `curl`.

A single app picks one. Different apps on the same node can pick different ones independently. The full reference for both is in the [README](../README.md#app-interface).

## Where to next

- Walk through [a working hello-world app](tutorial-hello-world.md) in Python, end-to-end.
- Read the [on-air protocol](../README.md#on-air-protocol) if you want to understand what DAPPS speaks between nodes.
- Read [the BPQ getting-started guide](../README.md#getting-started) if you don't have a node running yet.
