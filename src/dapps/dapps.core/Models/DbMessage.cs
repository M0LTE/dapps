using SQLite;

namespace dapps.core.Models;

public class DbMessage
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public required string DestNode { get; set; }
    public required string DestTopic { get; set; }
    public required byte[] Payload { get; set; }
}
