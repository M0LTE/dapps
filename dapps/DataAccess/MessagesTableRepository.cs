using Dapper;

namespace dapps.DataAccess;

internal class MessagesTableRepository
{
    private readonly DbConnectionFactory dbConnectionFactory;

    public MessagesTableRepository(DbConnectionFactory dbConnectionFactory)
    {
        this.dbConnectionFactory = dbConnectionFactory;
    }

    internal Task Save(DateTime timestamp, string sourceCall, string appName, byte[] payload)
    {
        using var connection = dbConnectionFactory.GetDbConnection();

        return connection.ExecuteAsync("INSERT INTO messages (datetime, sourceCall, appName, payload) VALUES (@datetime, @sourceCall, @appName, @payload);",
            new
            {
                datetime = timestamp,
                sourceCall,
                appName,
                payload
            });
    }
}