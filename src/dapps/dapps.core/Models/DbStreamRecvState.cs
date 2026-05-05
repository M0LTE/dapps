using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Receiver-side cursor for opt-in message ordering. One row per
/// (LocalCallsign, SenderCallsign, StreamId) tracks the next seq the
/// inbox expects to deliver. Messages with seq &gt; cursor park as
/// <see cref="DbMessage.PendingInOrder"/>; the cursor advances and
/// drains pending rows when the gap fills (or, in timeout mode, when
/// the gap deadline elapses).
///
/// SenderCallsign is the F1 originator callsign (not the link source) -
/// ordering is end-to-end at the originator's intent, irrespective of
/// the intermediate forwarding path.
/// </summary>
[Table("streamrecvstate")]
public sealed class DbStreamRecvState
{
    /// <summary>Composite key <c>{LocalCallsign}|{SenderCallsign}|{StreamId}</c>.
    /// Built by <see cref="MakeKey"/> on insert.</summary>
    [PrimaryKey, NotNull]
    public string Key { get; set; } = "";

    public string LocalCallsign { get; set; } = "";
    public string SenderCallsign { get; set; } = "";
    public string StreamId { get; set; } = "";

    /// <summary>The next seq the inbox will deliver. New streams start
    /// at 1; the first arrival with sn=1 delivers immediately and
    /// advances to 2. An arrival with sn &gt; this value parks until
    /// the gap fills.</summary>
    public uint NextExpectedSeq { get; set; } = 1;

    public DateTime LastReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant after which a still-open gap is allowed to
    /// be skipped (timeout mode). DateTime.MinValue means "no gap
    /// active" - either the cursor is up to date OR all parked rows
    /// were originated with gt=0 (strict). The sweeper computes the
    /// deadline from each parked message's StreamGapTimeoutSeconds and
    /// the LastReceivedAt of the row that caused the cursor to fall
    /// behind.</summary>
    public DateTime GapDeadline { get; set; } = DateTime.MinValue;

    public static string MakeKey(string localCallsign, string senderCallsign, string streamId)
        => $"{localCallsign}|{senderCallsign}|{streamId}";
}
