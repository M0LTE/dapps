using SQLite;

namespace dapps.core.Models;

[Table("bpqoptions")]
public class BpqOptions
{
    [PrimaryKey]
    public int Id { get; set; }
    
    public int TelnetTcpPort { get; set; }
    public required string Host { get; set; }
    public required string Ctext { get; set; }
}
