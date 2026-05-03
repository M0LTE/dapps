# Connect via XRouter (AGW)

**Status: AGW frame layer tested in CI; on-air shake-down pending.** XRouter (Paula Dowie, G8PZT) is a long-standing alternative to BPQ in the British packet community. Its AGW emulator implements the AGW protocol byte-for-byte the same way BPQ does (it reports a different version stamp - 2000.20 vs BPQ's 2003.999 - but the wire format is identical). The DAPPS integration test suite now spins up `ghcr.io/packethacking/xrouter:latest` alongside `m0lte/linbpq:latest`, and the same AGW handshake test that runs against BPQ runs unchanged against XRouter.

What that proves: DAPPS's AGW transport is genuinely host-agnostic - the BPQ-specific assumptions we'd worried about (callsign registration shape, frame layout, version stamp) aren't there. The remaining BPQ-specific bits in the docs (the `APPLICATION` line shape, the BPQ-port-byte naming) translate cleanly to XRouter's INTERFACE/PORT model.

What's still pending: a real on-air shake-down with XRouter as the bearer. Frame format works; we haven't yet verified end-to-end DAPPS message forwarding through an XRouter node. If you're an XRouter operator who wants to be in the loop on that, [open an issue](https://github.com/M0LTE/dapps/issues).

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

## What's tested in CI

`ghcr.io/packethacking/xrouter:latest` (Paula Dowie's container build of XrLin) runs alongside `m0lte/linbpq:latest` in the integration test matrix. On every PR, both containers spin up via Testcontainers and the same AGW handshake test runs against each. That covers the frame-format end-to-end - if XRouter's AGW emulator drifted from the documented protocol in a way DAPPS would care about, the test would catch it.

What's not yet tested: full two-instance topologies with XRouter on either end (analogous to BPQ's `TwoInstanceLinbpqFixture`), and real on-air shake-down with messages flowing through an XRouter-hosted DAPPS node. Both are reasonable follow-ups; neither is blocked on anything other than someone setting up the test.

## XRouter and RHPv2

XRouter supports **RHPv2** (Radio Host Protocol v2) as well as AGW. That's interesting because mainline BPQ doesn't ship RHPv2 yet - so when [DAPPS adds RHPv2 as a bearer](rhp.md), XRouter is actually the natural first test target. If you're an XRouter operator with RHPv2 enabled and want to be in the loop on that work, [open an issue](https://github.com/M0LTE/dapps/issues).

## Same goes for any AGW host

XRouter is named because it's the most common BPQ alternative in the packet community we want to interoperate with. Any other AGW host (Direwolf with its built-in AGW server, AGWPE itself, others) sits in the same bucket: AGW is the contract, the contract is well-defined, it should work, we haven't tested it. Same call to action - try it, tell us what you find.
