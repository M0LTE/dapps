using System.Text.Json;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Server-Sent Events surface for the dashboard. The dashboard uses
/// EventSource against <c>/Events/inbound</c> to live-tail messages
/// arriving at this node — handy for watching traffic during RF
/// testing without tailing journald.
///
/// Also serves the dashboard's queue-snapshot JSON endpoint at
/// <c>/Events/queue</c>, polled every few seconds by the dashboard
/// JS so the operator sees the live queue state without a full
/// page reload.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class EventsController(
    InboundEventBus bus,
    Database database,
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics) : ControllerBase
{
    /// <summary>
    /// Live operational counters + per-neighbour state + recent-event
    /// ring. Polled by the dashboard's Health panel.
    /// </summary>
    [HttpGet("health")]
    public OperationalMetrics.Snapshot GetHealth() => metrics.Take();

    /// <summary>
    /// Snapshot of the two operational queues (outbound forwards
    /// pending; messages for local apps not yet ack'd) plus
    /// summary counts. Returned as JSON so the dashboard can
    /// rerender the tables in place without a full reload.
    /// </summary>
    [HttpGet("queue")]
    public async Task<QueueSnapshot> GetQueueSnapshot()
    {
        var local = options.CurrentValue.Callsign.Split('-')[0];
        var recent = (await database.GetRecentMessages(50)).ToList();

        bool IsLocal(DbMessage m) =>
            string.Equals(m.Destination.Split('@').Last().Split('-')[0], local, StringComparison.OrdinalIgnoreCase);

        QueueRow Row(DbMessage m, string? appOverride = null) => new(
            Id: m.Id,
            Destination: m.Destination,
            App: appOverride ?? m.Destination.Split('@')[0],
            SourceCallsign: m.SourceCallsign,
            Bytes: m.Payload.Length,
            Ttl: m.Ttl,
            AgeSeconds: (int)Math.Max(0, (DateTime.UtcNow - m.CreatedAt).TotalSeconds));

        var outbound = recent.Where(m => !m.Forwarded && !IsLocal(m)).Select(m => Row(m)).ToList();
        var inbox = recent.Where(m => !m.LocallyDelivered && IsLocal(m))
            .Select(m => Row(m, m.Destination.Split('@')[0]))
            .ToList();

        return new QueueSnapshot(
            TotalMessages: await database.CountMessages(),
            PendingOutbound: await database.CountPendingOutbound(),
            UndeliveredLocal: await database.CountUndeliveredLocal(),
            Outbound: outbound,
            LocalInbox: inbox);
    }

    [HttpGet("inbound")]
    public async Task GetInbound(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        // Buffering off so each event flushes immediately rather than
        // sitting in Kestrel's response buffer.
        Response.Headers["X-Accel-Buffering"] = "no";

        using var sub = bus.Subscribe(out var reader);

        // Initial padding event — some browsers / proxies wait for the
        // first event before fully establishing the stream.
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        // Keepalive comment every 20s — keeps proxies and the browser's
        // EventSource happy on quiet links.
        var keepAlive = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    await Response.WriteAsync(": keepalive\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
            catch { /* client gone */ }
        }, ct);

        try
        {
            await foreach (var ev in reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(ev);
                await Response.WriteAsync($"event: inbound\ndata: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal client disconnect */ }
    }
}

public sealed record QueueRow(
    string Id,
    string Destination,
    string App,
    string SourceCallsign,
    int Bytes,
    int? Ttl,
    int AgeSeconds);

public sealed record QueueSnapshot(
    int TotalMessages,
    int PendingOutbound,
    int UndeliveredLocal,
    IReadOnlyList<QueueRow> Outbound,
    IReadOnlyList<QueueRow> LocalInbox);
