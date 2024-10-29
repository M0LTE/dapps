using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

public static class DbInfo
{
    public static SQLiteConnection GetConnection()
    {
        var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dapps.db");
        return new SQLiteConnection(databasePath);
    }

    public static SQLiteAsyncConnection GetAsyncConnection()
    {
        var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dapps.db");
        return new SQLiteAsyncConnection(databasePath);
    }
}

public class DbStartup : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var db = DbInfo.GetConnection();
        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}