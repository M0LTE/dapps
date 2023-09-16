using System.Net.Sockets;
using System.Text;

namespace dapps.Services;

internal static class Extensions
{
    public static string WaitToReceive(this StreamReader reader, string value)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int i = reader.Read();
            sb.Append((char)i);

            var s = sb.ToString();
            if (s.EndsWith(value))
            {
                return sb.ToString();
            }
        }
    }

    public static string WaitToReceive2(this StreamReader reader, string value)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int i = reader.Read();
            sb.Append((char)i);

            var s = sb.ToString();
            if (s.EndsWith(value) && !((NetworkStream)reader.BaseStream).DataAvailable)
            {
                return sb.ToString();
            }
        }
    }
}