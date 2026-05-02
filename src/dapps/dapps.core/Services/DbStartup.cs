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

public static class DbStartup
{
    /// <summary>Sentinel callsign in the seeded defaults. If the resolved
    /// value still matches this after seed it means the operator never
    /// configured a real one — refuse to start rather than transmit
    /// frames stamped with it.</summary>
    private const string PlaceholderCallsign = "N0CALL";

    /// <summary>
    /// Create every table the daemon needs and seed the first-run
    /// systemoptions defaults (env-var overrides → hardcoded fallback).
    /// Safe to call multiple times — every step is idempotent.
    ///
    /// Called once from Program.cs *before* <c>builder.Build()</c> so
    /// the eager DI materialisation of hosted services (which transit
    /// IRoutingAlgorithm → IOptionsMonitor&lt;SystemOptions&gt;.CurrentValue
    /// → SystemOptions Configure → OptionsRepo.GetOptions) finds a
    /// seeded table rather than racing it.
    /// </summary>
    public static void EnsureSchemaAndSeed(ILogger? logger = null)
    {
        using var db = DbInfo.GetConnection();
        logger?.LogInformation($"DB: {db.DatabasePath}");

        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();
        db.CreateTable<DbSystemOption>();
        db.CreateTable<DbRouteHint>();
        db.CreateTable<DbNeighbour>();
        db.CreateTable<DbAppToken>();
        db.CreateTable<DbDiscoveredPeer>();
        db.CreateTable<DbDiscoveryChannel>();
        db.CreateTable<DbDroppedMessage>();
        db.CreateTable<DbLearnedRoute>();
        db.CreateTable<DbFloodSeen>();
        db.CreateTable<DbDiscoveredPath>();
        db.CreateTable<DbProbedNode>();
        db.CreateTable<DbFragment>();
        db.CreateTable<DbPolledNode>();

        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");

        // Seeded defaults. When an env var DAPPS_<KEY> is set, it wins;
        // otherwise the hardcoded fallback applies. Either way the value
        // is only written when no row exists — once configured (here or
        // via /Config), the row sticks and env vars stop mattering.
        InsertIfNotPresent(db, options, "NodeType", "BPQ", logger);
        InsertIfNotPresent(db, options, "NodeHost", "localhost", logger);
        InsertIfNotPresent(db, options, "AgwPort", "8000", logger);
        InsertIfNotPresent(db, options, "DefaultBpqPort", "0", logger);
        InsertIfNotPresent(db, options, "Callsign", PlaceholderCallsign, logger);
        InsertIfNotPresent(db, options, "MqttPort", "1883", logger);
        InsertIfNotPresent(db, options, "UdpListenPort", "0", logger);
        InsertIfNotPresent(db, options, "AuthRequired", "false", logger);
        InsertIfNotPresent(db, options, "UpdateCheckEnabled", "true", logger);
        InsertIfNotPresent(db, options, "RoutingAlgorithm", "passive-flood", logger);
        InsertIfNotPresent(db, options, "ProbingEnabled", "false", logger);
        InsertIfNotPresent(db, options, "ProbeIntervalHours", "24", logger);
        InsertIfNotPresent(db, options, "FragmentThresholdBytes", "4096", logger);
        InsertIfNotPresent(db, options, "FragmentReassemblyTimeoutSeconds", "604800", logger);
        InsertIfNotPresent(db, options, "OpportunisticPollEnabled", "true", logger);
        InsertIfNotPresent(db, options, "ScheduledPollEnabled", "false", logger);
        InsertIfNotPresent(db, options, "PollIntervalHours", "6", logger);
        InsertIfNotPresent(db, options, "DiscoveryAirtimeBudgetSecondsPerHour", "0", logger);
        InsertIfNotPresent(db, options, "ProbeStrategy", nameof(ProbeStrategy.FixedInterval), logger);
        InsertIfNotPresent(db, options, "ProbeOvernightStartHour", "2", logger);
        InsertIfNotPresent(db, options, "ProbeOvernightEndHour", "6", logger);
        InsertIfNotPresent(db, options, "ProbeQuietWindowSeconds", "300", logger);

        ValidateRequiredConfig(db, logger);

        logger?.LogInformation("DB schema refreshed");
    }

    private static void InsertIfNotPresent(SQLiteConnection db, List<DbSystemOption> options, string key, string defaultValue, ILogger? logger)
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
            logger?.LogInformation("Seeded {0} from {1}", key, envKey);
        }
    }

    /// <summary>
    /// Refuse to start on a misconfigured-or-missing callsign. The
    /// placeholder default is fine in tests but actively dangerous in
    /// the wild — frames go on the air with the configured callsign,
    /// and pretending to be N0CALL is both wrong and traceable to us.
    /// </summary>
    private static void ValidateRequiredConfig(SQLiteConnection db, ILogger? logger)
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
            logger?.LogError(msg);
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
}
