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
ihave abcdeff len=11 fmt=p ts=12345678 dst=topicname@gb7aaa-4 ttl=1730070725 key=value\n
```

where:
- `abcdeff` is the SHA1 hash of the four bytes of the 64 bit integer timestamp, if any, followed by the payload bytes (i.e. `sha1(pppppppp..pppp)[..7]` or `sha1(ttttpppppppp..pppp)[..7]`), serving as a message id
- `11` is the number of bytes in the payload, after decompression if applicable
- `p` is `p` for when the payload is to be interpreted byte-for-byte, or `d` for Deflate algorithm compression
- `12345678` is an optional de-duplication salt, suggestion is the number of milliseconds after some epoch of your choice
- `dst=topicname@gb7aaa-4` is routing information- in this case the ultimate destination for this message is pub/sub topic `topicname` hosted at remote DAPPS instance `gb7aaa-4`
- `ttl=1730070725` is an optional TTL for the message, in seconds since epoch. DAPPS will make no attempt to deliver a message past its TTL.
- `key=value` is zero or more key/value pairs, akin to arbitrary headers
- `\n` is a newline character, not the string literal `\n`

### Sending a message

If the remote instance wants a message, it will reply with:

```
send abcdeff\n
```

At any time, send the message as follows:

```
data abcdeff\n
```

followed immediately by the raw payload bytes. When the remote DAPPS instance has received the expected number of bytes, it will reply with:

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

### Exchanging routes

tbc

## Application interface

tbc

## Previous writing

Caution, may not align with current thinking.

https://gist.github.com/M0LTE/be1fd071ca1867703d1f2d4c17fabca2

