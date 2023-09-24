﻿using Dapper;
using System.Data;
using System.Data.SQLite;

namespace dapps.DataAccess;

internal class DbConnectionFactory
{
    public IDbConnection GetDbConnection()
    {
        var connection = new SQLiteConnection("data source=dapps.sqlite");
        connection.Open();
        return connection;
    }

    public async Task SetupTables()
    {
        using var connection = GetDbConnection();

        await connection.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS messageQueue ( 
              id integer not null primary key autoincrement,
              datetime not null default current_timestamp
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
