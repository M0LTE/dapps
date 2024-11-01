using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

public class ConfigStartup(ILogger<ConfigStartup> logger, IOptions<SystemOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (options.Value.BpqFbbPort == 0)
        {
            await Task.Delay(1000);
        }

        logger.LogInformation($"Callsign: {options.Value.Callsign}");
        logger.LogInformation($"BPQ node: {options.Value.BpqFbbUser}@{options.Value.Host}:{options.Value.BpqFbbPort}");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
