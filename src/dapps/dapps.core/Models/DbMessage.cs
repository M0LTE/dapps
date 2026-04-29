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
}
