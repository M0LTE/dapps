using SQLite;

namespace dapps.core.Models;

[Table("messages")]
public class DbMessage
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public long? Salt { get; init; }
    public string Destination { get; init; } = "";

    /// <summary>
    /// The callsign that handed us this message. For an inbound message
    /// from a neighbour DAPPS instance this is the AX.25 connecting station
    /// (the link source — last hop, not necessarily the original sender if
    /// the message was relayed). For a message submitted by a local app
    /// this is our own NODECALL.
    /// </summary>
    public string SourceCallsign { get; init; } = "";

    public string AdditionalProperties { get; init; } = "";

    /// <summary>True once OutboundMessageManager has handed this off to a
    /// neighbour DAPPS instance (only relevant when Destination is remote).</summary>
    public bool Forwarded { get; init; }

    /// <summary>True once a local app has acked receipt via the MQTT or REST
    /// app interface (only relevant when Destination is local).</summary>
    public bool LocallyDelivered { get; init; }

    /// <summary>Residual lifetime in seconds at the moment we received this
    /// message (or null if no ttl= was supplied). The wire-side `ttl=` value
    /// frozen at receive time; remaining lifetime is computed against
    /// <see cref="CreatedAt"/>.</summary>
    public int? Ttl { get; init; }

    /// <summary>UTC instant the row was created — when the message arrived
    /// or was submitted locally. Used with <see cref="Ttl"/> to compute
    /// residual lifetime on forward and to age out expired rows.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
