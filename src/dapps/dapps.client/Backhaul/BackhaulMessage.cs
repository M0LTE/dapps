namespace dapps.client.Backhaul;

/// <summary>
/// A unit of forwarded DAPPS traffic — what one node hands to a neighbour
/// for the neighbour to enqueue, forward, or locally deliver.
///
/// Bearer-neutral. <see cref="IDappsBackhaul"/> implementations translate
/// this shape into bearer-specific frames (DAPPSv1 stream exchange for
/// AGW today; companion datagrams for MeshCore later).
///
/// Headers are intentionally omitted from this first cut: the wire
/// format already supports arbitrary KV headers but the codebase doesn't
/// thread them through the outbound submission path yet, so adding them
/// to the seam now would be empty surface. Easy to add when needed.
/// </summary>
public sealed record BackhaulMessage(
    string Id,
    string Destination,
    long? Salt,
    int? Ttl,
    byte[] Payload);
