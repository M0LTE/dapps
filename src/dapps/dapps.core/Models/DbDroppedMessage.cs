using SQLite;

namespace dapps.core.Models;

/// <summary>
/// A message that DAPPS dropped - TTL expiry, hash mismatch on receive,
/// no-route give-up, etc. Soft-deletion target: rows move from
/// <see cref="DbMessage"/> to here rather than being hard-deleted, so
/// the operator can see what was thrown away during RF testing.
///
/// Mirrors <see cref="DbMessage"/>'s shape plus <see cref="DroppedAt"/>
/// + <see cref="Reason"/>. No retention policy yet - operator can purge
/// manually if it grows.
/// </summary>
[Table("dropped_messages")]
public class DbDroppedMessage
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public long? Salt { get; init; }
    public string Destination { get; init; } = "";
    public string SourceCallsign { get; init; } = "";
    public string AdditionalProperties { get; init; } = "";
    public bool Forwarded { get; init; }
    public bool LocallyDelivered { get; init; }
    public int? Ttl { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>UTC instant we decided this message was dead.</summary>
    public DateTime DroppedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Short tag explaining the drop. Currently used:
    /// <c>ttl-expired</c>. Stable across releases - kept short for
    /// dashboard display.</summary>
    public string Reason { get; init; } = "";
}
