using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Inbound bearer: maintains an AGW client connection to BPQ, registers
/// our callsign so BPQ dispatches inbound L2 connects to us, and pumps
/// the resulting per-session byte streams into <see cref="InboundConnectionHandler"/>.
///
/// Replaces the old BPQ-Apps-Interface (HOST/CMDPORT TCP-bridge) inbound
/// path. AGW gives us:
///   - off-host operation (BPQ's HOST handler hard-codes 127.0.0.1; AGW
///     reaches across the network freely),
///   - frame-level visibility (no Telnet line-discipline rewrites),
///   - one TCP connection to BPQ multiplexing all sessions.
///
/// Operator note: BPQ still requires an <c>APPLICATION N,DAPPS,,&lt;CALL&gt;,...</c>
/// line in <c>bpq32.cfg</c> with an *empty* CMD field. Without that
/// line, BPQ's L2 layer doesn't accept frames addressed to the dapps
/// callsign and the AGW <c>'X'</c> registration is silently inert
/// (per linbpq apps-interface.md and AGWAPI.c:1427).
///
/// Reconnect policy: on any AGW socket error, sleep
/// <see cref="ReconnectBackoff"/> and retry. In-flight inbound sessions
/// are lost (their streams get EOF); the sender's bearer surfaces a
/// timeout and retries on its next forwarder run — matches the
/// existing at-least-once semantics.
/// </summary>
public sealed class AgwInboundService(
    IOptionsMonitor<SystemOptions> options,
    Database database,
    IBackhaulInbox inbox,
    ILoggerFactory loggerFactory,
    ILogger<AgwInboundService> logger) : IHostedService
{
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource stoppingTokenSource = new();
    private Task? loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        loopTask = Task.Run(() => RunLoop(stoppingTokenSource.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stoppingTokenSource.CancelAsync();
        if (loopTask is not null)
        {
            try { await loopTask; } catch { /* shutdown */ }
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound loop ended; reconnecting in {0}s", ReconnectBackoff.TotalSeconds);
            }

            try { await Task.Delay(ReconnectBackoff, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var localCall = opts.Callsign;
        if (string.IsNullOrWhiteSpace(localCall))
        {
            logger.LogWarning("Callsign not configured; AGW inbound idle");
            await Task.Delay(ReconnectBackoff, ct);
            return;
        }

        using var tcp = new TcpClient();
        logger.LogInformation("AGW inbound: connecting {0}:{1}", opts.NodeHost, opts.AgwPort);
        await tcp.ConnectAsync(opts.NodeHost, opts.AgwPort, ct);
        var framing = new AgwFrameTransport(tcp.GetStream());

        // 'X' register so BPQ knows where to dispatch inbound 'C' frames.
        // Note this is necessary but *not sufficient*: the operator must
        // also have the callsign on an APPLICATION line in bpq32.cfg
        // (apps-interface.md). Without that, the X register is a no-op
        // for inbound and we'll sit here happily forever, never being
        // dispatched to.
        await framing.WriteFrameAsync(
            new AgwFrame(0, 'X', 0, localCall, "", []), ct);

        var sessions = new ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await framing.ReadFrameAsync(ct);
                await Dispatch(frame, framing, sessions, ct);
            }
        }
        finally
        {
            // Tear down any in-flight sessions cleanly so handlers exit.
            foreach (var s in sessions.Values) s.SignalRemoteDisconnect();
            sessions.Clear();
        }
    }

    private async Task Dispatch(
        AgwFrame frame,
        AgwFrameTransport framing,
        ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream> sessions,
        CancellationToken ct)
    {
        switch (frame.Kind)
        {
            case 'X':
                logger.LogDebug("AGW inbound: 'X' ack");
                break;

            case 'C':
                await OnConnect(frame, framing, sessions, ct);
                break;

            case 'D':
                if (sessions.TryGetValue(KeyForData(frame), out var stream))
                {
                    await stream.PushIncoming(frame.Payload, ct);
                }
                else
                {
                    logger.LogDebug("AGW inbound: 'D' for unknown session {0}↔{1}",
                        frame.CallFrom, frame.CallTo);
                }
                break;

            case 'd':
                if (sessions.TryRemove(KeyForData(frame), out var dstream))
                {
                    dstream.SignalRemoteDisconnect();
                    logger.LogInformation("AGW inbound: session closed {0}↔{1}", frame.CallFrom, frame.CallTo);
                }
                break;

            default:
                logger.LogDebug("AGW inbound: ignoring frame kind '{0}'", frame.Kind);
                break;
        }
    }

    private async Task OnConnect(
        AgwFrame frame,
        AgwFrameTransport framing,
        ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream> sessions,
        CancellationToken ct)
    {
        // BPQ delivers an inbound 'C' frame with CallFrom = the remote
        // station that connected to us, CallTo = our local APPL call.
        var remote = frame.CallFrom;
        var local = frame.CallTo;
        var port = frame.Port;
        logger.LogInformation("AGW inbound: 'C' from {0} to {1} on port {2}", remote, local, port);

        // Some AGW emulators emit a "*** CONNECTED..." status string in
        // the 'C' payload; that's noise from dapps's POV and we just
        // discard it.

        var key = new SessionKey(local, remote, port);
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: async (data, c) =>
            {
                await framing.WriteFrameAsync(
                    new AgwFrame(port, 'D', 0xF0, local, remote, data), c);
            },
            sendRemoteDisconnect: async c =>
            {
                await framing.WriteFrameAsync(
                    new AgwFrame(port, 'd', 0, local, remote, []), c);
            });

        if (!sessions.TryAdd(key, stream))
        {
            logger.LogWarning("AGW inbound: duplicate connect for existing session {0}↔{1}; ignoring",
                local, remote);
            await stream.DisposeAsync();
            return;
        }

        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: remote, loggerFactory, database, inbox);

        _ = Task.Run(async () =>
        {
            try { await handler.Handle(stoppingTokenSource.Token); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound: session handler {0}↔{1} failed", local, remote);
            }
            finally
            {
                if (sessions.TryRemove(key, out var s))
                {
                    try { await s.DisposeAsync(); } catch { /* best effort */ }
                }
            }
        }, ct);
    }

    /// <summary>For 'D' / 'd' frames the (local, remote, port) tuple is
    /// flipped relative to the 'C' frame: BPQ uses CallFrom = peer,
    /// CallTo = us on the inbound and CallFrom = us, CallTo = peer for
    /// frames it forwards to us. Try both orientations.</summary>
    private static SessionKey KeyForData(AgwFrame frame) =>
        new(frame.CallTo, frame.CallFrom, frame.Port);

    private record struct SessionKey(string Local, string Remote, byte Port);
}
