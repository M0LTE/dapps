using dapps.client;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;
using System.Text.Json;

namespace dapps.core.Services;

public class OptionsRepo
{
    internal async Task<ICollection<DbSystemOption>> GetOptions()
    {
        var connection = DbInfo.GetAsyncConnection();
        // Defensive: make sure the table exists before querying. When
        // the SystemOptions configurator fires during DI resolution it
        // can race ahead of DbStartup's schema creation; CreateTable is
        // idempotent (CREATE TABLE IF NOT EXISTS semantics) so this is
        // free in the normal path and only matters on the cold-start
        // race. Any rows DbStartup later inserts are still picked up by
        // subsequent reads.
        await connection.CreateTableAsync<DbSystemOption>();
        var rows = await connection.QueryAsync<DbSystemOption>("select * from systemoptions");
        return rows;
    }
}

public class Database(
    ILogger<Database> logger,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider? timeProviderOpt = null)
{
    // Default to the system clock when DI / tests don't supply one.
    // Lets the existing test-fixture call sites
    // (`new Database(NullLogger, opts)`) keep working — they get
    // production semantics without each test having to construct a
    // FakeTimeProvider. Cadence-sensitive tests inject one.
    private readonly TimeProvider timeProvider = timeProviderOpt ?? TimeProvider.System;
    internal async Task DeleteOffer(string id)
    {
        await DbInfo.GetAsyncConnection().DeleteAsync<DbOffer>(id);
    }

    public async Task<ICollection<DbMessage>> GetPendingOutboundMessages()
    {
        var connection = DbInfo.GetAsyncConnection();
        // Outbound = destined for a remote node and not yet forwarded.
        // "Local" matches when the @-suffix of Destination matches our base callsign.
        var local = options.CurrentValue.Callsign.Split('-')[0];
        var rows = await connection.QueryAsync<DbMessage>(
            "select * from messages where forwarded=0 and not (destination like ?);",
            $"%@{local}%");
        return rows;
    }

    /// <summary>Messages destined for a local app that haven't been ack'd yet.</summary>
    public async Task<ICollection<DbMessage>> GetUnacknowledgedLocalMessagesForApp(string appName)
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        // Destination shape is `app@call[-ssid]`; match exact app + local-callsign prefix.
        var prefix = $"{appName}@{local}";
        var rows = await connection.QueryAsync<DbMessage>(
            "select * from messages where locallydelivered=0 and (destination=? or destination like ?);",
            prefix, $"{prefix}-%");
        return rows;
    }

    public async Task MarkLocallyDelivered(string id)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync(
            "update messages set locallydelivered=1 where id=?", id);
    }

    /// <summary>
    /// Persist a fresh outbound message submitted by a local app via the MQTT
    /// or REST app interface. Computes the message id from the payload + a
    /// salt; returns the id for the caller to log/echo back to the app.
    ///
    /// <paramref name="ttlSeconds"/> is the residual lifetime the app
    /// requests for this message — propagates onto the outgoing
    /// <c>ihave</c> as <c>ttl=N</c>. Null means "no expiry", which makes
    /// the message persist in the queue indefinitely if it can't be
    /// forwarded; apps that want guaranteed cleanup should set a value.
    /// </summary>
    public async Task<string> SubmitOutboundMessage(string appName, string destCallsign, byte[] payload, int? ttlSeconds = null)
    {
        var salt = (long)(timeProvider.GetUtcNow().UtcDateTime - DateTime.UnixEpoch).TotalMilliseconds;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        var destination = $"{appName}@{destCallsign}";
        var ourCall = options.CurrentValue.Callsign;
        // Local submission: we are both the link-source AND the
        // originator. Recorded explicitly so re-forwards downstream
        // surface us in the receiver's dapps-origin user property.
        await SaveMessage(id, payload, salt, destination, sourceCallsign: ourCall, "{}", ttl: ttlSeconds, originatorCallsign: ourCall);
        return id;
    }

    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer, long? salt, string destination, string sourceCallsign, string additionalProperties, int? ttl, string originatorCallsign = "", byte? floodHopsRemaining = null, string? sourceRouteCsv = null, string? traversedHopsCsv = null)
    {
        var connection = DbInfo.GetAsyncConnection();

        var message = await connection.FindAsync<DbMessage>(id);

        if (message != null)
        {
            logger.LogWarning("Message {0} already exists, overwriting", id);
            await connection.DeleteAsync<DbMessage>(id);
        }

        await DbInfo.GetAsyncConnection().InsertAsync(new DbMessage
        {
            Id = id,
            Salt = salt,
            Payload = buffer,
            Destination = destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originatorCallsign,
            AdditionalProperties = additionalProperties,
            Ttl = ttl,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            FloodHopsRemaining = floodHopsRemaining,
            SourceRouteCsv = sourceRouteCsv,
            TraversedHopsCsv = traversedHopsCsv,
        });
    }

    internal async Task SaveOffer(IHaveOffer offer)
    {
        var connection = DbInfo.GetAsyncConnection();

        var existing = await connection.FindAsync<DbOffer>(offer.Id);
        if (existing != null)
        {
            logger.LogWarning("We already have metadata for offer {0}, overwriting", offer.Id);
            await connection.DeleteAsync<DbOffer>(offer.Id);
        }

        await connection.InsertAsync(new DbOffer
        {
            Id = offer.Id,
            Length = offer.Length,
            Format = offer.Format,
            Salt = offer.Salt,
            CompressedLength = offer.CompressedLength,
            Destination = offer.Destination,
            OriginatorCallsign = offer.Originator ?? "",
            AdditionalProperties = JsonSerializer.Serialize(offer.AdditionalHeaders),
            Ttl = offer.Ttl,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });

        logger.LogInformation("Saved metadata for offer {0}", offer.Id);
    }

    internal async Task<DbRouteHint?> GetRouteHint(string destination)
    {
        return await DbInfo.GetAsyncConnection().FindAsync<DbRouteHint>(destination);
    }

    internal async Task<DbNeighbour> GetNeighbour(string callsign)
    {
        return await DbInfo.GetAsyncConnection().FindWithQueryAsync<DbNeighbour>("select * from neighbours where callsign=?", callsign);
    }

    internal async Task MarkMessageAsForwarded(string id)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync("update messages set forwarded=1 where id=?", id);
    }

    internal async Task DeleteMessage(string id)
    {
        await DbInfo.GetAsyncConnection().DeleteAsync<DbMessage>(id);
    }

    /// <summary>
    /// Soft-delete: copy the message into <c>dropped_messages</c> with
    /// the given reason + a now() timestamp, then remove from
    /// <c>messages</c>. Idempotent — a missing row is a no-op (it might
    /// have been concurrently dropped). Used by the forwarder and TTL
    /// sweeper instead of <see cref="DeleteMessage"/> when discarding
    /// rather than acknowledging.
    /// </summary>
    internal async Task SoftDeleteMessage(string id, string reason)
    {
        var c = DbInfo.GetAsyncConnection();
        var row = await c.FindAsync<DbMessage>(id);
        if (row is null) return;
        await c.InsertAsync(new DbDroppedMessage
        {
            Id = row.Id,
            Payload = row.Payload,
            Salt = row.Salt,
            Destination = row.Destination,
            SourceCallsign = row.SourceCallsign,
            AdditionalProperties = row.AdditionalProperties,
            Forwarded = row.Forwarded,
            LocallyDelivered = row.LocallyDelivered,
            Ttl = row.Ttl,
            CreatedAt = row.CreatedAt,
            DroppedAt = timeProvider.GetUtcNow().UtcDateTime,
            Reason = reason,
        });
        await c.DeleteAsync<DbMessage>(id);
    }

    public async Task<IReadOnlyList<DbDroppedMessage>> GetRecentDroppedMessages(int limit = 50)
    {
        var c = DbInfo.GetAsyncConnection();
        var rows = await c.QueryAsync<DbDroppedMessage>(
            "select * from dropped_messages order by DroppedAt desc limit ?", limit);
        return rows.ToList();
    }

    /// <summary>
    /// Soft-delete every message whose TTL has elapsed. Hard-delete
    /// every offer whose TTL has elapsed (offers are protocol-level
    /// scaffolding — there's no audit value in keeping a "we never
    /// got the data for that offer" row around). Returns the count of
    /// rows actioned across both tables.
    /// </summary>
    internal async Task<int> DeleteExpired(DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();

        // SQLite-net stores DateTime as ticks. CreatedAt + ttl seconds < now.
        // We can't do "+ ttl seconds" portably in SQL, so do the comparison
        // in C# after pulling the candidate rows. Both tables are small.
        var expiredOffers = (await connection.QueryAsync<DbOffer>(
                "select * from offers where Ttl is not null"))
            .Where(o => TtlMath.HasExpired(o.Ttl, o.CreatedAt, now))
            .ToList();
        foreach (var offer in expiredOffers)
        {
            await connection.DeleteAsync<DbOffer>(offer.Id);
        }

        var expiredMessages = (await connection.QueryAsync<DbMessage>(
                "select * from messages where Ttl is not null"))
            .Where(m => TtlMath.HasExpired(m.Ttl, m.CreatedAt, now))
            .ToList();
        foreach (var message in expiredMessages)
        {
            await SoftDeleteMessage(message.Id, "ttl-expired");
        }

        return expiredOffers.Count + expiredMessages.Count;
    }

    internal async Task<ICollection<DbNeighbour>> GetNeighbours()
    {
        return await DbInfo.GetAsyncConnection().QueryAsync<DbNeighbour>("select * from neighbours");
    }

    /// <summary>
    /// Most-recent messages across both local and remote destinations,
    /// newest first. Used by the dashboard to give a sysop a live view
    /// of activity without paging through the whole queue.
    /// </summary>
    public async Task<IReadOnlyList<DbMessage>> GetRecentMessages(int limit)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbMessage>(
            "select * from messages order by CreatedAt desc limit ?", limit);
    }

    /// <summary>Total message-table row count, for dashboard summaries.</summary>
    public async Task<int> CountMessages()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteScalarAsync<int>("select count(*) from messages");
    }

    /// <summary>Pending outbound rows — messages destined for a remote
    /// node that haven't yet been forwarded.</summary>
    public async Task<int> CountPendingOutbound()
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from messages where forwarded=0 and not (destination like ?);",
            $"%@{local}%");
    }

    /// <summary>Undelivered local rows — messages destined for a local
    /// app that haven't been ack'd by the app yet.</summary>
    public async Task<int> CountUndeliveredLocal()
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from messages where locallydelivered=0 and (destination like ?);",
            $"%@{local}%");
    }

    /// <summary>
    /// Insert or refresh a discovered-peer row. Keyed on
    /// (callsign, bearer, channel-key) so the same peer heard on
    /// multiple channels occupies multiple rows. Idempotent — beacons
    /// re-arrive every cadence and just bump
    /// <see cref="DbDiscoveredPeer.LastSeen"/>.
    /// </summary>
    internal async Task UpsertDiscoveredPeer(DbDiscoveredPeer peer)
    {
        peer.PeerKey = DbDiscoveredPeer.MakeKey(peer.Callsign, peer.Bearer, peer.ChannelKey);
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbDiscoveredPeer>(peer.PeerKey);
        if (existing is null)
        {
            await connection.InsertAsync(peer);
        }
        else
        {
            await connection.UpdateAsync(peer);
        }
    }

    /// <summary>Insert a new <see cref="DbDiscoveryChannel"/> row, or
    /// update the bearer-specific tunables on an existing one. Identity
    /// is (Bearer, ChannelKey) — same channel re-POSTed updates in
    /// place. Channel defaults are filled from <see cref="LinkClass"/>
    /// before persisting.</summary>
    internal async Task UpsertDiscoveryChannel(DbDiscoveryChannel channel)
    {
        channel.ApplyClassDefaults();
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindWithQueryAsync<DbDiscoveryChannel>(
            "select * from discoverychannels where Bearer=? and ChannelKey=?",
            channel.Bearer, channel.ChannelKey);
        if (existing is null)
        {
            await connection.InsertAsync(channel);
        }
        else
        {
            channel.Id = existing.Id;
            await connection.UpdateAsync(channel);
        }
    }

    public async Task<IReadOnlyList<DbDiscoveryChannel>> GetDiscoveryChannels()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveryChannel>(
            "select * from discoverychannels order by CostHint, Bearer, ChannelKey");
    }

    internal async Task<bool> RemoveDiscoveryChannel(int id)
    {
        var connection = DbInfo.GetAsyncConnection();
        var deleted = await connection.ExecuteAsync(
            "delete from discoverychannels where Id=?", id);
        return deleted > 0;
    }

    /// <summary>List all currently-known discovered peers, irrespective
    /// of staleness. The dashboard / debugger uses this; the routing
    /// resolver should age out before consulting.</summary>
    public async Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeers()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveredPeer>(
            "select * from discoveredpeers order by LastSeen desc");
    }

    /// <summary>Remove discovered-peer rows whose freshness window has
    /// elapsed. Returns the number of rows deleted.</summary>
    internal async Task<int> AgeOutDiscoveredPeers(DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var stale = (await connection.QueryAsync<DbDiscoveredPeer>(
                "select * from discoveredpeers"))
            .Where(p => (now - p.LastSeen).TotalSeconds > p.TtlSeconds)
            .ToList();
        foreach (var p in stale)
        {
            await connection.DeleteAsync<DbDiscoveredPeer>(p.PeerKey);
        }
        return stale.Count;
    }

    /// <summary>
    /// Insert a neighbour or update its bearer hints if one already
    /// exists for the same callsign. Idempotent: callers can re-POST
    /// the same neighbour without checking for prior existence.
    /// </summary>
    internal async Task UpsertNeighbour(string callsign, int? bpqPort, string? udpEndpoint = null)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindWithQueryAsync<DbNeighbour>(
            "select * from neighbours where callsign=?", callsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbNeighbour
            {
                Callsign = callsign,
                BpqPort = bpqPort,
                UdpEndpoint = udpEndpoint,
            });
        }
        else
        {
            existing.BpqPort = bpqPort;
            existing.UdpEndpoint = udpEndpoint;
            await connection.UpdateAsync(existing);
        }
    }

    /// <summary>
    /// Remove a neighbour by callsign. Returns true if a row was deleted,
    /// false if no neighbour existed with that callsign.
    /// </summary>
    internal async Task<bool> RemoveNeighbour(string callsign)
    {
        var deleted = await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from neighbours where callsign=?", callsign);
        return deleted > 0;
    }

    // ── Passive-learning routes (PR-B) ──────────────────────────────

    /// <summary>
    /// Record (or refresh) a learned route for the given destination.
    /// One row per destination base callsign — newer observations
    /// overwrite older ones. <see cref="DbLearnedRoute.LastSeenAt"/>
    /// always bumps to <c>now</c>; failure counter resets when the
    /// next-hop changes (a new path is fresh evidence of liveness).
    /// </summary>
    internal async Task UpsertLearnedRouteAsync(string destinationBaseCallsign, string nextHopCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbLearnedRoute
            {
                DestinationBaseCallsign = destinationBaseCallsign,
                NextHopCallsign = nextHopCallsign,
                LastSeenAt = now,
                LastUsedAt = DateTime.MinValue,
                ConsecutiveFailures = 0,
            });
            return;
        }

        existing.LastSeenAt = now;
        if (!string.Equals(existing.NextHopCallsign, nextHopCallsign, StringComparison.OrdinalIgnoreCase))
        {
            existing.NextHopCallsign = nextHopCallsign;
            existing.ConsecutiveFailures = 0;
        }
        await connection.UpdateAsync(existing);
    }

    /// <summary>Look up the current learned route, if any.</summary>
    internal async Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationBaseCallsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbLearnedRoute>(destinationBaseCallsign);

    /// <summary>Forward succeeded — reset failure counter, update <see cref="DbLearnedRoute.LastUsedAt"/>.</summary>
    internal async Task RecordLearnedRouteSuccessAsync(string destinationBaseCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (row is null) return;
        row.ConsecutiveFailures = 0;
        row.LastUsedAt = now;
        await connection.UpdateAsync(row);
    }

    /// <summary>Forward failed — increment failure counter; delete the
    /// row if the threshold is hit. Returns the new failure count, or
    /// <c>-1</c> if the row was deleted (i.e. invalidated).</summary>
    internal async Task<int> RecordLearnedRouteFailureAsync(string destinationBaseCallsign, int invalidationThreshold)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (row is null) return 0;
        row.ConsecutiveFailures++;
        if (row.ConsecutiveFailures >= invalidationThreshold)
        {
            await connection.DeleteAsync<DbLearnedRoute>(destinationBaseCallsign);
            return -1;
        }
        await connection.UpdateAsync(row);
        return row.ConsecutiveFailures;
    }

    /// <summary>All current learned routes — for dashboard / debug.</summary>
    public async Task<IReadOnlyList<DbLearnedRoute>> GetLearnedRoutesAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbLearnedRoute>("select * from learnedroutes order by LastSeenAt desc");
    }

    // ── Flood deduplication (PR-C) ──────────────────────────────────

    /// <summary>
    /// Have we already processed a flood with this (id, link-source)
    /// pair? Returns true if the row exists. The lookup is cheap
    /// (primary-key-indexed); used on every inbound flooded message
    /// to short-circuit duplicates before any expensive processing.
    /// </summary>
    internal async Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbFloodSeen.MakeKey(messageId, linkSourceCallsign);
        var row = await connection.FindAsync<DbFloodSeen>(key);
        return row is not null;
    }

    /// <summary>Idempotent record-this-flood. No-ops if the row
    /// already exists.</summary>
    internal async Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbFloodSeen.MakeKey(messageId, linkSourceCallsign);
        var existing = await connection.FindAsync<DbFloodSeen>(key);
        if (existing is not null) return;
        await connection.InsertAsync(new DbFloodSeen { Key = key, SeenAt = now });
    }

    /// <summary>Drop flood-seen rows older than <paramref name="cutoff"/>.
    /// The dedup window must outlive the maximum expected flood
    /// propagation time; everything older than that is just memory
    /// pressure.</summary>
    internal async Task<int> SweepFloodSeenAsync(DateTime cutoff)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteAsync(
            "delete from flood_seen where SeenAt < ?", cutoff.Ticks);
    }

    // ── Discovered paths (MeshCore-flavoured algorithm) ─────────────

    /// <summary>
    /// Record (or refresh) the intermediate-hop path to a destination.
    /// The most-recent observation wins; failure counter resets when
    /// the path itself changes (a different intermediate set is fresh
    /// evidence of liveness over the new path).
    /// </summary>
    internal async Task UpsertDiscoveredPathAsync(string destinationBaseCallsign, IReadOnlyList<string> intermediates, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var csv = DbDiscoveredPath.ToCsv(intermediates);
        var existing = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbDiscoveredPath
            {
                DestinationBaseCallsign = destinationBaseCallsign,
                IntermediatesCsv = csv,
                LastSeenAt = now,
                LastUsedAt = DateTime.MinValue,
                ConsecutiveFailures = 0,
            });
            return;
        }

        existing.LastSeenAt = now;
        if (!string.Equals(existing.IntermediatesCsv, csv, StringComparison.OrdinalIgnoreCase))
        {
            existing.IntermediatesCsv = csv;
            existing.ConsecutiveFailures = 0;
        }
        await connection.UpdateAsync(existing);
    }

    internal async Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationBaseCallsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbDiscoveredPath>(destinationBaseCallsign);

    internal async Task RecordDiscoveredPathSuccessAsync(string destinationBaseCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (row is null) return;
        row.ConsecutiveFailures = 0;
        row.LastUsedAt = now;
        await connection.UpdateAsync(row);
    }

    internal async Task<int> RecordDiscoveredPathFailureAsync(string destinationBaseCallsign, int invalidationThreshold)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (row is null) return 0;
        row.ConsecutiveFailures++;
        if (row.ConsecutiveFailures >= invalidationThreshold)
        {
            await connection.DeleteAsync<DbDiscoveredPath>(destinationBaseCallsign);
            return -1;
        }
        await connection.UpdateAsync(row);
        return row.ConsecutiveFailures;
    }

    /// <summary>All current discovered paths — for dashboard / debug.</summary>
    public async Task<IReadOnlyList<DbDiscoveredPath>> GetDiscoveredPathsAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveredPath>("select * from discoveredpaths order by LastSeenAt desc");
    }

    internal async Task SaveSystemOptions(SystemOptions systemOptions)
    {
        var connection = DbInfo.GetAsyncConnection();

        var options = await connection.QueryAsync<DbSystemOption>("select * from systemoptions;");

        await Upsert(connection, options, systemOptions.NodeHost, nameof(systemOptions.NodeHost));
        await Upsert(connection, options, systemOptions.AgwPort.ToString(), nameof(systemOptions.AgwPort));
        await Upsert(connection, options, systemOptions.DefaultBpqPort.ToString(), nameof(systemOptions.DefaultBpqPort));
        await Upsert(connection, options, systemOptions.Callsign, nameof(systemOptions.Callsign));
        await Upsert(connection, options, systemOptions.MqttPort.ToString(), nameof(systemOptions.MqttPort));
        await Upsert(connection, options, systemOptions.UpdateCheckEnabled.ToString(), nameof(systemOptions.UpdateCheckEnabled));
        await Upsert(connection, options, systemOptions.RoutingAlgorithm, nameof(systemOptions.RoutingAlgorithm));
        await Upsert(connection, options, systemOptions.ProbingEnabled.ToString(), nameof(systemOptions.ProbingEnabled));
        await Upsert(connection, options, systemOptions.ProbeIntervalHours.ToString(), nameof(systemOptions.ProbeIntervalHours));
    }

    private static async Task Upsert(SQLiteAsyncConnection connection, List<DbSystemOption> options, string value, string field)
    {
        if (options.Any(o => string.Equals(o.Option, field, StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("update systemoptions set value=? where option=?", value, field);
        }
        else
        {
            await connection.InsertAsync(new DbSystemOption { Option = field, Value = value });
        }
    }

    internal async Task<SystemOptions> GetSystemOptions()
    {
        var connection = DbInfo.GetAsyncConnection();
        var options = (await connection.QueryAsync<DbSystemOption>("select * from systemoptions;")).ToDictionary(item => item.Option, item => item.Value);
        return new SystemOptions
        {
            NodeHost = options[nameof(SystemOptions.NodeHost)],
            AgwPort = int.Parse(options[nameof(SystemOptions.AgwPort)]),
            DefaultBpqPort = int.Parse(options[nameof(SystemOptions.DefaultBpqPort)]),
            Callsign = options[nameof(SystemOptions.Callsign)],
            MqttPort = int.Parse(options[nameof(SystemOptions.MqttPort)]),
            UpdateCheckEnabled = !options.TryGetValue(nameof(SystemOptions.UpdateCheckEnabled), out var uce)
                || !bool.TryParse(uce, out var uceParsed) || uceParsed,
            RoutingAlgorithm = options.TryGetValue(nameof(SystemOptions.RoutingAlgorithm), out var ra) && !string.IsNullOrEmpty(ra)
                ? ra
                : "passive-flood",
            ProbingEnabled = options.TryGetValue(nameof(SystemOptions.ProbingEnabled), out var pe)
                && bool.TryParse(pe, out var peParsed) && peParsed,
            ProbeIntervalHours = options.TryGetValue(nameof(SystemOptions.ProbeIntervalHours), out var pih)
                && int.TryParse(pih, out var pihParsed) && pihParsed > 0
                ? pihParsed
                : 24,
        };
    }

    // ── Probed nodes (B6.1) ──────────────────────────────────────────

    /// <summary>List every <see cref="DbProbedNode"/> row, newest probe first.</summary>
    public async Task<IReadOnlyList<DbProbedNode>> GetProbedNodes()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbProbedNode>(
            "select * from probednodes order by " +
            "case when LastProbedAt is null then 1 else 0 end, " +
            "LastProbedAt desc, Callsign asc");
    }

    public async Task<DbProbedNode?> GetProbedNode(string callsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbProbedNode>(callsign);

    /// <summary>Idempotent upsert of a probed-node row. Used both by the
    /// scheduler when recording results, and by the controller when an
    /// operator pre-creates an opt-out entry.</summary>
    internal async Task UpsertProbedNode(DbProbedNode node)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbProbedNode>(node.Callsign);
        if (existing is null)
        {
            await connection.InsertAsync(node);
        }
        else
        {
            await connection.UpdateAsync(node);
        }
    }

    /// <summary>Remove a probed-node row by callsign. Returns true if a
    /// row was actually deleted.</summary>
    internal async Task<bool> RemoveProbedNode(string callsign)
    {
        var deleted = await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from probednodes where callsign=?", callsign);
        return deleted > 0;
    }
}