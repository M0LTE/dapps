using System.Net;
using System.Net.Sockets;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using Microsoft.Extensions.Options;
using dapps.core.Models;

namespace dapps.core.Services;

/// <summary>
/// UDP receiver for the datagram backhaul (Plan A0.4 stand-in for
/// MeshCore-style bearers). Binds to <see cref="SystemOptions.UdpListenPort"/>
/// when that's > 0; reads fragments, reassembles via
/// <see cref="Reassembler"/>, decodes via
/// <see cref="BackhaulMessageCodec"/>, hands the result to
/// <see cref="IBackhaulInbox"/>.
///
/// The bearer is fire-and-forget — we don't ack the sender, and we
/// don't currently advertise our endpoint. Discovery over UDP and
/// reliable delivery are higher-layer concerns (Phase B).
/// </summary>
public sealed class UdpDatagramListener(
    IOptionsMonitor<SystemOptions> options,
    IBackhaulInbox inbox,
    ILogger<UdpDatagramListener> logger) : BackgroundService
{
    /// <summary>Source address used for inbound delivery's sourceCallsign
    /// when the bearer doesn't carry an explicit one. The DAPPSv1
    /// session reader sees the connecting BPQ's callsign as the first
    /// line; UDP has no equivalent. Using the IP:port of the sender as
    /// a fallback keeps the audit trail meaningful without inventing a
    /// fake callsign.</summary>
    private const string UnknownSourceCallsign = "UDP";

    /// <summary>Drop reassembly state for a message we haven't completed
    /// within this window. Keeps a forever-leaky neighbour from pinning
    /// memory.</summary>
    private static readonly TimeSpan ReassemblyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often to sweep the reassembler. Cheap operation, low
    /// frequency suffices.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.CurrentValue.UdpListenPort;
        if (port <= 0)
        {
            logger.LogInformation("UDP datagram listener disabled (UdpListenPort=0)");
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        var reassembler = new Reassembler();
        var nextSweep = DateTime.UtcNow + SweepInterval;
        logger.LogInformation("UDP datagram listener bound on :{0}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UDP receive failed; continuing");
                continue;
            }

            try
            {
                var assembled = reassembler.Accept(received.Buffer, DateTime.UtcNow);
                if (assembled is null)
                {
                    continue;
                }

                BackhaulMessage decoded;
                try
                {
                    decoded = BackhaulMessageCodec.Decode(assembled);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "UDP datagram reassembled to {0} bytes but failed to decode",
                        assembled.Length);
                    continue;
                }

                logger.LogInformation("UDP datagram delivered: {0} from {1} (dst={2})",
                    decoded.Id, received.RemoteEndPoint, decoded.Destination);
                await inbox.DeliverAsync(decoded, UnknownSourceCallsign, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UDP fragment dispatch failed");
            }

            if (DateTime.UtcNow >= nextSweep)
            {
                var dropped = reassembler.DropOlderThan(DateTime.UtcNow - ReassemblyTimeout);
                if (dropped > 0)
                {
                    logger.LogInformation("UDP reassembler dropped {0} stale partial(s)", dropped);
                }
                nextSweep = DateTime.UtcNow + SweepInterval;
            }
        }
    }
}
