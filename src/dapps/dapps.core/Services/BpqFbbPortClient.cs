using dapps.core.Models;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace dapps.core.Services;

public class BpqFbbPortClient(IOptions<SystemOptions> options, ILoggerFactory logger) : IDisposable
{
    private ILogger<BpqFbbPortClient> logger = logger.CreateLogger<BpqFbbPortClient>();
    private readonly TcpClient client = new();
    private NetworkStream? stream;
    private StreamWriter? writer;
    public BpqSessionState State { get; private set; } = BpqSessionState.PreLogin;

    public enum BpqSessionState
    {
        PreLogin, LoggedIn,
    }

    public async Task<FbbLoginResult> Login(string user, string password)
    {
        if (State != BpqSessionState.PreLogin)
        {
            throw new InvalidOperationException("Already logged in");
        }

        if (0 == options.Value.BpqFbbPort)
        {
            throw new InvalidOperationException("BpqFbbPort is not configured");
        }

        await client.ConnectAsync(options.Value.Host, options.Value.BpqFbbPort);
        client.ReceiveTimeout = 50000;
        stream = client.GetStream();
        writer = new(stream);
        writer.AutoFlush = true;

        await writer.WriteAsync($"{user}\r{password}\rBPQTERMTCP\r");

        var loginResult = stream.Expect("Connected to TelnetServer\r"); // lies

        if (!loginResult.success)
        {
            return FbbLoginResult.UserInvalid;
        }

        State = BpqSessionState.LoggedIn;
        return FbbLoginResult.Success;
    }

    public Stream GetStream() => stream!;

    public void Dispose()
    {
        ((IDisposable)client).Dispose();
    }

    public async Task<bool> SendCommand(string command, string expect)
    {
        if (State != BpqSessionState.LoggedIn)
        {
            throw new InvalidOperationException("Not logged in");
        }

        await writer!.WriteAsync(command);

        var result = stream!.ReadUntil(new Dictionary<string, bool> { { expect, true } });

        if (result)
        {
            logger.LogInformation($"Matched '{expect}'");
        }
        else
        {
            logger.LogInformation("Did not match");
        }

        return result;
    }
}

public class ProtocolErrorException(string? message) : Exception(message)
{
    public ProtocolErrorException(string? message, string bufferContents) : this(message)
    {
        BufferContents = bufferContents;
    }

    public string? BufferContents { get; }
}

internal static class ExtensionMethods
{
    /// <summary>
    /// Read until the underlying client ReadTimeout expires, or the StreamReader ends with the matching string.
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="match"></param>
    /// <returns>The matching text if it was found, else null</returns>
    public static (bool success, string matchingValue) Expect(this Stream reader, string match)
    {
        return Expect(reader, s => s.EndsWith(match));
    }

    public static (bool success, string matchingValue) Expect(this Stream stream, Func<string, bool> predicate)
    {
        var buffer = new List<byte>();

        while (true)
        {
            try
            {
                buffer.Add((byte)stream.ReadByte());
            }
            catch (IOException)
            {
                throw new ProtocolErrorException("Failed to match predicate. Buffer contents: " + buffer.AsString().Printable(), buffer.AsString().Printable());
            }

            var s = Encoding.UTF8.GetString(buffer.ToArray());
            Debug.WriteLine(s);
            if (predicate(s))
            {
                return (true, s);
            }
        }
    }

    /// <summary>
    /// Read until the underlying client ReadTimeout expires, or one of the matches is found at the end of the StreamReader output.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="stream"></param>
    /// <param name="matches">A map of strings to look for, and corresponding values for this method to return for each case.</param>
    /// <returns></returns>
    /// <exception cref="IOException">If the system does not return one of the expected strings before the ReadTimeout</exception>
    public static T ReadUntil<T>(this Stream stream, Dictionary<string, T> matches)
    {
        var buffer = new List<byte>();

        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1)
            {
                throw new IOException("End of stream");
            }
            buffer.Add((byte)b);
            Debug.WriteLine(buffer.AsString());

            foreach (var match in matches)
            {
                var str = Encoding.UTF8.GetString(buffer.ToArray());

                if (str.EndsWith(match.Key))
                {
                    return match.Value;
                }
            }
        }
    }

    public static string AsString(this List<byte> list)
    {
        var sb = new StringBuilder();

        foreach (var b in list)
        {
            if (b >= 0x20 && b < 0x7F)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append($"{{{b.ToString("X2").ToLower()}}}");
            }
        }

        return sb.ToString();
    }

    public static string Printable(this string value)
    {
        var sb = new StringBuilder();

        foreach (char b in value)
        {
            if (b >= 0x20 && b < 0x7F)
            {
                sb.Append(b);
            }
            else
            {
                sb.Append($"{{{((byte)b).ToString("X2").ToLower()}}}");
            }
        }

        return sb.ToString();
    }
}

public enum FbbLoginResult
{
    UserInvalid, PasswordInvalid, Success
}