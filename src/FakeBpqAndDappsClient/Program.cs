// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.WriteLine("Hello, World!");

var client = new TcpClient();
client.Connect("localhost", 11000);

using var tcpStream = client.GetStream();
using var tcpStreamReader = new StreamReader(tcpStream);
using var tcpStreamWriter = new StreamWriter(tcpStream) { AutoFlush = true };
tcpStreamWriter.Write("M0LTE\n"); // Emulating what BPQ does

// read the prompt
var prompt = tcpStreamReader.ReadLine();
if (prompt != "DAPPSv1>")
{
    Console.WriteLine($"Expected 'DAPPSv1> ', but got '{prompt}'");
    return;
}

var r = new DappsMessage
{
    SourceNode = "GB7RDG",
    SourceCall = "M0LTE",
    DestNode = "GB7IOW",
    DestTopic = "someapp/in",
    Ttl = DateTime.UtcNow.AddHours(1),
    Data = "Hello, World!"
};

var json = JsonSerializer.Serialize(new[] { r });

int i = 0;

while (true)
{
    Console.Write("Press enter to send a message");
    Console.ReadLine();

    if (i == 0)
    {
        var msg = "Hello world";

        var ts = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds) - 1730068046000;
        var hash = ComputeHash(Encoding.UTF8.GetBytes(msg), ts);
        var truncatedHash = hash[..7];

        tcpStreamWriter.Write($"ihave {truncatedHash} len={msg.Length} fmt=d ts={ts}\n");

        var ihaveresponse = await tcpStreamReader.ReadLineAsync();

        if (ihaveresponse == "send " + truncatedHash)
        {
            using var compressor = new DeflateStream(tcpStream, CompressionLevel.Optimal, leaveOpen: true);
            using var writer = new StreamWriter(compressor) { AutoFlush = true };
            writer.Write(msg);
        }
        else
        {
            Console.WriteLine($"Expected 'send aabbccd', but got '{ihaveresponse}'");
        }
    }
    else if (i == 1)
    {
        var msg = "Hello world";
        tcpStreamWriter.Write($"ihave aabbccd len={msg.Length} mykey=myvalue fmt=p\n"); // plain
        tcpStreamWriter.Write(msg);
    }
    else if (i == 2)
    {
        // removed
        tcpStreamWriter.Write("cja\n"); // sending compressed json array
        using var compressor = new DeflateStream(tcpStream, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new StreamWriter(compressor) { AutoFlush = true };
        writer.WriteLine(json);
    }
    else if (i == 3)
    {
        // removed
        tcpStreamWriter.Write("ja\n"); // sending json array
        tcpStreamWriter.Write(json);
    }

    var response = await tcpStreamReader.ReadLineAsync();
    Console.WriteLine(response);
}

static string ComputeHash(byte[] data, long? timestamp)
{
    byte[] toHash;
    if (timestamp != null)
    {
        var tsBytes = BitConverter.GetBytes(timestamp.Value);
        toHash = [.. tsBytes, .. data];
    }
    else
    {
        toHash = data;
    }

    var sha = SHA1.Create();
    byte[] hashBytes = sha.ComputeHash(toHash);
    var str = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    return str;
}

tcpStreamWriter.Write("q\n"); // quit

await Task.Delay(1000);
Debugger.Break();

readonly record struct DappsMessage
{
    [JsonPropertyName("type")]
    public string RecordType => "Msg";

    public DateTime Ttl { get; init; }
    public string SourceNode { get; init; }
    public string SourceCall { get; init; }
    public string DestNode { get; init; }
    public string DestTopic { get; init; }
    public object Data { get; init; }
}