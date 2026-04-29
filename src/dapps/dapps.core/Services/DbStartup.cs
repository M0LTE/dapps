using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

public static class DbInfo
{
    /// <summary>Test override for the SQLite path. Production uses the
    /// default `data/dapps.db` (or `dapps.db` if no `data/` dir).</summary>
    public static string? OverridePath { get; set; }

    private static string GetPath()
    {
        if (!string.IsNullOrEmpty(OverridePath)) return OverridePath;
        if (Directory.Exists("data")) return "data/dapps.db";
        return "dapps.db";
    }

    public static SQLiteConnection GetConnection() => new(GetPath());

    public static SQLiteAsyncConnection GetAsyncConnection() => new(GetPath());
}

public class DbStartup(ILogger<DbStartup> logger) : IHostedService
{
    private readonly SQLiteConnection db = DbInfo.GetConnection();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation($"DB: {db.DatabasePath}");
        
        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();
        db.CreateTable<DbSystemOption>();
        db.CreateTable<DbRouteHint>();
        db.CreateTable<DbNeighbour>();

        var options = db.Query<DbSystemOption>($"select * from {db.Table<DbSystemOption>().Table.TableName};");
        InsertIfNotPresent(options, "NodeType", "BPQ");
        InsertIfNotPresent(options, "NodeHost", "localhost");
        InsertIfNotPresent(options, "AgwPort", "8000");
        InsertIfNotPresent(options, "DefaultBpqPort", "0");
        InsertIfNotPresent(options, "Callsign", "N0CALL");
        InsertIfNotPresent(options, "MqttPort", "1883");
        
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