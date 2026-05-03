# Connect via RHPv2

**Status: planned.** RHPv2 (Radio Host Protocol v2) is a more modern host-to-node protocol than AGW. The DAPPS bearer is on the [Phase H roadmap](https://github.com/M0LTE/dapps/blob/master/plan.md#phase-h---concrete-bearer-integrations).

[XRouter](xrouter.md) already supports RHPv2, so it's the natural first test target once we add the bearer. Mainline BPQ does not yet, but is expected to in due course; when it does, DAPPS will prefer RHPv2 over AGW for any host that exposes both.

## Why RHPv2

AGW is a 1990s protocol - it works fine for what it does, but it has limits that show up under modern use. RHPv2 addresses several of them:

- **Better session multiplexing.** AGW assumes one TCP connection per host application; multiple apps on the same machine each maintain their own.
- **Cleaner inbound dispatch.** AGW's "register a callsign and receive every connect that matches" model has edge cases around shared callsigns and SSIDs that RHPv2 handles more gracefully.
- **Actually defined error semantics.** AGW's behaviour on TNC reconnect, port up/down events, etc., is empirically discovered; RHPv2 makes it part of the spec.

For DAPPS the day-to-day difference would be modest - the AGW path works well - but the long tail (reconnect storms, multi-app coexistence, shared callsign experiments) gets cleaner.

## What integration will look like

The DAPPS bearer-agnostic seam already supports stream-shaped session bearers (that's what AGW is). RHPv2 plugs in alongside, with a config knob to prefer RHPv2 when it's available:

```
DAPPS_RHP_ENABLED=true
DAPPS_RHP_HOST=127.0.0.1
DAPPS_RHP_PORT=<rhp port BPQ exposes>
```

When both AGW and RHPv2 are configured, DAPPS would prefer RHPv2 for new sessions and fall back to AGW only if the RHPv2 connection isn't available. Existing sessions keep using whichever bearer they started on.

## What stays the same

Everything above the bearer seam - the protocol, the app interface, the discovery model, the routing graph, the dashboard - works identically over RHPv2.

## When?

Tracking issue: [Phase H in plan.md](https://github.com/M0LTE/dapps/blob/master/plan.md#phase-h---concrete-bearer-integrations). XRouter's existing RHPv2 support means we don't have to wait for mainline BPQ to start work on the DAPPS side. If you're an XRouter operator with RHPv2 enabled and would like to be in the loop on testing, [open an issue](https://github.com/M0LTE/dapps/issues).
