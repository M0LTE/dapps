using System.Collections.Concurrent;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>
/// Inbound listener for the RHPv2 bearer. Equivalent of
/// <see cref="AgwInboundService"/> for hosts that expose RHPv2 (XRouter
/// today; future BPQ versions). Maintains one TCP connection to the
/// host's RHPv2 port, opens a passive AX.25 stream socket, binds the
/// local callsign, listens for inbound connects, and dispatches each
/// accepted session into <see cref="InboundConnectionHandler"/> the
/// same way the AGW service does.
///
/// Architectural advantage over AGW for the XRouter case: RHPv2's
/// session-handle model and per-handle event stream means we can
/// share one TCP connection between inbound and outbound work without
/// the AGW per-connection-callsign-claim collision that breaks DAPPS-
/// on-XR via AGW. (The current implementation still uses separate
/// connections for outbound via <c>Rhpv2OutboundTransport</c> for
/// simplicity; sharing the connection is a follow-up optimisation.)
/// </summary>
public sealed class Rhpv2InboundService(
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<Rhpv2InboundService> logger,
    Database database,
    IBackhaulInbox inbox,
    OperationalMetrics metrics) : BackgroundService
{
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? cycleTokenSource;
    private IDisposable? optionsChangeSubscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cancel the current connection-cycle on any SystemOptions change
        // so a /Config save (callsign, RHP host/port/auth, bearer flip)
        // takes effect on the next iteration without a daemon restart.
        optionsChangeSubscription = options.OnChange((_, _) => cycleTokenSource?.Cancel());
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                cycleTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var cycleCt = cycleTokenSource.Token;
                try
                {
                    await RunOnce(cycleCt);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Cycle cancelled by an options change; loop and reconnect.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "RHP inbound: connection lost; reconnecting in {0}s", ReconnectBackoff.TotalSeconds);
                }

                try { await Task.Delay(ReconnectBackoff, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            optionsChangeSubscription?.Dispose();
            optionsChangeSubscription = null;
        }
    }

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;

        // Bearer-active gate: only run when RHPv2 is configured.
        // AgwInboundService runs alongside us with the matching gate on
        // "agw"; OnChange fires when /Config flips the value.
        if (!string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(IdleBackoff, stoppingToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Callsign)
            || string.Equals(opts.Callsign, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Callsign not configured; RHP inbound idle (waiting for /Setup or /Config)");
            await Task.Delay(IdleBackoff, stoppingToken);
            return;
        }

        var host = string.IsNullOrEmpty(opts.NodeHost) ? "127.0.0.1" : opts.NodeHost;
        var port = opts.RhpPort > 0 ? opts.RhpPort : RhpClient.DefaultPort;

        logger.LogInformation("RHP inbound: connecting to {host}:{port}", host, port);
        await using var rhp = await RhpClient.ConnectAsync(host, port, stoppingToken);
        metrics.RecordAgwReconnect();  // re-using the AGW counter; same operator concept

        if (!string.IsNullOrEmpty(opts.RhpUser))
        {
            await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", stoppingToken);
        }

        // socket + bind + listen for inbound AX.25 streams to our callsign.
        // Port omitted = listen across all configured XRouter ports.
        var listenerHandle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, stoppingToken);
        await rhp.BindAsync(listenerHandle, local: opts.Callsign, port: null, stoppingToken);
        await rhp.ListenAsync(listenerHandle, OpenFlags.Passive, stoppingToken);
        logger.LogInformation("RHP inbound: listener bound to {call} on handle {h}", opts.Callsign, listenerHandle);

        var sessions = new ConcurrentDictionary<int, MultiplexedAgwSessionStream>();
        var disconnect = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<RhpAcceptedEventArgs> acceptedHandler = (_, e) =>
        {
            if (e.Message.Handle != listenerHandle) return;
            var child = e.Message.Child;
            var remote = e.Message.Remote ?? "(unknown)";
            logger.LogInformation("RHP inbound: ACCEPT child={child} from {remote}", child, remote);
            metrics.RecordInboundConnect(remote);

            var stream = new MultiplexedAgwSessionStream(
                writeOutgoing: async (data, c) => await rhp.SendOnHandleAsync(child, data, c),
                sendRemoteDisconnect: async c =>
                {
                    try { await rhp.CloseAsync(child, c); }
                    catch { /* may already be closed */ }
                });

            if (!sessions.TryAdd(child, stream))
            {
                logger.LogWarning("RHP inbound: duplicate child handle {child}; ignoring", child);
                _ = stream.DisposeAsync().AsTask();
                return;
            }

            var handler = new InboundConnectionHandler(
                stream, sourceCallsign: remote, loggerFactory, database, inbox, metrics);

            _ = Task.Run(async () =>
            {
                try { await handler.Handle(stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "RHP inbound: session handler {child} failed", child); }
                finally
                {
                    if (sessions.TryRemove(child, out var s))
                    {
                        try { await s.DisposeAsync(); } catch { }
                    }
                    try { await rhp.CloseAsync(child, CancellationToken.None); } catch { }
                }
            }, stoppingToken);
        };

        EventHandler<RhpReceivedEventArgs> recvHandler = (_, e) =>
        {
            if (sessions.TryGetValue(e.Message.Handle, out var stream))
            {
                var bytes = RhpDataEncoding.FromWireString(e.Message.Data);
                _ = stream.PushIncoming(bytes, stoppingToken);
            }
        };

        EventHandler<RhpClosedEventArgs> closedHandler = (_, e) =>
        {
            if (sessions.TryGetValue(e.Handle, out var stream))
            {
                stream.SignalRemoteDisconnect();
            }
        };

        EventHandler<Exception?> disconnectedHandler = (_, ex) =>
        {
            disconnect.TrySetResult(ex);
        };

        rhp.Accepted += acceptedHandler;
        rhp.Received += recvHandler;
        rhp.Closed += closedHandler;
        rhp.Disconnected += disconnectedHandler;

        try
        {
            // Wait for either: external cancellation, or RhpClient
            // disconnect (TCP socket closed by peer or local error).
            using var reg = stoppingToken.Register(() => disconnect.TrySetCanceled());
            var ex = await disconnect.Task;
            if (ex is not null) throw ex;
        }
        finally
        {
            rhp.Accepted -= acceptedHandler;
            rhp.Received -= recvHandler;
            rhp.Closed -= closedHandler;
            rhp.Disconnected -= disconnectedHandler;
            foreach (var s in sessions.Values) s.SignalRemoteDisconnect();
            sessions.Clear();
        }
    }
}
