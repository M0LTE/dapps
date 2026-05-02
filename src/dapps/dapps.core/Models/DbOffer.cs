using SQLite;

namespace dapps.core.Models;

[Table("offers")]
internal sealed class DbOffer
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public int Length { get; init; }
    [NotNull]
    public string Format { get; init; } = "";
    public long? Salt { get; init; }
    public int? CompressedLength { get; init; }
    public string Destination { get; init; } = "";

    /// <summary>F1 end-to-end source tracking: <c>src=</c> from the
    /// <c>ihave</c> line, or empty if not supplied. Carried forward into
    /// the <c>DbMessage</c> row when the payload arrives.</summary>
    public string OriginatorCallsign { get; init; } = "";

    public string AdditionalProperties { get; set; } = "{}";

    /// <summary>Residual ttl in seconds from the offer line, or null if not
    /// supplied. Carried forward into the <c>DbMessage</c> row when the
    /// payload arrives.</summary>
    public int? Ttl { get; init; }

    /// <summary>UTC instant the offer was accepted. Used to expire offers
    /// whose payload never arrived.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>F2 multi-part: <c>mid=</c> from the <c>ihave</c> line,
    /// or null when this is a regular single-part message. Grouping id
    /// for fragment reassembly at the final destination. Carried into
    /// the <c>BackhaulMessage</c> the inbox receives.</summary>
    public string? MasterId { get; init; }

    /// <summary>F2 multi-part: 1-based fragment index parsed from
    /// <c>frag=N/M</c>. Null when not fragmented; non-null only when
    /// <see cref="MasterId"/> is also non-null.</summary>
    public int? FragmentIndex { get; init; }

    /// <summary>F2 multi-part: total fragment count parsed from
    /// <c>frag=N/M</c>. Null when not fragmented; non-null only when
    /// <see cref="MasterId"/> is also non-null. Always ≥ 2 when set
    /// (single-part messages skip the fragment headers entirely).</summary>
    public int? FragmentTotal { get; init; }
}
