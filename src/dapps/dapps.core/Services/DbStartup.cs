using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

public class DbStartup : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dapps.db");
        var db = new SQLiteConnection(databasePath);
        db.CreateTable<DbMessage>();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}