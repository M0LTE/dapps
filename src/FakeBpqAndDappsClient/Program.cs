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
client.Connect("gb7rdg-node", 11000);

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

bool deflated = true;

while (true)
{
    Console.Write("Press enter to send a message");
    Console.ReadLine();

    var msg = "Hello world";
    var bytes = Encoding.UTF8.GetBytes(msg);

    var ts = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds) - 1730068046000;
    var hash = ComputeHash(Encoding.UTF8.GetBytes(msg), ts);
    var truncatedHash = hash[..7];
    var dst = "queuename@gb7rdg-4";

    var ihave = $"ihave {truncatedHash} len={bytes.Length} fmt={(deflated ? 'd' : 'p')} ts={ts} mykey=myvalue 💩=💩 dst={dst}";
    var chk = Checksum(ihave);

    tcpStreamWriter.Write($"{ihave} chk={chk}\n");

    var ihaveresponse = await tcpStreamReader.ReadLineAsync();

    if (ihaveresponse == "send " + truncatedHash)
    {
        tcpStreamWriter.Write($"data {truncatedHash}\n");
        Console.WriteLine($"Sent data {truncatedHash}\\n");
        if (deflated)
        {
            using var compressor = new DeflateStream(tcpStream, CompressionLevel.Optimal, leaveOpen: true);
            using var deflateWriter = new StreamWriter(compressor) { AutoFlush = true };
            await deflateWriter.WriteAsync(msg);
            await deflateWriter.FlushAsync();
        }
        else
        {
            await tcpStream.WriteAsync(bytes);
            await tcpStream.FlushAsync();
        }
        
        Console.WriteLine($"Sent {bytes.Length} bytes and flushed");
    }
    else
    {
        Console.WriteLine($"Expected 'send aabbccd', but got '{ihaveresponse}'");
    }


    var response = await tcpStreamReader.ReadLineAsync();
    Console.WriteLine(response);
}

static string Checksum(string ihave)
{
    var hash = SHA1.HashData(Encoding.UTF8.GetBytes(ihave));
    return BitConverter.ToString(hash).Replace("-", "").ToLower()[..2];
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
    byte[] hashBytes = SHA1.HashData(toHash);
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