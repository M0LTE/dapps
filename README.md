# DAPPS

DAPPS is an acronym for Distributed Asyncronous Packet Pub-Sub.

The idea of DAPPS is to overlay an asyncronous pub-sub messaging subsystem over a packet radio network.

Ultimately, the vision is for an application at one node to be able to post a message to a queue at a remote node, without caring about the details of delivery whatsoever.

Realtime connectivitity is not a goal. Think of this roughly as packet mail for application developers.

This does not replace a packet node, but complements it (and indeed requires one).

This does not deliver any user-facing functionality on its own, but simplifies building of network applications.

This does not replace packet mail, but could piggy back on some the peering/routing arrangements in place for mail, particularly in its early days. It could also potentially enhance mail delivery.

This does not replace AX.25, but can run over it. It could also run over other transports, so conceptually isn't limited strictly to packet radio.

This implementation is not intended to be the only implementation - multiple implementations are healthy. The application interface and on-air protocol need to be compatible, but that's it. Nothing in here is technology-specific.

## Credits

To all at OARC who participated in the RFC, helping take this from a rough idea to a workable system.

## On-air protocol

### Prompt

When connecting to a DAPPS instance, expect a prompt:

```
DAPPSv1>\n
```

### Offering a message

The fully-decorated form of an offer line is:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value chk=6907\n
```

The minimum hand-craftable form is:

```
ihave 7b502c3 len=11 dst=appname@gb7aaa-4\n
```

— message id, payload length, destination, and nothing else. The id is the first 7 hex chars of the SHA1 of the payload bytes (no `s=` salt means no prefix). For the literal payload `Hello world` (11 bytes, no trailing newline):

```
$ printf 'Hello world' | sha1sum
7b502c3a1f48c8609ae212cdfb639dee39673f5e  -
```

The first 7 hex characters → id `7b502c3`. With `s=N` set, you'd instead hash the 8-byte little-endian representation of `s` followed by the payload, e.g. `printf '<8 raw bytes><payload>' | sha1sum` — a one-liner in any language with a binary-pack helper but not pleasant by hand. Computing the id is the only step a hand-typer can't avoid; every other field has a default and can be dropped.

The cost of dropping fields is operational peril rather than a protocol error: no salt means identical-content messages dedup; no `ttl` lets a forwarder pick its own default; no `chk` leaves you trusting the link layer's own corruption detection; no `fmt`/`clen` means plain payload only.

**Required:** the message id (`abcdeff` here), `len`, and `dst`. Plus `clen` when `fmt=d`. Everything else is optional.

Where:

- `abcdeff` (**required**) is the SHA1 hash of the (decompressed) payload bytes, optionally prefixed by the 8-byte little-endian representation of `s` when supplied: `sha1(payload)[:7]` or `sha1(s_bytes_le ++ payload)[:7]`. Truncated to the first 7 hex characters this serves as a unique message id (inspired by git hashes).
- `len=11` (**required**) is the size of the **decompressed** payload in bytes.
- `fmt=p` (optional, default `p` if absent) declares the payload bytes on the wire are the literal payload (**p**lain). `fmt=d` declares them Deflate-compressed, in which case `clen=N` MUST also be supplied, giving the number of **c**ompressed bytes the receiver reads from the wire before decompressing. `clen` MUST NOT appear when `fmt` is `p` or absent.
- `s=12345678` (optional) is a 64-bit signed integer salt (decimal on the wire) folded into the message ID hash. Its job is to disambiguate identical-payload messages so they get distinct IDs (otherwise two senders shouting "hello" generate the same message ID and dedup as one). Sender's choice of meaning — a unix-epoch timestamp, a counter, anything sufficiently distinct.
- `dst=appname@gb7aaa-4` (**required**) is the routing destination: `gb7aaa-4` is the call+SSID of the DAPPS instance the message is bound for, and `appname` is the application / topic / queue name on that instance.
- `ttl=86400` (optional) is the residual lifetime in seconds. The sender sets it to "how many more seconds should this still be valid for" at the moment of sending; each forwarding DAPPS instance decrements `ttl` by the time the message spent in its queue before re-sending. Any node MUST drop a message whose `ttl` reaches zero or below. IP-TTL semantics, but in seconds rather than hops.
- `key=value` (optional) is zero or more arbitrary headers. Use sparingly and not in place of payload. Keys and values MUST NOT contain space, `=`, or newline.
- `chk=6907` (optional, rarely needed on AX.25) is a 4-character hex CRC-16/CCITT-FALSE checksum covering the `ihave` line bytes only — it does not protect the payload that follows after `data` (the message id does that). See below for the rationale and when it's worth using. When supplied it MUST be the last key-value pair; it is not a terminator.
- `\n` is a newline byte (0x0A), not the literal string `\n`.

#### "ihave" command checksum

`chk` is **optional and, on packet radio, rarely worth bothering with** — every AX.25 frame already carries a 16-bit FCS at the link layer, so corruption that would show up at the DAPPS layer has already been caught and retransmitted underneath. Most senders should omit it.

The field exists at all for two reasons:

1. DAPPS isn't strictly tied to AX.25 — it can run over transports without strong line integrity, and `chk` is the only protection on those.
2. The message id (`sha1(payload)`) covers the payload but does **not** cover `dst`, `ttl`, or `key=value` headers — those bytes never enter the hash. On a transport without its own line CRC, a bit-flip in `dst=` would silently mis-route a message whose id check still passes. `chk` is what closes that gap.

So: keep `chk` in your back pocket for non-AX.25 transports or belt-and-braces deployments; default to omitting it on a packet-radio link. Manual / interactive / debugging use omits it entirely — CRC-16 isn't computable by hand. **If you're not using `chk`, skip the rest of this section** — the line still ends with `\n` either way.

When `chk` *is* supplied, compute and validate it as follows:

1. `chk=NNNN` MUST be the last key-value pair on the line, immediately followed by `\n`. Receivers MUST reject any `ihave` line where `chk=` appears in any non-final position.
2. Take the line bytes from the first byte of `ihave ` up to *but not including* the leading space before `chk=NNNN` — i.e. drop the trailing ` chk=NNNN\n` (one space, the `chk=NNNN` token, and the newline). Compute CRC-16/CCITT-FALSE (polynomial `0x1021`, initial value `0xFFFF`, no reflection, final XOR `0x0000`) over those bytes. The lowercase 4-character hex rendering of the resulting 16-bit value is `NNNN`.

Pinning `chk` to the last position when present lets both sides do this with a single `lastIndexOf(" chk=")` and a substring — no field re-normalisation, no awareness of how the rest of the KVs were spaced.

##### Example

For the offer line:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value chk=6907\n
```

The bytes covered (everything before ` chk=6907\n`) are:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value
```

Their CRC-16/CCITT-FALSE value is `0x6907`. Rendered as 4 lowercase hex characters that's `6907` — matching the value sent over the air, so the line is intact.

### Sending a message

If the remote instance wants a message, it will reply with:

```
send abcdeff\n
```

At any time, send the message as follows:

```
data abcdeff\n
```

followed immediately by the raw payload bytes. 

Similarly to MQTT, the payload encoding is completely delegated to the consuming application - DAPPS just passes byte arrays.

When the remote DAPPS instance has received the expected number of bytes, it will reply with:

```
ack abcdeff\n
```

If the payload bytes received by the remote end did not match the id hash, it will reply with:

```
bad abcdeff\n
```

The remote DAPPS instance will not respond at all until it has received the specified number of payload bytes (`len` bytes when `fmt=p`, `clen` bytes when `fmt=d`), at which point it will respond immediately with `ack` or `bad` — there is no message terminator. The hash check that decides between `ack` and `bad` is performed against the **decompressed** bytes.

If the remote instance does not want a message, it will reply to `send` with a message like `no abcdeff\n`. 

### Polling for messages

The following command may be sent by the calling node to request that the remote node discloses any messages waiting for it, using the `ihave` and `send` pattern:

```
rev\n
```

### Multiple messages

`ihave` and `send` may be sent multiple times in a session. `send` may be followed by multiple message ids, space separated, to signify that the remote server wants to accept multiple messages which have been offered.

### Timeouts

Both sides SHOULD apply an inactivity timeout while waiting for an expected reply — payload bytes after `data <id>\n` (receiver side), or `send` / `no` / `ack` / `bad` (sender side) — and close the session if exceeded. No specific value is mandated; AX.25's T3 (~3 minutes) is a reasonable starting point on a packet-radio link.

On timeout, just close the session. No error frame is needed: a stalled peer can't be relied upon to receive one, and the content-addressed message id makes retry idempotent — when the sender reconnects later the receiver dedups by id, so a timed-out exchange can be safely re-attempted from scratch.

For DAPPS-over-AX.25 the link layer's own T3 inactivity timer largely handles this case for free, but a DAPPS-level timeout is still useful for non-AX.25 transports and for software-side stalls where the underlying link is healthy.

### Discovering neighbours and exchanging routes

Very much tbc. 

I see a hand-wavey model along the lines of:

- DAPPS instance advertises its presence on air with short UI frame broadcasts (requiring the node to allow external applications to send UI frames)
- Other DAPPS instances listen out for those broadcasts (requiring the node to provide a feed of frames heard - MQTT?)
- At randomised long intervals, DAPPS instances connect to other instances they have heard and tell each other about DAPPS nodes they know about. Not all the routing information needs to go every time, more along the lines of "oh, by the way, I can connect to GB7AUG". This could be based on keeping track of what stations the node is able to successfully link to. (Requires the node to provide a feed of link connect / disconnect events to DAPPS)
- From those exchanges, each DAPPS instance should build up a local routing table (space is no longer a problem)
- From that routing table, DAPPS can route messages appropriately, including to intermediate DAPPS instances.

## Application interface

DAPPS exposes two parallel surfaces for local applications: an embedded MQTT broker (real-time pub/sub) and a REST API (POST + poll). Both share the same SQLite-backed queue and the same ack contract — pick whichever fits your app.

### MQTT

Embedded MQTTnet broker, listening on `MqttPort` (default 1883). Connect with any MQTT 5 client; clean session is fine.

| Topic | Direction | Purpose |
|---|---|---|
| `dapps/in/<app>` | DAPPS → app | Apps subscribe; DAPPS publishes incoming messages destined for `<app>` on this node. Each delivery carries `dapps-id` (the 7-char message id) and `dapps-source` (the callsign that handed us this message — the last hop, not necessarily the original sender) as MQTT 5 user properties. |
| `dapps/out/<app>/<dest-callsign>` | app → DAPPS | Apps publish here to send a message to `<app>` running at `<dest-callsign>`. The payload is the message body. |
| `dapps/ack/<app>` | app → DAPPS | Apps publish a message id (as the UTF-8 payload) to acknowledge receipt. |

### REST

Same semantics, pull-based. All endpoints under `/AppApi`:

| Method + path | Body | Purpose |
|---|---|---|
| `POST /AppApi/outbound` | `{ "app": "...", "destCallsign": "...", "payload": <bytes> }` | Submit an outbound message. Returns `{ "id": "..." }`. |
| `GET /AppApi/inbound/{app}` | — | Returns `[ { "id": "...", "sourceCallsign": "...", "payload": <bytes> }, … ]` of unacked inbound messages for `{app}`. |
| `POST /AppApi/inbound/{app}/{id}/ack` | — | Mark message `{id}` as delivered. Idempotent. |

REST and MQTT can be used by different apps simultaneously — they're just two views of the same queue.

### Durability and at-least-once semantics

**DAPPS is the durable layer; the broker is just a real-time delivery channel.** Messages live in SQLite from the moment DAPPS receives them until the app explicitly acks. The broker holds no persistent state of its own — and that's intentional, because designing around broker persistence means designing around its limits (per-clientId persistent sessions, retention bounds, etc.) that don't fit a "queue of pending messages for an app that may not have run yet" model.

What this gets you in practice:

- **App offline when message arrives** — message persists in SQLite. When the app eventually subscribes to `dapps/in/<app>` (or polls `GET /AppApi/inbound/<app>`), it sees the backlog.
- **DAPPS restarts mid-delivery** — broker is fresh, DB still has the row with `LocallyDelivered=0`. App reconnects, re-subscribes, replay fires.
- **App processed the message but DAPPS crashed before persisting the ack** — same as above; the message will be redelivered on next subscribe.

The cost: **apps must be idempotent on `dapps-id`**. The message id is content-addressed (`sha1(salt + payload)[:7]`), so a redelivered message arrives with the same id — apps that store "ids I've already processed" handle duplicates correctly without further coordination. This is the natural shape for at-least-once delivery and matches what the spec already implies via content-addressed ids.

Don't use MQTT retained messages — they'd give you "last message wins on this topic", which is the wrong shape for a queue. Replay-on-subscribe is the right pattern for "everything pending."

## Progress

Very much work in progress.

### Implemented

- A dummy app which pretends to be a DAPPS client connecting through from a correctly configured BPQ instance, to emulate receiving a call from a remote DAPPS instance over the air without involving any hardware
- A real DAPPS server which implements:
  - `ihave` command - allowing a message to be offered - with all of the documented bits supported
  - `chk=nn` checksum validation of `ihave` command
  - `data` command - allowing a message payload to be sent
  - payload integrity checking and error responses
  - saving of offers and payloads into a sqlite database
  - `ack` and `bad` responses to the `data` command
  - Deflate-compressed and uncompressed message payloads

### Roadmap

- onward transmission of messages to another DAPPS instance
  - starting with having DAPPS make outbound connections to other nodes through BPQ telnet
  - this will include validating a real DAPPS server against a real BPQ instance
- manual routing between DAPPS instances
- application interface and a sample app
- packaging / distribution
- automatic routing (route discovery) between DAPPS instances
- `rev` command - polling for waiting remote messages - not a huge priority because, like with mail, if both partners are set up for forward connections and immediate sending, polling should not be required
- compatibility with nodes other than BPQ
- a human-interactive mode in the node application - to allow a human to enter a message by hand for testing/fun
- multi-part messages
- some kind of web interface for config

## References

BPQ binary transparency tests: https://github.com/M0LTE/bpq-fbb-test-apps

### Previous writing

Caution, may not align with current thinking.

https://gist.github.com/M0LTE/be1fd071ca1867703d1f2d4c17fabca2


## Installation

Plan is to go docker-first.

Rough steps:

### Install docker engine on Debian / Raspberry Pi OS 64 bit (bookworm)

```
# install docker engine, use --dry-run to inspect
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh ./get-docker.sh

# run containers without root:
sudo usermod -aG docker $USER
newgrp docker
docker run hello-world

# fix up logging
echo "{
  \"log-driver\": \"local\",
  \"log-opts\": {
    \"max-size\": \"10m\"
  }
}" | sudo tee /etc/docker/daemon.json
sudo systemctl restart docker.service
```

then:

```
mkdir dapps
cd dapps
echo "services:
  dapps-core:
    image: m0lte/dapps-core
    restart: unless-stopped
    ports:
      - 11000:11000
      - 8099:8080
    volumes:
      - ./dapps-data:/app/data
    extra_hosts:
      - host.docker.internal:host-gateway
    environment:
      - BPQ_HOST=host.docker.internal
      - MQTT_HOST=mqtt
      - MQTT_PORT=1833
      - MQTT_USERNAME=
      - MQTT_PASSWORD=
" | tee docker-compose.yml
docker compose up -d
```

then browse to http://your-node:8099/swagger and you should see an API. Dapps is up...

maybe one day it will have a UI
