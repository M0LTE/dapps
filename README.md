# DAPPS

**Distributed Asynchronous Packet Pub-Sub.** An asynchronous messaging overlay for packet-radio networks: applications queue messages destined for `app@CALLSIGN`, DAPPS finds a path, delivers when it can. Real-time connectivity is not a goal - think of it as packet mail for application developers, with proper delivery semantics and a modern app interface (MQTT or REST).

## 60-second pitch

A DAPPS node is a small daemon you run alongside your packet node (BPQ today; MeshCore and RHPv2 in flight). It exposes:

- **An app interface** - local applications publish and subscribe over MQTT (or REST). They name their destination as `app@CALLSIGN` and DAPPS handles routing, forwarding, fragmenting, retrying, and acking.
- **A backhaul** - DAPPS opens sessions to other DAPPS nodes (over AGW today) to move messages towards their destination, hop by hop, with TTL.

Bearer-agnostic by design: anything that exposes an AGW-compatible session bearer works the same way, and once RHPv2 lands in mainstream BPQ it'll plug in alongside.

## Documentation

**The full operator and developer manual is at [https://m0lte.github.io/dapps/](https://m0lte.github.io/dapps/).**

It covers:

- [Getting started](https://m0lte.github.io/dapps/getting-started/) - 10-minute install-to-message tour.
- [Install](https://m0lte.github.io/dapps/install/) - Linux/systemd, Docker, Windows, macOS.
- [Connect a node](https://m0lte.github.io/dapps/connect/) - BPQ via AGW and XRouter via RHPv2 supported today; MeshCore in flight.
- [Configure](https://m0lte.github.io/dapps/configure/), [Run](https://m0lte.github.io/dapps/run/), [Tune](https://m0lte.github.io/dapps/tune/) - every operator knob, what each background loop does, what to leave alone.
- [Discovery & routing](https://m0lte.github.io/dapps/discovery-and-routing/), [Operate](https://m0lte.github.io/dapps/operate/), [Update](https://m0lte.github.io/dapps/update/) - the day-to-day surfaces.
- [MCP for assistants](https://m0lte.github.io/dapps/mcp/) - let an AI assistant drive the operator surface.
- [App developers](https://m0lte.github.io/dapps/app-developers/) - concepts, hello-world tutorial, full reference, sample gallery.
- [Troubleshooting](https://m0lte.github.io/dapps/troubleshooting/) and [Glossary](https://m0lte.github.io/dapps/glossary/).

The roadmap and engineering notes (including the design discussion behind the bearer seam, MeshCore-as-backhaul tradeoffs, etc.) live in [`plan.md`](plan.md) and [`docs/`](docs/) in this repo.

## Credits

To all at OARC who participated in the RFC, helping take this from a rough idea to a workable system.
