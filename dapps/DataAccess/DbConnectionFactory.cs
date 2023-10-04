using Dapper;
using System.Data;
using System.Data.SQLite;

namespace dapps.DataAccess;

internal class DbConnectionFactory
{
    private readonly ILogger<DbConnectionFactory> logger;

    public DbConnectionFactory(ILogger<DbConnectionFactory> logger)
    {
        this.logger = logger;
    }

    public IDbConnection GetDbConnection()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "dapps.sqlite");
        var cs = $"data source={path}";

        logger.LogInformation("path:{path}", path);
        logger.LogInformation("cs:{cs}", cs);
        var connection = new SQLiteConnection(cs, true);
        connection.Open();
        return connection;
    }

    public async Task SetupTables()
    {
        using var connection = GetDbConnection();

        await connection.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS messages ( 
              id integer not null primary key autoincrement,
              datetime not null default current_timestamp,
              sourceCall text not null,
              appName text not null,
              payload blob not null
            );");

        //await AddColumnIfNotExists(connection, tableName: "messageQueue", fieldName: "myfield", definition: "integer null");
        //await DropColumn(connection, table: "messageQueue", column: "myfield");
    }

    private async Task AddColumnIfNotExists(IDbConnection connection, string tableName, string fieldName, string definition)
    {
        try
        {
            await connection.ExecuteAsync($"ALTER TABLE {tableName} ADD COLUMN {fieldName} {definition}");
        }
        catch (SQLiteException ex) when (ex.Message.Contains("duplicate column name"))
        {
        }
    }

    private Task DropColumn(IDbConnection connection, string table, string column) => connection.ExecuteAsync($"ALTER TABLE {table} DROP COLUMN {column};");
}
