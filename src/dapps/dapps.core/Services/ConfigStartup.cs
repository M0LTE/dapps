using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

public class ConfigStartup(ILogger<ConfigStartup> logger, IOptionsMonitor<SystemOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var optionsValue = options.CurrentValue;
        while (optionsValue.FbbPort == 0)
        {
            await Task.Delay(1000, cancellationToken);
        }

        logger.LogInformation($"Callsign: {optionsValue.Callsign}");
        logger.LogInformation($"BPQ node: {optionsValue.FbbUser}@{optionsValue.NodeHost}:{optionsValue.FbbPort}");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
