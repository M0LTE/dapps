# XRouter AGW inbound-dispatch investigation - RESOLVED

## TL;DR

**Inbound AGW L2 dispatch on XRouter works with X-frame registration alone.
DO NOT add an APPL block for the DAPPS callsign on the XR side - the
APPL block claims the callsign and causes XR to refuse the X-frame.
DAPPS's existing X-frame code (expects `0x01` for success) is correct
for both BPQ and XR.**

## Goal

Build `scripts/sim-mixed-bearer.sh` - a separate simulator script that
brings up real BPQ + XRouter containers wired together over AX.25-over-
UDP point-to-point partner links, with DAPPS daemons attached to each
container via AGW. Topology: hub-and-spoke, 4 nodes (2 BPQ + 2 XR),
proves DAPPS routes messages across mixed bearers in a non-trivial
topology (3-hop XR-2 -> BPQ-1 -> BPQ-3 -> XR-4).

## Key finding (the resolution)

XR's X-frame reply byte:
- `0x01` = callsign was free, now registered to this AGW client
- `0x00` = callsign refused (claimed by NODECALL / CHATCALL / APPL block,
  or already registered by another AGW client)

This is the SAME semantics as BPQ - the `0x01`-is-success expectation in
`AgwOutboundTransport.cs:48` (`registerReply.Payload[0] != 0x01`) works
correctly against XR.

The earlier "X-frame fails" symptom came from putting a matching
`APPL=N` block in `XROUTER.CFG`. The APPL block is XR's way of
declaring an L2 callsign for an *internal* application (TCP server,
DEDHOST, WA8DED, etc.). When an external AGW client tries to register
the same callsign via X-frame, XR refuses because the callsign is
already claimed.

**Practical implication for the sim**: on the XR side, don't write an
`APPL` block for the DAPPS callsign. Just configure the AXUDP partner
link and let DAPPS register itself dynamically via X-frame. Same as
how DAPPS works against BPQ today (where the BPQ `APPLICATION` line
*does* claim the call, but BPQ's AGW emulator is more permissive about
external-client registration).

## Verification (the experiment that proved it)

Setup:
- BPQ container with AXUDP partner -> XR (UDP port 10093 listen, MAP
  G9DUM-7 -> 127.0.0.1:10094 partner)
- XR container, sharing BPQ's netns, AXUDP partner back to BPQ
  (UDPLOCAL=10094, IPLINK=127.0.0.1, UDPREMOTE=10093),
  **NO APPL block**, AGW listening on 8001 (collides with BPQ on 8000
  if same).

Test:
1. AGW client connects to XR's AGW (TCP 8001).
2. Sends X-frame for `G9DUM-7`. Reply: payload `0x01` (success).
3. BPQ telnet console issues `C 2 G9DUM-7`.
4. AGW client receives a `C` frame: kind=`C`, from=`N0BPQ`, to=`G9DUM-7`,
   payload=`*** CONNECTED To Station N0BPQ\r\x00`.
5. BPQ telnet console shows `Connected to G9DUM-7`.

Inbound L2 from BPQ landed at the registered AGW client on XR. End-to-
end working.

## XR config quirks (carried over from earlier notes)

- `IPADDRESS=44.128.0.1` (any non-zero) required for IP services.
- `LOCATOR` must be a valid Maidenhead grid (e.g. `IO91PM`); "NONE"
  rejected.
- `NODECALL` must look like a real callsign; `N0CALL-1` rejected
  ("Invalid argument") because of the 0-as-second-letter shape. Use
  e.g. `G9DUM-1`.
- `AGWPORT=8000` takes a single arg.
- `AGWPORT` clash with BPQ when sharing netns: give XR a different
  port, e.g. `AGWPORT=8001` while BPQ uses `AGWPORT=8000`.
- ACCESS.SYS isn't relevant for AGW dispatch in the loopback case
  (the gate that mattered was the APPL-block conflict, not access
  control).

## What worked, byte for byte

### XR side (XROUTER.CFG, no APPL block)

```
DNS=8.8.8.8
NODECALL=G9DUM-1
NODEALIAS=XRTST
LOCATOR=IO91PM
CONSOLECALL=G9DUM
CHATCALL=G9DUM-8
CHATALIAS=XRCHAT
AGWPORT=8001
IPADDRESS=44.128.0.1

INTERFACE=1
    TYPE=AXUDP
    MTU=256
ENDINTERFACE

PORT=1
    ID="AXUDP to N0BPQ"
    INTERFACENUM=1
    UDPLOCAL=10094
    IPLINK=127.0.0.1
    UDPREMOTE=10093
ENDPORT
```

### BPQ side (bpq32.cfg, AXUDP partner)

```
SIMPLE=1
NODECALL=N0BPQ
NODEALIAS=BPQTST
LOCATOR=NONE
NODESINTERVAL=1
AGWPORT=8000
AGWSESSIONS=10
AGWMASK=1

PORT
 ID=Telnet
 DRIVER=Telnet
 CONFIG
 TCPPORT=8010
 MAXSESSIONS=20
 USER=test,test,N0BPQ,,SYSOP
ENDPORT

PORT
 ID=AXUDP
 DRIVER=BPQAXIP
 QUALITY=200
 MINQUAL=1
 CONFIG
 UDP 10093
 MAP G9DUM-7 127.0.0.1 UDP 10094 B
ENDPORT
```

### Container layout (netns sharing)

```bash
docker run -d --name bpq -p 28010:8010 -p 28000:8000 -p 28001:8001 \
  -v /path/to/bpq:/data m0lte/linbpq:latest
docker run -d --name xr --network "container:bpq" \
  -v /path/to/xr:/data ghcr.io/packethacking/xrouter:latest
```

XR shares BPQ's netns so loopback partner addressing works without
container-IP discovery. AGW ports for both must differ (BPQ:8000,
XR:8001).

## Next steps

1. ✅ Bearer interop proven (BPQ <-> XR over AXUDP).
2. ✅ XR AGW frame layer proven (R-frame).
3. ✅ XR AGW inbound L2 dispatch proven (X-frame, no APPL block).
4. ⬜ Write `scripts/sim-mixed-bearer.sh`:
   - 4 containers (2 BPQ + 2 XR), each with its own AGW port.
   - Hub-and-spoke topology: BPQ-1 <-> XR-2, BPQ-1 <-> BPQ-3,
     BPQ-3 <-> XR-4.
   - 4 DAPPS daemons, one per container, attached via AGW; each
     registers its callsign via X-frame.
   - Test exercise: A->B, A->D (cross-bearer), B->D (3-hop mixed).
5. ⬜ Telnet automation only needed if NODES gossip via BROADCAST NODES
   doesn't propagate routes between BPQ and XR. Test first; only
   wire telnet drivers if needed.

## Notes on this session's WSL stability

Earlier in the session WSL crashed twice when running the XR docker
image. This session resumed cleanly and ran the same image without
issue (single XR container, then BPQ+XR pair sharing netns). The
crashes appear to have been transient WSL/host issues rather than a
reproducible problem with the XR image. If they recur, the workaround
is to test on a real Linux host or different VM.

## Sticky reminders

- User strongly dislikes em-dashes (memory `feedback_no_em_dashes.md`).
  ASCII hyphens only.
- Don't WebFetch enormous pages in long sessions.
