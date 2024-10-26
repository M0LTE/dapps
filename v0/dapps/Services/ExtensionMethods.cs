using System.Text;

namespace dapps.Services;

internal static partial class ExtensionMethods
{
    public static string ToPrintableString(this IList<byte> data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 32 && b <= 126)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append($"<{b.ToHex()}>");
            }
        }
        return sb.ToString();
    }

    public static string ToHex(this byte data)
    {
        var hex = Convert.ToHexString(new[] { data }).ToLower();
        if (hex.Length == 1)
        {
            hex = "0" + hex;
        }
        return hex;
    }

    public static void WriteNewline(this StreamWriter streamWriter, string text)
    {
        //streamWriter.Write(text + "\n");
        streamWriter.Write(text + "\r\n");
    }
}