# Connect via RHPv2

**Status: planned, blocked on BPQ.** RHPv2 (Radio Host Protocol v2) is a more modern host-to-node protocol than AGW. As soon as BPQ ships RHPv2 in mainline, DAPPS will add a bearer that uses it preferentially over AGW. Until then, [BPQ AGW](bpq.md) is the path.

## Why RHPv2

AGW is a 1990s protocol — it works fine for what it does, but it has limits that show up under modern use. RHPv2 addresses several of them:

- **Better session multiplexing.** AGW assumes one TCP connection per host application; multiple apps on the same machine each maintain their own.
- **Cleaner inbound dispatch.** AGW's "register a callsign and receive every connect that matches" model has edge cases around shared callsigns and SSIDs that RHPv2 handles more gracefully.
- **Actually defined error semantics.** AGW's behaviour on TNC reconnect, port up/down events, etc., is empirically discovered; RHPv2 makes it part of the spec.

For DAPPS the day-to-day difference would be modest — the AGW path works well — but the long tail (reconnect storms, multi-app coexistence, shared callsign experiments) gets cleaner.

## What integration will look like

The DAPPS bearer-agnostic seam already supports stream-shaped session bearers (that's what AGW is). RHPv2 plugs in alongside, with a config knob to prefer RHPv2 when it's available:

```
DAPPS_RHP_ENABLED=true
DAPPS_RHP_HOST=127.0.0.1
DAPPS_RHP_PORT=<rhp port BPQ exposes>
```

When both AGW and RHPv2 are configured, DAPPS would prefer RHPv2 for new sessions and fall back to AGW only if the RHPv2 connection isn't available. Existing sessions keep using whichever bearer they started on.

## What stays the same

Everything above the bearer seam — the protocol, the app interface, the discovery model, the routing graph, the dashboard — works identically over RHPv2.

## When?

Tracking issue: [Phase H in plan.md](https://github.com/M0LTE/dapps/blob/master/plan.md#phase-h--concrete-bearer-integrations). The blocker is that mainline BPQ doesn't yet ship an RHPv2 listener; we'd need that before there's anything to talk to. If you have visibility into the BPQ side of this, [open an issue](https://github.com/M0LTE/dapps/issues).
