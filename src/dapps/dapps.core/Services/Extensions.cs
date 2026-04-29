using System.Text;

namespace dapps.core.Services;

public static class Extensions
{
    /// <summary>
    /// Read a line (terminated by `\n`) from the stream as UTF-8. The trailing
    /// newline is stripped. Returns empty string on EOF.
    /// </summary>
    public static async Task<string> ReadLine(this Stream stream, CancellationToken stoppingToken)
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(oneByte, stoppingToken);
            if (n == 0) break;            // EOF
            if (oneByte[0] == '\n') break; // line terminator
            buffer.Add(oneByte[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Run an async operation with an inactivity timeout layered over the
    /// caller's stoppingToken. The op must accept the linked token and use it
    /// in its underlying read. On timeout the inner operation throws
    /// OperationCanceledException; callers can distinguish "timed out" from
    /// "shutdown" by checking the original stoppingToken.
    /// </summary>
    public static async Task<T> WithInactivityTimeout<T>(
        Func<CancellationToken, Task<T>> op,
        TimeSpan timeout,
        CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(timeout);
        return await op(cts.Token);
    }

    public static async Task WithInactivityTimeout(
        Func<CancellationToken, Task> op,
        TimeSpan timeout,
        CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(timeout);
        await op(cts.Token);
    }
}
