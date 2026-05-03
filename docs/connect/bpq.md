# Connect via BPQ (AGW)

This page is the full BPQ recipe for getting DAPPS on-air. Same shape applies to any AGW host (Direwolf, AGWPE, etc.) - only the specifics of the config file change.

## What we're wiring up

DAPPS uses **one** BPQ surface - AGW - for both inbound and outbound traffic. Outbound: DAPPS opens an AGW client connection to BPQ, registers its callsign, and dials remote callsigns when it has something to ship. Inbound: when a remote node L2-connects to your callsign with the application command `DAPPS`, BPQ dispatches the session over AGW to the registered DAPPS client.

This is one TCP connection from DAPPS to BPQ (not co-located requirements; DAPPS can run on a different host as long as it can reach BPQ's AGW port over TCP).

## Step 1: configure BPQ

Add an `APPLICATION` line to your `bpq32.cfg`:

```
APPLICATION 1,DAPPS,,,,, 0
```

Field-by-field:

- `1` - application slot number. Bump this if slot 1 is already in use; the value doesn't matter beyond uniqueness.
- `DAPPS` - the command operators type at the BPQ node prompt to enter the DAPPS slot. If you'd rather use a different name (`MSG`, `APPS`, whatever), put it here, then set `DAPPS_NODE_PROMPT_APPLICATION_COMMAND` in the daemon's environment to match.
- `,,,,,` - empty CMD field on purpose. Older recipes used `C N HOST K TRANS S` here; for DAPPS, leave it empty so BPQ doesn't run any node command on inbound - it just dispatches the L2 'C' frame to the registered AGW client.
- `0` - application alias / number flag.

Restart BPQ for the change to land.

You can also enable AXIP / AXUDP / serial ports as you would for any application - DAPPS doesn't care which physical link AGW is fronting, as long as BPQ delivers sessions over the AGW socket.

## Step 2: confirm AGW is reachable from where DAPPS will run

```bash
# From the host DAPPS runs on
nc -vz <bpq-host> 8000
```

Should connect immediately. If not, check BPQ's AGW listener is enabled (`AGWPORT 8000` in `bpq32.cfg`) and that no firewall is in the way.

## Step 3: tell DAPPS where BPQ is

Set these in DAPPS's environment (in the systemd unit, the docker compose, or wherever):

```
DAPPS_CALLSIGN=M0LTE-1
DAPPS_NODE_HOST=<bpq-host>
DAPPS_AGW_PORT=8000
DAPPS_DEFAULT_BPQ_PORT=0
```

`DAPPS_CALLSIGN` is the callsign DAPPS registers with AGW for inbound dispatch. SSID matters: BPQ dispatches based on the full call+SSID, so `M0LTE-1` is different from `M0LTE-2`.

`DAPPS_DEFAULT_BPQ_PORT` is the BPQ port byte (0-indexed; the order they appear in `bpq32.cfg`'s `PORTNUM` lines) used when originating an outbound session if a per-neighbour override isn't set. Leave it `0` unless you have multiple ports and you want to default to a specific one.

## Step 4: start DAPPS, watch for AGW registration

A successful start logs:

```
AGW: connected to <host>:8000, registered M0LTE-1 for inbound dispatch
```

If you see repeated reconnect attempts instead, AGW isn't listening or the callsign is busy elsewhere.

## Step 5: prove the inbound side

From another packet node (or from the BPQ console):

```
c <your-callsign>
```

You should land at the `DAPPSv1>` prompt - that's DAPPS having taken the inbound session from AGW. Disconnect.

## Step 6: prove the outbound side

Add a manual neighbour from the dashboard's **Neighbours** panel (or the [discovery system](../discovery-and-routing.md), but a hand-add gets you there fastest):

| Field          | Value                                       |
|----------------|---------------------------------------------|
| Callsign       | another DAPPS node's callsign with SSID     |
| BPQ port       | the BPQ port byte to use for AGW originates |
| UDP endpoint   | leave blank                                 |

Send a test message via the dashboard's **Send a test message** form. The forwarder service will pick it up within a few seconds, dial the remote callsign over AGW, and you'll see the session in BPQ's MHEARD / connections view.

## Multi-port setups

If your BPQ has multiple radio ports (e.g. port 1 = VHF FM, port 2 = HF), every neighbour record can specify which BPQ port to use for that peer. The dashboard's add-neighbour form has the BPQ-port field; the discovery system records the port a peer was heard on automatically.

You don't need an `APPLICATION` line per port - the single `APPLICATION DAPPS` line covers all ports. The port choice is per-neighbour for outbound; for inbound, BPQ dispatches the same way no matter which port the connect arrived on.

## Sharing a callsign between BPQ and DAPPS

The recommended pattern is to give DAPPS its own SSID under your callsign - `M0LTE-1` for the BBS, `M0LTE-7` for DAPPS, etc. AGW dispatches by exact call+SSID, so they coexist cleanly. You can run as many DAPPS instances against one BPQ as you have spare SSIDs.

Same-callsign-same-SSID with another application is **not** supported - AGW will dispatch to whichever client registered last and the first registration silently stops getting inbound traffic.

## Troubleshooting

See the [Troubleshooting](../troubleshooting.md) page for the AGW-specific failure modes (registration timeouts, port-byte confusion, BPQ refusing the inbound dispatch).

## Other AGW hosts

The AGW protocol is well-defined and stable; any host that implements it should work. Direwolf in particular ships an AGW host as a built-in option. The configuration shape is the same: enable AGW, give DAPPS the callsign and host:port, register on connect.

If you've got a non-BPQ AGW setup working, please [open an issue](https://github.com/M0LTE/dapps/issues) so we can document it here.
