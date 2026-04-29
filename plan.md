# DAPPS roadmap

Living planning document. Aim is to get DAPPS into the hands of node operators with enough confidence and documentation that they can run it, and to give app developers a guide they can build against.

## Where we are now

Seven PRs have landed since the restart:

| # | Title | What it did |
|---|---|---|
| 1 | Pin v1 on-air protocol; archive v0 | README protocol section pinned: `chk` rules, `clen=` for `fmt=d`, `s=` salt rename, per-hop residual TTL, fmt-defaults, KV-format constraints. Archived v0 source tree. |
| 2 | Bring parser into compliance with v1 spec | CRC-16/CCITT-FALSE chk impl, `s=` rename in parser, `clen` enforcement, fmt-default, inactivity timeouts on receiver. |
| 3 | Internal Timestamp→Salt rename + IHaveValidator extraction + tests | Code cosmetic alignment with the spec, parser logic lifted into a pure static for testability, 4→37 tests. |
| 4 | AGW outbound transport behind a pluggable interface; remove FBB-telnet | `IDappsOutboundTransport`, `AgwOutboundTransport`, `DappsProtocolClient`. FBB-telnet path entirely deleted. Real-BPQ integration tests via `m0lte/linbpq` Docker image. 60 tests. |
| 7 | App interface: embedded MQTT broker + REST mirror | Embedded MQTTnet broker + REST endpoints sharing one SQLite-backed queue. DAPPS-as-queue, broker-as-channel design. Source-callsign threading. 77 tests. |
| 8 | .NET 10 + central package management + xunit.v3 + Microsoft Testing Platform + this plan.md | Toolchain refresh + roadmap doc. |
| 9 | A1: TTL forwarder logic + two-instance integration test (closes #6) | `DbMessage.Ttl` + `CreatedAt` columns, residual-decrement at forward, drop-on-expiry, `TtlSweeperService`, `TwoInstanceLinbpqFixture`, end-to-end TTL test through real BPQ over AXIP-UDP. 105 tests. Diagnosed M0LTE/linbpq#41 (image's `mail chat` default CMD) along the way. |

The protocol is fully specified (`README.md`'s "On-air protocol" section). The implementation matches the spec for the parts it implements. The on-air format is byte-validated against real BPQ in CI via `m0lte/linbpq`. Local apps can talk to a DAPPS instance via MQTT (durable, idempotent on `dapps-id`) or REST (POST + poll). TTL forwarding works end-to-end across two BPQs.

What's missing to call this complete is the parts that turn a single-node demo into a network: a sysop-friendly neighbour table, peer discovery, route exchange, a deployable container, sysop and developer documentation, and a couple of specific spec follow-ups (multi-part messages, end-to-end source tracking).

## Tom's scratchpad of ideas

- when looking at long distance routing what about looking into the routing implementation in Meshcore?
- what about using Meshcore as a transport?
- I think we should look at shipping an actual usable app, ideally an actual phone app, maybe a messenger app. Or maybe a long form mail app so as not to conflict with whatsapp.

## Open tasks (issues filed)

- **#5** — Switch integration tests from raw `Process` to Testcontainers. Today the `LinbpqIntegrationFixture` shells `docker run` directly; should be on Testcontainers.NET like the rest of the .NET integration-test world. Cleanup, no feature impact.

## Phase A — make forwarding actually forward

**Goal:** the existing transit-and-deliver path works correctly across two nodes for a local sysop running both BPQs.

### A1. TTL forwarder logic *(done — PR #9)*

Landed: `DbMessage.Ttl` + `CreatedAt` columns, residual-TTL decrement on forward, drop-and-delete on expiry, `TtlSweeperService` for background expiry of offers and messages, two-instance integration fixture with end-to-end coverage through real BPQ over AXIP-UDP. M0LTE/linbpq#41 (image's `mail chat` CMD blocking AGW dispatch) was diagnosed and fixed in the same arc, which closed #6.

### A2. Better neighbour table than "manual MAP entries"

Today `DbNeighbour.ConnectScript` is gone (replaced with `BpqPort` from PR #4) but neighbour selection in `OutboundMessageManager.ResolveNeighbour` still relies on `DbRouteHint` — a flat callsign→nexthop table populated by hand. Fine for v0 but not for "give it to a sysop and let them go."

Sysop-friendly model:

- `dapps neighbours add <callsign> [--bpq-port N]` and `... remove`, `... list` — REST endpoints (probably `/Neighbours`) + sample CLI scripts.
- Optional: surface in the planned web UI (Phase D).
- The auto-discovery work (Phase B) feeds this same table; manual + auto coexist.

### A3. Sender-side inactivity timeout on AGW

Receiver-side timeouts landed in PR #2. Sender-side (in `DappsProtocolClient` / `AgwOutboundTransport`) currently just relies on caller-supplied cancellation tokens. The spec says **both** sides SHOULD apply an inactivity timeout. Add per-operation cancellation defaults inside the protocol client so a hung peer can't wedge a forwarder run indefinitely.

### A4. Per-app authentication on MQTT/REST

Today anyone reachable on `MqttPort` or HTTPPORT can pretend to be any app. Acceptable for "single-host loopback" deployments, not for shared nodes.

Minimum:
- MQTT: username/password on connect (MQTT 5 supports it natively; MQTTnet validates via a connection-handler hook).
- REST: bearer token in `Authorization` header; check against a small list of apps stored in DB.
- Both: configurable via the existing `SystemOptions` / config DB, not a separate file.

Doesn't need to be PKI-grade — operators can issue tokens out-of-band. This isn't an internet protocol.

## Phase B — peer discovery via UI frame beacons

**Goal:** DAPPS nodes find each other on a shared frequency without the sysop hand-coding neighbour tables.

The on-air protocol already has a hand-wavey "discovery" section in the README. AGW exposes the primitives (`'M'` / `'V'` for UI send, `'m'` for monitor). The transport interface in PR #4 is shaped for it but doesn't expose UI yet.

### B1. Extend the transport interface

Add a parallel surface to `IDappsOutboundTransport`:

```csharp
public interface IDappsUiTransport
{
    Task SendUiFrameAsync(string fromCall, byte[] payload, int bpqPort, CancellationToken ct);
    IAsyncEnumerable<UiFrameReceived> MonitorUiFramesAsync(int bpqPort, CancellationToken ct);
}
```

`AgwOutboundTransport` (or a sibling `AgwUiTransport`) implements both via `'M'` send + `'m'` monitor on the existing AGW connection. RHP doesn't currently support UI in BPQ's implementation, so the RHP variant of this interface stays a stub until BPQ catches up.

### B2. Beacon protocol

Beacon payload is a tiny text line, parseable by hand:

```
DAPPS v1 callsign hash hops ttl
```

Open question: should the beacon also advertise *known* peers (link-state-style), or only *self* (distance-vector-style)? Distance-vector matches AX.25 NET/ROM tradition and keeps payloads tiny. Recommend distance-vector for v1.

Beacon cadence: every N minutes (configurable; default 30 min so we don't pollute the channel).

### B3. Discovery + routing daemon

Background hosted service:
- Sends our beacon on configured BPQ port(s) every N minutes.
- Subscribes to UI monitor; parses received DAPPS beacons; updates a `DbDiscoveredPeer` table with `LastSeen`.
- A new `RouteResolver` consults discovered peers + manual `DbNeighbour` entries.
- Auto-aging of stale peers (drop if not seen for K beacon intervals).

### B4. Routing decisions

When `OutboundMessageManager` needs to forward a message:
- Direct neighbour with that callsign → use it.
- Neighbour with hops < some-budget that knows the destination via beacon hop-counts → use that path's first hop.
- Otherwise → drop with "no route" + log + leave in DB until TTL expires.

## Phase C — deployable, runnable for sysops

**Goal:** a sysop can `docker run m0lte/dapps-core:latest` (or `apt install dapps`) and it Just Works with a config file.

### C1. Docker image publishing

The repo's README points at `m0lte/dapps-core` already (Installation section). The image isn't currently published. Tasks:
- Multi-stage Dockerfile — already present at `src/dapps/Dockerfile`; needs auditing for net10.0.
- GitHub Actions workflow that builds + pushes on `master` (mirrors how `m0lte/linbpq` is published).
- Tag strategy: `latest`, `master-<sha>`, semantic version on release.
- ARM64 + amd64 builds (Raspberry Pi is the obvious target).

### C2. Config tooling

Today config lives in `dapps.db` (the `systemoptions` table) and the only way to seed it is via REST `/Config` POST or by editing the DB directly. Improve:
- CLI subcommand `dapps configure --callsign <X> --node-host <Y>` etc. that writes to the DB.
- Or: read defaults from environment variables on startup (`DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, …) and seed missing rows. Easiest path for `docker-compose`.
- Sensible error if required values are missing instead of silently using `N0CALL`.

### C3. Health, logs, observability

- `/Health` endpoint with subsystem checks (BPQ AGW reachable, MQTT broker port listening, DB writable).
- Structured log output (JSON option) so sysops who want to ingest into Loki / Elastic can.
- A simple metrics endpoint (Prometheus format) covering: queue depth, messages forwarded last hour, neighbours seen, failed forward attempts.

### C4. Install / upgrade docs

The README's "Installation" section is fine for a happy path. Needs to grow:
- Upgrading: schema changes (`dapps.db` rebuilds aren't great if state matters).
- Backups: just back up `dapps.db` and the config dir.
- Troubleshooting: the most common things that go wrong are AGW config mismatches, callsign typos, BPQ port-byte indexing surprises.

## Phase D — web management UI

**Goal:** sysops who want a GUI to inspect state, exercise the system, and verify config can do so without leaving a browser.

The existing app exposes ASP.NET Core + Scalar/Swagger for its REST surface — that's enough scaffolding to host a UI alongside.

### D1. Inspection / dashboard

- **Overview** — callsign, AGW status (connected/last-seen), MQTT broker status, queue depths (inbound, outbound, total messages).
- **Messages** table — id, source, destination, ttl, status (pending/forwarded/locally-delivered/expired), age. Filter by app, callsign, status. Click for payload preview.
- **Neighbours** — manually configured + auto-discovered, with last-heard timestamps.
- **Routes** — current routing table state.
- **Logs tail** — recent log entries (last N), filterable by level.

### D2. Exercising the system

A **"send test message"** form: dropdown of known apps + neighbours, payload textbox, hit send. Useful to verify a node-to-node link works without writing an app first.

A **"subscribe to inbound"** view: pick an app, get a live stream (Server-Sent Events or WebSocket) of messages arriving for that app. Browser becomes a stand-in MQTT subscriber for ad-hoc testing.

A **"manual ihave"** terminal: paste an `ihave` line, see how the validator parses it, see what `chk` it would compute. Already-pinned `IHaveValidator` is the engine.

### D3. Configuration UI

Replace the bare `POST /Config` endpoint with a form: callsign, AGW host/port, MQTT port, default BPQ port, neighbour list. Save persists to the DB; restart prompts when needed.

### D4. Auth on the UI

Same auth model as the REST API (Phase A4) — bearer token, or HTTP Basic for easy bookmarking.

### D5. Implementation notes

Likeliest-cheapest stack: server-rendered Razor or Blazor Server. Avoid SPA tooling unless someone wants to take that on. The existing Scalar OpenAPI page already gives free API exploration; build the UI as a sibling, not a replacement.

## Phase E — application developer guide

**Goal:** someone who's never seen DAPPS can read the guide, understand the model, and ship a working app in an afternoon.

### E1. Concepts page

- What DAPPS *is* and what it *isn't* (eventual delivery, not real-time; one-way fire-and-forget, not RPC).
- The `app@callsign` destination model.
- Content-addressed message ids and what idempotency means in practice.
- Why DAPPS, not raw AX.25.
- Why not packet mail.

### E2. Tutorial: hello-world application

Walk through a Python script that:
1. Connects to local DAPPS over MQTT.
2. Subscribes to `dapps/in/hello`.
3. Publishes a message back via `dapps/out/hello/<dest>`.
4. Acks via `dapps/ack/hello`.

Same tutorial in C# / Go / Node (community contributions welcome) once the Python baseline is solid.

### E3. Reference

- MQTT topic structure — already in README; lift into the developer guide and expand with examples per topic.
- REST endpoint reference with `curl` examples.
- The `dapps-id` / `dapps-source` user properties on inbound deliveries.
- Idempotency contract.
- Limits: payload size (current de-facto limit is the message-id collision space, ~268M unique combos, but practical limit is whatever fits in a few AX.25 frames per message).

### E4. Sample app gallery

Each one a 1-2 page write-up + working code:
- Group chat (one app, multiple subscribers per node).
- Sensor data publisher (pubs every N min from one node, subscribers on others).
- Mailing-list / forum-style "post and view" application.
- Two-way pager / messenger ("send to N0CALL with payload X").

These exist partly to validate the API surface, partly to give early adopters something to copy from.

### E5. Reference implementation in another language

A second compatible implementation forces the spec to be precise. Python is the obvious second language (large amateur radio Python community, easy onboarding). Aim for:
- A minimal `dapps-py` that implements the on-air protocol and the AGW transport.
- Talks to the same `m0lte/linbpq` Docker image we use in CI.
- Cross-implementation interop test in CI: `dapps.core` (C#) on one side, `dapps-py` on the other, message goes through.

This is the proof that the spec is portable rather than an accidental description of the C# implementation.

## Phase F — spec maturation

Items deferred from earlier discussions, mostly "we know it'll need to happen, just not yet":

### F1. End-to-end source tracking

Today `dapps-source` user property is the **link source** — the callsign that handed us the message. In a multi-hop scenario it's the last hop, not the original sender. Spec change needed: an `src=` field on the `ihave` line carrying the originator's callsign.

Recompute `chk` example. Update parser. Update outbound emitter. Surface via a new MQTT user property (e.g. `dapps-origin`) so apps can distinguish "delivered by N1FOO" from "originally sent by G7BAR".

### F2. Multi-part messages

Messages today are atomic. For payloads larger than a comfortable AX.25 paclen (say 5 KB+), apps would benefit from native chunking with reassembly on the receiver side. Possible spec extension: `frag=N/M` headers on `ihave` + a small reassembly buffer in DAPPS.

### F3. `rev` polling

Documented in the spec since v1 ("polling for messages") but deliberately deprioritized — with forward connections both ways, polling shouldn't be needed. Keep on the back-burner; revisit if a real deployment turns out to need it.

### F4. Protocol versioning policy

Pre-emptive decision before the next breaking change: do we cut `DAPPSv2>` for any incompatible wire change, or stay on `v1` forever and use feature negotiation (`have=feature1,feature2` on the prompt or in `ihave` headers)?

Weak preference: bump on any incompatible wire change. The prompt is the natural carrier and `v2` clients can offer to talk `v1` if they detect it. But pin the answer in writing before the next change.

### F5. Authenticated message origin (signing)

Mentioned in the original gist. Long-term: messages signed by the source node so the recipient can verify the origin chain hasn't been tampered with. Pointless without a ham-radio-friendly identity layer; defer indefinitely until that exists.

## Phase G — community + governance

The README already says "multiple compatible implementations is healthy." Once the developer guide exists and there's a Python reference implementation:

- Move the spec into a versioned document (in `docs/spec/v1.md`, with future `v2.md` etc.).
- Issue tracker labels for spec discussion vs. implementation issues.
- Release notes / changelog discipline so implementers know what's changed.

Probably also worth a public conversation about whether DAPPS sits under OARC (the original RFC venue) or stays as a personal project — has implications for spec governance and license expectations.

## Cross-cutting: licensing housekeeping

Resolved during the toolchain refresh: tests use **AwesomeAssertions** (MIT-licensed community fork of FluentAssertions, drop-in API-compatible). FluentAssertions 8.x's switch to a paid Xceed licence isn't a concern. No further action.

## Suggested ordering

Roughly:

1. **A1** (TTL forwarding) — *done*. **A2** (neighbour-table cleanup) — next.
2. **C1 + C2 + C4** (docker image, config tooling, install docs) — gets the thing into one sysop's hands.
3. **A4** (per-app auth) — needed before anyone with a publicly-reachable node can run it.
4. **D1 + D2** (web UI inspection + exercise) — turns "running" into "comfortable to run".
5. **B1–B4** (UI beacon discovery + auto-routing) — graduates from manual neighbour config to a real network.
6. **E1–E5** (developer guide + sample apps + Python ref impl) — unlocks third-party app development.
7. **A3, C3, D3, D4, F1–F4** in parallel as polish.
8. **G** when there's a community to govern.

Phases A and C can ship as a single "v0.1.0 — runnable" release. Phase D as "v0.2.0 — operable". Phase E + B as "v1.0.0 — networked + developable".

## Useful pointers for a fresh session

If picking this up cold:

- `README.md` — current spec + app interface docs.
- `src/dapps/dapps.core/` — main app (ASP.NET Core hosted services).
- `src/dapps/dapps.client/` — sender-side library (`AgwOutboundTransport`, `DappsProtocolClient`, `DappsMessage`).
- `src/dapps/dapps.core/Services/` — `MqttBrokerService`, `OutboundMessageManager`, `InboundConnectionHandler`, `IHaveValidator`, `Database`, etc.
- `src/dapps/dapps.core.tests/` — unit (xunit.v3, MTP runner) + integration (Docker `m0lte/linbpq`).
- `src/dapps/Directory.Packages.props` — central package versions; update there, not in csproj.
- `~/src/linbpq/docs/protocols/` — apps-interface.md, rhp.md, bpqtoagw.md — the BPQ protocol surfaces we built against.
- `~/src/linbpq/tests/integration/` — the linbpq test suite, especially `test_two_instance_agw_tunnel.py` for the topology pattern that feeds issue #6.
- Open issues #5 and #6 for known test-infra gaps.

Test runner today is `dotnet test` — MTP filter syntax is `-- --filter-trait "Category=Integration"` (not `--filter Category=...`, which is the old VSTest syntax).

The protocol is frozen at v1. The implementation now performs TTL forwarding end-to-end across hops; remaining spec features that are still words-on-a-page are the explicit deferrals in Phase F (multi-part, end-to-end source, signing, polling). The integration-test setup proves the wire format is correct against real BPQ.
