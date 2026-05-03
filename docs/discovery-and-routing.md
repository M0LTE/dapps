# Discovery & routing

How a DAPPS node finds other DAPPS nodes, and how it decides where to send a given message.

## The mental model

Three tables, each with a single job:

1. **Discovery channels** — bearers / frequencies / multicast groups DAPPS will beacon on, listen on, and run scheduled solicits over. You configure these explicitly; nothing is on by default.
2. **Discovered peers** — callsigns heard on a channel, with bearer hints and a "last seen" timestamp. Populated automatically by beacons + solicits.
3. **Neighbours** — callsigns DAPPS will actually forward to. May be hand-added by the operator, or auto-promoted from the discovered-peers table.

Plus two derived stores:

4. **Probed nodes** — per-callsign liveness state from connected-mode probes (Phase B6.1). Tracks success/failure history, source (`neighbour` for direct, `via:CALL` for transitive, `node-prompt:CALL` for node-prompt-discovered).
5. **Learned routes** — for the `passive-flood` algorithm, observed forwards build up an internal "I know how to reach X via Y" map.

## Discovery channels

A discovery channel is the tuple `(bearer, channel-key)`. For AGW, the channel key is the BPQ port byte. For UDP datagram, it's the multicast endpoint. Each channel carries:

- **Beacon cadence** — how often we transmit our own beacon on this channel.
- **Advertised TTL** — how long peers should consider our beacon valid.
- **Link-class hint** — used by the routing cost model.
- **Optional per-channel airtime budget** — caps tx on this channel independently of the global budget.
- **Optional scheduled-solicit interval** — for HF NVIS where push-only beaconing is too expensive.
- **Enabled flag** + free-form notes.

There is **no default channel**. Operators add channels via the dashboard's **Discovery channels** section once they've thought about which BPQ port DAPPS should beacon on. Defaulting to "beacon on port 0" would silently put DAPPS chatter on whatever band that port happens to be — possibly a band the operator's licence class doesn't permit at the relevant power level. Explicit add is the right contract.

## Beacons

When you enable a discovery channel, the beaconer sends a small frame on its cadence — your callsign + bearer hint + cost. Every other DAPPS node that hears it adds (or refreshes) a row in its discovered-peers table.

A beacon is one packet, not a session — it's stateless. The advertised TTL is how long the receiver should remember the row before it ages out.

## Solicits (B6.2)

Beacons are push: I send mine, you happen to be listening. Solicits are pull: I ask "is anyone out there?" and listen for replies for a window.

Useful on **HF NVIS** where the round-trip cost of a beacon (and the airtime budget you'd have to give it) makes "transmit and hope" expensive. Solicits are operator-triggered (via the dashboard) or scheduled per-channel.

## Probes (B6.1)

A probe is a connected-mode session — DAPPS opens a real DAPPS session to a peer's callsign and confirms the round-trip works. Probing is **off by default**; turn on with `DAPPS_PROBING_ENABLED=true`.

Three flavours:

| Flavour                | What it does                                                                                                                          | Source flag             |
|------------------------|---------------------------------------------------------------------------------------------------------------------------------------|-------------------------|
| **Phase 1**            | Direct probe: open a DAPPSv1 session, confirm prompt, hang up. Records success/failure.                                              | `neighbour`             |
| **Phase 2 — `peers`**  | After a successful Phase 1 probe, ask the peer "who do you know?" via the `peers` command. Seed each unknown callsign as a candidate. | `via:<asked-peer>`      |
| **Phase 2b — node-prompt** | For peers that aren't (yet) DAPPS — connect to the BPQ node prompt, type the application command (`DAPPS` by default), and probe from there. | `node-prompt:<source>`  |

Phase 2b auto-discovery is gated on `DAPPS_AUTO_DISCOVER_VIA_NODE_CALL=true`. When on, every AGW DAPPS beacon also seeds a node-prompt-probe candidate for the source's base callsign.

## Neighbours

The actual forwarding partners. Two ways a row lands here:

- **Operator-added** via the dashboard's Neighbours panel or the `/Neighbours` REST endpoint.
- **Auto-promoted** from a successful probe (when configured).

Each neighbour row carries: callsign, BPQ port (for AGW), UDP endpoint (for the datagram bearer), and an optional cost override.

## The two routing algorithms

Selected via `DAPPS_ROUTING_ALGORITHM` (restart required).

### `passive-flood` (default)

AODV-flavoured. Routes are learned from observed forwards: when a message is forwarded *through* this node, the source becomes a known reach, the next hop becomes the way to reach the destination. The learned-routes table builds up over time without any extra airtime.

Looks at the message destination, walks: explicit per-destination route hint → known-good neighbour → discovered peer → previously-learned route. First match wins.

Works well on small meshes. The trade-off is no proactive route discovery — destinations not yet seen via traffic are unknown until a forward goes through us.

### `meshcore`

DSR-flavoured. The sender stamps the path on the message; intermediate nodes follow it. Adds a few bytes per message; works better when the topology is volatile, when you want explicit per-message path control, or when you're emulating MeshCore-on-AX.25 for testing.

## Route hints

A manual override. The `/RouteHints` endpoint (and dashboard panel) let you say "for messages destined for X, always try Y first." Useful for steering around a known-broken link, or for asymmetric routing where the natural route in one direction differs from the other.

## What you actually do

For most operators, the simplest workable setup is:

1. Add **one or two manual neighbours** for the peers you actually want to talk to. This works without any discovery system at all.
2. **Add a discovery channel** for the BPQ port your DAPPS beacon should go on, with a sensible cadence (e.g. every 10 minutes for VHF FM, longer for HF). Set a per-channel airtime budget.
3. Once you've heard from a few peers via beacons and have an idea of the on-air ecosystem, **enable probing** with the `Overnight` strategy so the connectivity matrix is verified during quiet hours.
4. Decide whether you want **scheduled polling** on (you probably don't if your peers are well-behaved; opportunistic polling already covers the common case).

The dashboard's discovery panels show the live state of all of this — heard peers, probed nodes, learned routes — so you can confirm what's working without guessing.
