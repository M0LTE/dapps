namespace dapps.client.Transport.Agw;

/// <summary>
/// Reads and writes <see cref="AgwFrame"/>s over a duplex Stream. Writes are
/// serialised so concurrent senders can't interleave.
/// </summary>
public sealed class AgwFrameTransport(Stream stream)
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task WriteFrameAsync(AgwFrame frame, CancellationToken ct)
    {
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
}
