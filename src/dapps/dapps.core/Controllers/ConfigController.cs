using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class ConfigController(Database database, ILogger<ConfigController> logger) : ControllerBase
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
}
