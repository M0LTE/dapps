# Connect via XRouter (AGW)

**Status: untested, but expected to work.** XRouter (Paula Dowie, G8PZT) is a long-standing alternative to BPQ in the British packet community. It exposes an AGW interface in much the same shape BPQ does, and the AGW protocol is well-defined and stable, so DAPPS *should* connect to it the same way - register a callsign for inbound dispatch, dial out to remote callsigns on demand. We just haven't put a node on the air against XRouter ourselves yet.

If you're an XRouter operator: we'd love to hear from you. [Open an issue](https://github.com/M0LTE/dapps/issues) if you try it - either to confirm it works (so we can drop the "untested" caveat) or to report what's different (so we can document the XRouter recipe properly here).

## What we expect

The shape is the same as the [BPQ recipe](bpq.md):

1. Tell XRouter to dispatch a named application command (e.g. `DAPPS`) over AGW to the registered DAPPS client.
2. Tell DAPPS where XRouter's AGW listener is.
3. Add a manual neighbour, send a test message, watch it land.

The XRouter equivalents of `bpq32.cfg`'s `APPLICATION` line and the `AGWPORT` knob will be in XRouter's own configuration files - those are the bits we'd need an XRouter operator to fill in for us.

## Configuration on the DAPPS side

Identical to BPQ - DAPPS doesn't know or care which AGW host is on the other end:

```
DAPPS_CALLSIGN=M0LTE-1
DAPPS_NODE_HOST=<xrouter-host>
DAPPS_AGW_PORT=<xrouter-agw-port>
DAPPS_DEFAULT_BPQ_PORT=0
```

`DAPPS_DEFAULT_BPQ_PORT` is named for historical reasons; it's just the byte AGW uses to identify which radio port to originate on. The XRouter mapping should be the same as the order ports appear in XRouter's config.

## Why we haven't tested yet

We've got a [Docker image for BPQ](https://hub.docker.com/r/m0lte/linbpq) that the integration tests spin up two of in CI, so every PR runs a real two-instance BPQ topology end-to-end. There isn't an equivalent XRouter image yet - building one would let us add XRouter to the integration matrix and ship the same level of confidence we have for BPQ.

If you're a Docker-friendly XRouter operator who'd like to help dockerise XRouter for a CI fixture, that would unlock the testing story here.

## XRouter and RHPv2

XRouter supports **RHPv2** (Radio Host Protocol v2) as well as AGW. That's interesting because mainline BPQ doesn't ship RHPv2 yet - so when [DAPPS adds RHPv2 as a bearer](rhp.md), XRouter is actually the natural first test target. If you're an XRouter operator with RHPv2 enabled and want to be in the loop on that work, [open an issue](https://github.com/M0LTE/dapps/issues).

## Same goes for any AGW host

XRouter is named because it's the most common BPQ alternative in the packet community we want to interoperate with. Any other AGW host (Direwolf with its built-in AGW server, AGWPE itself, others) sits in the same bucket: AGW is the contract, the contract is well-defined, it should work, we haven't tested it. Same call to action - try it, tell us what you find.
