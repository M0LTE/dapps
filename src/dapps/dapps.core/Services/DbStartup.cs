using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;
using System.Diagnostics;

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

public class DbStartup(IOptions<SystemOptions> optionsObj, ILogger<DbStartup> logger) : IHostedService
{
    private readonly SQLiteConnection db = DbInfo.GetConnection();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation($"DB: {db.DatabasePath}");
        Debug.WriteLine(optionsObj.Value.BpqFbbPort);
        
        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();
        db.CreateTable<DbSystemOption>();
        db.CreateTable<DbRouteHint>();
        db.CreateTable<DbNeighbour>();

        var options = db.Query<DbSystemOption>($"select * from {db.Table<DbSystemOption>().Table.TableName};");
        InsertIfNotPresent(options, "NodeType", "BPQ");
        InsertIfNotPresent(options, "NodeHost", "localhost");
        InsertIfNotPresent(options, "FbbPort", "8011");
        InsertIfNotPresent(options, "FbbUser", "telnetportuser");
        InsertIfNotPresent(options, "FbbPassword", "telnetportpassword");
        InsertIfNotPresent(options, "Callsign", "N0CALL");
        
        logger.LogInformation("DB schema refreshed");
        return Task.CompletedTask;
    }

    private void InsertIfNotPresent(List<DbSystemOption> options, string key, string defaultValue)
    {
        if (!options.Any(o => string.Equals(o.Option, key, StringComparison.OrdinalIgnoreCase)))
        {
            db.Insert(new DbSystemOption { Option = key, Value = defaultValue });
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}