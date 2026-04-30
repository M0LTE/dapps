using System.Text;
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

    /// <summary>Sentinel callsign in the seeded defaults. If the resolved
    /// value still matches this after seed it means the operator never
    /// configured a real one — refuse to start rather than transmit
    /// frames stamped with it.</summary>
    private const string PlaceholderCallsign = "N0CALL";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation($"DB: {db.DatabasePath}");

        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();
        db.CreateTable<DbSystemOption>();
        db.CreateTable<DbRouteHint>();
        db.CreateTable<DbNeighbour>();
        db.CreateTable<DbAppToken>();

        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");

        // Seeded defaults. When an env var DAPPS_<KEY> is set, it wins;
        // otherwise the hardcoded fallback applies. Either way the value
        // is only written when no row exists — once configured (here or
        // via /Config), the row sticks and env vars stop mattering.
        InsertIfNotPresent(options, "NodeType", "BPQ");
        InsertIfNotPresent(options, "NodeHost", "localhost");
        InsertIfNotPresent(options, "AgwPort", "8000");
        InsertIfNotPresent(options, "DefaultBpqPort", "0");
        InsertIfNotPresent(options, "Callsign", PlaceholderCallsign);
        InsertIfNotPresent(options, "MqttPort", "1883");
        InsertIfNotPresent(options, "UdpListenPort", "0");
        InsertIfNotPresent(options, "AuthRequired", "false");

        ValidateRequiredConfig();

        logger.LogInformation("DB schema refreshed");
        return Task.CompletedTask;
    }

    private void InsertIfNotPresent(List<DbSystemOption> options, string key, string defaultValue)
    {
        if (options.Any(o => string.Equals(o.Option, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var envKey = "DAPPS_" + ToScreamingSnake(key);
        var envValue = Environment.GetEnvironmentVariable(envKey);
        var value = string.IsNullOrEmpty(envValue) ? defaultValue : envValue;

        db.Insert(new DbSystemOption { Option = key, Value = value });

        if (!string.IsNullOrEmpty(envValue))
        {
            logger.LogInformation("Seeded {0} from {1}", key, envKey);
        }
    }

    /// <summary>
    /// Refuse to start on a misconfigured-or-missing callsign. The
    /// placeholder default is fine in tests but actively dangerous in
    /// the wild — frames go on the air with the configured callsign,
    /// and pretending to be N0CALL is both wrong and traceable to us.
    /// </summary>
    private void ValidateRequiredConfig()
    {
        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");
        var callsignRow = options.SingleOrDefault(
            o => string.Equals(o.Option, "Callsign", StringComparison.OrdinalIgnoreCase));
        var callsign = callsignRow?.Value ?? "";

        if (string.IsNullOrWhiteSpace(callsign)
            || string.Equals(callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Callsign is not configured (currently '{callsign}'). " +
                $"Set the DAPPS_CALLSIGN environment variable before first start, " +
                $"or POST {{\"Callsign\":\"YOUR-CALL\", ...}} to /Config and restart.";
            logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Convert a PascalCase or camelCase identifier to SCREAMING_SNAKE_CASE
    /// for use as an environment-variable suffix. <c>NodeHost</c> →
    /// <c>NODE_HOST</c>; <c>DefaultBpqPort</c> → <c>DEFAULT_BPQ_PORT</c>.
    /// </summary>
    private static string ToScreamingSnake(string identifier)
    {
        var sb = new StringBuilder(identifier.Length + 4);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
