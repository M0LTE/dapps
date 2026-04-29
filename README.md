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

To offer a message to a remote DAPPS instance, send the following as bytes:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value chk=2c\n
```

where:
- `abcdeff` is the SHA1 hash of the (decompressed) payload bytes, optionally prefixed by the 8-byte little-endian representation of `s` when supplied: `sha1(payload)[:7]` or `sha1(s_bytes_le ++ payload)[:7]`. Truncated to the first 7 hex characters this serves as a unique message id (inspired by git hashes).
- `len=11` is the size of the **decompressed** payload in bytes. Always required.
- `fmt=p` declares the payload bytes on the wire are the literal payload (**p**lain). `fmt=d` declares them Deflate-compressed, in which case `clen=N` MUST also be supplied, giving the number of **c**ompressed bytes the receiver reads from the wire before decompressing. `clen` MUST NOT be supplied when `fmt=p`.
- `s=12345678` is an optional 64-bit signed integer salt (decimal on the wire) folded into the message ID hash. Its job is to disambiguate identical-payload messages so they get distinct IDs (otherwise two senders shouting "hello" generate the same message ID and dedup as one). Sender's choice of meaning — a unix-epoch timestamp, a counter, anything sufficiently distinct.
- `dst=appname@gb7aaa-4` is the routing destination: `gb7aaa-4` is the call+SSID of the DAPPS instance the message is bound for, and `appname` is the application / topic / queue name on that instance. Always required.
- `ttl=86400` is an optional residual lifetime in seconds. The sender sets it to "how many more seconds should this still be valid for" at the moment of sending; each forwarding DAPPS instance decrements `ttl` by the time the message spent in its queue before re-sending. Any node MUST drop a message whose `ttl` reaches zero or below. IP-TTL semantics, but in seconds rather than hops — and the wire cost stays small regardless of year.
- `key=value` is zero or more arbitrary headers. Use sparingly and not in place of payload. Keys and values MUST NOT contain space, `=`, or newline.
- `chk=2c` is an optional 2-character hex checksum guarding against bit-flips on the line (see below). When supplied it MUST be the last key-value pair; it is not a terminator.
- `\n` is a newline byte (0x0A), not the literal string `\n`.

#### "ihave" command checksum

`chk=NN` is **optional**. Manual / interactive / debugging use omits it entirely — the underlying packet transport's own corruption detection (AX.25 FCS, etc.) is usually sufficient, and computing a SHA1 prefix by hand is impractical. **If you're not using `chk`, skip the rest of this section** — the line still ends with `\n` either way.

When `chk` *is* supplied, compute and validate it as follows:

1. `chk=NN` MUST be the last key-value pair on the line, immediately followed by `\n`. Receivers MUST reject any `ihave` line where `chk=` appears in any non-final position.
2. Take the line bytes from the first byte of `ihave ` up to *but not including* the leading space before `chk=NN` — i.e. drop the trailing ` chk=NN\n` (one space, the `chk=NN` token, and the newline). Compute SHA1 over those bytes. The lowercase hex of the first byte of the resulting digest is `NN`.

Pinning `chk` to the last position when present lets both sides do this with a single `lastIndexOf(" chk=")` and a substring — no field re-normalisation, no awareness of how the rest of the KVs were spaced.

##### Example

For the offer line:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value chk=2c\n
```

The bytes hashed (everything before ` chk=2c\n`) are:

```
ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value
```

Their SHA1 digest is:

```
2c4e6caf8303b76aff162ca7ba44a4b9a72c69ee
```

The first two hex characters are `2c` — matching the value sent over the air, so the line is intact.

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

### Discovering neighbours and exchanging routes

Very much tbc. 

I see a hand-wavey model along the lines of:

- DAPPS instance advertises its presence on air with short UI frame broadcasts (requiring the node to allow external applications to send UI frames)
- Other DAPPS instances listen out for those broadcasts (requiring the node to provide a feed of frames heard - MQTT?)
- At randomised long intervals, DAPPS instances connect to other instances they have heard and tell each other about DAPPS nodes they know about. Not all the routing information needs to go every time, more along the lines of "oh, by the way, I can connect to GB7AUG". This could be based on keeping track of what stations the node is able to successfully link to. (Requires the node to provide a feed of link connect / disconnect events to DAPPS)
- From those exchanges, each DAPPS instance should build up a local routing table (space is no longer a problem)
- From that routing table, DAPPS can route messages appropriately, including to intermediate DAPPS instances.

## Application interface

The application interface is likely to be something like MQTT, with DAPPS holding on to messages until it knows it has delivered them to integrated applications. Collaboration very welcome.

Details tbc.

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
