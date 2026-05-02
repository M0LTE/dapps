using System.Diagnostics;
using System.Net.Sockets;
using dapps.core.Models;
using dapps.core.Services;
using dapps.core.Updater;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C3 — lightweight liveness endpoint distinct from
/// <c>/Events/health</c> (the metrics dump). HTTP 200 when every
/// critical dependency is up; 503 with details when degraded so
/// systemd watchdog units / external uptime monitors / load balancers
/// have a clean signal to act on.
///
/// Definition of healthy:
/// <list type="bullet">
/// <item><description>Callsign is configured (not the placeholder).</description></item>
/// <item><description>BPQ AGW is reachable on the configured host:port.</description></item>
/// <item><description>The MQTT broker is bound on the configured port.</description></item>
/// </list>
/// All three are degradations the operator wants to know about
/// immediately. Pending-outbound count and last-forward-success
/// timestamp are surfaced for context but don't affect status.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class HealthController(
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    Database database) : ControllerBase
{
    private const string PlaceholderCallsign = "N0CALL";

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var opts = options.CurrentValue;

        var callsignOk = !string.IsNullOrWhiteSpace(opts.Callsign)
            && !string.Equals(opts.Callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);

        var bpqOk = await ProbeTcp(opts.NodeHost, opts.AgwPort, TimeSpan.FromMilliseconds(500));
        var mqttOk = await ProbeTcp("127.0.0.1", opts.MqttPort, TimeSpan.FromMilliseconds(250));

        var pendingOutbound = await database.CountPendingOutbound();

        var status = (callsignOk && bpqOk && mqttOk) ? "healthy" : "degraded";
        var body = new HealthResponse(
            Status: status,
            Callsign: opts.Callsign,
            Version: UpdaterCli.ResolveCurrentVersion(),
            UptimeSeconds: (long)Math.Max(0, (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds),
            CallsignConfigured: callsignOk,
            BpqAgwReachable: bpqOk,
            MqttBrokerUp: mqttOk,
            LastForwardSuccessAt: metrics.LastForwardSuccessAt,
            PendingOutboundCount: pendingOutbound);

        return status == "healthy" ? Ok(body) : StatusCode(503, body);
    }

    private static async Task<bool> ProbeTcp(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record HealthResponse(
    string Status,
    string Callsign,
    string Version,
    long UptimeSeconds,
    bool CallsignConfigured,
    bool BpqAgwReachable,
    bool MqttBrokerUp,
    DateTime? LastForwardSuccessAt,
    int PendingOutboundCount);
