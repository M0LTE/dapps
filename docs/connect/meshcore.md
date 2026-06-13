# Connect via MeshCore

**Status: planned.** MeshCore as a backhaul bearer for DAPPS is on the roadmap but not yet implemented in shipping code. This page exists so you can see the shape of the integration and plan around it.

## Why MeshCore

MeshCore is a small, modern mesh radio firmware (LoRa-shaped today) with a built-in routing layer that's a much better fit for slow, lossy, mostly-unreliable RF than AX.25. As a bearer for DAPPS it gives:

- A datagram interface (no AX.25 connection setup, no T1/T2/T3 timer dance) - better for short, frequent messages.
- A working hop-by-hop mesh underneath, so DAPPS doesn't have to solve "how do I reach a node three hops away" the way it does for the broadcast bearer of AX.25.
- A real low-cost long-haul story for operators without HF - LoRa NVIS-style propagation is well-documented at this point.

The DAPPS architecture has already been refactored to support a non-stream bearer - the existing UDP-datagram backhaul (`UdpDatagramBackhaul`) is the test stand-in proving the bearer-agnostic layer works. MeshCore lands in the same shape: a small adapter that emits and ingests `BackhaulMessage` units over the radio.

## What integration will look like

Two flavours, expected as separate packages:

### MeshCore Companion

Talks to MeshCore via the **Companion API** - a USB / BLE / Wi-Fi link to a Companion-mode radio, where the radio handles the mesh and exposes a friendly API for an attached host. Best for operators who want a self-contained DAPPS-on-MeshCore stack without dealing with KISS at all.

Configuration sketch (subject to change before release):

```
DAPPS_MESHCORE_COMPANION_ENABLED=true
DAPPS_MESHCORE_COMPANION_TRANSPORT=usb       # or ble, wifi
DAPPS_MESHCORE_COMPANION_DEVICE=/dev/ttyUSB0  # for usb
```

DAPPS would auto-register a MeshCore "address" on the radio for inbound dispatch (analogous to AGW's callsign registration today) and use the Companion API for both directions.

### MeshCore KISS

For operators who already have a radio in **KISS-TNC mode** and want DAPPS to drive it directly. This is the closer analogue of the BPQ AGW path - DAPPS opens a TCP/serial connection to the KISS endpoint and emits MeshCore frames itself, including doing its own retries.

```
DAPPS_MESHCORE_KISS_ENABLED=true
DAPPS_MESHCORE_KISS_HOST=127.0.0.1
DAPPS_MESHCORE_KISS_PORT=8001
```

## What stays the same

- The DAPPSv1 application layer is unchanged. An app subscribing to `dapps/in/<app>` doesn't know or care whether the underlying bearer is AGW, MeshCore Companion, or MeshCore KISS.
- The neighbour table holds MeshCore peers alongside AGW peers - same row shape, just a different bearer hint.
- The discovery / routing layer treats MeshCore links as another link-class with cost hints; the routing algorithm picks per destination.
- The dashboard, REST, MQTT, MCP - unchanged.

## Routing implications

MeshCore doing its own mesh routing under DAPPS is interesting. It means DAPPS's hop-count model and MeshCore's hop-count model are stacked: a DAPPS message that's "one hop" from DAPPS's perspective might traverse three MeshCore hops underneath. DAPPS uses cost hints rather than raw hop counts, so this works out, but it's worth understanding when you're tuning a multi-bearer setup.

A separate design note covering the MeshCore-as-a-bearer trade-offs is in [docs/meshcore-backhaul-routing.md in the repo](https://github.com/packet-net/dapps/blob/master/docs/meshcore-backhaul-routing.md). Most of it is still relevant; some of the concrete API sketches will be revised as the integration lands.

## When?

Currently blocked on hardware availability for testing - we want a real two-radio setup in the loop before declaring it shippable rather than relying on emulation.

If you've got a MeshCore radio and want to be a tester, [open an issue](https://github.com/packet-net/dapps/issues).
