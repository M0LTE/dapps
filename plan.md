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
| 8 | .NET 10 + central package management + xunit.v3 + Microsoft Testing Platform + this plan.md | Toolchain refresh + roadmap doc. **Subsequently rolled back to .NET 8 LTS** — see below for the why. |
| 9 | A1: TTL forwarder logic + two-instance integration test (closes #6) | `DbMessage.Ttl` + `CreatedAt` columns, residual-decrement at forward, drop-on-expiry, `TtlSweeperService`, `TwoInstanceLinbpqFixture`, end-to-end TTL test through real BPQ over AXIP-UDP. 105 tests. Diagnosed M0LTE/linbpq#41 (image's `mail chat` default CMD) along the way. |

The protocol is fully specified (`README.md`'s "On-air protocol" section). The implementation matches the spec for the parts it implements. The on-air format is byte-validated against real BPQ in CI via `m0lte/linbpq`. Local apps can talk to a DAPPS instance via MQTT (durable, idempotent on `dapps-id`) or REST (POST + poll). TTL forwarding works end-to-end across two BPQs.

What's missing to call this complete is the parts that turn a single-node demo into a network: a transport-neutral backhaul seam, a sysop-friendly neighbour table, peer discovery, route exchange, a deployable container, sysop and developer documentation, and a couple of specific spec follow-ups (multi-part messages, end-to-end source tracking).

## Tom's scratchpad of ideas

- ~~when looking at long distance routing what about looking into the routing implementation in Meshcore?~~ *(actioned — see B5.1; selectable alongside the default passive-flood stack)*
- what about using Meshcore as a transport? *(separate question; tracked under Phase H1)*
- I think we should look at shipping an actual usable app, ideally an actual phone app, maybe a messenger app. Or maybe a long form mail app so as not to conflict with whatsapp.
- RHP (v2?) support
- MCP server endpoint exposing some DAPPS surface to LLMs — could let an agent participate in routing decisions ("explore via this neighbour and report what you find"), help with network discovery / topology mapping, or surface a richer query interface than the dashboard JSON. Open question what the right tools are: read-only diagnostics, controlled probes, route hints, app-traffic synthesis for testing? Worth a short design pass when it gets pulled forward.
- **Global airtime budget for discovery.** A single operator-tuneable cap on airtime consumed by *all* discovery-class transmissions — beacons (B1–B4), HF solicits (B6.2), connected-mode probes (B6.1). Today each subsystem has its own cadence knobs (per-channel `BeaconIntervalSeconds`, `ProbeIntervalHours`, future solicit cadence) and they don't know about each other. A frequency-coordinator's view of "this DAPPS node is using N% of channel time" is useful — especially on shared 1200-baud VHF — and the obvious knob is one budget that the schedulers compete for. Shape: `DiscoveryAirtimeBudgetSecondsPerHour` on `SystemOptions`, optionally per-channel; each subsystem reports its planned transmission length to a shared accountant before transmitting; the accountant defers or drops if the budget is exceeded. Worth designing properly before we add a third or fourth discovery-class subsystem.
- **Probe strategies, not bare intervals.** The `ProbeIntervalHours` knob on B6.1 today is a placeholder shape — it picks a fixed cadence regardless of what the network's doing. Real options the operator wants: *run-overnight* (configurable local-time window, the obvious default), *run-when-quiet* (defer probes when the OutboundForwarderService is actively forwarding or AGW saw recent traffic), *fixed-interval* (today's behaviour, kept for tests / for sysops who want it deterministic). Strategy as an enum on `SystemOptions`, with a small dispatcher in `ProbeSchedulerService`. Pairs naturally with the airtime-budget idea above — "run when quiet" is a special case of "share the budget gracefully." Probably done together when the time-of-day work lands.

## Open tasks (issues filed)

- **#5** — Switch integration tests from raw `Process` to Testcontainers. Today the `LinbpqIntegrationFixture` shells `docker run` directly; should be on Testcontainers.NET like the rest of the .NET integration-test world. Cleanup, no feature impact.

- **No automatic forwarder loop** *(fixed)*. `OutboundMessageManager.DoRun` was only triggered by an explicit POST to `/Message/dorun`, so messages would queue and sit until an operator poked the API. Fixed with `OutboundForwarderService` — a `BackgroundService` that ticks `DoRun` every 5 seconds (after a 3-second startup grace). A `SemaphoreSlim` on `DoRun` makes concurrent triggers (auto-tick racing the manual `/Message/dorun`) safe — second-and-later concurrent calls return immediately rather than racing through the same pending list and double-sending. Manual `/Message/dorun` still works for "kick now" semantics. Opportunistic kicks on submit / inbound delivery would shave latency further but aren't strictly necessary now that the tick is automatic.

- **`UdpMulticastDiscoveryBearer` multi-channel-per-port leakage** *(fixed)*. When one process subscribed to multiple discovery channels that shared a UDP port, the per-channel `recv` sockets all bound `0.0.0.0:port` with `SO_REUSEADDR` and Linux's REUSEADDR + multicast filtering edge cases let packets leak across groups within the process. Fixed by binding each `recv` socket to the multicast group address itself (`endpoint.Address:endpoint.Port`) rather than `IPAddress.Any`, so the kernel applies the group filter at the socket level. Regression test: `UdpMulticastDiscoveryTests.TwoChannels_SamePortDifferentGroups_NoCrossChannelLeakage`.

- **Inject `TimeProvider` so time-dependent code is testable.** Today's `OutboundForwarderService`, `TtlSweeperService`, `UpdateChecker`, `OperationalMetrics` timestamps, and `TtlMath` all read `DateTime.UtcNow` directly, which forces tests to use real-time `Task.Delay` and tickInterval/startupDelay knobs to simulate cadence — the `OutboundForwarderService` tests already had to plumb a test-only ctor for this and tripped CI flake. Switch to `Microsoft.Extensions.TimeProvider.Abstractions` (built into .NET 8) and inject `TimeProvider` everywhere `DateTime.UtcNow` appears. Tests use `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider.Advance(TimeSpan)` instead of `Task.Delay`. **Do this in one go** across all the affected services — half-converted code is worse than not converted at all because some parts respect the fake clock and some don't, so a "fast-forward 30s" doesn't fast-forward consistently. Pay-off: TTL/age-out/cadence tests get truly deterministic and seconds faster; the ad-hoc test-only ctor on `OutboundForwarderService` goes away. Belongs in Phase A polish.

**Goal:** DAPPS core talks in terms of forwarding durable DAPPS units to neighbours, not in terms of opening a stream and speaking one specific session protocol.

The current factoring around `IDappsOutboundTransport` + `Stream` was the right move to get AGW under an interface, but it is still too transport-shaped if DAPPS is going to support a datagram bearer such as MeshCore without contorting the rest of the system. This is high priority while the code is still in progress.

### A0.1. Introduce a DAPPS-owned backhaul interface *(done — PR #12)*

Outbound seam: `IDappsBackhaul.SendAsync(BackhaulMessage, BackhaulRoute, localCallsign)`. `OutboundMessageManager` no longer opens streams or speaks DAPPSv1 itself; it constructs a backhaul message and hands it off. Inbound seam: `IBackhaulInbox.DeliverAsync(BackhaulMessage, sourceCallsign)`. Bearer-specific receive code (today the `DAPPSv1>` session reader, future MeshCore receivers) calls into the inbox once a message is fully received and validated; the inbox owns DB persistence and conditional MQTT delivery. Types live in `dapps.client/Backhaul/` so other-bearer projects can take a dependency without dragging in the AGW stack.

### A0.2. Refactor the current BPQ/AGW path behind the seam *(done — PR #12)*

Outbound: `Dappsv1SessionBackhaul` wraps `IDappsOutboundTransport` + `DappsProtocolClient`. Inbound: `InboundConnectionHandler` retains DAPPSv1 session/parsing (offer↔data correlation, hash check, on-the-wire ack), but now hands the validated message to `IBackhaulInbox` instead of writing DB rows + MQTT-injecting directly.

### A0.3. Define stable DAPPS backhaul units *(done — PR #12)*

`BackhaulMessage(Id, Destination, Salt, Ttl, Payload, Headers?)` is the bearer-neutral unit. Carries everything DAPPS callers need to forward or deliver; nothing bearer-specific. Fragmentation/reassembly (Phase F2) will sit between this unit and the bearer adapter, not at this layer.

### A0.4. Datagram bearer as the forcing function *(UDP stand-in done)*

The seam needed a non-stream bearer to validate the architecture before any real bearer with a small MTU landed. Implemented a UDP datagram backhaul (`UdpDatagramBackhaul` + `UdpDatagramListener`) plus a bearer-agnostic `Packetiser` and `BackhaulMessageCodec` in `dapps.client/Backhaul/Datagram/`. End-to-end tests on loopback exercise both single-fragment and multi-fragment messages with an artificially low MTU (64 bytes), proving the seam supports a fire-and-forget datagram bearer with DAPPS-owned fragmentation.

`IDappsBackhaul.CanHandle(BackhaulRoute)` and per-route bearer hints (`BackhaulRoute.UdpEndpoint`, `DbNeighbour.UdpEndpoint`) let the OMM dispatch to the right bearer per neighbour without leaking bearer-specific code into queue/router logic.

Real radio-bearer integrations (MeshCore Companion, MeshCore KISS, RHP UI, …) live in **Phase H**. The packetiser and codec carry over; each bearer just adds a wire-emit / wire-ingest layer.

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

### A8. Roll back runtime to .NET 8 LTS *(done — v0.2.0)*

The .NET 10 v0.1.0/v0.1.1 binaries failed to load on Raspberry Pi OS 11 (Bullseye, glibc 2.31): `/opt/dapps/dapps` requires `GLIBC_2.32`–`2.34` and `GLIBCXX_3.4.29`–`3.4.30`. Initial diagnosis (transitive native deps from build host) was wrong — self-contained `PublishSingleFile` produces byte-identical output regardless of build host. The actual cause: .NET 10 dropped Debian 11 from its supported Linux list; its apphost is built against Bookworm-baseline glibc 2.36.

The previous restart (PR #8 entry above) left a comment that .NET 8 was where they had stayed; this confirms why. Rolling back:

- `<TargetFramework>` flipped to `net8.0` in all four csprojs.
- `Microsoft.Extensions.Logging*` pinned to `8.0.x`.
- `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` dropped — those are .NET 9+ APIs. Dashboard remains; the `/openapi/v1.json` + `/scalar` API explorer routes are gone for now. Re-add when we eventually move back to a newer runtime.
- `System.IO.Pipelines` added explicitly to `dapps.client.csproj` (not transitively in scope under the Library SDK on net8 the way it is under the Web SDK).
- `MultiplexedAgwSessionStream.ReadAsync` refactored to extract Span-typed work into a sync helper — async methods can't hold ref-struct locals in C# 12 (the .NET 8 SDK's default).
- CI: `dotnet-version` 8.0.x, dropped the bullseye-container split (the original split was based on the wrong diagnosis; .NET 8's apphost targets glibc 2.23 so any modern build host's output is fine).

.NET 8 LTS support runs to Nov 2026, giving us roughly a year before the next runtime question. By then either Pi OS Bookworm is universal (.NET 10 viable) or we evaluate a different runtime story (NativeAOT, etc.).

267 tests pass on net8 unchanged.

### A7. AGW for both directions *(done)*

Replaces the BPQ Apps Interface (HOST/CMDPORT TCP-bridge) inbound path with AGW dispatch. dapps now uses one BPQ surface — AGW — for both inbound and outbound, multiplexed over a single TCP connection.

Why: the BPQ HOST handler hard-codes the dial-out target as `127.0.0.1` (TelnetV6.c:2689), forcing dapps to either co-locate with BPQ or maintain an SSH reverse tunnel. AGW has no such constraint — dapps connects *out* to BPQ's `AGWPORT` from wherever it runs, so different hosts work with just a network route. Also avoids the Telnet driver's LF→CR rewriting in the app→user direction (a quirk that required dapps's protocol client to accept `\r` as a line terminator).

Implementation:
- `MultiplexedAgwSessionStream` — duplex Stream over one logical AGW session when many sessions share a single AGW socket. Reads pull from a `Pipe` fed by the dispatcher; writes serialise into 'D' frames via a callback. `SignalRemoteDisconnect` closes the read pipe when 'd' arrives.
- `AgwInboundService` (hosted) — connects to BPQ AGW, registers the dapps callsign with an 'X' frame, dispatches inbound 'C'/'D'/'d' frames to per-session streams, hands each new session to `InboundConnectionHandler`. Reconnects on socket loss.
- `InboundConnectionHandler` is now bearer-neutral: takes `Stream` + `sourceCallsign` instead of `TcpClient` + read-first-line. Keeps the protocol logic isolated.
- Removed: `BpqConnectionListener`, `InboundConnectionHandlerFactory`, `BpqInboundListenerPort` option (and the seed/round-trip in `DbStartup`/`Database`), `TwoInstanceAttachFixture`, `BpqAttachBridgeTests`, `InboundDeliveryViaBpqAttachTests`.

Operator config change: the inbound `APPLICATION` line drops the `C N HOST K TRANS S` CMD field (now empty) — BPQ no longer runs a node command on inbound, just dispatches the L2 'C' frame to the registered AGW client. README updated.

Tests: `MultiplexedAgwSessionStreamTests` (5 unit tests for the new stream), `AgwInboundDeliveryTests` (full real-DAPPS-receiver E2E reusing `TwoInstanceLinbpqFixture` — the AGW dispatch shape was already proven by `TwoInstanceAgwSmokeTests`, this adds dapps receiver behaviour on top). 267 tests pass.

Bonus side-effects: outbound and inbound become byte-for-byte symmetric on the wire. Operationally simpler — one socket to BPQ, one config knob (`AGWPORT` + `AGWMASK`), one failure mode.

### A6. BPQ APPLICATION+TCP-bridge inbound coverage *(superseded by A7)*

Issue #32 closed by PR #33 with a HOST-form bridge fixture and E2E tests. Subsequently superseded when we moved to AGW-for-both (A7) — the Apps Interface code and tests were removed entirely. Keeping this entry for historical record: the work surfaced two real bugs (`DappsProtocolClient` didn't tolerate the BPQ Telnet driver's LF→CR rewriting; `BpqConnectionListener` leaked its OS socket on shutdown), the first of which transferred to the AGW path as a no-op (AGW frames are binary-transparent, no rewrites to defend against — though the line-tolerant `ReadLineAsync` is harmless to keep).

### A5. Outbound TTL on the app interface *(done)*

Apps can now request a residual lifetime when submitting a message, and inbound delivery surfaces residual TTL so apps can discriminate near-expiry messages from fresh ones. REST `OutboundRequest` gains an optional `Ttl` (positive int seconds; 0/negative → 400). MQTT publish reads optional `dapps-ttl` user property; malformed values fall through to no-TTL rather than rejecting the publish (the broker has no way to NACK after the fact). On delivery, both surfaces report the *residual* TTL — initial TTL minus dwell time on this node — so an app polling `/AppApi/inbound/{app}` and an app subscribed to `dapps/in/<app>` see the same number. Closes the spec gap where the on-air protocol carried `ttl=` end-to-end but the app interface couldn't read or write it.

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

**MeshCore appears in two unrelated lanes** — keep them separate when reading anything below:

1. *MeshCore as a bearer* — using a MeshCore radio to carry DAPPS traffic. That's an `IDappsBackhaul` implementation question and lives in **Phase H** (concrete bearer integrations).
2. *MeshCore as a source of ideas for routing* — flood-then-learn, link-quality-weighted next-hop, etc. — applied to whatever bearer DAPPS happens to be on. That's **B5** below.

B5 doesn't depend on H, and H doesn't gate B5. A future DAPPS deployment could ship one without the other and still be coherent.

### B1. Discovery seam *(done — channels are first-class)*

A bearer (AGW, UDP multicast, future MeshCore) can serve many *channels*: AGW BPQ port 1 (VHF) and BPQ port 3 (AXIP) share one AGW socket; future MeshCore radios are per-channel; UDP can hold multiple multicast groups. The seam:

```csharp
public interface IDiscoveryBearer : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct);
    Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct);
    IAsyncEnumerable<ReceivedBeacon> ListenAsync(CancellationToken ct);
}
```

`ReceivedBeacon = (BeaconFrame, ChannelKey)` so the daemon knows which channel a peer was heard on. Each channel has its own beacon cadence and advertised TTL — chattering every 5 minutes is fine on AXIP, antisocial on 1200 baud VHF, inappropriate on HF where propagation is part-time.

### B2. Channels-as-first-class + LinkClass *(done)*

`DbDiscoveryChannel` table — one row per channel — is the authoritative configuration. Fields:

- `Bearer` (`agw` / `udp` / future `meshcore`)
- `ChannelKey` (bearer-specific: BPQ port byte, multicast endpoint, MeshCore radio+channel)
- `LinkClass` enum: `InternetIp` / `LanMulticast` / `VhfUhfFm` / `Hf` / `MeshCore` / `Unknown`
- `BeaconIntervalSeconds`, `AdvertisedTtlSeconds`, `CostHint` — default per `LinkClass`, operator-overridable
- `Enabled`, `Notes`

`LinkClassDefaults` encodes the channel-nature knowledge: HF advertises a 24-hour TTL because propagation closes overnight and a peer that "went away" at sundown is back at sunup; MeshCore beacons every hour because the bearer floods anyway; LAN multicast is cheap and frequent; VHF/UHF FM is in between.

`/DiscoveryChannels` REST surface (parallel to `/Neighbours`): GET / POST upsert / DELETE. Dashboard shows channel list and discovered-peers tagged with channel + link class + cost.

### B3. Beacon protocol *(done)*

Wire form: `DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300`. KV style rather than positional so future fields slot in without breaking parsers. The `Bearer` field on `BeaconFrame` is stamped by the receiver — never carried on the wire (would let a misbehaving peer claim routes it doesn't have).

Distance-vector for v1: beacons advertise self only. A peer reachable on three channels gets three rows; the resolver picks by `CostHint`.

### B4. Routing decisions *(done)*

`OutboundMessageManager` now resolves a `BackhaulRoute` per pending message, in this precedence order:

1. **Manual `DbNeighbour`** with matching base callsign — explicit operator override.
2. **Fresh `DbDiscoveredPeer` rows** for that base callsign, freshness-filtered by `LastSeen + TtlSeconds`, sorted by `CostHint` then by hop count.
3. **`DbRouteHint`** next-hop fallback — explicit "I know X is reachable via Y" when there's no live discovery record.

Cost ordering is **RF-first** per the project's amateur-radio identity. Default `CostHint` per class:

| Class | Cost | Notes |
|---|---:|---|
| `VhfUhfFm` | 1 | RF, line-of-sight, ~always-on — the preferred channel |
| `MeshCore` | 3 | RF mesh, slow but in-spirit |
| `Hf` | 5 | RF continental, propagation-locked |
| `LanMulticast` | 8 | IP, scoped — testing ergonomics |
| `InternetIp` | 10 | IP, last-resort bridge between RF islands |

Internet routes exist to glue isolated RF islands together, not as a preferred path. The denormalised `LinkClass` + `CostHint` on each peer row means the resolver doesn't need to join `discoverychannels`. Tie on cost breaks on hop count.

`LinkClassDefaultsTests` pins this ordering so a casually-tweaked default doesn't quietly invert the project's identity.

### B5. Routing as a learned graph (flood-then-learn) *(done — PRs #51, #52, #53)*

Landed across three PRs: PR-A introduced the `IRoutingAlgorithm` seam (interface designed to fit MeshCore-style source routing later), PR-B added passive learning (every inbound message teaches reverse-direction routes via F1's `src=`), PR-C added bounded-flood fallback for cold-start. The decisions called out below were made consciously — see `docs/routing-prior-art.md` for the comparison.

Resolution precedence (today): static (manual / discovered / hint) → learned (from passive observation) → bounded flood (cold-start). All three layered as decorators over `IRoutingAlgorithm`.

The decisions made:

- **learned whole paths vs. next-hop hints** — next-hop. Source routing was rejected for primary AX.25 deployments (path bytes per data packet expensive on slow links); kept the seam open via `RouteDecision.SourceRoute` which can land later if MeshCore-bearer integration (Phase H1) needs it.
- **route freshness and expiry** — three failed forwards invalidate; success resets the counter; new observation with a different next-hop also resets (fresh path is fresh evidence of liveness).
- **direct-vs-flood promotion** — static and learned ALWAYS win when available; flood is the last-resort fallback. Learned routes from a successful flood path become available the moment a reply traverses back, so floods diminish as the network warms.
- **neighbour advertisements vs. route exchange** — neither. Routes are learned from data-message observations rather than dedicated control packets. Lower control-plane overhead at the cost of needing bidirectional traffic OR a flood to converge.
- **what belongs in DAPPS core vs. bearer-specific** — all of the above is DAPPS-core / bearer-neutral. The wire-format additions (`src=`, `LinkSourceCallsign`, `FloodHopsRemaining`) live in `BackhaulMessage` / codec v4 and apply uniformly across AGW + UDP + future bearers.

The result is closest to AODV in shape (RFC 3561) but with control-plane traffic stripped — passive learning replaces RREQ/RREP. NET/ROM and INP3 explicitly off the table per operator experience (see `docs/routing-prior-art.md`).

#### B5.1 — MeshCore-flavoured DSR alternative *(done)*

A second algorithm stack ships alongside the default — selectable via `SystemOptions.RoutingAlgorithm = "meshcore"`. Source routing with passive discovery: cold-start floods accumulate a `TraversedHops` list at each transit node; arriving floods give every node along the path a discovered-path entry back to the originator (stored in `DbDiscoveredPath` with the full intermediate-hop list, not just next-hop). Subsequent sends embed the path in `BackhaulMessage.SourceRoute`; each hop strips the head before re-encoding. Codec bumped to v5 with new `SourceRoute` and `TraversedHops` fields (plus the long-overdue strip of v1/v2/v3 backward compatibility — we're pre-shipping, the version mechanism stays so future format changes hard-fail cleanly). Algorithm choice is global per-node and applied at startup.

This isn't a replacement for the default — both stacks have legitimate trade-offs (path bytes per packet vs. per-flow next-hop lookup; AODV's mid-flow path repair vs. DSR's ability to load-balance across discovered alternatives). Picking is an operator decision.

### B6. Active discovery — DAPPS goes asking

B1-B4 are passive discovery (beacon, listen). B5 is the routing graph on top. B6 is the third mode: DAPPS proactively explores the network on a slow cadence, building topology that beacons can't see (peers behind a hop, peers on a propagation path that's open right now).

Two sub-mechanisms, distinct because they target different propagation realities:

#### B6.1 — Connected-mode probe-and-map *(Phase 1 + Phase 2 done; Phase 2b queued)*

Phase 1 of B6.1 ships a **direct-connect liveness probe** for every callsign DAPPS already knows about. Sources: manual `DbNeighbour` rows (skipping UDP-routed ones) and AGW-bearer `DbDiscoveredPeer` rows, deduped by callsign with neighbour port winning over peer port. The probe AGW-connects to the target callsign on the chosen port, looks for the `DAPPSv1>` banner (reusing `DappsProtocolClient.ReadInitialPromptAsync`), and disconnects. The result lands in a new `DbProbedNode` row keyed by callsign — `LastProbedAt`, `LastSuccessAt`, `LastError`, `ConsecutiveFailures`, `SuccessCount`, plus an operator `OptOut` flag.

A `ProbeSchedulerService` (`BackgroundService`) drives the cadence: 15-minute startup grace, then a sweep every `ProbeIntervalHours` (default 24h). Within a sweep, individual probes are spaced by 5–30s of random jitter so two nodes on the same cron offset don't dial the same BPQ simultaneously. Off by default — `SystemOptions.ProbingEnabled` opts in. REST surface at `/Probes`: list, run-sweep, run-one, set-opt-out, forget-row. On-demand probes bypass the opt-out filter so a sysop can still test a peer they've muted. Dashboard panel sits between "Discovered peers" and "Recently dropped" with a "Probe all now" button, per-row probe / forget actions, and an opt-out toggle. Settings panel grows two new fields.

Phase 2 adds a new **`peers` command on the DAPPSv1 protocol** (`who` aliased) and ties it to transitive discovery. Server side: receivers respond with one `peer <callsign> source=<n|d>[ port=<byte>]` line per known forward target — manual neighbours emit `source=n`; AGW-bearer discovered peers emit `source=d`; UDP-only entries are excluded (asker can't reach them on the same bearer); same-callsign duplicates are de-deduped with the neighbour winning. Response is terminated with `end`. Client side: `DappsProtocolClient.RequestPeersAsync` parses the response tolerantly (forward-compat — unknown lines between `peer …` and `end` are skipped, partial responses on EOF return what was received, out-of-range port hints are dropped). After every successful Phase-1 probe (`fetchPeers: true` by default), `NodeProber.ProbeAsync` issues `peers` and stashes the result in `ProbeResult.DiscoveredPeers`. The scheduler then upserts each previously-unknown callsign as a candidate `DbProbedNode` row with `Source = via:<callsign-of-asked-peer>` and `LastBpqPort = <peer-supplied port or our port to source>` — never overwriting an existing direct probe row. The next sweep picks those candidates up automatically (`EnumerateTargets` was extended to include `Source` starts with `via:`). Dashboard adds a `Source` column and pills `via:CALLSIGN` candidates as info-level so a sysop sees the difference between direct and hearsay rows. The current node's own callsign is filtered out (the remote always reports us as a peer, since we just talked to them — recording that is noise). Phase 2 added 19 tests across `DappsProtocolClientPeersTests`, `InboundConnectionHandlerPeersTests`, and additions to `ProbeSchedulerServiceTests` — full suite at 401/401.

**What's still queued (Phase 2b):** node-prompt-then-`DAPPS` discovery — probing AX.25 nodes by NODECALL (not DAPPS callsign), reading the BPQ node prompt heuristically, sending `DAPPS\r`, then expecting `DAPPSv1>`. Needs an AGW state machine that can react to a BPQ node banner (and the banner format isn't standardised — operators configure their own). That's its own engineering surface; explicitly out of scope until someone wants the discovery-of-non-DAPPS-NODECALLS angle. Also still queued: per-channel daylight-hours politeness on HF, hard caps per night (likely subsumed by the airtime-budget idea in the scratchpad), and feeding probe results into B5's learned-route graph.

**Convention:** "DAPPS lives here" = an `APPLICATION` line whose alias is `DAPPS` (typing `DAPPS` at the node prompt connects to the local DAPPSv1 instance). Already what we recommend; documented in the install README. Phase 2b leans on this.

#### B6.2 — HF NVIS solicit-and-listen

UI-frame discovery's missing twin. Beacons say "I'm here." A solicit says "anyone there?" — broadcast on the configured discovery channel, then listen for a bounded window for replies. Especially useful on 40m NVIS HF where the propagation footprint is shifting and broadcast-shaped: tonight's reachable peers aren't yesterday's, and beacons that happened to TX while propagation was closed don't reach anyone.

Builds on the B1 bearer/channel concept — would be a new channel mode parallel to "beacon" (which is "scheduled outbound broadcasts") rather than a separate bearer. Operator turns it on per-channel: the AGW UI bearer can do solicits on HF channels but doesn't have to on VHF (where beacons + line-of-sight do the job adequately).

Implementation notes:
- **Frame format**: a UI frame with a known discriminator ("DAPPS-SOLICIT" or similar) — receivers reply with their normal beacon. Keeps reply path identical to passive discovery; just gives nudges.
- **Cadence**: opt-in per channel. HF channels say "yes, solicit"; VHF channels say "no, beacons are enough."
- **Receive window**: bounded ("listen for replies for N seconds, then move on"). Receivers pick a random delay 0-N before replying so the channel doesn't get hammered with simultaneous responses.
- **Storage**: replies populate the existing `DbDiscoveredPeer` table — a peer heard via solicit-reply is no different from a peer heard via beacon. The solicit just made it more likely we'd hear them at all.

#### Sequencing

B6.1 wants B5 (the routing graph). B6.2 is mostly orthogonal — could land after B5 too but doesn't strictly need it. Both are bigger engineering investments than the polish items in C3/D-anything; B5 → B6.1 → B6.2 is the natural order if we tackle this whole sub-phase.

## Phase C — deployable, runnable for sysops

**Goal:** a sysop downloads a single binary for their platform from a GitHub Release, drops it next to a `dapps.db`, and it Just Works.

### C1. Native single-file binary releases

Direction: native single-file binaries published as GitHub Release assets, not a Docker image. Operators run a binary; no .NET runtime install, no Docker install, no container plumbing. The Dockerfile in the repo stays as a secondary option for those who want it but isn't the primary distribution.

- Single workflow `.github/workflows/ci.yml` handles the full PR-to-release path. Triggered on PR (test only) and master push (test, then conditional release). Tag-push triggers are gone; the release decision is driven by `<Version>` in `src/dapps/Directory.Build.props` — bump it to release, leave it to land a normal commit.
- Master-push flow: build + test → query whether `v$(Version)` already exists as a GitHub Release → if not, fan out the matrix (`linux-x64`, `linux-arm64`, `linux-arm`, `win-x64`, `osx-arm64`) and `gh release create` with all five binaries attached. Same version twice = second push is a no-op, so re-merging or amending master won't overwrite a published release.
- `dotnet publish ... --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` so the SQLite native lib bundles into the executable.
- Trimming left off — ASP.NET Core + DI + JSON have enough reflection that trimming tends to break startup in subtle ways. Binary is ~100MB; acceptable for the ergonomics win.

### C2. Config tooling *(env-var seeding done; CLI subcommand deferred)*

`DbStartup` now seeds missing `systemoptions` rows from `DAPPS_*` environment variables (`DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, `DAPPS_AGW_PORT`, `DAPPS_DEFAULT_BPQ_PORT`, `DAPPS_MQTT_PORT`, `DAPPS_NODE_TYPE`). Once a row exists, env vars stop mattering — no surprise overwrites of operator-set values on restart. After seed, the startup refuses to run with the placeholder `N0CALL` callsign and logs a clear error pointing the operator at `DAPPS_CALLSIGN` or `/Config`. The originally-listed `dapps configure` CLI subcommand is unnecessary given env-var seeding plus the existing `/Config` REST endpoint; deferred unless a real need surfaces.

### C3. Health, logs, observability

- `/Health` endpoint with subsystem checks (BPQ AGW reachable, MQTT broker port listening, DB writable).
- Structured log output (JSON option) so sysops who want to ingest into Loki / Elastic can.
- A simple metrics endpoint (Prometheus format) covering: queue depth, messages forwarded last hour, neighbours seen, failed forward attempts.

### C4. Install / upgrade docs *(done)*

README "Getting started" rewritten end-to-end for the native-binary distribution: prerequisites, the BPQ-side `bpq32.cfg` snippet (AGW + Apps Interface slot + APPLICATION line with `TRANS`), download/run instructions for each release artifact, env-var bootstrap, neighbour add/remove via REST, and a verification step that connects from a node prompt and sees the `DAPPSv1>` banner. Troubleshooting block covers the usual surprises (AGW config, callsign typos, BPQ port-byte indexing, `TRANS` flag missing). Backups + upgrade notes added.

### C5. Self-update

Manual `curl` + `install` + `systemctl restart` is workable for the operator running through the README, miserable for a deployed estate. Goal: keep the bar of "single static binary, drop it in `/opt`, run via systemd" but stop expecting the sysop to chase release notes by hand.

Three phases by complexity / risk; ship in order, evaluate before moving on.

#### C5.1 — Visibility *(done)*

`UpdateChecker` (a `BackgroundService`) polls `https://api.github.com/repos/M0LTE/dapps/releases/latest` every 6 hours after a 15s startup delay, caches the result, and exposes `Current` / `Latest` / `IsAvailable` / `IsDevBuild`. `Current` is resolved from `AssemblyInformationalVersion`; `dev-<sha>` builds are flagged so the dashboard never claims a dev build is "out of date". Compared via a tolerant dotted-decimal semver comparator. `SystemOptions.UpdateCheckEnabled` (default true, seeded by `DbStartup`, editable in the dashboard's settings panel) is the opt-out for sysops who pin a version on purpose. `EventsController` exposes the snapshot at `GET /Events/version`. The dashboard's `#version-pill` in the header polls that endpoint on load + every 10 minutes and renders one of three states: dev build (info pill), up-to-date (muted pill, `vX.Y.Z`), update available (warn pill, `vX.Y.Z → vX.Y.W available`, links to the GitHub release). Cross-platform — pure polling + UI.

#### C5.2 — Triggered update (Linux / systemd)
Sysop clicks a dashboard button (or POSTs `/Config/update`); dapps signals a separate privileged `dapps-updater.service` to do the swap. dapps itself stays unprivileged.

- Companion `dapps-updater.service` + `.timer` that runs as root, polls a "ready to update" signal file written by dapps in `/var/lib/dapps/`, downloads the binary, atomic-swaps `/opt/dapps/dapps` (keeping `/opt/dapps/dapps.previous` as a rollback), `systemctl restart dapps.service`. If the new binary fails to start within a window, the updater restores `.previous` and restarts again.
- The companion ships in a `scripts/dapps-updater/` directory with its own systemd unit + a one-line install recipe in the README.
- API: `GET /Update/status` (current version, latest available, last check), `POST /Update/apply` (writes the signal file → updater picks up next tick).

#### C5.3 — Auto-update on a schedule
Off by default. Operator opts in: "auto-update during quiet hours, randomised offset within a window."

- Adds an `AutoUpdate=true` config option + a `QuietHours` window (default 02:00–05:00 local).
- Updater runs in this window, checks, applies, restarts.
- Skips if traffic was forwarded in the last N minutes (don't reboot mid-conversation).
- Per-major-version pinning option ("auto-update within 0.x but not across 0→1") so a backwards-incompatible bump doesn't surprise people overnight.

#### Out of scope for now

- Binary signing / signature verification. GitHub Releases over HTTPS is the trust model today; signing is its own initiative (Sigstore? minisign? — separate decision).
- Channels (stable / beta / dev). YAGNI until we have releases that warrant the distinction.
- Cross-platform privileged-update (Windows service control / launchd). Linux/systemd first; Windows + macOS get C5.1 (visibility) and stay manual otherwise.

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

### E1. Concepts page *(done)*

`docs/concepts.md` covers the eventual-delivery model, `app@callsign` destinations, content-addressed message ids and what idempotency looks like in practice, the TTL-as-residual-lifetime contract, and the "why DAPPS rather than raw AX.25 / packet mail" framing. Linked from the README's app-interface section.

### E2. Tutorial: hello-world application *(done)*

`docs/tutorial-hello-world.md` walks through `docs/examples/hello.py` — a Python app using paho-mqtt 2.x (MQTT 5) that subscribes to `dapps/in/hello`, replies with `hello, <name>!` on `dapps/out/hello/<sender>`, and acks via `dapps/ack/hello`. The tutorial covers reading the `dapps-id` / `dapps-source` / `dapps-ttl` user properties, idempotent reprocessing on redelivery, and the equivalent flow expressed as `curl` against the REST endpoints. Same tutorial in C# / Go / Node welcomed as community contributions once Python is the worked example.

### E3. Reference *(done)*

`docs/reference.md` is the full app-interface reference: every MQTT topic, every REST endpoint, every user property with type and semantics, the idempotency contract spelled out with code-shaped pseudocode, and a discussion of practical payload-size limits (bearer MTU, multi-part fragmentation status, id collision space). The README app-interface section links to it as the place to look once you know what `app@callsign` means.

### E4. Sample app gallery *(done)*

`docs/gallery.md` indexes three runnable examples that cover the major DAPPS-shaped patterns:
- `docs/examples/chat.py` — group chat (one-to-many fan-out, symmetric peers, no central registry).
- `docs/examples/sensor.py` — periodic publisher (no `on_message`, no acks, long TTL, supports `--once` for cron-style use).
- `docs/examples/pager.py` — two-way messenger (long-running listener + one-shot sender modes sharing the same app slot).

Combined with the existing `hello.py` from E2, the gallery covers request/reply, many-to-many, one-shot submit, periodic submit, and mixed-mode shapes. Mailing-list / forum-style apps were considered but cut as borderline DAPPS-shape (more about server architecture than DAPPS surface) — defer to early adopters who actually need that pattern.

## Phase G — second-language reference implementation

**Goal:** prove the spec is portable, not an accidental description of the C# implementation.

A second compatible implementation forces the spec to be precise. Promoted out of Phase E because it's a multi-week engineering effort (new package, new CI lane, cross-implementation interop tests), not a developer-guide doc.

Python is the obvious second language — large amateur-radio Python community, easy onboarding. Aim for:
- A minimal `dapps-py` that implements the on-air protocol (DAPPSv1 codec + parser) and the AGW transport.
- Talks to the same `m0lte/linbpq` Docker image we use in CI today.
- Cross-implementation interop test in CI: `dapps.core` (C#) on one side, `dapps-py` on the other, message goes through end-to-end.

The implementation does not need feature parity with `dapps.core` — it only needs to be *interop-correct*. Subset of MQTT/REST app interface (probably MQTT only), no dashboard, no auth, no persistence (in-memory queue is fine for an interop tester). The point is to surface ambiguities in the spec by forcing a second author to read it and implement it.

## Phase F — spec maturation

Items deferred from earlier discussions, mostly "we know it'll need to happen, just not yet":

### F1. End-to-end source tracking *(done — PR #47)*

`ihave` gained an optional `src=<callsign>` field carrying the originating callsign — distinct from the link source. Originators set it; relays preserve it verbatim across re-forwards. Receivers expose it as the `dapps-origin` MQTT user property and the REST `OriginatorCallsign` field. Pre-F1 senders that omit `src=` round-trip cleanly with originator unknown. `BackhaulMessageCodec` bumped to v2 with a `HasOriginator` flag bit; v1 messages still decode. The 6-node multi-hop simulator's five canned exercises validate it end-to-end across path lengths 1-4 and branching.

### F2. Multi-part messages

Messages today are atomic. For payloads larger than a comfortable AX.25 paclen (say 5 KB+), apps would benefit from native chunking with reassembly on the receiver side. Possible spec extension: `frag=N/M` headers on `ihave` + a small reassembly buffer in DAPPS.

### F3. `rev` polling

Documented in the spec since v1 ("polling for messages") but deliberately deprioritized — with forward connections both ways, polling shouldn't be needed. Keep on the back-burner; revisit if a real deployment turns out to need it.

### F4. Protocol versioning policy

Pre-emptive decision before the next breaking change: do we cut `DAPPSv2>` for any incompatible wire change, or stay on `v1` forever and use feature negotiation (`have=feature1,feature2` on the prompt or in `ihave` headers)?

Weak preference: bump on any incompatible wire change. The prompt is the natural carrier and `v2` clients can offer to talk `v1` if they detect it. But pin the answer in writing before the next change.

### F5. Authenticated message origin (signing)

Mentioned in the original gist. Long-term: messages signed by the source node so the recipient can verify the origin chain hasn't been tampered with. Pointless without a ham-radio-friendly identity layer; defer indefinitely until that exists.

## Phase H — concrete bearer integrations

**Bearer-specific work, distinct from the routing decisions in Phase B.** These are about *what wire forms DAPPS speaks*; routing logic (how DAPPS picks where to send) is solved one layer up and is bearer-agnostic. Each item here is an `IDappsBackhaul` (and optionally `IDiscoveryBearer`) implementation slotting under the existing seams without changing core routing.

### H1. MeshCore Companion-over-USB

Use a MeshCore companion radio (e.g. Heltec / TTGO LoRa boards running the MeshCore companion firmware) as a bearer. The companion exposes a serial protocol over USB; DAPPS opens that serial port, sends companion datagrams carrying our `BackhaulMessage` payloads, and listens for inbound datagrams. Discovery beacon support if the companion firmware allows it; otherwise rely on Phase B5's flood-then-learn over this bearer instead of explicit beacons.

### H2. MeshCore KISS-over-USB

Same hardware as H1 but via the MeshCore KISS interface — raw frames rather than companion-level datagrams. Trades convenience for control. Same `BackhaulMessage` codec; same packetiser. Probably done after H1 once H1 has shaken out the wire-shape questions.

### H3. RHP (Routed Host Protocol) bearer

When BPQ adds RHP UI-frame support, port the AGW UI bearer over to RHP as a sibling. Today RHP doesn't expose UI frames in BPQ's implementation; the discovery seam is shaped to accept it as soon as it does.

### H4. Other bearers as they show up

Anything that can carry a bytestring with sane addressing fits under `IDappsBackhaul`. KISS-over-TCP, AX.25-over-Ethernet, custom radio-modem JSON-RPC, …. Listed here so they don't get muddled with Phase B routing work.

## Phase G — community + governance

The README already says "multiple compatible implementations is healthy." Once the developer guide exists and there's a Python reference implementation:

- Move the spec into a versioned document (in `docs/spec/v1.md`, with future `v2.md` etc.).
- Issue tracker labels for spec discussion vs. implementation issues.
- Release notes / changelog discipline so implementers know what's changed.

Probably also worth a public conversation about whether DAPPS sits under OARC (the original RFC venue) or stays as a personal project — has implications for spec governance and license expectations.

## Cross-cutting: licensing housekeeping

Resolved during the toolchain refresh: tests use **AwesomeAssertions** (MIT-licensed community fork of FluentAssertions, drop-in API-compatible). FluentAssertions 8.x's switch to a paid Xceed licence isn't a concern. No further action.

## Cross-cutting: local multi-hop simulator

A dev-machine testbed for routing + forwarding work, in the absence of an RF test network. Maps **multicast group ≈ RF broadcast domain**: each group is a population of nodes that hear each other, and a node subscribed to multiple groups stands in for one within RF range of multiple disjoint populations.

Minimum topology — three `dapps.core` instances on the same host (different ports + DB paths):

- **A** — discovery channel `udp` `239.0.0.1:54321`
- **B** — discovery channels `udp` `239.0.0.1:54321` *and* `udp` `239.0.0.2:54321` (the relay)
- **C** — discovery channel `udp` `239.0.0.2:54321`

A's beacons reach B but not C; C's beacons reach B but not A; B is heard by both. For A→C to work, B must forward — which is exactly the multi-hop behaviour we want to exercise. Discovery already uses multicast (Phase B); message forwarding stays unicast to the discovered peer, which is also how RF works (broadcast discovery, point-to-point forward).

Validates:
- **F1** end-to-end source tracking — receiver at C sees originator A, not link-source B.
- **B5** flood-then-learn over a non-trivial graph — paths form and decay as channels go up/down.
- General routing / forwarding / TTL behaviour without burning RF time.

Doesn't validate:
- RF-specific behaviour: half-duplex, contention, lossy paths, AX.25 connect/disconnect quirks. For loss/jitter realism, layer `tc netem` on `lo`. AGW-bearer behaviour still needs a real BPQ in the loop.

Implementation cost is low — the UDP datagram bearer (A0.4) and discovery channels are already in place. Mostly a matter of a `scripts/sim-multihop.sh` that spins up three configured instances, plus a short README section so a contributor can reproduce. Worth doing as the *first* concrete validation harness for F1 or B5, before either lands.

**Status:** landed in PR #47 alongside F1. Six-node mesh (one branching relay; path lengths 1–4; an off-spine route that never touches A or B), driven by five canned exercises that exercise longest path, reverse longest path, off-spine, fan-out, and concurrent cross-traffic. Verifies F1 end-to-end at every receiver. Two real bugs surfaced during the build: one filed under "Open tasks" above (multicast bearer multi-channel-per-port leakage — channels in the simulator now use distinct ports per group as a workaround); the other is the missing automatic-forwarder-loop, also filed there (the simulator has to drain by hand after every send).

## Suggested ordering

Roughly:

1. **A0.1–A0.3** (backhaul seam) — *done*. **A1** (TTL forwarding) — *done*. **A2** (neighbour-table cleanup) — *done*.
3. **C1 + C2 + C4** (docker image, config tooling, install docs) — gets the thing into one sysop's hands.
4. **A4** (per-app auth) — *done*.
5. **D1 + D2** (web UI inspection + exercise) — *MVP done*. SSE inbound feed + ihave terminal still pending.
6. **B1–B4** (channels-first-class discovery + cost-based resolver) — *done*. **B5** (learned-graph routing inside DAPPS, bearer-agnostic) — *done*. **B6.1** Phase 1 (direct-connect liveness probes) and Phase 2 (`peers` command + transitive discovery) — *done*. **B6.1 Phase 2b** (node-prompt discovery against non-DAPPS NODECALLs) and **B6.2** (HF NVIS solicit-and-listen) still queued.
7. **E1–E4** (concepts + tutorial + reference + sample-app gallery) — *done*. Developer guide is complete; third-party app development is unlocked.
8. **H** (concrete bearer integrations — MeshCore Companion, MeshCore KISS, RHP, …) on its own track, doesn't gate the routing or developer-guide work.
9. **A3** — *done*. **C5.1** (update-availability banner) — *done*. **C3, C5.2/C5.3 (triggered + scheduled self-update), D3, D4, F1–F4** in parallel as polish.
10. **Phase G** (second-language reference impl) once the spec has been exercised by enough first-party apps that the ambiguities are likely to surface.

Phases A and C can ship as a single "v0.1.0 — runnable" release. Phase D as "v0.2.0 — operable". Phase B + E as "v1.0.0 — networked + developable".

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
