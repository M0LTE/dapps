# DAPPS

**Distributed Asynchronous Packet Pub-Sub** - an asynchronous messaging overlay for packet-radio networks. Apps queue messages destined for `app@CALLSIGN`, DAPPS finds a path and delivers when it can. Real-time connectivity is not a goal; think of it as packet mail for application developers, with proper delivery semantics and a modern app interface.

## What this manual covers

- [**Getting started**](getting-started.md) - what DAPPS is, why you might want it, and a 10-minute tour from install to first message.
- [**Install**](install/index.md) - Linux/systemd, Docker, Windows, macOS.
- [**Configure**](configure.md) - every operator-tunable knob, set via environment variable or the dashboard.
- [**Connect a node**](connect/index.md) - wire DAPPS up to your packet node. BPQ AGW today; MeshCore and RHPv2 in flight.
- [**Run**](run.md) - what each background loop does and how to watch it.
- [**Tune**](tune.md) - airtime budgets, probe strategies, fragment thresholds, routing algorithm.
- [**Discovery & routing**](discovery-and-routing.md) - channels, beacons, probes, neighbours, route hints.
- [**Operate**](operate.md) - dashboard, `/Health` and `/Operational`, MQTT heartbeat.
- [**Audit log**](audit.md) - persistent record of every transmission, with the reason for it.
- [**Update**](update.md) - banner, one-click apply, MCP-driven, rollback.
- [**MCP for assistants**](mcp.md) - let an AI assistant drive the operator surface.
- [**App developers**](app-developers/index.md) - concepts, tutorial, reference, sample gallery.
- [**Troubleshooting**](troubleshooting.md) - common failure modes and how to diagnose.
- [**Glossary**](glossary.md) - terms.

## Bearer-agnostic by design

DAPPS does **not** require BPQ. The default setup guide is BPQ because that's where the on-air ecosystem lives today, but DAPPS talks to the bearer through a small interface - anything that exposes an AGW-compatible session bearer works the same way, and once RHPv2 lands in mainstream BPQ it'll plug in alongside. A non-stream bearer is already in tree (UDP datagram, used as the test stand-in for what MeshCore will become). The bits that change per bearer are isolated; the rest of the system doesn't care whether your link is 1200-baud VHF AX.25 or LoRa mesh.

## Status

- **Protocol**: DAPPSv1 specified end-to-end. See the [protocol reference for app developers](app-developers/reference.md) for the wire format.
- **Implementation**: in active development. The [versioning policy](configure.md#versioning) describes where breaking changes are still fair game versus where compatibility is preserved.
- **Bearers**: AGW (BPQ today, any AGW host in principle) is production-quality. MeshCore Companion + KISS, and RHPv2, are in the [Phase H roadmap](https://github.com/M0LTE/dapps/blob/master/plan.md#phase-h--concrete-bearer-integrations).

If you've never heard of DAPPS before, [start here](getting-started.md).
