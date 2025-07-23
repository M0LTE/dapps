using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class AppApiController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok("App API is running");
    }

    [HttpPost]
    public IActionResult Post([FromBody] Message value)
    {
        // Here you can handle the posted value
        return Ok($"Received value: {value}");
    }
}

public readonly record struct Message
{
    public string FromCall { get; init; }
    public string ToNode { get; init; }
    public string ToApp { get; init; }
    public DateTime Ttl { get; init; }
    public byte[] Payload { get; init; }
}