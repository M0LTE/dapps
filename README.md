# DAPPS

DAPPS is an acronym for Distributed Asyncronous Packet Pub-Sub.

The idea of DAPPS is to overlay an asyncronous pub-sub messaging subsystem over a packet radio network.

Ultimately, the vision is for an application at one node to be able to post a message to a queue at a remote node, without caring about the details of delivery whatsoever.

Realtime connectivitity is not a goal. Think of this roughly as packet mail for application developers.

This does not replace a packet node, but complements it (and indeed requires one).

This does not deliver any user-facing functionality on its own, but simplifies building of network applications.

This does not replace packet mail, but could piggy back on some the peering/routing arrangements in place for mail, particularly in its early days. It could also potentially enhance mail delivery.

This does not replace AX.25, but can run over it. It could also run over other transports, so conceptually isn't limited strictly to packet radio.

## On-air protocol

Caution, the code in this repo doesn't implement quite what is described below.

### Prompt

When connecting to a DAPPS instance, expect a prompt:

```
DAPPSv1>\n
```

### Offering a message

To offer a message to a remote DAPPS instance, send the following as bytes:

```
ihave abcdeff len=11 fmt=p ts=12345678 dst=topicname@gb7aaa-4 ttl=1730070725 dst=queuename@gb7aaa-4 key=value chk=0f\n
```

where:
- `abcdeff` is the SHA1 hash of the four bytes of the 64 bit integer timestamp, if any, followed by the payload bytes (i.e. `sha1(pppppppp..pppp)[..7]` or `sha1(ttttpppppppp..pppp)[..7]`), serving as a message id
- `11` is the number of bytes in the payload, after decompression if applicable
- `p` is `p` for when the payload is to be interpreted byte-for-byte, or `d` for Deflate algorithm compression
- `12345678` is an optional de-duplication salt, suggestion is the number of milliseconds after some epoch of your choice
- `dst=topicname@gb7aaa-4` is routing information- in this case the ultimate destination for this message is pub/sub topic `topicname` hosted at remote DAPPS instance `gb7aaa-4`
- `ttl=1730070725` is an optional TTL for the message, in seconds since epoch. DAPPS will make no attempt to deliver a message past its TTL.
- `dst=queuename@gb7aaa-4` is the ultimate destination of this message. In this example, `gb7aaa-4` is the call + ssid of the node and DAPPS instance which the DAPPS system will attempt to deliver this message to, and `queuename` relates to the remote DAPPS-using application.
- `key=value` is zero or more key/value pairs, akin to arbitrary headers. These should be used sparingly and not in place of a message payload.
- `chk=0f` is an optional checksum, calculated as below, validated at the receiving side. For ease, this should be the last key-value pair.
- `\n` is a newline character, not the string literal `\n`

#### "ihave" command checksum

Since packet doesn't guarantee corruption-free transmission, and it's pretty important that the `ihave` command is received
free of errors, it makes sense to be able to provide a checksum. This one is calculated as follows:

`sha1([the ihave command, with chk=nn removed, and trimmed of whitespace])`

The first two characters of the hex representation of the resulting SHA1 checksum should match the value of `chk`.

##### Example

To validate the checksum `a1` for this `ihave` command sent over the air:

```
ihave abcdeff len=11 fmt=p ts=12345678 dst=topicname@gb7aaa-4 ttl=1730070725 dst=queuename@gb7aaa-4 key=value chk=a1\n
```

we remove `chk=a1`:

```
ihave abcdeff len=11 fmt=p ts=12345678 dst=topicname@gb7aaa-4 ttl=1730070725 dst=queuename@gb7aaa-4 key=value\n
```

we trim off the line ending:

```
ihave abcdeff len=11 fmt=p ts=12345678 dst=topicname@gb7aaa-4 ttl=1730070725 dst=queuename@gb7aaa-4 key=value
```

and we calculate its SHA1 hash:

```
a1732af9a48b161f30299cbff93a41fdb3037e18
```

and the sum is the first two characters of the hash:

`a1` - which matches that sent over the air, i.e. the command is valid.

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

The remote DAPPS instance will not respond at all until it has received the specified number of payload bytes, at which point it will respond immediately with `ack` or `bad`- i.e. there is no message terminator, and the `len` parameter is compulsory.

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

The application interface is likely to be something like MQTT, with DAPPS holding on to messages until it knows it has delivered them to integrated applications.

Details tbc.

## Progress

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

## Previous writing

Caution, may not align with current thinking.

https://gist.github.com/M0LTE/be1fd071ca1867703d1f2d4c17fabca2

