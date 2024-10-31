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

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger("");
        var line = Encoding.UTF8.GetString(buffer.ToArray());
        logger.LogInformation("Read line: {0}", line);
        return Task.FromResult(line);
    }
}