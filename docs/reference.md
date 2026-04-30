# DAPPS app interface reference

The complete shape of the local app interface — both surfaces, every field, every property.

If you're new, read [concepts.md](concepts.md) first and walk through [tutorial-hello-world.md](tutorial-hello-world.md). This page assumes you know what `app@callsign` means and have written one app already.

The on-air protocol DAPPS speaks *between nodes* is documented separately in [the README](../README.md#on-air-protocol). This page is only about the local app surface.

## MQTT

Embedded MQTTnet broker, default port 1883. Speak MQTT 5 — clean session is fine and recommended. The broker holds no persistent state; messages are durable in DAPPS' SQLite queue, the broker is just the real-time delivery channel ([why](../README.md#durability-and-at-least-once-semantics)).

### Topics

#### `dapps/in/<app>` — inbox

DAPPS publishes one message here for each pending delivery to `<app>` on this node. Subscribe with QoS 1.

**Replay on subscribe**: every pending message arrives the moment you subscribe — even ones submitted while the app was offline. There is no separate "fetch backlog" call. A subscriber that disconnects, reconnects, and re-subscribes will see every still-unacked message again.

**Payload**: the raw bytes the sender submitted. No envelope, no length prefix, no encoding imposed by DAPPS — interpret them however the app expects.

**User properties**:

| Name | Type | Meaning |
|---|---|---|
| `dapps-id` | string (7 hex chars) | The message id. Use this as the idempotency key. |
| `dapps-source` | string (callsign) | The callsign that handed us this message. In a single-hop scenario it's the original sender; in a multi-hop scenario it's the last hop. (End-to-end source tracking is planned — see Phase F1 in `plan.md`.) |
| `dapps-ttl` | string (decimal int) | *Optional.* Residual lifetime in seconds at the moment of delivery. Absent if the sender didn't set a TTL. |

#### `dapps/out/<app>/<dest-callsign>` — outbox

Publish here to submit a message for `<app>` running at `<dest-callsign>`. Use QoS 1.

The destination is encoded in the topic, not the payload. So submitting to `dapps/out/chat/G7XYZ` is "send to the `chat` app at G7XYZ"; submitting to `dapps/out/chat/M0LTE-1` is a different message to a different node, even if the bytes are identical.

**Payload**: the raw bytes you want delivered. DAPPS does not transform them.

**User properties** (all optional):

| Name | Type | Meaning |
|---|---|---|
| `dapps-ttl` | string (positive decimal int) | Initial TTL in seconds. Decremented at each forwarder by the time the message dwelt in that hop's queue. Malformed values (non-integer, zero, negative) are silently ignored — the message is queued without a TTL, since the broker has no way to NACK a publish after the fact. |

#### `dapps/ack/<app>` — acknowledge

Publish a 7-character message id (as the UTF-8 payload) to mark it processed. Idempotent — re-acking is a no-op. Until you ack, DAPPS treats the message as still pending and will redeliver on next subscribe.

QoS 1 is fine; QoS 0 is acceptable since at worst the message gets redelivered (and your idempotency check will catch it).

### Authentication

When `SystemOptions.AuthRequired` is enabled (off by default; see [Auth](../README.md#auth-on-the-app-interface)):

- CONNECT must include `username=<app>` and `password=<token-plaintext>`.
- An app authenticated as `chat` may only publish to `dapps/out/chat/*` and `dapps/ack/chat`, and only subscribe to `dapps/in/chat`. Cross-app traffic is rejected with `NotAuthorized`.

Mint tokens via `POST /AppTokens` (admin endpoint, currently expected to be reached over loopback).

## REST

Same queue, pull-based. All endpoints under `/AppApi`. Default port 5000. Set `ASPNETCORE_URLS` to bind elsewhere.

### `POST /AppApi/outbound`

Submit a message.

**Request body**:

```json
{
  "app": "chat",
  "destCallsign": "G7XYZ",
  "payload": "<base64-encoded bytes>",
  "ttl": 300
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `app` | string | yes | App name. Must not be blank. |
| `destCallsign` | string | yes | Destination callsign. Must not be blank. |
| `payload` | bytes (base64 in JSON) | yes | Must be at least one byte. Empty payloads return `400`. |
| `ttl` | int seconds | no | Must be positive if present. `0` or negative returns `400`. |

**Response**: `200 OK` with `{ "id": "abc1234" }`.

**Errors**: `400` on missing/empty fields or non-positive TTL. `403` if `AuthRequired` is on and the bearer token doesn't match `app`.

```bash
curl -X POST http://localhost:5000/AppApi/outbound \
    -H 'Content-Type: application/json' \
    -d '{"app":"chat","destCallsign":"G7XYZ","payload":"aGVsbG8=","ttl":300}'
# {"id":"abc1234"}
```

### `GET /AppApi/inbound/{app}`

List currently unacknowledged messages for `{app}`.

**Response**: `200 OK` with a JSON array. Each entry:

```json
{
  "id": "abc1234",
  "sourceCallsign": "G7XYZ",
  "payload": "<base64-encoded bytes>",
  "ttl": 287
}
```

| Field | Type | Notes |
|---|---|---|
| `id` | string | Stable across redeliveries. Use as idempotency key. |
| `sourceCallsign` | string | Same semantics as `dapps-source` on MQTT — last hop, not necessarily original sender. |
| `payload` | bytes (base64) | Raw payload. |
| `ttl` | int seconds, nullable | **Residual** lifetime — initial TTL minus dwell time on this node. `null` if the message has no TTL. The same number an MQTT subscriber would see on the `dapps-ttl` user property. |

```bash
curl http://localhost:5000/AppApi/inbound/chat
# [{"id":"abc1234","sourceCallsign":"G7XYZ","payload":"aGVsbG8=","ttl":287}]
```

### `POST /AppApi/inbound/{app}/{id}/ack`

Mark `{id}` as processed. Idempotent.

**Response**: `204 No Content`.

```bash
curl -X POST http://localhost:5000/AppApi/inbound/chat/abc1234/ack
```

### Authentication

When `SystemOptions.AuthRequired` is on, send `Authorization: Bearer <token>` on every `/AppApi/*` request. The token's app scope must match the `{app}` path segment (or the `app` field in the body for `POST /AppApi/outbound`); mismatches return `403`.

## Idempotency contract

DAPPS guarantees **at-least-once** delivery. The same message can arrive twice or more in the following scenarios:

1. The app processed the message but DAPPS crashed before recording the ack.
2. The app processed the message but failed to publish the ack (broker disconnect, network blip).
3. The app published the ack but DAPPS hasn't yet seen it (rare, but possible during shutdown).

In every case, the redelivered message arrives with **the same `dapps-id`**. The id is content-addressed:

```
id = sha1(salt_le_8_bytes ++ payload)[:7]
```

Where `salt` is set to the submission timestamp in milliseconds since epoch. Two submissions of the same payload, milliseconds apart, get different ids.

The recommended app pattern is:

```
on receive (id, payload):
    if seen(id):
        ack(id)               # drain the queue without re-doing work
        return
    do_real_work(payload)
    record_seen(id)           # before the ack, in the same transaction if possible
    ack(id)
```

The `seen` set should be persistent (SQLite, Redis, a file). An in-memory `Set<string>` works for demos but loses every memory of past work on restart, which means on the first connect after a restart the app will redo every still-unacked message.

A `seen` set can be size-bounded:

- **Bounded by TTL**: keep `(id, expiry)` rows; sweep periodically. The TTL on the row should comfortably exceed the longest expected redelivery window — any pending message in DAPPS' queue can be redelivered, so use the largest TTL you'd reasonably accept on submission as a lower bound.
- **Bounded by count** (LRU): cheap, works in practice, fails badly if a backlog of old still-pending messages exceeds the LRU capacity. Pair with a maximum `dapps-ttl` you'll honour.

If you process a message and the work is itself idempotent (e.g. UPSERT-shaped database mutation), you can skip the `seen` set entirely — at-least-once redelivery becomes a non-issue. This is often the easiest path.

## Limits

DAPPS deliberately does not impose a hard payload-size limit at the app interface — submit any byte array. In practice you are constrained by:

- **The on-air bearer.** AX.25 frames are typically 256 bytes; a 4kB payload becomes ~16 frames per hop, which under realistic packet-radio conditions might mean retries and noticeable latency. MeshCore frames are smaller (~150 bytes) and unacknowledged at the radio layer. UDP-datagram backhaul (the test bearer) is configured with low MTU specifically to force fragmentation, exercising packetiser code paths that real RF bearers will hit.
- **Multi-part fragmentation** is planned (Phase F2 in `plan.md`) but not implemented yet. Until it lands, any single message must fit in one DAPPSv1 `data` block; the practical ceiling is whatever the bearer's connection-oriented stream can carry without hitting application or transport timeouts. For sub-1kB payloads on AX.25 you should expect this to "just work"; above that, test before relying on it.
- **The id collision space** is `2^28` (~268M unique ids). Two messages submitted with the same salt and the same payload get the same id, which is fine — they're the same message. Two messages with different content but the same first 7 hex of `sha1(salt ++ payload)` would collide, but at content scale that's astronomically unlikely. If your app submits *millions of distinct messages per day from a single source*, you may want to think about this; otherwise it's not a concern.

## Topic / endpoint summary

| What you want to do | MQTT | REST |
|---|---|---|
| Receive incoming messages | Subscribe `dapps/in/<app>` (QoS 1) | `GET /AppApi/inbound/{app}` (poll) |
| Send a message | Publish `dapps/out/<app>/<dest>` (QoS 1) | `POST /AppApi/outbound` |
| Acknowledge a message | Publish id to `dapps/ack/<app>` | `POST /AppApi/inbound/{app}/{id}/ack` |
| Read message id | `dapps-id` user property | `id` field on inbound JSON |
| Read source callsign | `dapps-source` user property | `sourceCallsign` field |
| Read residual TTL | `dapps-ttl` user property (absent if no TTL) | `ttl` field (`null` if no TTL) |
| Set TTL on submit | `dapps-ttl` user property | `ttl` field |

## See also

- [`docs/concepts.md`](concepts.md) — the model, why-DAPPS-not-AX.25, the design choices.
- [`docs/tutorial-hello-world.md`](tutorial-hello-world.md) — a Python app, end-to-end.
- [`docs/gallery.md`](gallery.md) — worked examples of common app shapes (chat, sensor, pager).
- [README on-air protocol](../README.md#on-air-protocol) — what DAPPS speaks between nodes (most apps don't need this).
