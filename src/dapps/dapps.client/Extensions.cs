using System.Text;

namespace dapps.client;

public static class Extensions
{
    public static async Task WriteUtf8AndFlush(this Stream stream, string value)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        await stream.FlushAsync();
    }
}
