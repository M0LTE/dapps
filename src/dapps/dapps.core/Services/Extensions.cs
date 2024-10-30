using System.Text;

namespace dapps.core.Services;

public static class Extensions
{
    public static Task<string> ReadLine(this Stream stream) => ReadLine(stream, CancellationToken.None);

    public static Task<string> ReadLine(this Stream stream, CancellationToken stoppingToken)
    {
        var buffer = new List<byte>();

        while (true)
        {
            var c = stream.ReadByte();

            if (c == -1)
            {
                break;
            }

            if (c == '\n')
            {
                break;
            }

            buffer.Add((byte)c);
        }

        return Task.FromResult(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}