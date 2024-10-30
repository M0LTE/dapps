using dapps.core.Models;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace dapps.core.Services;

public class BpqTelnetClient(IOptions<BpqOptions> options, ILogger logger) : IDisposable
{
    private readonly TcpClient client = new();
    private NetworkStream? stream;
    private StreamWriter? writer;
    public BpqSessionState State { get; private set; } = BpqSessionState.PreLogin;

    public enum BpqSessionState
    {
        PreLogin, LoggedIn,
        Bbs
    }

    public async Task<TelnetLoginResult> Login(string user, string password)
    {
        if (State != BpqSessionState.PreLogin)
        {
            throw new InvalidOperationException("Already logged in");
        }

        if (0 == options.Value.TelnetTcpPort)
        {
            throw new InvalidOperationException("TelnetTcpPort is not configured");
        }

        await client.ConnectAsync(options.Value.Host, options.Value.TelnetTcpPort);
        client.ReceiveTimeout = 50000;
        stream = client.GetStream();
        writer = new(stream);
        writer.AutoFlush = true;
        writer.NewLine = "\r\n"; // seems even Linux BPQ expects CRLF

        var userPromptResult = stream.Expect("user:");
        if (!userPromptResult.success)
        {
            throw new ProtocolErrorException("Expected 'user:' - is this a BPQ telnet port?");
        }

        writer.WriteLine(user);

        var usernameCorrect = stream.ReadUntil(new Dictionary<string, bool> {
            { "user:", false }, {"password:", true }
        });

        if (!usernameCorrect)
        {
            return TelnetLoginResult.UserInvalid;
        }

        writer.WriteLine(password);

        // a CTEXT line that reads as follows:
        //   CTEXT=Welcome to GB7RDG Telnet Server\n Enter ? for list of commands\n\n
        // comes through like this:
        //   {0d}{0a}Welcome to GB7RDG Telnet Server{0d}{0a} Enter ? for list of commands{0d}{0a}{0d}{0a}{0d}

        // i.e. each "\n" (not newline but the actual characters '\', 'n' in the ctext, which is single-line)
        // is replaced with a CR LF, and a \r is added to the end

        var successMatch = options.Value.Ctext.Replace("\\n", "\r\n") + "\r";

        var result = stream.ReadUntil(new Dictionary<string, TelnetLoginResult> {
            { "password:",  TelnetLoginResult.PasswordInvalid },
            { successMatch, TelnetLoginResult.Success }
        });

        if (result == TelnetLoginResult.Success)
        {
            State = BpqSessionState.LoggedIn;
        }

        return result;
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

        writer!.WriteLine(command);

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

            //Debug.WriteLine(buffer.AsString());

            var s = Encoding.UTF8.GetString(buffer.ToArray());
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
            buffer.Add((byte)stream.ReadByte());
            Debug.WriteLine(buffer.AsString());

            foreach (var match in matches)
            {
                if (buffer.EndsWith(match.Key))
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

    public static bool EndsWith(this List<byte> list, string value)
    {
        if (list.Count < value.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (list[list.Count - value.Length + i] != value[i])
            {
                return false;
            }
        }

        Debug.WriteLine($"Matched '{Printable(value)}'");
        return true;
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

public enum TelnetLoginResult
{
    UserInvalid, PasswordInvalid, Success
}