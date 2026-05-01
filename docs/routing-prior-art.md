# Routing prior art for DAPPS B5 / B6

Read before implementing B5 (learned-graph routing) or B6 (active discovery). The plan.md note for B5 says "DAPPS should not cargo-cult any one of these packet formats" — this doc is the work the author of B5 should do *before* picking a flavour, so the choice is documented for future contributors and the chosen design is shaped by the union of needs of the candidates rather than a guess at the right interface.

DAPPS's B5/B6 needs (the lens to read each candidate through):

- **Bearer-agnostic.** Routing decisions live one layer above `IDappsBackhaul`. Today that's AGW UI + UDP datagram; soon MeshCore Companion / MeshCore KISS / RHP. Whatever B5 ships MUST run unchanged across all of these.
- **Slow + lossy + half-duplex links.** AX.25 at 1200-9600 bps; HF much slower; multi-second per-frame latency on busy channels.
- **Few nodes.** Dozens, not thousands. Algorithms designed for global-scale internet routing (BGP) are over-engineered.
- **Long latency-tolerance.** DAPPS messages are mail-shaped, not call-shaped. Convergence of minutes is fine.
- **Identity = callsigns.** No flat hash space; routing keys are human-typed `M0LTE-9` strings with SSID semantics already part of the spec.
- **Store-and-forward already there.** DAPPS has TTLs, F1 originator tracking, dedup-by-id, retries. A routing layer that carries its own ack/timeout scheme would duplicate this.

## The candidates

### 1. AODV — *Ad-hoc On-demand Distance Vector* (RFC 3561, 2003)

**Mechanism.** Routes computed only when needed. To reach destination D the source floods an RREQ (Route Request) with a destination sequence number and an RREQ ID. Each forwarding node records the *reverse* route to the source (next-hop = whichever neighbour the RREQ arrived from), de-dupes on (source, RREQ ID), and forwards. When the RREQ reaches D (or a node with a fresh enough route to D), an RREP (Route Reply) returns along the reverse path; each hop on the way back records the *forward* route (next-hop = whichever neighbour the RREP arrived from). Subsequent data packets follow the forward route as a normal next-hop lookup. RERR messages invalidate broken routes; HELLO messages or link-layer feedback detect link loss.

**Wire shape.** RREQ / RREP / RERR are dedicated control packets, not piggybacked on data. Sequence numbers per destination prevent count-to-infinity and stale-route loops.

**Active discovery.** None proactive. Floods only happen when there's a message to send to a destination with no live route. Cheap when traffic is sparse, expensive when many sources want fresh routes simultaneously.

**Failure handling.** Active forward fails → RERR back to source → source re-RREQs. Convergence in 1 round-trip on success.

**Ham-radio fit.** Decent. The on-demand model matches mail-shaped traffic — you don't pay routing overhead until you have something to send. The control packets are small. The flood-once-per-cold-start cost is acceptable for a ham mesh of dozens of nodes.

### 2. Babel (RFC 8966, 2021 — supersedes RFC 6126)

**Mechanism.** Distance-vector. Each node periodically advertises its routing table to direct neighbours; recipients pick the best (lowest-metric) entry per destination. Sequence numbers per (destination, originator) prevent loops and give monotonic freshness. Designed *specifically* for unstable wireless meshes — handles link-quality changes, route flapping, and asymmetric links without the count-to-infinity problems classical distance-vector has.

**Wire shape.** Periodic IHU (I Heard You) + Update + Request packets. Modest overhead — neighbour table size proportional to count of active routes, not network diameter.

**Active discovery.** Continuous. Every node beacons its routing table on a timer. Convergence is proactive — when a new node joins, it advertises and learns within one period.

**Failure handling.** Sequence-number-based; "retract" updates with infinity metric propagate route loss explicitly.

**Ham-radio fit.** Not bad on faster links; questionable on HF. The proactive overhead is the main concern — you're paying routing bandwidth even when nobody's sending data. But the design specifically tolerates the failure modes ham radio has (asymmetric links, intermittent connectivity, link quality fluctuating), and the reference implementation (`babeld`, ~2000 LOC) is genuinely small enough to fit in a head.

### 3. AX.25 NET/ROM (1980s, the established ham-radio precedent)

**Mechanism.** Distance-vector with periodic NODES broadcasts. Each node maintains a routes table keyed by callsign with `(next-hop, hop-count, quality)` entries. Periodically (typically every ~30-60 minutes) every node broadcasts its routes table to direct AX.25 neighbours. Recipients merge in entries that improve their own table (lower hop count or higher quality). Has been in use across the global ham packet network for ~40 years.

**Wire shape.** NODES broadcast packets carry the full table — bandwidth grows linearly with network size. Acceptable for the few-dozen-node networks NET/ROM was designed for.

**Active discovery.** Periodic, on a long timer (30-60 min default). Slow convergence by modern standards; appropriate for the link characteristics of 1200-baud packet.

**Failure handling.** Routes age out if not refreshed. Quality metric updated by observed link behaviour over time.

**Ham-radio fit.** Best by definition — this is what the ham community already understands. *But* NET/ROM lives at the AX.25 layer below DAPPS. DAPPS-over-NET/ROM-routing would mean letting BPQ's NET/ROM table do the routing and DAPPS treats it as opaque transit. That's a viable architecture, but it's not "DAPPS implements NET/ROM" — it's "DAPPS defers routing to BPQ on AX.25 bearers and needs its own answer for non-AX.25 bearers." Mixed-bearer meshes (AGW + MeshCore + UDP) wouldn't have NET/ROM as a uniform substrate.

### 4. Reticulum (RNS / LXMF, modern Python)

**Mechanism.** Each destination announces itself via flooded "announce" packets containing the destination hash and public key. Transport nodes record "which neighbour did this announce arrive from?" and use that as the next-hop for that destination. When a destination is needed but no announce has been heard, a node can issue a "path request" that propagates as a flood. Sustained communication uses Links (3-packet handshake establishes an encrypted channel; subsequent traffic uses a 16-bit link ID with negligible per-packet overhead).

**Wire shape.** Announces are 167 bytes each, capped at 2% of interface bandwidth (so a 1200 bps interface allows roughly one announce every 6-7 seconds globally — congestion-controlled). Link request 83 bytes; link proof 115 bytes; link ID per data packet adds ~2 bytes.

**Active discovery.** Continuous via announce broadcasts with anti-storm controls. New nodes announce on join and on a slow drift afterwards; transport nodes prioritise local-relevance announces over distant ones on congested links.

**Failure handling.** Announces timeout from path tables; explicit "path requests" on demand.

**Ham-radio fit.** Designed for it — Reticulum's stated baseline is "5 bps lower bound." Path discovery model is similar to MeshCore but with *proactive* announces rather than purely on-demand. The Python reference is open source; the protocol is well-specified. Identifier model (32-byte hash) is incompatible with DAPPS's callsign-keyed addressing without a translation layer.

### 5. MeshCore (LoRa-mesh, contemporary)

**Mechanism.** DSR-flavoured source routing. First message to a never-seen destination is sent as `ROUTE_TYPE_FLOOD`; relays append their hash to a `path` field as the packet propagates. When the message reaches the destination, the destination sends a *delivery report* back (also flood-routed) carrying the full forward path. The sender stores that path against the destination's contact entry; subsequent messages set `ROUTE_TYPE_DIRECT` with the path embedded, and intermediate relays follow it as a source route. If a hop on the stored path goes offline, the next attempt fails and the sender falls back to flooding to discover a fresh path.

**Wire shape.** Header byte (route type), optional 4-byte transport code, path-length byte (hop count + hash size), `hop_count * hash_size` path bytes, payload up to 184 bytes (LoRa MTU). Floods are cheap on cold start; direct routing has zero discovery overhead but pays the path bytes per data packet.

**Active discovery.** None proactive. Like AODV: cold-start flood, then learn, then direct. Different from AODV in *what* gets stored — MeshCore stores the full source route at the *originator*, AODV stores next-hop hints at *every node along the route*.

**Failure handling.** Delivery report carries the path; no path = no delivery report = sender retries with flood. Stale paths fail at first use, which triggers the next flood. Simple, no sequence numbers.

**Ham-radio fit.** Designed for it (LoRa is the original target, AX.25 has similar characteristics). Specifically integrates with DAPPS's Phase H plans — the bearer is already on the roadmap. The "Companion" vs "Repeater" role distinction mirrors DAPPS's eventual sysop/relay node split.

## Comparison at a glance

| Algorithm | Discovery | Storage location | Wire/data overhead | Wire/control overhead | Convergence | Ham fit |
|---|---|---|---|---|---|---|
| AODV | Reactive flood | Next-hop per destination at every node | None per data packet | RREQ/RREP only on cold-start | 1 round-trip | Good |
| Babel | Proactive announce | Routing table at every node | None per data packet | Periodic update broadcasts | 1 period (seconds) | Marginal — too chatty for HF |
| NET/ROM | Proactive NODES | Routing table at every node | None per data packet | Periodic (~30-60 min) NODES broadcasts | Slow | Native (this IS the ham precedent) |
| Reticulum | Proactive announce + on-demand path-request | Next-hop per destination at every node | Negligible (link ID) | Anti-storm-controlled announces | Slow but continuous | Designed for it |
| MeshCore | Reactive flood + delivery report | Full source route at originator only | `hop_count * hash_size` bytes per data packet | Flood once per cold-start, repeat on path break | 1 round-trip | Designed for it |

## Where each lands on `IRoutingAlgorithm`

The proposed seam (from the conversation that produced this doc):

```csharp
public interface IRoutingAlgorithm
{
    Task<BackhaulRoute?> ResolveAsync(string destination, IRoutingContext ctx, CancellationToken ct);
    Task ObserveInbound(BackhaulMessage message, string linkSource, CancellationToken ct);
    Task ObserveForwardOutcome(string destination, BackhaulRoute route, BackhaulSendResult result, CancellationToken ct);
    Task RunAsync(IRoutingContext ctx, CancellationToken ct);  // optional active loop
}
```

How each candidate fits:

- **AODV.** `Resolve` looks up next-hop table; if missing, returns null and the OMM falls back to flood-via-`ctx`. `ObserveInbound` records the reverse-path next-hop for the source. `ObserveForwardOutcome` invalidates on failure → next `Resolve` returns null → re-flood. `RunAsync` empty.
- **Babel.** `Resolve` looks up next-hop table. `ObserveInbound` consumes inbound `Update` packets and updates the table. `ObserveForwardOutcome` notes link quality. `RunAsync` emits periodic Update broadcasts on a timer.
- **NET/ROM.** Same shape as Babel — periodic NODES broadcasts in `RunAsync`, table lookup in `Resolve`, observed updates in `ObserveInbound`. Likely best as a *bearer-specific* algorithm only used on AX.25 bearers (where BPQ already speaks NET/ROM natively).
- **Reticulum.** `Resolve` → next-hop from announce table. `ObserveInbound` records announces. `RunAsync` emits anti-storm-controlled announces; also handles "path request" on `Resolve`-miss before falling back to flood.
- **MeshCore.** `Resolve` returns a *full path* not a next-hop — the interface needs a richer return type, OR the algorithm injects path-bearing messages itself via `ctx`. `ObserveInbound` consumes inbound delivery-reports and stores paths. `RunAsync` empty (purely reactive). The fact that MeshCore alone wants source-routing rather than next-hop tables is the key constraint on the interface — if we want MeshCore to fit cleanly, the interface needs to abstract over both models.

## Verdict for DAPPS

No clear winner; the choice depends on what we optimise for. Three options seem worth taking seriously:

**Option A: AODV-flavoured passive learning + bounded flood.** The simplest of the bunch and what was sketched in chat. Passive learning (every inbound message teaches reverse-direction routes via F1's `src=`) gives free routing for bidirectional traffic; bounded flood handles cold-start. No wire-format addition beyond F1; `IRoutingAlgorithm` clean. Cost: convergence requires bidirectional traffic OR a flood — purely-listening nodes never get routes inferred about them. *Most pragmatic if we want B5 done in one PR pair (#1 passive, #2 bounded flood).*

**Option B: Adopt MeshCore's routing model wholesale.** Same wire format on DAPPS that MeshCore uses on LoRa. Phase H1 (MeshCore Companion bearer) becomes simpler because the routing decisions match. Cost: source routing puts path bytes on every data packet, painful on slow AX.25. Probably wrong for primary AX.25-target deployments. *Pursue if MeshCore is the strategic target.*

**Option C: Defer to NET/ROM on AX.25, do something else off it.** On AX.25 bearers, treat BPQ's existing NET/ROM as the L2.5 routing layer — DAPPS just hands off to BPQ and lets it sort out the AX.25 path. On non-AX.25 bearers (UDP, MeshCore, RHP) implement Option A or B. Cost: per-bearer routing logic; "what does B5 mean" varies by bearer. *Pursue if the goal is "play nice with the existing ham mesh."*

Recommendation, weak: **Option A** for the v0.x phase, with the `IRoutingAlgorithm` interface designed to also fit Option B and Option C cleanly so we can revisit. Specifically, the interface's `Resolve` return type should be `RouteDecision` (a discriminated union of "next-hop callsign" and "source route", so MeshCore-style fits without breaking AODV-style), and `RunAsync` is a first-class hook so a future Babel / NET-ROM-style proactive algorithm can slot in without breaking existing implementations.

## Reading list

- AODV — RFC 3561 (2003), Perkins/Belding-Royer/Das. ~30 pages. <https://www.rfc-editor.org/rfc/rfc3561>
- DSR — RFC 4728 (2007), Johnson/Hu/Maltz. The source-routing-with-flood-discovery model MeshCore borrows. <https://www.rfc-editor.org/rfc/rfc4728>
- Babel — RFC 8966 (2021), Chroboczek/Schinazi. ~75 pages. <https://www.rfc-editor.org/rfc/rfc8966>
- NET/ROM — TheNet protocol spec (Ron Raikes WA8DED, 1987). Best primary source is the [NET/ROM L4 protocol description](http://www.tapr.org/pdf/CNC1985-NETROM-WA8DED.pdf). BPQ's implementation: <https://www.cantab.net/users/john.wiseman/Documents/>.
- Reticulum — <https://reticulum.network/manual/understanding.html> for the architecture; full spec under `manual/`.
- MeshCore — <https://github.com/meshcore-dev/MeshCore/blob/main/docs/packet_format.md> for the wire format, FAQ for the routing model.
