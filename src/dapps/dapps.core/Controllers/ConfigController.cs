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
    /// First-run / wizard helper: tell the caller which packet-node
    /// software is running so /Setup can default the bearer dropdown.
    ///
    /// Two signals, OR'd:
    /// <list type="number">
    /// <item><description>Process scan. Walks the local process list
    /// for <c>linbpq</c> / <c>bpq32</c> (-> AGW) and <c>xrlin</c> /
    /// <c>xrouter</c> (-> RHPv2). Authoritative when DAPPS shares a
    /// host with the packet node, which is the common deployment.
    /// Doesn't depend on the bearer's listener being up at this exact
    /// moment - operators commonly haven't enabled <c>AGWPORT</c> /
    /// <c>RHPPORT</c> yet on a fresh BPQ/XRouter install.</description></item>
    /// <item><description>TCP probe of the well-known ports (AGW 8000,
    /// RHPv2 9000) on the configured host. Catches setups where DAPPS
    /// runs on a different host from the node.</description></item>
    /// </list>
    /// Either signal alone counts the bearer as available.
    /// </summary>
    [HttpGet("detect-bearer")]
    public async Task<DetectBearerResponse> DetectBearer([FromQuery] string? host = null)
    {
        var nodeHost = string.IsNullOrWhiteSpace(host) ? options.CurrentValue.NodeHost : host.Trim();
        if (string.IsNullOrWhiteSpace(nodeHost)) nodeHost = "localhost";

        var (procAgw, procRhp, procNote) = ScanLocalProcesses();

        var agwTask = ProbeTcp(nodeHost, 8000, TimeSpan.FromMilliseconds(250));
        var rhpTask = ProbeTcp(nodeHost, 9000, TimeSpan.FromMilliseconds(250));
        await Task.WhenAll(agwTask, rhpTask);

        var agw = procAgw || agwTask.Result;
        var rhp = procRhp || rhpTask.Result;
        var suggested = (agw, rhp) switch
        {
            (true, false) => "agw",
            (false, true) => "rhpv2",
            (true, true) => "rhpv2",   // both detected - prefer RHPv2 (sidesteps XR's AGW per-conn-claim quirk)
            _ => "unknown",
        };

        var notes = new List<string>();
        if (procNote.Length > 0) notes.Add(procNote);
        if (agwTask.Result) notes.Add("AGW :8000 listening");
        if (rhpTask.Result) notes.Add("RHPv2 :9000 listening");

        return new DetectBearerResponse(nodeHost, agw, rhp, suggested, string.Join("; ", notes));
    }

    /// <summary>Walk the local process list and detect known packet-node
    /// processes. Returns (any-AGW-host, any-RHPv2-host, human-readable-note).
    /// Catches all exceptions per-process - permission denied / zombies /
    /// other transient races shouldn't take the whole detection down.</summary>
    private static (bool agw, bool rhpv2, string note) ScanLocalProcesses()
    {
        bool agw = false, rhpv2 = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    string name;
                    try { name = (p.ProcessName ?? "").ToLowerInvariant(); }
                    catch { continue; }

                    if (name == "linbpq" || name == "bpq32")
                    {
                        agw = true;
                        seen.Add(p.ProcessName!);
                    }
                    else if (name == "xrlin" || name == "xrouter")
                    {
                        rhpv2 = true;
                        seen.Add(p.ProcessName!);
                    }
                }
            }
        }
        catch
        {
            // Process.GetProcesses can fail on locked-down systems.
            // Fall back to port probes alone.
        }
        var note = seen.Count > 0 ? string.Join(", ", seen) + " running" : "";
        return (agw, rhpv2, note);
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
/// detected; <c>"rhpv2"</c> if only RHPv2 (or both) detected;
/// <c>"unknown"</c> if neither is reachable. <c>Notes</c> describes what
/// triggered the detection (e.g. <c>"linbpq running; AGW :8000 listening"</c>)
/// for surfacing to the operator.
/// </summary>
public sealed record DetectBearerResponse(string Host, bool Agw, bool Rhpv2, string Suggested, string Notes);
