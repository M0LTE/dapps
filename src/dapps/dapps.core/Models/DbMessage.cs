﻿using SQLite;

namespace dapps.core.Models;

[Table("messages")]
public class DbMessage
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public byte[] Payload { get; init; } = [];
}
