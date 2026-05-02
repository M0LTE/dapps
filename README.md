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
- `src=gb7zzz-1` (optional) is the originating callsign — the node where the message was first submitted by a local app. Distinct from the link source (the callsign that handed *this* hop the message); on a single-hop send they're the same, on a multi-hop relay they diverge. Originating nodes MUST set this; forwarding nodes MUST preserve the value verbatim across re-forwards. Receivers expose it as the `dapps-origin` MQTT user property and the REST `OriginatorCallsign` field. Pre-F1 senders omit it; in that case the originator is unknown and apps fall back to the link source.
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

> New to DAPPS? Start with the [concepts page](docs/concepts.md) for the model, then walk through the [Python hello-world tutorial](docs/tutorial-hello-world.md). For a complete API surface (every topic, every endpoint, every user property) see [the reference](docs/reference.md), and [the gallery](docs/gallery.md) has worked examples of common app shapes — group chat, sensor publisher, two-way pager.

### MQTT

Embedded MQTTnet broker, listening on `MqttPort` (default 1883). Connect with any MQTT 5 client; clean session is fine.

| Topic | Direction | Purpose |
|---|---|---|
| `dapps/in/<app>` | DAPPS → app | Apps subscribe; DAPPS publishes incoming messages destined for `<app>` on this node. Each delivery carries `dapps-id` (the 7-char message id) and `dapps-source` (the callsign that handed us this message — the last hop, not necessarily the original sender) as MQTT 5 user properties. If the message was submitted with a TTL, `dapps-ttl` carries the *residual* lifetime in seconds at the moment of delivery (initial TTL minus dwell time on this node). |
| `dapps/out/<app>/<dest-callsign>` | app → DAPPS | Apps publish here to send a message to `<app>` running at `<dest-callsign>`. The payload is the message body. Optional MQTT 5 user property `dapps-ttl` sets the message lifetime in seconds (positive integer); malformed values are ignored and the message is queued without a TTL. |
| `dapps/ack/<app>` | app → DAPPS | Apps publish a message id (as the UTF-8 payload) to acknowledge receipt. |

### REST

Same semantics, pull-based. All endpoints under `/AppApi`:

| Method + path | Body | Purpose |
|---|---|---|
| `POST /AppApi/outbound` | `{ "app": "...", "destCallsign": "...", "payload": <bytes>, "ttl": <seconds, optional> }` | Submit an outbound message. `ttl` (if present) must be a positive integer; non-positive values return `400`. Returns `{ "id": "..." }`. |
| `GET /AppApi/inbound/{app}` | — | Returns `[ { "id": "...", "sourceCallsign": "...", "payload": <bytes>, "ttl": <residual seconds or null> }, … ]` of unacked inbound messages for `{app}`. `ttl` is the *residual* lifetime (initial TTL minus dwell time) at the moment of the GET, or `null` if the message has no TTL. |
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


## Getting started

You'll need:

- A working linbpq instance you control (or another packet node speaking the SV2AGW protocol).
- A licensed callsign with an unused SSID for DAPPS to live under (e.g. `M0LTE-9`).
- A Linux, macOS, or Windows host that can reach BPQ's `AGWPORT` over TCP. Same machine is the simplest case but not required.

DAPPS ships as a self-contained single-file binary; no .NET runtime install, no Docker required. The Linux binaries are built against the .NET 8 LTS baseline (glibc 2.23+), so they run on Raspberry Pi OS 11 (Bullseye) and newer, Debian 11+, Ubuntu 18.04+ — anything modern enough to be in active use.

### 1. Configure BPQ for DAPPS

DAPPS speaks AGW to BPQ for both inbound and outbound. One TCP connection to BPQ, all sessions multiplexed, no co-location requirement.

```ini
; --- AGW emulator: BPQ's standard "talk to me from external apps" surface ---
AGWPORT=8000
AGWSESSIONS=20
; AGWMASK is a bitmask of APPLICATION slots an AGW client may register
; against. Bit N enables slot N+1: 0x01 = slot 1, 0x02 = slot 2,
; 0x04 = slot 3, 0x08 = slot 4, 0x10 = slot 5, 0xFF = slots 1..8.
; If you're going to put DAPPS in slot 5, AGWMASK must include 0x10.
; The simplest thing is 0xFF — permissive within the first 8 slots.
AGWMASK=0xFF

; --- Wire the DAPPS callsign to an APPLICATION slot ---
; Pick a slot number that isn't already in use by another APPLICATION
; line in your config. (Existing nodes often have BBS, CHAT, etc. on
; slots 1 and 2.)
;
; The CMD field (between DAPPS and the callsign) is intentionally empty:
; with AGW, BPQ doesn't run a node command on inbound — it just dispatches
; the inbound L2 'C' frame to whoever has registered the callsign via an
; AGW 'X' frame. The line is still required: without it, BPQ's L2 layer
; doesn't accept frames addressed to the callsign and the AGW registration
; is silently inert (linbpq apps-interface.md).
APPLICATIONS=DAPPS
APPL1CALL=M0LTE-9
APPL1ALIAS=DAPPS
APPLICATION 1,DAPPS,,M0LTE-9,DAPPS,0
```

Replace `M0LTE-9` with your DAPPS callsign. **If `AGWPORT=8000` clashes with something else on the host** (it's a popular default — webmail, etc.), pick another free port; `AGWPORT=8002` is what gb7rdg-node uses for example. Restart linbpq for the changes to take effect.

DAPPS connects to `AGWPORT` from wherever it runs. BPQ doesn't need to reach DAPPS — DAPPS is the AGW client. So **DAPPS and BPQ can live on different hosts** with nothing more than a network route between them; expose `AGWPORT` (firewall it appropriately) on the BPQ host and point DAPPS's `DAPPS_NODE_HOST` at it.

If you're trying out two BPQs over AXIP-UDP for testing, see [`src/dapps/dapps.core.tests/Integration/TwoInstanceLinbpqFixture.cs`](src/dapps/dapps.core.tests/Integration/TwoInstanceLinbpqFixture.cs) for a worked example mirroring the config above.

### 2. Download the DAPPS binary

Grab the latest release for your platform from <https://github.com/M0LTE/dapps/releases>:

- `dapps-linux-x64` — typical desktop / VPS Linux
- `dapps-linux-arm64` — Raspberry Pi 4 / 5 (64-bit Pi OS)
- `dapps-linux-arm` — older Raspberry Pi (32-bit Pi OS)
- `dapps-osx-arm64` — Apple silicon macOS
- `dapps-win-x64.exe` — Windows

```sh
# Example for an x86_64 Linux box:
mkdir -p ~/dapps && cd ~/dapps
curl -L -o dapps https://github.com/M0LTE/dapps/releases/latest/download/dapps-linux-x64
chmod +x dapps
```

The binary writes its database (`dapps.db`) into the current working directory.

### 3. Run it

DAPPS reads its first-time configuration from environment variables; on subsequent starts the values come from `dapps.db` and env vars are ignored.

```sh
export DAPPS_CALLSIGN=M0LTE-9
export DAPPS_NODE_HOST=127.0.0.1     # where BPQ is listening
export DAPPS_AGW_PORT=8000           # match BPQ's AGWPORT
export DAPPS_DEFAULT_BPQ_PORT=0      # 0-indexed BPQ port byte for outbound (BPQ port 1)
export DAPPS_MQTT_PORT=1883          # embedded MQTT broker port (for app subscribers)

# Where the HTTP API binds. Default is localhost:5000 — override to expose on the LAN.
export ASPNETCORE_URLS=http://127.0.0.1:5000

./dapps
```

DAPPS refuses to start with the placeholder `N0CALL` value — set `DAPPS_CALLSIGN` to a real callsign before the first run, or POST to `/Config` after it.

You should see startup logs ending with something like:

```
BPQ AGW: 127.0.0.1:8000 (default port byte 0)
MQTT broker: localhost:1883
Now listening on: http://localhost:5000
```

Browse to <http://localhost:5000/>. The first request hits a one-shot setup form — pick a sysop password, you're signed in, and the dashboard appears. From then on, signing out goes back through `/Login` with that password. The cookie lasts 90 days, sliding. **Set the password before binding off loopback** so the LAN doesn't get a moment to set it for you. Forgot the password? Stop dapps, run `sqlite3 /var/lib/dapps/dapps.db "delete from systemoptions where option like 'AdminPassword%';"`, restart, and the setup form re-arms.

To bind the HTTP API on a different interface or port, set `ASPNETCORE_URLS` before launch — e.g. `ASPNETCORE_URLS=http://0.0.0.0:5000` to make the dashboard reachable from elsewhere on the LAN, or `http://0.0.0.0:8080` to move ports.

App-interface auth (separate from the dashboard's sysop password — it gates `/AppApi/*` and the MQTT broker for *applications*) is opt-in. By default any app on the host can act as any app name; to require per-app tokens:

1. Mint a token for each app: `curl -X POST http://localhost:5000/AppTokens -H 'content-type: application/json' -d '{"App":"myapp"}'` — capture the returned `token` (it's only shown once).
2. POST `{"AuthRequired":true, ...}` to `/Config` (or set `DAPPS_AUTH_REQUIRED=true` before first start).
3. Restart. MQTT clients now CONNECT with `username=app` + `password=token`; REST clients send `Authorization: Bearer <token>` on `/AppApi/*`. Topic / endpoint scope is enforced — an app authenticated as `myapp` can only act on its own slot.

### 4. Add a neighbour

Tell DAPPS where to forward messages destined for another node:

```sh
curl -X POST http://localhost:5000/Neighbours \
     -H 'content-type: application/json' \
     -d '{"callsign":"G7XYZ-9","bpqPort":0}'
```

`bpqPort` is the BPQ port byte (0-indexed) DAPPS should originate connects through. List with `GET /Neighbours`, remove with `DELETE /Neighbours/G7XYZ-9`.

### 5. Verify the link

From any BPQ node prompt that can route to your callsign:

```
C M0LTE-9
```

You should see `*** Connected to DAPPS` followed by the DAPPSv1 prompt:

```
DAPPSv1>
```

Type `info` for help, `q` to quit.

### 6. Run as a system service

The shell-based start above is fine for kicking the tyres. For a real deployment you want DAPPS coming up at boot, surviving crashes, logging to journald, with its files in conventional places. Here's the layout that's been validated end-to-end on Raspberry Pi OS — adapt for your distro / sysv init system as needed.

**File layout:**

| Path | What it is |
|---|---|
| `/opt/dapps/dapps` | the binary, root-owned, mode 755 |
| `/etc/dapps.env` | env-var bootstrap (callsign, ports). Mode 640, group `dapps` |
| `/var/lib/dapps/` | working directory; `dapps.db` lands here. Owned by the `dapps` user, mode 750 |
| `/etc/systemd/system/dapps.service` | the unit file (below) |

**Create the user, directories, and download the binary** (one-time, as root):

```sh
sudo useradd --system --shell /usr/sbin/nologin --home-dir /var/lib/dapps --no-create-home dapps
sudo mkdir -p /opt/dapps /var/lib/dapps
sudo chown dapps:dapps /var/lib/dapps && sudo chmod 750 /var/lib/dapps

# Replace `linux-arm` with the RID for your platform.
sudo curl -L --fail \
    -o /opt/dapps/dapps \
    https://github.com/M0LTE/dapps/releases/latest/download/dapps-linux-arm
sudo chmod 755 /opt/dapps/dapps
```

**Write `/etc/dapps.env`:**

```sh
# /etc/dapps.env — sourced by the dapps systemd unit.
DAPPS_CALLSIGN=M0LTE-9
DAPPS_NODE_HOST=127.0.0.1
DAPPS_AGW_PORT=8000
DAPPS_DEFAULT_BPQ_PORT=0
DAPPS_MQTT_PORT=1883
```

After first start the values land in `dapps.db`; the env vars are ignored on subsequent starts. Keep `/etc/dapps.env` around anyway — it's the operator's record of the bootstrap values. The dashboard's sysop password isn't here — it's set through the browser on first access (see §3).

```sh
sudo install -m 640 -o root -g dapps /dev/stdin /etc/dapps.env <<EOF
... (paste the above, edit values) ...
EOF
```

**Install `/etc/systemd/system/dapps.service`:**

A ready-to-use unit file lives in [`scripts/dapps.service`](scripts/dapps.service). Drop it into place:

```sh
sudo curl -L --fail \
    -o /etc/systemd/system/dapps.service \
    https://raw.githubusercontent.com/M0LTE/dapps/master/scripts/dapps.service
```

Or copy from a local clone (`sudo install -m 644 scripts/dapps.service /etc/systemd/system/`).

The unit binds the dashboard to `127.0.0.1:5000` by default (loopback only — set the sysop password through the browser before exposing on the LAN), runs as the unprivileged `dapps` user with the standard systemd hardening flags, and includes `RestartPreventExitStatus=78` so dapps doesn't crash-loop on operationally-fatal config errors (e.g. when an MQTT port conflict means restarting won't help — the journal will surface a clear error and dapps will stop, instead of retrying forever).

**Enable + start:**

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now dapps.service
sudo systemctl status dapps.service
sudo journalctl -u dapps.service -f       # live logs
```

A correct startup looks like the same lines you saw in §3 — `BPQ AGW: ...`, `MQTT broker: ...`, `Now listening on: ...` — followed by the AGW dispatcher reporting it's connected.

**Upgrades** are a binary swap:

```sh
sudo curl -L --fail \
    -o /opt/dapps/dapps.new \
    https://github.com/M0LTE/dapps/releases/latest/download/dapps-linux-arm
sudo install -o root -g root -m 755 /opt/dapps/dapps.new /opt/dapps/dapps
sudo rm /opt/dapps/dapps.new
sudo systemctl restart dapps.service
```

The DB schema auto-migrates on next start; existing rows are preserved.

## Troubleshooting

- **`Callsign is not configured`** at startup → set `DAPPS_CALLSIGN` env var (or `/etc/dapps.env`) and restart, or POST to `/Config`.
- **`Did not see DAPPSv1> prompt`** when forwarding → the remote DAPPS isn't reachable through BPQ. Check the neighbour's BPQ-side `APPLICATION` line and that it's running.
- **`AGW register ... failed`** in the logs → BPQ isn't accepting the registration. Check `AGWMASK` covers the slot you used (e.g. slot 5 needs `0x10`; `0xFF` is permissive within the first 8) and that the AGW port is reachable from where DAPPS is running.
- **`linbpq` started but isn't listening on `AGWPORT`** (no entry on `ss -tlnp | grep AGWPORT`) → silently lost a port-bind race against another service. Pick a different `AGWPORT` (8002, 8004, etc.) in `bpq32.cfg`, set `DAPPS_AGW_PORT` to match, restart both. Default 8000 is popular and often taken.
- **Inbound L2 connects to DAPPS's callsign hit the node prompt instead of being dispatched** → check that `bpq32.cfg` has the `APPLICATION N,DAPPS,,<call>,DAPPS,0` line for that callsign. Without it, BPQ treats the inbound as a regular node session and the AGW `'X'` registration is silently inert.
- **`APPLICATION` slot collision** → if you already have BBS / CHAT / etc. on slots 1–4, put DAPPS on a free slot (5+) and adjust `AGWMASK` accordingly. Slots are 1-indexed; the line is `APPLICATION <slot>,DAPPS,...`.
- **Port byte indexing surprises** → AGW port bytes are 0-based; BPQ port numbers in `bpq32.cfg` are 1-based. AGW port byte 0 = BPQ port 1.
- **Pre-existing `/lib/.../libstdc++.so.6: version GLIBCXX_3.4.29 not found` or similar** → you're on a glibc older than the .NET 8 baseline (2.23). Pi OS 11 / Bookworm / Bullseye are all fine; older systems aren't supported.
- **Dashboard says `connecting…` forever on the Live inbound feed** → the SSE connection isn't getting through. If you're behind a reverse proxy, make sure it doesn't buffer `text/event-stream` (`X-Accel-Buffering: no` is set server-side; nginx may still need `proxy_buffering off`).

## Backups

Back up `dapps.db` (the SQLite file in the working directory). It carries every queued message, neighbour entry, and config value.

## Upgrades

Replace the binary; restart. The schema auto-migrates: new columns are added to existing tables on next start, no manual steps required. Existing rows are preserved.
