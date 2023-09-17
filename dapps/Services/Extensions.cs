using System.Text;

namespace dapps.Services;

internal static class Extensions
{
    public static string WaitToReceive(this StreamReader reader, string value)
    {
        var sb = new StringBuilder();

        while (true)
        {
            var i = reader.Read();

            sb.Append((char)i);

            var s = sb.ToString();
            if (s.EndsWith(value))
            {
                return sb.ToString();
            }
        }
    }

    public static bool WaitToReceive(this StreamReader reader, string value, TimeSpan timeout, out string received)
    {
        var sb = new StringBuilder();

        reader.BaseStream.ReadTimeout = (int)timeout.TotalMilliseconds;

        try
        {
            while (true)
            {
                int i;

                try
                {
                    i = reader.Read();
                }
                catch (Exception)
                {
                    received = sb.ToString();
                    return false;
                }

                sb.Append((char)i);

                var s = sb.ToString();
                if (s.EndsWith(value))
                {
                    received = sb.ToString();
                    return true;
                }
            }
        }
        finally
        {
            reader.BaseStream.ReadTimeout = -1;
        }
    }
}