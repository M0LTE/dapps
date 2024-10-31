using SQLite;

namespace dapps.core.Models;

[Table("systemoptions")]
public class DbSystemOption
{
    [PrimaryKey]
    public string Option { get; set; } = "";
    public string? Value { get; set; }
}

public class SystemOptions
{
    public string Host { get; set; } = "";
    public int BpqFbbPort { get; set; }
}