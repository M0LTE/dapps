namespace dapps.client.Backhaul;

/// <summary>
/// Outcome of a backhaul send. <c>Accepted=true</c> means the neighbour
/// confirmed receipt per the bearer's ack contract; the caller can mark
/// the message as forwarded and stop retrying. Otherwise <c>Error</c>
/// carries a human-readable reason for logging.
/// </summary>
public sealed record BackhaulSendResult(bool Accepted, string? Error)
{
    public static BackhaulSendResult Ok() => new(true, null);
    public static BackhaulSendResult Fail(string error) => new(false, error);
}
