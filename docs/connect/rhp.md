# Connect via RHPv2

**Status: implemented.** RHPv2 (Remote Host Protocol v2) is a more modern host-to-node protocol than AGW. DAPPS speaks it via the [`RhpV2.Client` NuGet package](https://www.nuget.org/packages/RhpV2.Client) and selects between AGW and RHPv2 with a single env var.

[XRouter](xrouter.md) supports RHPv2 natively, and that's the recommended bearer for DAPPS-on-XRouter today (XRouter's per-TCP-connection AGW callsign claim makes per-send-fresh-connection AGW outbound fragile in a way RHPv2 sidesteps). Mainline BPQ does not yet ship RHPv2; when it does, DAPPS will work over it without code changes.

## Why RHPv2

AGW is a 1990s protocol - it works fine for what it does, but it has limits that show up under modern use. RHPv2 addresses several of them:

- **Better session multiplexing.** AGW assumes one TCP connection per host application. RHPv2 multiplexes many sessions over one TCP connection, with handle-based addressing. DAPPS's inbound listener and per-outbound-forward sends share one socket, where the AGW path opens a fresh TCP connection per outbound forward.
- **Cleaner inbound dispatch.** AGW's "register a callsign and receive every connect that matches" model has edge cases around shared callsigns and SSIDs. In particular, XRouter scopes the AGW callsign claim per-TCP-connection, so DAPPS's inbound listener and any concurrent outbound forwarder collide on the same callsign - the second connection is silently refused. RHPv2 has no equivalent claim and handles this naturally.
- **Actually defined error semantics.** AGW's behaviour on TNC reconnect, port up/down events, etc., is empirically discovered; RHPv2 makes it part of the spec.

## Configure DAPPS to use RHPv2

In DAPPS's environment (systemd unit, shell, etc.):

```
DAPPS_NODE_BEARER=rhpv2
DAPPS_NODE_HOST=<xrouter-host>
DAPPS_RHP_PORT=<xrouter rhp port>
DAPPS_DEFAULT_BPQ_PORT=0
```

`DAPPS_NODE_BEARER=rhpv2` flips the bearer selector from AGW (the default) to RHPv2. The AGW knobs (`DAPPS_AGW_PORT`) are then unused.

`DAPPS_RHP_PORT` matches XRouter's `RHPPORT=` directive in `XROUTER.CFG` (default 9000).

`DAPPS_DEFAULT_BPQ_PORT` is the same 0-indexed port byte used in the AGW path. DAPPS adds 1 internally to derive the RHPv2 port name (which is 1-indexed in XRouter, matching `PORT=N` in `XROUTER.CFG`). So `DAPPS_DEFAULT_BPQ_PORT=0` -> RHPv2 port "1" -> `PORT=1` in XRouter's config.

If RHPv2 requires authentication on your XRouter, also set:

```
DAPPS_RHP_USER=<user>
DAPPS_RHP_PASS=<pass>
```

These are skipped if `DAPPS_RHP_USER` is empty.

## Enable RHPv2 in XRouter

Add the `RHPPORT=` directive to `XROUTER.CFG`:

```
RHPPORT=9000
```

Restart XRouter. No equivalent of AGW's APPL-block-collision hazard exists on the RHPv2 side - DAPPS binds its callsign with a passive listen at runtime, no config-file declaration is needed.

## What stays the same

Everything above the bearer seam - the protocol, the app interface, the discovery model, the routing graph, the dashboard - works identically over RHPv2 and AGW. Switching between them is a restart with one env var changed; no data migration, no on-air-format change, no neighbour table rebuild.

## Testing

`scripts/sim-mixed-bearer.sh` brings up a 4-node BPQ+XRouter mesh (two BPQ, two XRouter) with DAPPS daemons attached - the BPQ-side daemons use AGW, the XRouter-side daemons use RHPv2. End-to-end exercise covers all five paths including the 3-hop XR->BPQ->BPQ->XR mixed-bearer route. The XRouter integration in CI stays on AGW (single-container frame-format coverage); the mixed-bearer four-container run is operator-driven on a workstation where the XRouter image is already pulled.
