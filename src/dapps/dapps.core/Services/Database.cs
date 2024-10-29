
using dapps.core.Models;

namespace dapps.core.Services;

public class Database(ILogger<Database> logger)
{
    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer)
    {
        var connection = DbInfo.GetAsyncConnection();

        var offer = connection.GetAsync<DbOffer>(id);

        await DbInfo.GetAsyncConnection().InsertAsync(new DbMessage
        {
            Id = id,
            Payload = buffer
        });
    }

    internal async Task SaveOfferMetadata(string id, Dictionary<string, string> kvps)
    {
        await DbInfo.GetAsyncConnection().InsertAsync(new DbOffer
        {
            Id = id,
            Length = int.Parse(kvps["len"]),
            Format = kvps["fmt"],
            Timestamp = kvps.ContainsKey("ts") ? long.Parse(kvps["ts"]) : null
        });

        logger.LogInformation("Saved metadata for offer {0}", id);
    }
}