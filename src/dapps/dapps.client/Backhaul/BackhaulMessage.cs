namespace dapps.client.Backhaul;

/// <summary>
/// A unit of DAPPS traffic — bearer-neutral. Outbound:
/// <see cref="IDappsBackhaul"/> implementations translate this shape into
/// bearer-specific frames (DAPPSv1 stream exchange for AGW today;
/// companion datagrams for MeshCore later). Inbound: bearer-specific
/// receive code constructs one of these from a fully-received-and-
/// validated message and hands it to <see cref="IBackhaulInbox"/>.
///
/// <see cref="Headers"/> carries any non-reserved KVs from the on-air
/// `ihave` line (post-A0 the outbound submission path doesn't populate
/// it, but inbound preserves what the peer sent).
/// </summary>
public sealed record BackhaulMessage(
    string Id,
    string Destination,
    long? Salt,
    int? Ttl,
    byte[] Payload,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Originator = null,
    string? LinkSourceCallsign = null,
    byte? FloodHopsRemaining = null);

// LinkSourceCallsign: the *immediate sender's* callsign, distinct from
// Originator (the F1 end-to-end source). Carried on bearers that don't
// natively identify the sender — UDP being the prime example, since
// the source port is ephemeral and there's no session-level handshake
// that establishes peer identity. Stamped by the bearer's send path
// with the local callsign; consumed by the receive path so the inbox
// (and downstream passive-learning algorithms) can see who handed
// each hop the message.
//
// AGW already identifies the link source from the C-frame's CallFrom
// field, so AGW-bearer SendAsync may leave this null; the inbound
// path uses the AGW-supplied identity directly.
//
// FloodHopsRemaining: when set, this message is a B5 cold-start
// flood. Each forwarding hop decrements before re-flooding; the
// flood stops when the value reaches zero. null means "this is a
// regular routed message, not a flood." The bounded-flood fallback
// (FloodFallbackAlgorithm) is the only thing that originates floods;
// other algorithms / inbox handlers just propagate them.
