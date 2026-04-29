namespace dapps.client.Backhaul;

/// <summary>
/// Sends DAPPS backhaul messages to neighbours. The seam between
/// queue/router logic (which knows about messages, neighbours, residual
/// TTL) and bearer mechanics (which know about wire formats and session
/// protocols).
///
/// Plan A0.1: implementations own the bearer's session/ack contract;
/// callers don't see streams, frames, or retry quirks. The DAPPSv1
/// `prompt` / `ihave` / `send` / `data` / `ack` exchange is one such
/// implementation (<see cref="Dappsv1SessionBackhaul"/>); a future
/// MeshCore companion or KISS bearer would be another, carrying the
/// same <see cref="BackhaulMessage"/> over different wire frames.
/// </summary>
public interface IDappsBackhaul
{
    /// <summary>
    /// Forward <paramref name="message"/> to <paramref name="route"/>.
    /// Returns whether the neighbour accepted the message; whatever
    /// "accepted" means is the bearer's contract (an explicit ack frame
    /// for stream bearers, a delivery report for datagram bearers, etc).
    /// </summary>
    Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct);
}
