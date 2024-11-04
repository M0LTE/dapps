using System.Diagnostics;
using System.Text;

namespace DappsClientLib;

public static class Extensions
{
    public static void AssertIsLoggedInToLocalBpq(this BpqSessionState state)
    {
        if (state != BpqSessionState.LoggedIn)
        {
            throw new InvalidOperationException("Not logged in");
        }
    }

    public static void AssertTruth(this bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static async Task WriteUtf8AndFlush(this Stream stream, string value)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        await stream.FlushAsync();
    }

    /// <summary>
    /// Read until the stream yields the matching string.
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="match"></param>
    /// <returns>True and the whole matched value when a match is found, otherwise false/undefined.</returns>
    public static (bool success, string matchingValue) Expect(this Stream reader, string match) => Expect(reader, s => s.EndsWith(match));

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
}
