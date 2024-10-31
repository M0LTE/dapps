using dapps.core.Models;
using dapps.core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace dapps.core.tests;

public class NullDisposable : IDisposable
{
    public void Dispose()
    {
    }
}

public class XUnitLoggerProvider(XunitLogAdapter xunitLogAdapter) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => xunitLogAdapter;

    public void Dispose()
    {
    }
}

public class UnitTest1 (ITestOutputHelper output)
{
    [Fact]
    public async Task Test1()
    {
        var loggerFactory = new LoggerFactory();
        var loggerProvider = new XUnitLoggerProvider(new XunitLogAdapter(output));
        loggerFactory.AddProvider(loggerProvider);
        
        BpqFbbPortClient client = new BpqFbbPortClient(
            new Options("gb7rdg-node", 8011), loggerFactory);

        var loginResult = await client.Login("tf", "rad10stuff");
        loginResult.Should().Be(FbbLoginResult.Success);
        client.State.Should().Be(BpqFbbPortClient.BpqSessionState.LoggedIn);

        (await client.SendCommand("dapps\r", "DAPPSv1>\n")).Should().BeTrue();

        var stream = client.GetStream();

        var ihave = "ihave de75866 len=11 fmt=p ts=100000000 dst=testqueue@gb7rdg";
        stream.Write(Encoding.UTF8.GetBytes($"{ihave}\n"));
        stream.Flush();

        var ihaveresponse = await stream.ReadLine();
        ihaveresponse.Should().Be($"send de75866");

        await stream.WriteAsync(Encoding.UTF8.GetBytes("data de75866\n"));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello world"));
        await stream.FlushAsync();

        var dataResponse = await stream.ReadLine();
        dataResponse.Should().Be($"ack de75866");
    }

    /*
        var msg = "Hello world";
        var bytes = Encoding.UTF8.GetBytes(msg);
        var ts = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds) - 1730068046000;
        var hash = ComputeHash(Encoding.UTF8.GetBytes(msg), ts);
        var truncatedHash = hash[..7];
        var dst = "queuename@gb7rdg-4";
        bool deflated = true;
        var ihave = $"ihave {truncatedHash} len={bytes.Length} fmt={(deflated ? 'd' : 'p')} ts={ts} mykey=myvalue 💩=💩 dst={dst}";
        var chk = Checksum(ihave);
        stream.Write(Encoding.UTF8.GetBytes($"{ihave} chk={chk}\n"));
     */

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
}

internal class Options(string bpqHost, int fbbPort) : IOptions<SystemOptions>
{
    public SystemOptions Value => new() { Host = bpqHost, BpqFbbPort = fbbPort };
}
