using dapps.client.Tx;

namespace dapps.client.Transport.Agw;

/// <summary>
/// Reads and writes <see cref="AgwFrame"/>s over a duplex Stream. Writes are
/// serialised so concurrent senders can't interleave.
///
/// Single chokepoint for AGW-side TX gating: every AGW byte heading toward
/// the node passes through <see cref="WriteFrameAsync"/>. The configured
/// <see cref="IDappsTxGate"/> is consulted on each write; when closed, frames
/// whose <see cref="AgwFrame.Kind"/> produces RF (connect / data / UNPROTO /
/// raw) raise <see cref="TxStoppedException"/>. Node-control kinds
/// (register, login, port queries, monitor toggle, ...) are always permitted
/// so admin traffic to BPQ/XR keeps flowing while TX is gagged. Disconnect
/// frames ('d') are likewise allowed through to avoid leaking sessions.
/// </summary>
public sealed class AgwFrameTransport
{
    private readonly Stream stream;
    private readonly IDappsTxGate txGate;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public AgwFrameTransport(Stream stream, IDappsTxGate? txGate = null)
    {
        this.stream = stream;
        this.txGate = txGate ?? AlwaysOpenTxGate.Instance;
    }

    public async Task WriteFrameAsync(AgwFrame frame, CancellationToken ct)
    {
        if (IsRfEmitting(frame.Kind) && !txGate.TxAllowed)
        {
            throw new TxStoppedException(
                $"AGW frame kind '{frame.Kind}' (port {frame.Port}, {frame.CallFrom}->{frame.CallTo}): {txGate.BlockReason ?? "(no reason)"}");
        }

        var bytes = frame.ToBytes();
        await writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<AgwFrame> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[AgwFrame.HeaderLength];
        await stream.ReadExactlyAsync(header, ct);
        var dataLength = AgwFrame.ReadDataLength(header);

        if (dataLength < 0)
        {
            throw new InvalidDataException($"AGW header has negative DataLength: {dataLength}");
        }

        var payload = dataLength == 0 ? [] : new byte[dataLength];
        if (dataLength > 0)
        {
            await stream.ReadExactlyAsync(payload, ct);
        }
        return AgwFrame.ParseHeader(header, payload);
    }

    /// <summary>
    /// AGW frame kinds whose write produces an on-air emission. The set
    /// is small and stable per the AGW host-protocol spec - everything
    /// else is node-control or RX-side.
    ///   'C' connect (SABM)
    ///   'v' connect via digipeaters
    ///   'c' connect with non-standard PID
    ///   'D' data (I-frame)
    ///   'M' UNPROTO send (UI frame)
    ///   'V' UNPROTO via digi
    ///   'K' raw frame
    /// </summary>
    private static bool IsRfEmitting(char kind) => kind switch
    {
        'C' or 'v' or 'c' or 'D' or 'M' or 'V' or 'K' => true,
        _ => false,
    };
}
