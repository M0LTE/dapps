namespace dapps.client.Discovery;

/// <summary>
/// Plan B6.2 - HF NVIS solicit-and-listen. A solicit is the asking
/// half of discovery: "anyone there?". Receivers reply with their
/// normal beacon after a small random delay (so the channel doesn't
/// collide with simultaneous responses). Especially useful on HF
/// channels where propagation footprints shift hour-to-hour and
/// scheduled beacons can miss their window.
///
/// Bearer-neutral: AX.25 UI frames carry it as their information
/// field, UDP multicast carries it as the datagram body - same
/// transport the beacons use, distinguished by the wire form's
/// <c>solicit</c> keyword (see <see cref="SolicitCodec"/>).
/// </summary>
public sealed record SolicitFrame(string Callsign);
