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

What's missing to call this complete is the parts that turn a single-node demo into a network: a transport-neutral backhaul seam, a sysop-friendly neighbour table, peer discovery, route exchange, a deployable container, sysop and developer documentation, and a couple of specific spec follow-ups (multi-part messages, end-to-end source tracking).

## Tom's scratchpad of ideas

- when looking at long distance routing what about looking into the routing implementation in Meshcore?
- what about using Meshcore as a transport?
- I think we should look at shipping an actual usable app, ideally an actual phone app, maybe a messenger app. Or maybe a long form mail app so as not to conflict with whatsapp.
- RHP (v2?) support

## Open tasks (issues filed)

- **#5** — Switch integration tests from raw `Process` to Testcontainers. Today the `LinbpqIntegrationFixture` shells `docker run` directly; should be on Testcontainers.NET like the rest of the .NET integration-test world. Cleanup, no feature impact.

## Phase A0 — insert the backhaul seam before transport choices harden

**Goal:** DAPPS core talks in terms of forwarding durable DAPPS units to neighbours, not in terms of opening a stream and speaking one specific session protocol.

The current factoring around `IDappsOutboundTransport` + `Stream` was the right move to get AGW under an interface, but it is still too transport-shaped if DAPPS is going to support a datagram bearer such as MeshCore without contorting the rest of the system. This is high priority while the code is still in progress.

### A0.1. Introduce a DAPPS-owned backhaul interface *(done — PR #12)*

Outbound seam: `IDappsBackhaul.SendAsync(BackhaulMessage, BackhaulRoute, localCallsign)`. `OutboundMessageManager` no longer opens streams or speaks DAPPSv1 itself; it constructs a backhaul message and hands it off. Inbound seam: `IBackhaulInbox.DeliverAsync(BackhaulMessage, sourceCallsign)`. Bearer-specific receive code (today the `DAPPSv1>` session reader, future MeshCore receivers) calls into the inbox once a message is fully received and validated; the inbox owns DB persistence and conditional MQTT delivery. Types live in `dapps.client/Backhaul/` so other-bearer projects can take a dependency without dragging in the AGW stack.

### A0.2. Refactor the current BPQ/AGW path behind the seam *(done — PR #12)*

Outbound: `Dappsv1SessionBackhaul` wraps `IDappsOutboundTransport` + `DappsProtocolClient`. Inbound: `InboundConnectionHandler` retains DAPPSv1 session/parsing (offer↔data correlation, hash check, on-the-wire ack), but now hands the validated message to `IBackhaulInbox` instead of writing DB rows + MQTT-injecting directly.

### A0.3. Define stable DAPPS backhaul units *(done — PR #12)*

`BackhaulMessage(Id, Destination, Salt, Ttl, Payload, Headers?)` is the bearer-neutral unit. Carries everything DAPPS callers need to forward or deliver; nothing bearer-specific. Fragmentation/reassembly (Phase F2) will sit between this unit and the bearer adapter, not at this layer.

### A0.4. Datagram bearer as the forcing function *(UDP stand-in done; MeshCore deferred)*

The seam needed a non-stream bearer to validate the architecture before MeshCore lands. Implemented a UDP datagram backhaul (`UdpDatagramBackhaul` + `UdpDatagramListener`) plus a bearer-agnostic `Packetiser` and `BackhaulMessageCodec` in `dapps.client/Backhaul/Datagram/`. End-to-end tests on loopback exercise both single-fragment and multi-fragment messages with an artificially low MTU (64 bytes), proving the seam supports a fire-and-forget datagram bearer with DAPPS-owned fragmentation.

`IDappsBackhaul.CanHandle(BackhaulRoute)` and per-route bearer hints (`BackhaulRoute.UdpEndpoint`, `DbNeighbour.UdpEndpoint`) let the OMM dispatch to the right bearer per neighbour without leaking bearer-specific code into queue/router logic.

MeshCore-specific work (Companion-over-USB framing, KISS framing, neighbour discovery on a real mesh) is the next milestone here. The packetiser and codec carry over; only the wire-emit + wire-ingest layer is new.

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

### A3. Sender-side inactivity timeout on AGW *(done)*

`DappsProtocolClient` wraps every per-byte read with a 3-minute inactivity timeout (matches receiver-side T3-default). On expiry the read surfaces a `TimeoutException` rather than blocking forever; the forwarder loop catches and moves on. `AgwOutboundTransport.ConnectAsync` got the same treatment on the connect-confirm wait. Outer `CancellationToken` (from shutdown) takes precedence over the inactivity timer.

### A4. Per-app authentication on MQTT/REST *(done)*

Per-app credentials issued via `/AppTokens` (POST mints + returns plaintext once; DELETE revokes). Tokens hashed at rest with PBKDF2-HMAC-SHA256 + 16-byte salt. Verification is constant-time-equality on the derived bytes.

Off by default — `SystemOptions.AuthRequired` is false initially, so existing single-host-loopback deployments don't break on upgrade. Operators issue tokens, then flip the flag (via `/Config` or `DAPPS_AUTH_REQUIRED=true` on first run).

When enabled:
- **REST**: `BearerAuthMiddleware` on `/AppApi/*` requires `Authorization: Bearer <token>` and stamps the authenticated app onto `HttpContext.Items`. Controllers call `IsAuthorisedForApp(...)` and return 403 on path-app mismatch.
- **MQTT**: `MqttBrokerService.OnValidatingConnection` validates `username` (= app name) + `password` (= token plaintext); rejects with `BadUserNameOrPassword` on mismatch. `InterceptingPublish` and `InterceptingSubscription` enforce that a connected client only operates on its own `dapps/in/<app>`, `dapps/out/<app>/...`, `dapps/ack/<app>` topics.
- **Admin surfaces** (`/Config`, `/Neighbours`, `/AppTokens` itself) are deliberately not behind the bearer check — pairing them with bearer auth would be a chicken-and-egg on first use. Loopback-binding remains the recommendation in the README until proper admin auth lands.

Not PKI-grade by design (no TLS, OAuth, JWT, 2FA). For TLS, sysops front the REST API with a reverse proxy.

## Phase B — peer discovery and routing evolution

**Goal:** DAPPS nodes find each other on a shared frequency without the sysop hand-coding neighbour tables, and route learning evolves toward a transport-agnostic automatic-routing model.

The on-air protocol already has a hand-wavey "discovery" section in the README. AGW exposes the primitives (`'M'` / `'V'` for UI send, `'m'` for monitor). The transport interface in PR #4 is shaped for it but doesn't expose UI yet.

MeshCore is relevant here in two separate ways:

1. as a possible bearer under the new backhaul seam (Phase A0)
2. as a source of ideas for DAPPS's own route/discovery model

The second one matters even if DAPPS never ships a MeshCore backend.

### B1. Discovery seam *(done — different shape than originally sketched)*

The originally-sketched `IDappsUiTransport` was AGW-flavoured. After the A0 backhaul-seam work it made more sense to lift discovery to the same level — bearer-neutral. Implemented as:

```csharp
public interface IDiscoveryBearer : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(CancellationToken ct);
    Task AnnounceAsync(BeaconFrame beacon, CancellationToken ct);
    IAsyncEnumerable<BeaconFrame> ListenAsync(CancellationToken ct);
}
```

Two implementations: `AgwUiDiscoveryBearer` (AGW `'X'` register + `'m'` monitor + `'M'` send + parses `'U'` frames) and `UdpMulticastDiscoveryBearer` (UDP multicast group, useful for LAN dev/testing without a BPQ stack).

### B2. Beacon protocol *(done)*

Wire form: `DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300`. KV style rather than positional so future fields slot in without breaking existing parsers. The `Bearer` field on `BeaconFrame` is stamped by the receiver from the channel a beacon arrived on — never carried on the wire (would let a misbehaving peer claim routes it doesn't have).

Cadence: configurable `DiscoveryBeaconIntervalSeconds`, default 300s. Operators on shared RF channels should bump to 1800+ before enabling AGW discovery.

Distance-vector for v1, as recommended in the original sketch — beacons advertise self only; future hop-count routing tracks paths by remembering which bearer the beacon arrived on.

### B3. Discovery daemon *(done)*

`DiscoveryService` is the hosted service. For each configured bearer it: starts the bearer, fires its own beacon on a timer, concurrently iterates the bearer's listen stream, upserts `DbDiscoveredPeer` rows. Sweeper drops rows whose `(now - LastSeen) > beacon.Ttl`.

The dashboard surfaces discovered peers (callsign, bearer, hops, source endpoint, age, ttl) so a sysop can verify discovery in real time without log-grepping.

### B4. Routing decisions *(deferred — DiscoveredPeer rows present, resolver doesn't consult them yet)*

`OutboundMessageManager.ResolveNeighbour` currently consults `DbNeighbour` (manual) and `DbRouteHint` (manual fallback). Promoting `DbDiscoveredPeer` into the resolver is a small follow-up; the data is already in the DB and the seam is bearer-neutral, so the resolver just needs to merge sources and pick by hops.

### B5. Explore MeshCore-style route learning inside DAPPS

MeshCore's model is worth studying explicitly while this work is still fluid:

- first delivery by bounded flood
- learn a useful path from the successful exchange
- reuse that path for later deliveries
- reset / decay stale paths when delivery fails
- keep flooding bounded by policy

DAPPS should not cargo-cult MeshCore packet formats, but it should decide deliberately which of those ideas belong in a transport-agnostic DAPPS routing layer:

- learned whole paths vs. next-hop hints
- route freshness and expiry
- direct-vs-flood promotion
- neighbour advertisements vs. route exchange
- what belongs in DAPPS core vs. what stays bearer-specific

If the answer ends up being "DAPPS learns next-hop reachability and leaves path details to the bearer," that is still a useful outcome — but it should be reached consciously.

## Phase C — deployable, runnable for sysops

**Goal:** a sysop downloads a single binary for their platform from a GitHub Release, drops it next to a `dapps.db`, and it Just Works.

### C1. Native single-file binary releases

Direction: native single-file binaries published as GitHub Release assets, not a Docker image. Operators run a binary; no .NET runtime install, no Docker install, no container plumbing. The Dockerfile in the repo stays as a secondary option for those who want it but isn't the primary distribution.

- GitHub Actions matrix build on tag push (`v*.*.*`) — `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`. ARM Linux is the Raspberry Pi case.
- `dotnet publish ... --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` so the SQLite native lib bundles into the executable.
- Trimming left off — ASP.NET Core + DI + JSON have enough reflection that trimming tends to break startup in subtle ways. Binary is ~100MB; acceptable for the ergonomics win.
- Release workflow auto-creates the GitHub Release with the four binaries attached, named `dapps-<rid>` / `dapps-<rid>.exe`.

### C2. Config tooling *(env-var seeding done; CLI subcommand deferred)*

`DbStartup` now seeds missing `systemoptions` rows from `DAPPS_*` environment variables (`DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, `DAPPS_AGW_PORT`, `DAPPS_DEFAULT_BPQ_PORT`, `DAPPS_MQTT_PORT`, `DAPPS_NODE_TYPE`). Once a row exists, env vars stop mattering — no surprise overwrites of operator-set values on restart. After seed, the startup refuses to run with the placeholder `N0CALL` callsign and logs a clear error pointing the operator at `DAPPS_CALLSIGN` or `/Config`. The originally-listed `dapps configure` CLI subcommand is unnecessary given env-var seeding plus the existing `/Config` REST endpoint; deferred unless a real need surfaces.

### C3. Health, logs, observability

- `/Health` endpoint with subsystem checks (BPQ AGW reachable, MQTT broker port listening, DB writable).
- Structured log output (JSON option) so sysops who want to ingest into Loki / Elastic can.
- A simple metrics endpoint (Prometheus format) covering: queue depth, messages forwarded last hour, neighbours seen, failed forward attempts.

### C4. Install / upgrade docs *(done)*

README "Getting started" rewritten end-to-end for the native-binary distribution: prerequisites, the BPQ-side `bpq32.cfg` snippet (AGW + Apps Interface slot + APPLICATION line with `TRANS`), download/run instructions for each release artifact, env-var bootstrap, neighbour add/remove via REST, and a verification step that connects from a node prompt and sees the `DAPPSv1>` banner. Troubleshooting block covers the usual surprises (AGW config, callsign typos, BPQ port-byte indexing, `TRANS` flag missing). Backups + upgrade notes added.

## Phase D — web management UI

**Goal:** sysops who want a GUI to inspect state, exercise the system, and verify config can do so without leaving a browser.

The existing app exposes ASP.NET Core + Scalar/Swagger for its REST surface — that's enough scaffolding to host a UI alongside.

### D1. Inspection / dashboard *(MVP done)*

Single Razor Pages dashboard at `/`. Covers: callsign, BPQ AGW reachability probe, MQTT/UDP listener status, auth-required flag, queue depths (total / pending-outbound / undelivered-local / neighbour count), neighbours table, recent-messages table (id, dst, src, bytes, status, ttl, age).

Deferred for follow-up: filter by app/callsign/status, click-for-payload-preview, dedicated routes view (DbRouteHint is mostly gone post-A2 anyway), logs-tail page (sysops can use stdout / journalctl). Filed as items here so they're not lost.

### D2. Exercising the system *(send-test-message done; SSE + ihave terminal deferred)*

Send-test-message form on the dashboard POSTs into `Database.SubmitOutboundMessage` — same path an app would take via REST/MQTT. Useful for verifying a node-to-node link without writing an app first.

The "subscribe to inbound" SSE view and the "manual ihave" terminal are still useful but bigger surfaces; deferred. Both have natural homes once the dashboard grows beyond a single page (separate `/Inbound` and `/IHave` Razor pages).

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

1. **A0.1–A0.3** (backhaul seam) — *done*. **A1** (TTL forwarding) — *done*. **A2** (neighbour-table cleanup) — *done*.
3. **C1 + C2 + C4** (docker image, config tooling, install docs) — gets the thing into one sysop's hands.
4. **A4** (per-app auth) — *done*.
5. **D1 + D2** (web UI inspection + exercise) — *MVP done*. SSE inbound feed + ihave terminal still pending.
6. **B1–B3** (beacon discovery seam, AGW UI + UDP multicast bearers, daemon, dashboard surface) — *done*. **B4** (resolver consulting DbDiscoveredPeer) and **B5** (MeshCore-inspired flood-and-learn) remain.
7. **A0.4** — UDP datagram stand-in *done*; first real alternate bearer (likely MeshCore Companion) when the seam is ready.
8. **E1–E5** (developer guide + sample apps + Python ref impl) — unlocks third-party app development.
9. **A3, C3, D3, D4, F1–F4** in parallel as polish.
10. **G** when there's a community to govern.

Phases A and C can ship as a single "v0.1.0 — runnable" release. Phase D as "v0.2.0 — operable". Phase E + B as "v1.0.0 — networked + developable".

## Useful pointers for a fresh session

If picking this up cold:

- `README.md` — current spec + app interface docs.
- `src/dapps/dapps.core/` — main app (ASP.NET Core hosted services).
- `src/dapps/dapps.client/` — sender-side library (`AgwOutboundTransport`, `DappsProtocolClient`, `DappsMessage`).
- `src/dapps/dapps.core/Services/` — `MqttBrokerService`, `OutboundMessageManager`, `InboundConnectionHandler`, `IHaveValidator`, `Database`, etc.
- `src/dapps/dapps.core.tests/` — unit (xunit.v3, MTP runner) + integration (Docker `m0lte/linbpq`).
- `docs/meshcore-backhaul-routing.md` — design note on the backhaul seam, MeshCore Companion/KISS implications, and MeshCore-inspired routing/discovery questions.
- `src/dapps/Directory.Packages.props` — central package versions; update there, not in csproj.
- `~/src/linbpq/docs/protocols/` — apps-interface.md, rhp.md, bpqtoagw.md — the BPQ protocol surfaces we built against.
- `~/src/linbpq/tests/integration/` — the linbpq test suite, especially `test_two_instance_agw_tunnel.py` for the topology pattern that feeds issue #6.
- Open issues #5 and #6 for known test-infra gaps.

Test runner today is `dotnet test` — MTP filter syntax is `-- --filter-trait "Category=Integration"` (not `--filter Category=...`, which is the old VSTest syntax).

The protocol is frozen at v1. The implementation now performs TTL forwarding end-to-end across hops; remaining spec features that are still words-on-a-page are the explicit deferrals in Phase F (multi-part, end-to-end source, signing, polling). The integration-test setup proves the wire format is correct against real BPQ.
