using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Per-(message-id, link-source) flood-deduplication ledger. Every
/// flooded message we receive — whether destined for us or just
/// transiting — gets recorded here so a re-flood from the same
/// upstream peer is silently dropped instead of starting an infinite
/// echo around the mesh.
///
/// Composite primary key: message id is 7 chars, link-source callsign
/// is variable; a single string concatenation
/// (<c>{messageId}|{linkSource}</c>) keeps the schema simple. A sweep
/// task ages out rows older than the dedup window (sized so that the
/// expected propagation time of a flood is comfortably less).
/// </summary>
[Table("flood_seen")]
public class DbFloodSeen
{
    [PrimaryKey]
    public string Key { get; set; } = "";

    public DateTime SeenAt { get; set; }

    public static string MakeKey(string messageId, string linkSourceCallsign)
        => $"{messageId}|{linkSourceCallsign}";
}
