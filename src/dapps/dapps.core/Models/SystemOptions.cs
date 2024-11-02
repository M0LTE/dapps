using SQLite;

namespace dapps.core.Models;

[Table("systemoptions")]
public class DbSystemOption
{
    [PrimaryKey]
    public string Option { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SystemOptions
{
    public string NodeHost { get; set; } = "";
    public int FbbPort { get; set; }
    public string FbbUser { get; set; } = "";
    public string FbbPassword { get; set; } = "";
    public string Callsign { get; set; } = "";
}