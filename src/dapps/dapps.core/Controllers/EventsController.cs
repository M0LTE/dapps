using System.Text.Json;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Server-Sent Events surface for the dashboard. The dashboard uses
/// EventSource against <c>/Events/inbound</c> to live-tail messages
/// arriving at this node — handy for watching traffic during RF
/// testing without tailing journald.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class EventsController(InboundEventBus bus) : ControllerBase
{
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
