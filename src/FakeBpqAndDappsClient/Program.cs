// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
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

int i = 1;

if (i == 0)
{
    var msg = "Hello world";
    tcpStreamWriter.Write($"ihave aabbccd len={msg.Length} mykey=myvalue fmt=d\n");
    using var compressor = new DeflateStream(tcpStream, CompressionLevel.Optimal, leaveOpen: true);
    using var writer = new StreamWriter(compressor) { AutoFlush = true };
    writer.Write(msg);
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