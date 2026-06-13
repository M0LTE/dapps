# Local simulators

Three simulator scripts live in [`scripts/`](../scripts/), each one heavier than the last. Same DAPPS daemon runs under all three; what changes is how realistic the layer underneath gets. Pick the lightest one that exercises what you're trying to validate - the iteration loop is seconds-vs-minutes between them.

| Script                                          | What's real                                       | What's faked                                                              | Reach for it when |
|-------------------------------------------------|---------------------------------------------------|---------------------------------------------------------------------------|-------------------|
| [`sim-multihop.sh`](../scripts/sim-multihop.sh) | DAPPS daemons, routing, F1 source-tracking        | RF channel = UDP multicast; bearer = UDP datagram                         | Routing / forwarding logic. Fastest, no Docker. |
| [`sim-mixed-bearer.sh`](../scripts/sim-mixed-bearer.sh) | DAPPS + linbpq + XRouter packet-node containers   | RF channel = AX.25-over-UDP point-to-point links                          | Real BPQ↔XR interop; AGW + RHPv2 wire protocols. |
| [`sim-rf-channel.sh`](../scripts/sim-rf-channel.sh) | DAPPS + BPQ/XR + KISS-TCP + simulated RF channel  | Nothing below the antenna - net-sim's modems do the framing, capture, loss | RF-specific behaviour: collisions, hidden terminals, multi-band paths, multi-hop AX.25 chains via connect-scripts. |

If a routing bug repros under `sim-multihop` you don't need the others, and the iteration loop is seconds. Drop down to `sim-mixed-bearer` only when AGW/RHPv2 wire shape is in scope. Drop further to `sim-rf-channel` only when the *RF* layer is in scope - real KISS framing, real channel-access semantics, real per-modem panics.

All three follow the same shape:

```
scripts/<sim>.sh up         # bring up all containers + DAPPS daemons
scripts/<sim>.sh exercise   # run the canned set of sends + show inboxes
scripts/<sim>.sh status     # PIDs, ports, container health
scripts/<sim>.sh verify     # show every DAPPS node's inbox
scripts/<sim>.sh send X Y   # one-shot send between two letter-named nodes
scripts/<sim>.sh down       # tear everything down
```

`SIM_DIR` (default `/tmp/dapps-...-sim`) controls where the per-node working directories land. The sims publish the `dapps.core` binary into `$SIM_DIR/bin` once and reuse it; they re-publish if any `*.cs` file under `src/dapps` is newer than the cached binary.

---

## sim-multihop.sh

Six DAPPS instances on loopback, on the same host, no Docker. UDP multicast groups stand in for RF "broadcast domains" - a node subscribed to multiple groups stands in for one within RF range of multiple disjoint populations. Beacons are multicast, forwarding is unicast UDP - the same way RF works (broadcast discovery, point-to-point forward).

```
       A ──G1── B ──G2── C ──G3── D                 (1, 2, 3 hops from A)
                         │
                         G4
                         │
                         E ──G5── F                 (3 hops, then 4 from A)
```

Five canned exercises cover longest path, reverse longest path, off-spine, fan-out, and concurrent cross-traffic. Validates F1 end-to-end source tracking and B5 flood-then-learn at every receiver. Run with `SIM_ALGO=meshcore` to switch to the DSR-style stack instead of the default passive-flood.

What it doesn't validate: RF-specific behaviour (half-duplex, contention, lossy paths, AX.25 connect/disconnect quirks). For loss/jitter realism you can layer `tc netem` on `lo`, but that doesn't get you AX.25.

## sim-mixed-bearer.sh

Four containers (`mxsim-bpq1`, `mxsim-xr2`, `mxsim-bpq3`, `mxsim-xr4`) all sharing one network namespace, wired together with AX.25-over-UDP partner links (`DRIVER=BPQAXIP` for BPQ, `INTERFACE TYPE=AXUDP` for XRouter). DAPPS daemons run on the host and reach BPQ over AGW, XR over RHPv2.

```
              DAPPS-A
              on BPQ-1 (hub)
             /        \
          AXUDP      AXUDP
           |           |
         XR-2        BPQ-3 ────── AXUDP ────── XR-4
          |           |                         |
       DAPPS-B      DAPPS-C                  DAPPS-D
```

Path lengths: A↔B 1 hop (BPQ↔XR), A↔C 1 hop (BPQ↔BPQ), A↔D 2 hops (BPQ↔BPQ↔XR), B↔C 2 hops (XR↔BPQ↔BPQ), B↔D 3 hops with mixed bearers (XR↔BPQ↔BPQ↔XR) - the showpiece.

The packet-node layer is real - real BPQ, real XRouter, real AGW + RHPv2 wire protocols against real callsign dispatch tables - but the RF channel is faked away. AXUDP carries already-formed AX.25 frames between hosts; there's no modulation, no collision, no loss, no capture-effect. If those don't matter for what you're testing, this is the cheapest "real packet node" sim.

## sim-rf-channel.sh

Heaviest sibling. Real-channel sim wired around the [packethacking/net-sim](https://github.com/packethacking/net-sim) container. net-sim spawns one TNC child process per port (samoyed by default, direwolf opt-in via `tnc: direwolf`), runs the audio path entirely in userspace (no PulseAudio / PipeWire / loopback ALSA), and connects everything together with an FM-capture mixer that does real collision and capture-effect maths between RX-overlapping signals.

Every BPQ/XR container connects to net-sim over plain KISS-TCP:

- linbpq: `PORT / TYPE=ASYNC / PROTOCOL=KISS / IPADDR / TCPPORT` plus L2 parameters (`MAXFRAME`, `FRACK`, `RESPTIME`, `RETRIES`, `PACLEN`).
- XRouter: `INTERFACE TYPE=TCP / PROTOCOL=KISS / IOADDR / INTNUM / KISSOPTIONS=NONE / MTU=...`.

What BPQ does on the wire is bit-for-bit what it'd do into a real Direwolf or QtSoundModem in front of an actual radio. All BPQ/XR containers join net-sim's network namespace via `--network=container:<netsim>`, so KISS-TCP at `127.0.0.1:<kiss_port>` is reachable from inside any of them, no per-container IP discovery needed. net-sim itself publishes every host-side port (AGW / RHPv2 / Telnet) on 127.0.0.1 so the host-side DAPPS daemons reach BPQ / XRouter normally.

Two scenarios selectable via `SIM_SCENARIO=mesh|chain` (default `mesh`).

### mesh

4 nodes (2 BPQ + 2 XR) on a single afsk1200 channel:

```
              A (BPQ, hub)
           /  │  \
       clean clean  no path
         /    │      \
        B     C ─clean─ D
       (XR)  (BPQ)    (XR)
        │     │
        ╰─marginal (loss_db=6)
```

A is the hub; B and C are clean to A; D hangs off C only (no direct A↔D RF path). B↔C is the marginal hidden-terminal pair - audible to A but lossy to each other. Same neighbour + route-hint shape as `sim-mixed-bearer.sh` so the two scenarios are diff-able. Five canned exercises:

- A→B 1-hop BPQ→XR (direct)
- A→C 1-hop BPQ→BPQ (direct)
- A→D 2-hop via C (no direct A-D RF path)
- B→C 2-hop XR via A
- B→D 3-hop XR→BPQ→BPQ→XR mixed bearer (showpiece)

### chain

3 BPQ in a chain, each with two radio ports. DAPPS only on A and C; M is a plain BPQ (no DAPPS daemon attached). VHF afsk1200 backbone for A↔M, UHF gfsk9600 for M↔C; the second port on each end node is "spare" (its own kiss_port on net-sim, no peer link, inert).

```
 DAPPS-A              (plain BPQ)              DAPPS-C
  on BPQ-A             BPQ-M                   on BPQ-C
  port1 vhf  ─────  port1 vhf
  port2 spare         port2 uhf  ──────────  port1 uhf
                                              port2 spare
```

DAPPS-A and DAPPS-C cannot reach each other directly: the only path is operator-style "connect, then connect, then DAPPS" through M's prompt - exactly the connect-script case [`docs/multi-hop.md`](../docs/multi-hop.md) describes. Each end's neighbour row points at G0CHM-1 with a connect-script attached:

```
C G0CHC-1|Connected to CHC
DAPPS|DAPPSv1>|60
```

A route-hint binds the far-end DAPPS callsign to that M-fronted neighbour. Forward path A → C:

1. DAPPS-A AGW connect to G0CHM-1 over BPQ-A's KISS-VHF port.
2. BPQ-M accepts the L2 connect, presents its node prompt.
3. Script sends `C G0CHC-1\r`; M dials BPQ-C over UHF (M's ROUTES table puts G0CHC-1 on UHF port 3).
4. BPQ-C accepts, replies `Connected to CHC:G0CHC-1` (alias-prefixed - that's what we match on).
5. Script sends `DAPPS\r`; BPQ-C dispatches to APPL1CALL=G0CDC-1 (the local DAPPS daemon's registered AGW callsign).
6. DAPPS-C answers with `DAPPSv1>`; the script terminates and the regular `ihave / send / data / ack` exchange runs over the chain.

Because the connect-script is a bearer-level construct, DAPPS sees A and C as direct neighbours - the two AX.25 hops underneath are invisible at the DAPPS protocol level. Compare to the mesh A→D 2-hop case which has DAPPS-C as a real DAPPS-protocol relay (`source=G0DPC-1` in the inbox row); the chain shows `source=G0CDA-1` because there's only one DAPPS hop end-to-end, even though the L2 path traverses three packet nodes.

### TNC backend mix

Each scenario uses both TNC backends (samoyed and direwolf) - the chain runs A's VHF and C's UHF (the live backbone ports) on direwolf and M's two ports on samoyed; the mesh runs the two XR-side ports on direwolf and the two BPQ-side ports on samoyed. Diversifying away from a single TNC engine bounds the blast radius of a per-engine bug (we hit one - see "Bring-up nuances" below).

### Bring-up nuances captured here so the next reader doesn't repeat the diagnosis

1. **BPQ KISS-TCP driver.** `DRIVER=KISSHF / CONFIG / ADDR <host> <port>` parses cleanly and BPQ logs `Connected to KISS TNC Port N`, but it's a raw modem channel - `C <port> <call>` from the node prompt comes back with "Sorry, port is not an a.25 port". The right shape is `TYPE=ASYNC PROTOCOL=KISS IPADDR=... TCPPORT=...` plus the L2 parameters above. linbpq logs `TCPKISS IP <host> Port <port> Chan A` for that and treats it as a real AX.25 carrier. The L2 parameters aren't optional - without them, BPQ logs `Driver installation failed`.

2. **BPQ ROUTES port field.** The third comma-separated field is PORT, not OBSCOUNT, and it IS required for multi-port nodes. `G0CHC-1,200,2` pins C's route to BPQ port 2. The mesh sim got away with `,200,2` (port 2 = the only KISS port after Telnet on port 1) but the chain's middle BPQ has two KISS ports and needs `G0CHC-1,200,3` to put C on the UHF port not the VHF one. [cantab.net BPQ docs](https://www.cantab.net/users/john.wiseman/Documents/BPQCFGFile.html) document this; the m0lte/linbpq fork keeps the same shape.

3. **AGWMASK semantics.** AGWMASK is the AGW *application* mask (one bit per APPL slot), not a port-exposure mask. With `APPLICATIONS=DAPPS / APPL1CALL=...` we have one application and `AGWMASK=1` is correct. AGW outbound port-byte routing is independent of AGWMASK - it indexes the cfg's PORT block order, 0-indexed. AGW port byte 0 = first PORT block (Telnet) = no L2; byte 1 = second PORT block = first KISS radio. `DAPPS_DEFAULT_BEARER_PORT=1` is what DAPPS expects.

4. **DAPPS Setup auth flow.** `/Setup` is a Razor Pages endpoint with named handlers; `POST /Setup` without `?handler=Password` silently no-ops (the form posts get accepted, but no handler runs, the password is never stored, and `/Login` then returns "Wrong password"). Two-step bring-up: `POST /Setup?handler=Password` (sets the password and signs in via the `dapps.admin` cookie), then `POST /Login` for the explicit cookie write. Filed as [#134](https://github.com/packet-net/dapps/issues/134) - a one-shot `/Api/Bootstrap` covering this case is overdue.

5. **Connect-script expect string.** When BPQ has the destination's NODEALIAS in its NODES table (which happens after the first NODES gossip exchange), its "Connected to" reply is `Connected to <ALIAS>:<CALL>`, not `Connected to <CALL>`. Match on the alias prefix: `Connected to CHC` will match `Connected to CHC:G0CHC-1` cleanly without depending on whether the SSID lands.

6. **net-sim modem panic on AX.25 ID frames.** linbpq's periodic ID UI broadcast (`<NODECALL>>ID:` empty info field) panics samoyed's APRS decoder (`runtime error: index out of range [0] with length 0`, `decode_aprs.go:215`). When the modem child dies, the KISS-TCP listener it owned goes with it, and BPQ's TCPKISS driver doesn't auto-reconnect - so a single panic is effectively fatal for the whole sim run. Workaround in our cfg: `IDINTERVAL=0` to suppress the broadcasts. Filed upstream as [doismellburning/samoyed#504](https://github.com/doismellburning/samoyed/issues/504); the per-port direwolf opt-in is a second-line defence.

7. **XR PORT block keywords.** XR exits silently with code 255 if the cfg has any unrecognised keyword in a PORT block. `BROADCAST=YES` is BPQAXIP-specific and XR rejects it with `XROUTER.CFG: ERROR in line N [BROADCAST] - Invalid keyword` *after* writing nothing else to its log. The `INTERFACE TYPE=TCP / PROTOCOL=KISS / IOADDR / INTNUM / KISSOPTIONS=NONE / MTU=...` shape works for outbound KISS-over-TCP; the matching PORT block is just `ID / INTERFACENUM`, no broadcast-related fields needed.

### Status

Both scenarios green end-to-end through the DAPPS app API on every node. Mesh's five exercise paths and chain's two-direction exercise all deliver into `/AppApi/inbound/chat` with the right `originator` / `source` shape.
