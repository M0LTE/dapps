using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Admin surface for issuing per-app credentials. POSTing returns the
/// plaintext token exactly once — capture it on creation and hand it
/// to the app owner, because we only store the hash.
///
/// This endpoint is unauthenticated by design: pairing it with bearer
/// auth would be a chicken-and-egg on first use. The README's
/// getting-started warns operators to bind the REST surface to
/// loopback (or a trusted LAN) until proper admin auth lands.
/// </summary>
[ApiController]
[Route("[controller]")]
public class AppTokensController(AppTokenStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<AppTokenInfo>> List()
    {
        return await store.ListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<CreateAppTokenResponse>> CreateOrRotate([FromBody] CreateAppTokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.App))
        {
            return BadRequest("app is required");
        }
        var plaintext = await store.CreateOrRotateAsync(req.App.Trim());
        return Ok(new CreateAppTokenResponse(req.App.Trim(), plaintext));
    }

    [HttpDelete("{app}")]
    public async Task<IActionResult> Revoke(string app)
    {
        var deleted = await store.RevokeAsync(app);
        return deleted ? NoContent() : NotFound();
    }
}

public sealed record CreateAppTokenRequest(string App);

/// <summary>
/// Returned only on POST. The <see cref="Token"/> value is plaintext;
/// the API will never return it again. Subsequent GETs surface
/// <see cref="AppTokenInfo"/> rows that omit the secret.
/// </summary>
public sealed record CreateAppTokenResponse(string App, string Token);
