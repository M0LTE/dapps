# MeshCore backhaul and routing note

This note supports the roadmap entries in `plan.md`. It exists so the plan can stay readable while still capturing the architectural decisions and questions that matter for the upcoming work.

## Why this matters

DAPPS currently factors inter-node forwarding around a session-oriented exchange. That fits the current BPQ/AX.25 path, but it is the wrong architectural center if DAPPS is going to support datagram bearers such as MeshCore cleanly.

There are two MeshCore-related threads to keep separate:

1. **MeshCore as an alternate bearer/backhaul**
2. **MeshCore as a source of ideas for DAPPS-native routing/discovery**

Both should influence the plan now.

## Backhaul seam

### Recommendation

Insert a DAPPS-owned seam above bearer mechanics and below queue/router semantics.

DAPPS core should own:

- ids / correlation semantics
- TTL semantics
- dedup / replay behaviour
- ack contract
- fragmentation / reassembly policy
- routing intent

Backends should own:

- carrier interaction
- framing
- bearer-specific retries
- capability reporting
- transport-specific packet encoding

### What not to abstract

Do not make the seam:

- "open a stream"
- "read/write bytes"
- "switch between stream and datagram"

That would keep the design transport-shaped and leak the current BPQ path upward.

### What to abstract

The seam should be closer to:

- send a DAPPS backhaul unit to neighbour X
- receive a DAPPS backhaul unit from neighbour X
- expose only the transport capabilities that affect packing/chunking

## Stable DAPPS backhaul units

The same logical DAPPS backhaul units should be carried by:

- the current BPQ/AGW backend
- a future MeshCore Companion backend
- a later MeshCore KISS backend

That means DAPPS should define the logical unit first and then let backends map it to their bearer.

Likely fields include:

- version
- unit type
- message id / correlation id
- source node
- destination node
- app/topic/queue
- TTL / residual lifetime
- fragment number / total fragments
- payload
- integrity field as needed

Exact on-wire encoding can change per backend; ownership of the semantics should not.

## Why start with USB Serial Companion

USB Serial Companion appears to be the fastest route to a working alternate bearer:

- host integration already exists over serial
- higher-level companion operations already exist
- lower implementation risk than raw KISS

That makes it a good first backend once the seam is in place.

### Rule

Do not leak Companion-specific concepts into DAPPS core:

- channel slot numbers
- device-managed channel lists
- companion polling model
- companion command names

Companion should be a backend, not the architecture.

## Why keep KISS in view

KISS may be the cleaner long-term bearer if DAPPS wants to own:

- packet shaping
- group datagram handling
- bearer behaviour
- channel secret management

But KISS should arrive as a backend swap, not as a redesign of DAPPS core. That only works if the seam and logical backhaul units are defined first.

## Routing/discovery ideas worth borrowing

MeshCore's strategy is worth studying explicitly:

- bounded flood for first reachability
- learn from successful delivery
- reuse learned path on later sends
- decay/reset stale paths after failures
- keep flood behaviour policy-bounded

DAPPS should decide deliberately which of those ideas belong in its own routing layer.

Questions worth answering:

- should DAPPS learn whole paths, next hops, or route hints?
- how should route freshness and expiry interact with TTL?
- what information can/should a bearer surface upward?
- what belongs in DAPPS core vs. in a bearer-specific adapter?

## Recommended order

1. define the seam
2. define the logical DAPPS backhaul units
3. refactor the current BPQ path behind the seam
4. add a MeshCore Companion backend
5. explore MeshCore-inspired routing/discovery alongside route-table work
6. add KISS later against the same seam
