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
    IReadOnlyDictionary<string, string>? Headers = null);
