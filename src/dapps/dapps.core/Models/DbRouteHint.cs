using SQLite;

namespace dapps.core.Models;

[Table("routehints")]
public class DbRouteHint
{
    [PrimaryKey]
    public string Destination { get; set; } = "";
    public string NextHop { get; set; } = "";
}

[Table("neighbours")]
public class DbNeighbour
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Unique = true)]
    public string Callsign { get; set; } = "";
    
    public string ConnectScript { get; set; } = "";
}