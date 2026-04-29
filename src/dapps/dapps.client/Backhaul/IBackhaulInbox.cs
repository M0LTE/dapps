namespace dapps.client.Backhaul;

/// <summary>
/// The seam where bearer-specific receive code hands a complete,
/// validated DAPPS message off to bearer-neutral processing
/// (persistence, local-app delivery, future forwarding decisions).
///
/// Plan A0 inbound counterpart to <see cref="IDappsBackhaul"/>: the
/// DAPPSv1 session reader, a future MeshCore datagram reader, and any
/// other bearer all converge here once they have a hashed-and-checked
/// message. Anything that DAPPS does to a received message — write to
/// the queue, push to MQTT, decide to forward onwards — happens behind
/// this interface, not in the bearer code.
/// </summary>
public interface IBackhaulInbox
{
    Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct);
}
