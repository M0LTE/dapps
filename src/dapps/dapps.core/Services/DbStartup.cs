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
    /// <summary>Sentinel callsign in the seeded defaults. The daemon
    /// starts with this in place but refuses to operate (won't bind
    /// the inbound bearer, won't forward outbound, /Health reports
    /// degraded with <c>setupRequired</c>) until the operator configures
    /// a real callsign via /Setup or /Config. Frames stamped with the
    /// placeholder never go on the air.</summary>
    public const string PlaceholderCallsign = "N0CALL";

    /// <summary>Option key holding the SSID used when deriving this
    /// instance's callsign from the host node's callsign (see
    /// <see cref="NodeCallsignEnvVar"/>). Seeded to <see cref="DefaultSsid"/>;
    /// env-overridable as <c>DAPPS_SSID</c> like every other seeded
    /// option. Only consulted at derivation time - a stored or
    /// env-supplied <c>Callsign</c> always wins.</summary>
    public const string SsidOptionKey = "Ssid";

    /// <summary>Proposed convention: DAPPS lives at SSID -7 of the host
    /// node's callsign (matches the M0LTE-7 acceptance-test identity).
    /// There is no packet-wide DAPPS SSID convention yet; this default
    /// is the proposal.</summary>
    public const string DefaultSsid = "7";

    /// <summary>Injected by a pdn host into supervised app processes:
    /// the node's own callsign text, e.g. <c>M9YYY</c> (may carry an
    /// SSID, which we strip before composing). Absent when DAPPS runs
    /// standalone alongside BPQ/XRouter.</summary>
    public const string NodeCallsignEnvVar = "PDN_NODE_CALLSIGN";

    /// <summary>
    /// Every option key EnsureSchemaAndSeed seeds, with its hardcoded
    /// fallback default. Single source of truth for seeding, for the
    /// per-start env application, and for the dashboard's
    /// "managed by environment" markers.
    /// </summary>
    private static readonly (string Key, string Default)[] SeededOptions =
    [
        ("NodeHost", "localhost"),
        ("AgwPort", "8000"),
        ("DefaultBearerPort", "0"),
        ("Callsign", PlaceholderCallsign),
        ("MqttPort", "1883"),
        ("UdpListenPort", "0"),
        ("AuthRequired", "false"),
        ("UpdateCheckEnabled", "true"),
        ("RoutingAlgorithm", "passive-flood"),
        ("ProbingEnabled", "false"),
        ("ProbeIntervalHours", "24"),
        ("FragmentThresholdBytes", "4096"),
        ("FragmentReassemblyTimeoutSeconds", "604800"),
        ("RouteGossipStalenessHours", "6"),
        ("OpportunisticPollEnabled", "true"),
        ("ScheduledPollEnabled", "false"),
        ("PollIntervalHours", "6"),
        ("DiscoveryAirtimeBudgetSecondsPerHour", "0"),
        ("ProbeStrategy", nameof(Models.ProbeStrategy.FixedInterval)),
        ("ProbeOvernightStartHour", "2"),
        ("ProbeOvernightEndHour", "6"),
        ("ProbeQuietWindowSeconds", "300"),
        ("HeartbeatEnabled", "true"),
        ("HeartbeatIntervalSeconds", "60"),
        ("AutoDiscoverViaNodeCall", "false"),
        ("NodePromptApplicationCommand", "DAPPS"),
        ("NodeBearer", "agw"),
        ("RhpPort", "9000"),
        ("RhpUser", ""),
        ("RhpPass", ""),
        (SsidOptionKey, DefaultSsid),
    ];

    /// <summary>
    /// Create every table the daemon needs, seed the first-run
    /// systemoptions defaults (env-var overrides → hardcoded fallback),
    /// and re-apply any env-set values to existing rows (deployment-
    /// managed config). Safe to call multiple times - every step is
    /// idempotent.
    ///
    /// Called once from Program.cs *before* <c>builder.Build()</c> so
    /// the eager DI materialisation of hosted services (which transit
    /// IRoutingAlgorithm → IOptionsMonitor&lt;SystemOptions&gt;.CurrentValue
    /// → <see cref="SystemOptionsStore"/>'s constructor read) finds a
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
        db.CreateTable<DbTransmission>();
        db.CreateTable<DbStreamSendState>();
        db.CreateTable<DbStreamRecvState>();
        db.CreateTable<DbRouteGossipState>();

        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");

        // Seeded defaults. When an env var DAPPS_<KEY> is set, it wins -
        // on EVERY start, not just the first: a set env var is
        // deployment-managed config (the pdn supervised-app case) and is
        // re-applied over whatever the row holds. An UNSET env var never
        // touches an existing row, so the standalone flow (no DAPPS_*
        // env; configure via /Setup // /Config) is unchanged, and a
        // standalone operator who seeds once via env then unsets it
        // keeps dashboard control exactly as before.
        foreach (var (key, defaultValue) in SeededOptions)
        {
            SeedOrApplyEnv(db, options, key, defaultValue, logger);
        }

        DeriveCallsignFromHostNodeIfUnset(db, logger);

        ValidateRequiredConfig(db, logger);

        logger?.LogInformation("DB schema refreshed");
    }

    private static void SeedOrApplyEnv(SQLiteConnection db, List<DbSystemOption> options, string key, string defaultValue, ILogger? logger)
    {
        var envKey = EnvVarFor(key);
        var envValue = Environment.GetEnvironmentVariable(envKey);

        var existing = options.FirstOrDefault(o => string.Equals(o.Option, key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var value = string.IsNullOrEmpty(envValue) ? defaultValue : envValue;
            db.Insert(new DbSystemOption { Option = key, Value = value });

            if (!string.IsNullOrEmpty(envValue))
            {
                logger?.LogInformation("Seeded {0} from {1}", key, envKey);
            }
            return;
        }

        if (!string.IsNullOrEmpty(envValue) && !string.Equals(existing.Value, envValue, StringComparison.Ordinal))
        {
            existing.Value = envValue;
            db.Update(existing);
            logger?.LogInformation(
                "SystemOption {Key} applied from environment ({EnvVar}) — this value is deployment-managed; " +
                "dashboard edits will be overridden while the variable remains set",
                key, envKey);
        }
    }

    /// <summary>
    /// "DAPPS resides at an SSID of the node callsign": when DAPPS runs
    /// supervised under a pdn node, the host injects
    /// <see cref="NodeCallsignEnvVar"/> and we derive
    /// <c>&lt;base-of-node-call&gt;-&lt;Ssid&gt;</c> as the callsign -
    /// but only while the stored callsign is absent or still the
    /// <see cref="PlaceholderCallsign"/> placeholder. An explicit
    /// <c>DAPPS_CALLSIGN</c> env var or a real stored callsign always
    /// wins over derivation. Standalone installs (no PDN_NODE_CALLSIGN)
    /// are untouched.
    /// </summary>
    private static void DeriveCallsignFromHostNodeIfUnset(SQLiteConnection db, ILogger? logger)
    {
        // An explicit DAPPS_CALLSIGN wins over derivation. (It was
        // already applied above; this guard also keeps a pathological
        // DAPPS_CALLSIGN=N0CALL from being re-derived underneath.)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarFor("Callsign"))))
        {
            return;
        }

        var options = db.Query<DbSystemOption>("select * from systemoptions;");
        var callsignRow = options.FirstOrDefault(
            o => string.Equals(o.Option, "Callsign", StringComparison.OrdinalIgnoreCase));
        var stored = callsignRow?.Value ?? "";
        var unset = string.IsNullOrWhiteSpace(stored)
            || string.Equals(stored, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        if (!unset)
        {
            return; // a real stored callsign always wins
        }

        var ssidRow = options.FirstOrDefault(
            o => string.Equals(o.Option, SsidOptionKey, StringComparison.OrdinalIgnoreCase));
        var ssid = string.IsNullOrWhiteSpace(ssidRow?.Value) ? DefaultSsid : ssidRow!.Value.Trim();
        var derived = DeriveCallsignFromHostNode(ssid);
        if (derived is null)
        {
            return; // not pdn-hosted - standalone setup-required flow as before
        }

        if (callsignRow is null)
        {
            db.Insert(new DbSystemOption { Option = "Callsign", Value = derived });
        }
        else
        {
            callsignRow.Value = derived;
            db.Update(callsignRow);
        }

        logger?.LogInformation(
            "Callsign {Derived} derived from the host node ({EnvVar}={NodeCall}, SSID {Ssid}). " +
            "Set DAPPS_CALLSIGN or configure a callsign via the dashboard to pin a different identity.",
            derived, NodeCallsignEnvVar, Environment.GetEnvironmentVariable(NodeCallsignEnvVar), ssid);
    }

    /// <summary>Compose the conventional pdn-hosted DAPPS callsign:
    /// base of the host node's callsign (any SSID stripped, upper-cased)
    /// + <paramref name="ssid"/>. Null when <see cref="NodeCallsignEnvVar"/>
    /// is not set or empty (standalone install).</summary>
    public static string? DeriveCallsignFromHostNode(string ssid)
    {
        var nodeCall = Environment.GetEnvironmentVariable(NodeCallsignEnvVar);
        if (string.IsNullOrWhiteSpace(nodeCall))
        {
            return null;
        }

        var baseCall = nodeCall.Trim().ToUpperInvariant().Split('-')[0].Trim();
        if (baseCall.Length == 0)
        {
            return null;
        }

        return $"{baseCall}-{ssid}";
    }

    /// <summary>Overload reading the stored Ssid option (fallback
    /// <see cref="DefaultSsid"/>). Used by /Setup to prefill the
    /// callsign field when the daemon is pdn-hosted and still in
    /// setup-required mode.</summary>
    public static string? DeriveCallsignFromHostNode()
    {
        using var db = DbInfo.GetConnection();
        db.CreateTable<DbSystemOption>();
        var ssidRow = db.Query<DbSystemOption>("select * from systemoptions;")
            .FirstOrDefault(o => string.Equals(o.Option, SsidOptionKey, StringComparison.OrdinalIgnoreCase));
        var ssid = string.IsNullOrWhiteSpace(ssidRow?.Value) ? DefaultSsid : ssidRow!.Value.Trim();
        return DeriveCallsignFromHostNode(ssid);
    }

    /// <summary>The env var that overrides the given seeded option key,
    /// e.g. <c>NodeHost</c> → <c>DAPPS_NODE_HOST</c>.</summary>
    public static string EnvVarFor(string key) => "DAPPS_" + ToScreamingSnake(key);

    /// <summary>True when the given option key's <c>DAPPS_*</c> env var
    /// is currently set (non-empty) - i.e. the value is deployment-
    /// managed and re-applied at every start.</summary>
    public static bool IsEnvManaged(string key) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarFor(key)));

    /// <summary>Seeded option keys whose env var is currently set, for
    /// the dashboard's "managed by environment" field markers.</summary>
    public static IReadOnlyList<string> EnvManagedKeys() =>
        SeededOptions.Select(s => s.Key).Where(IsEnvManaged).ToArray();

    /// <summary>
    /// Warn if the callsign is the seeded placeholder. The daemon starts
    /// either way - inbound bearer services and the outbound forwarder
    /// gate themselves on a real callsign at runtime, and /Health reports
    /// <c>setupRequired</c> until the operator configures one via the
    /// dashboard's /Setup or /Config form. Letting the daemon start with
    /// the placeholder is what makes "drop the binary, run the systemd
    /// unit, configure in the browser" possible.
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
            logger?.LogWarning(
                "Callsign is not configured (currently '{0}'). The daemon is in setup-required mode: " +
                "open the dashboard's /Setup page to configure a callsign and bearer. Inbound bearer " +
                "services and the outbound forwarder will not bind/transmit until then.",
                callsign);
        }
    }

    /// <summary>
    /// Convert a PascalCase or camelCase identifier to SCREAMING_SNAKE_CASE
    /// for use as an environment-variable suffix. <c>NodeHost</c> →
    /// <c>NODE_HOST</c>; <c>DefaultBearerPort</c> → <c>DEFAULT_BEARER_PORT</c>.
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
