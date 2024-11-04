using System.Diagnostics;
using System.Text;

public static class Extensions
{
    public static async Task Write(this Stream stream, string value)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        await stream.FlushAsync();
    }
}