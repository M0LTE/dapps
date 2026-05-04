# Connect via XRouter (AGW)

This page is the AGW recipe for connecting DAPPS to XRouter. **For XRouter we now recommend the [RHPv2 bearer](rhp.md) instead** - it sidesteps an XRouter-specific AGW limitation around per-TCP-connection callsign authorisation that makes outbound forwards fragile. Use this page if you have a specific reason to stay on AGW (e.g. an older XRouter without RHPv2 support, or compatibility testing); use [RHPv2](rhp.md) for new deployments.

## What we're wiring up

DAPPS opens a TCP connection to XRouter's AGW emulator and registers a callsign for inbound dispatch. When a remote node L2-connects to that callsign, XRouter routes the session to the registered DAPPS client. Outbound is the mirror image: DAPPS asks XRouter to dial a remote callsign over a chosen port, the session is established at L2, DAPPS speaks DAPPSv1 over it.

One TCP connection from DAPPS to XRouter (loopback or LAN). DAPPS does not need to be on the same host as XRouter, as long as it can reach XRouter's AGW port over TCP.

## Step 1: enable XRouter's AGW emulator

XRouter's AGW emulator (the AGWHOST) is on by default, listening on TCP 8000 unless told otherwise. The relevant directive in `XROUTER.CFG`:

```
AGWPORT=8000
```

If port 8000 is already taken on your machine (an existing AGWPE installation, for instance), pick another:

```
AGWPORT=8001
```

You'll set `DAPPS_AGW_PORT` to match in step 4.

## Step 2: pick a callsign for DAPPS - and **don't** add an APPL block for it

DAPPS needs its own callsign with SSID. Convention is to use your own callsign with an unused SSID, e.g. `M0LTE-7`.

**Important**: do **not** add an `APPL=N ... APPLCALL=M0LTE-7 ... ENDAPPL` block in `XROUTER.CFG` for the DAPPS callsign. APPL blocks claim a callsign for an *internal* XRouter application (TCP server, DEDHOST, WA8DED hostmode, etc.). When DAPPS connects via AGW and tries to register that callsign, XRouter refuses because it's already claimed.

DAPPS registers its callsign dynamically via the AGW protocol's X-frame at startup; no config-file declaration is needed. Make sure your `NODECALL`, `CONSOLECALL`, `CHATCALL`, and any `APPL` blocks all use *different* callsigns from the one you'll give DAPPS.

If you accidentally collide and the DAPPS startup log shows an AGW registration failure, change either the DAPPS callsign or the conflicting XRouter declaration and restart.

## Step 3: ACCESS.SYS, if non-loopback

If DAPPS runs on a different host from XRouter, your `ACCESS.SYS` needs to allow AGW connections from DAPPS's IP. The default file requires a password from `0.0.0.0/0` which would block DAPPS. Add a line for the subnet DAPPS connects from, with flag `1` (callsign-only, no password):

```
192.168.1.0/24    1
```

If DAPPS is on the same host as XRouter (loopback), you don't need to touch `ACCESS.SYS` - XRouter doesn't gate loopback AGW connections.

Restart XRouter to pick up `ACCESS.SYS` changes.

## Step 4: tell DAPPS where XRouter is

In DAPPS's environment (systemd unit, shell, etc.):

```
DAPPS_CALLSIGN=M0LTE-7
DAPPS_NODE_HOST=<xrouter-host>
DAPPS_AGW_PORT=8000
DAPPS_DEFAULT_BPQ_PORT=0
```

`DAPPS_NODE_HOST` is `localhost` (or `127.0.0.1`) if DAPPS shares a host with XRouter, otherwise the host XRouter runs on.

`DAPPS_AGW_PORT` matches the `AGWPORT` you set in step 1.

`DAPPS_DEFAULT_BPQ_PORT` is the byte AGW uses to identify which radio port to originate sessions on. With XRouter, the byte numbering is 0-indexed against the order ports appear in `XROUTER.CFG`'s `PORT=N` blocks: `PORT=1` -> byte 0, `PORT=2` -> byte 1, etc. Pick the port you want DAPPS's outbound sessions to go over.

## Step 5: start DAPPS, watch for AGW registration

A successful DAPPS start logs:

```
AGW: connected to <xrouter-host>:8000, registered M0LTE-7 for inbound dispatch
```

If you see `AGW register M0LTE-7 failed`, the most likely cause is the APPL-block collision from step 2. Check `XROUTER.CFG` for any block that declares the DAPPS callsign and remove it.

If you see repeated reconnect attempts, AGW isn't listening or isn't reachable from DAPPS's host - confirm with:

```bash
nc -vz <xrouter-host> 8000
```

## Step 6: prove the inbound side

From any other node on the same air:

```
c <your-DAPPS-callsign>
```

You should land at the `DAPPSv1>` prompt. That's DAPPS having taken the inbound L2 dispatch from XRouter via AGW. Disconnect.

## Step 7: prove the outbound side

Add a manual neighbour from the dashboard's **Neighbours** panel:

| Field          | Value                                       |
|----------------|---------------------------------------------|
| Callsign       | another DAPPS node's callsign with SSID     |
| BPQ port       | the XRouter port byte to use for the originate |
| UDP endpoint   | leave blank                                 |

Send a test message via the dashboard's **Send a test message** form. The forwarder service picks it up within a few seconds, asks XRouter to dial the remote callsign, and you'll see the session in XRouter's stats / monitor view.

## Multi-port setups

If your XRouter has multiple radio ports (`PORT=1`, `PORT=2`, etc.), every neighbour record can specify which port to use. The dashboard's add-neighbour form has the field; the discovery system records the port a peer was heard on automatically.

The byte numbering in DAPPS is 0-indexed against the order ports appear in `XROUTER.CFG`. `PORT=1` -> byte 0, `PORT=2` -> byte 1, and so on.

## Sharing a callsign between XRouter and DAPPS

The recommended pattern is to give DAPPS its own SSID under your callsign. XRouter's NODECALL / CONSOLECALL / CHATCALL / APPL blocks each consume one callsign+SSID; DAPPS consumes one more. Pick a free SSID for DAPPS (often `-7`, `-9`, or whatever else you have spare).

Same-callsign-same-SSID with another XRouter declaration is **not** supported - XRouter will refuse the AGW registration as described in step 2.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `AGW: connection refused` | XRouter isn't listening on `DAPPS_AGW_PORT`, or you've got a firewall in the way. Confirm with `nc -vz <host> <port>`. |
| `AGW register <callsign> failed` | APPL-block / NODECALL / CONSOLECALL / CHATCALL collision on the DAPPS callsign. See step 2. |
| Inbound `c <DAPPS-callsign>` lands at the XRouter node prompt instead of `DAPPSv1>` | DAPPS isn't successfully registered - either it's not running, or AGW registration failed. Check DAPPS logs. |
| Inbound `c <DAPPS-callsign>` fails with no L2 response | The remote node doesn't have a route to your DAPPS callsign. NetROM/AX.25 routing is XRouter's job, not DAPPS's. |
| Outbound forwards never connect | DAPPS's neighbour has the wrong `BPQ port` byte - check the port-numbering note above. |

For more general DAPPS troubleshooting, see the main [Troubleshooting](../troubleshooting.md) page.
