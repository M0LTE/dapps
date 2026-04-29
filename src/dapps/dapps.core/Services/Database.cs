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
        var rows = await connection.QueryAsync<DbSystemOption>("select * from systemoptions");
        return rows;
    }
}

public class Database(ILogger<Database> logger, IOptionsMonitor<SystemOptions> options)
{
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
    /// </summary>
    public async Task<string> SubmitOutboundMessage(string appName, string destCallsign, byte[] payload)
    {
        var salt = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        var destination = $"{appName}@{destCallsign}";
        var ourCall = options.CurrentValue.Callsign;
        await SaveMessage(id, payload, salt, destination, sourceCallsign: ourCall, "{}", ttl: null);
        return id;
    }

    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer, long? salt, string destination, string sourceCallsign, string additionalProperties, int? ttl)
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
            AdditionalProperties = additionalProperties,
            Ttl = ttl,
            CreatedAt = DateTime.UtcNow,
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
            AdditionalProperties = JsonSerializer.Serialize(offer.AdditionalHeaders),
            Ttl = offer.Ttl,
            CreatedAt = DateTime.UtcNow,
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
    /// Delete every message and offer whose ttl has elapsed. Returns the
    /// total row count removed. Rows with no ttl set are left alone.
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
            await connection.DeleteAsync<DbMessage>(message.Id);
        }

        return expiredOffers.Count + expiredMessages.Count;
    }

    internal async Task<ICollection<DbNeighbour>> GetNeighbours()
    {
        return await DbInfo.GetAsyncConnection().QueryAsync<DbNeighbour>("select * from neighbours");
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
        };
    }
}