using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class ConfigController(Database database, AdminPasswordStore adminPassword) : ControllerBase
{
    [HttpGet]
    public async Task<SystemOptions> Get()
    {
        return await database.GetSystemOptions();
    }

    [HttpPost]
    public async Task<IActionResult> Post(SystemOptions systemOptions)
    {
        await database.SaveSystemOptions(systemOptions);
        return Ok();
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
