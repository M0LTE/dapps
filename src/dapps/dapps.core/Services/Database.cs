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
        var rows = await connection.QueryAsync<DbMessage>("select * from messages where destination != ? and forwarded=0;", options.CurrentValue.Callsign.Split('-')[0]);
        return rows;
    }

    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer, long? timestamp, string destination, string additionalProperties)
    {
        var connection = DbInfo.GetAsyncConnection();

        var offer = connection.GetAsync<DbOffer>(id);

        var message = await connection.FindAsync<DbMessage>(id);

        if (message != null)
        {
            logger.LogWarning("Message {0} already exists, overwriting", id);
            await connection.DeleteAsync<DbMessage>(id);
        }

        await DbInfo.GetAsyncConnection().InsertAsync(new DbMessage
        {
            Id = id,
            Timestamp = timestamp,
            Payload = buffer,
            Destination = destination,
            AdditionalProperties = additionalProperties
        });
    }

    internal async Task SaveOfferMetadata(string id, Dictionary<string, string> kvps)
    {
        var connection = DbInfo.GetAsyncConnection();

        var offer = await connection.FindAsync<DbOffer>(id);
        if (offer != null)
        {
            logger.LogWarning("We already have metadata for offer {0}, overwriting", id);
            await connection.DeleteAsync<DbOffer>(id);
        }

        await connection.InsertAsync(new DbOffer
        {
            Id = id,
            Length = int.Parse(kvps["len"]),
            Format = kvps["fmt"],
            Timestamp = kvps.TryGetValue("ts", out string? value) ? long.Parse(value) : null,
            Destination = kvps["dst"],
            AdditionalProperties = JsonSerializer.Serialize(kvps.Keys.Except(["ts", "chk", "dst", "fmt", "len"]).ToDictionary(k => k, k => kvps[k]))
        });

        logger.LogInformation("Saved metadata for offer {0}", id);
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
        
        await Upsert(connection, options, systemOptions.FbbPort.ToString(), nameof(systemOptions.FbbPort));
        await Upsert(connection, options, systemOptions.FbbPassword, nameof(systemOptions.FbbPassword));
        await Upsert(connection, options, systemOptions.FbbUser, nameof(systemOptions.FbbUser));
        await Upsert(connection, options, systemOptions.NodeHost, nameof(systemOptions.NodeHost));
        await Upsert(connection, options, systemOptions.Callsign, nameof(systemOptions.Callsign));
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
            FbbPort = int.Parse(options[nameof(SystemOptions.FbbPort)]),
            FbbPassword = options[nameof(SystemOptions.FbbPassword)],
            FbbUser = options[nameof(SystemOptions.FbbUser)],
            NodeHost = options[nameof(SystemOptions.NodeHost)],
            Callsign = options[nameof(SystemOptions.Callsign)]
        };
    }
}