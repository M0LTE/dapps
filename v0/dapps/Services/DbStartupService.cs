using dapps.DataAccess;

namespace dapps.Services;

internal class DbStartupService : IHostedService
{
    private readonly DbConnectionFactory dbConnectionFactory;

    public DbStartupService(DbConnectionFactory dbConnectionFactory) => this.dbConnectionFactory = dbConnectionFactory;

    public Task StartAsync(CancellationToken cancellationToken) => dbConnectionFactory.SetupTables();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
