using System.Text;

namespace dapps.core.Services;

public static class Extensions
{
    public static Task<string> ReadToCr(this StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var c = reader.Read();

            if (c == -1)
            {
                break;
            }

            if (c == '\r')
            {
                break;
            }

            sb.Append((char)c);
        }
        return Task.FromResult(sb.ToString());
    }
}