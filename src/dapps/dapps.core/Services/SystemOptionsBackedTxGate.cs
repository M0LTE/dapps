using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Production <see cref="IDappsTxGate"/> implementation. Combines two
/// signals:
///   - <b>Local</b>: <see cref="SystemOptions.TxEnabled"/>, an operator
///     toggle persisted in the systemoptions table and exposed in the
///     dashboard header.
///   - <b>Remote</b>: a centrally-published kill-switch (PR 3).
///     <see cref="ITxKillSwitchSignal"/> is the seam; the default
///     <see cref="OpenTxKillSwitchSignal"/> registered here always
///     allows TX. PR 3 swaps it for the URL-polling implementation.
///
/// TX is allowed iff both signals allow. Block reasons compose so the
/// UI banner can show *which* signal closed the gate.
///
/// IOptionsMonitor.CurrentValue means a /Config-driven flip of
/// TxEnabled is reflected on the very next bearer call, no service
/// restart, no re-resolution.
/// </summary>
public sealed class SystemOptionsBackedTxGate(
    IOptionsMonitor<SystemOptions> options,
    ITxKillSwitchSignal remote) : IDappsTxGate
{
    public bool TxAllowed => LocalAllowed && remote.RemoteAllowed;

    public string? BlockReason
    {
        get
        {
            var local = LocalAllowed ? null : "operator pressed master TX-stop";
            var rem = remote.RemoteAllowed ? null : remote.RemoteBlockReason ?? "remote kill-switch active";
            return (local, rem) switch
            {
                (null, null) => null,
                (null, var r) => r,
                (var l, null) => l,
                (var l, var r) => $"{l}; {r}",
            };
        }
    }

    /// <summary>True when the local operator toggle allows TX. Exposed
    /// so the dashboard banner can distinguish "you stopped TX" from
    /// "the remote kill-switch stopped TX" without parsing the
    /// composed <see cref="BlockReason"/>.</summary>
    public bool LocalAllowed => options.CurrentValue.TxEnabled;

    /// <summary>True when the remote kill-switch allows TX. PR 3 wires
    /// the URL-polling signal; until then the registered signal is
    /// <see cref="OpenTxKillSwitchSignal"/> and this is always true.</summary>
    public bool RemoteAllowed => remote.RemoteAllowed;

    /// <summary>Reason text from the remote kill-switch when it's
    /// closed. Null when the remote allows TX.</summary>
    public string? RemoteBlockReason => remote.RemoteBlockReason;
}

/// <summary>
/// Seam for the centralised kill-switch signal. PR 3 implements it as
/// a <c>BackgroundService</c> that polls a URL and updates the
/// in-memory state; PR 2 ships only <see cref="OpenTxKillSwitchSignal"/>
/// so the gate composes correctly today.
/// </summary>
public interface ITxKillSwitchSignal
{
    bool RemoteAllowed { get; }
    string? RemoteBlockReason { get; }
}

public sealed class OpenTxKillSwitchSignal : ITxKillSwitchSignal
{
    public bool RemoteAllowed => true;
    public string? RemoteBlockReason => null;
}
