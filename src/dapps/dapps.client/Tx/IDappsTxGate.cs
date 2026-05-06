namespace dapps.client.Tx;

/// <summary>
/// Per-instance kill-switch consulted by every bearer immediately
/// before it puts bytes that produce on-air emissions on the wire.
/// When closed, the bearer either throws <see cref="TxStoppedException"/>
/// (low-level chokepoints like AgwFrameTransport) or returns a failed
/// result (high-level chokepoints like UdpDatagramBackhaul.SendAsync).
///
/// Inbound traffic is unaffected; only emissions originated by this
/// instance are gated. AX.25 disconnect frames (AGW kind 'd', RHP
/// CloseAsync) are deliberately NOT gated - blocking them would leak
/// session handles and leave remotes thinking we're still connected
/// until their idle timeout. The point of the gate is to suppress new
/// transmissions, not to refuse to clean up existing sessions.
///
/// Composes two sources: a local operator toggle and a centralised
/// remote kill-switch URL. Implementations decide how to combine them
/// (typically: local-toggle AND (remote-allowed OR remote-stale-and-fail-open)).
/// PR 1 ships only the seam plus an always-open default; PRs 2 and 3
/// add the local toggle and the remote poller.
/// </summary>
public interface IDappsTxGate
{
    /// <summary>
    /// True when bearers may emit. Cheap; called on every TX.
    /// </summary>
    bool TxAllowed { get; }

    /// <summary>
    /// Human-readable explanation when <see cref="TxAllowed"/> is false,
    /// suitable for logging and audit. Empty/null when TX is allowed.
    /// </summary>
    string? BlockReason { get; }
}

/// <summary>
/// Default no-op gate that always allows TX. Used when DI hasn't
/// registered a real gate (tests, the dapps.client.harness) and as
/// the optional-constructor fallback so adding the gate doesn't
/// require updating dozens of test call sites.
/// </summary>
public sealed class AlwaysOpenTxGate : IDappsTxGate
{
    public static readonly AlwaysOpenTxGate Instance = new();
    public bool TxAllowed => true;
    public string? BlockReason => null;
}

/// <summary>
/// Thrown by low-level bearer chokepoints (AgwFrameTransport,
/// Rhpv2OutboundTransport) when a TX is blocked by the gate. Higher
/// layers (Dappsv1SessionBackhaul, OutboundMessageManager) catch this
/// and convert it to an audited failed-send result.
/// </summary>
public sealed class TxStoppedException : Exception
{
    public TxStoppedException(string reason)
        : base($"transmission blocked by tx-gate: {reason}") { }
}
