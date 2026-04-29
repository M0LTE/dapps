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
        await SaveMessage(id, payload, salt, destination, "{}");
        return id;
    }

    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer, long? salt, string destination, string additionalProperties)
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
            AdditionalProperties = additionalProperties
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