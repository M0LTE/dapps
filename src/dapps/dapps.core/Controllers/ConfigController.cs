using System.Net.Sockets;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class ConfigController(SystemOptionsStore options, AdminPasswordStore adminPassword) : ControllerBase
{
    [HttpGet]
    public ActionResult<SystemOptions> Get() => options.CurrentValue;

    [HttpPost]
    public async Task<IActionResult> Post(SystemOptions systemOptions)
    {
        await options.SaveAsync(systemOptions);
        return Ok();
    }

    /// <summary>
    /// First-run / wizard helper: probe the configured node host on the
    /// well-known AGW (8000) and RHPv2 (9000) ports and tell the caller
    /// what's reachable. Used by /Setup to suggest a bearer choice
    /// without making the operator guess.
    ///
    /// Probes are short (250ms each) and parallel. The default host is
    /// the persisted <see cref="SystemOptions.NodeHost"/> (typically
    /// localhost on a fresh install).
    /// </summary>
    [HttpGet("detect-bearer")]
    public async Task<DetectBearerResponse> DetectBearer([FromQuery] string? host = null)
    {
        var nodeHost = string.IsNullOrWhiteSpace(host) ? options.CurrentValue.NodeHost : host.Trim();
        if (string.IsNullOrWhiteSpace(nodeHost)) nodeHost = "localhost";

        var agwTask = ProbeTcp(nodeHost, 8000, TimeSpan.FromMilliseconds(250));
        var rhpTask = ProbeTcp(nodeHost, 9000, TimeSpan.FromMilliseconds(250));
        await Task.WhenAll(agwTask, rhpTask);

        var agw = agwTask.Result;
        var rhp = rhpTask.Result;
        var suggested = (agw, rhp) switch
        {
            (true, false) => "agw",
            (false, true) => "rhpv2",
            (true, true) => "rhpv2",   // both responded - prefer RHPv2 (more capable; sidesteps XR's AGW per-conn-claim quirk)
            _ => "unknown",
        };
        return new DetectBearerResponse(nodeHost, agw, rhp, suggested);
    }

    private static async Task<bool> ProbeTcp(string host, int port, TimeSpan timeout)
    {
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

    /// <summary>
    /// Set or rotate the dashboard admin password. Empty/null
    /// <c>password</c> clears it (auth then off).
    ///
    /// First-use bootstrap: when no password is set, the admin auth
    /// middleware lets requests through, so this endpoint is reachable
    /// without auth on a fresh node - same chicken-and-egg model as
    /// the existing app-token and bearer-auth bootstraps. Once a
    /// password is set, every subsequent call (including future
    /// rotations) goes through the cookie-authed path.
    /// </summary>
    [HttpPost("admin-password")]
    public async Task<IActionResult> SetAdminPassword([FromBody] AdminPasswordRequest req)
    {
        await adminPassword.SetAsync(req.Password);
        return NoContent();
    }
}

public sealed record AdminPasswordRequest(string? Password);

/// <summary>
/// Result of <see cref="ConfigController.DetectBearer"/>. <c>Suggested</c>
/// is what the /Setup wizard should default to: <c>"agw"</c> if only AGW
/// responded; <c>"rhpv2"</c> if only RHPv2 (or both) responded;
/// <c>"unknown"</c> if neither is reachable.
/// </summary>
public sealed record DetectBearerResponse(string Host, bool Agw, bool Rhpv2, string Suggested);
