using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace dapps.core.tests.Integration;

/// <summary>
/// Spins up a single m0lte/linbpq container with AGW exposed to the host
/// over a random port. Sufficient for tests that exercise AgwOutboundTransport's
/// frame layout against a real BPQ AGW listener and its connect-failure path.
///
/// A two-instance topology (AXIP-UDP between containers, A→B AGW connect via
/// APPL1CALL) was attempted but ran into a BPQ-side issue where inbound 'C'
/// frames don't reach a registered AGW client through the application-pre-
/// allocated listener path under Docker bridge networking — needs separate
/// investigation. The single-instance fixture covers what we actually need
/// at this layer (frame format + connect handshake + 'd' failure handling).
/// </summary>
public sealed class LinbpqIntegrationFixture : IAsyncLifetime
{
    private const string Image = "m0lte/linbpq:latest";
    private const int InsideAgwPort = 8000;
    private const int InsideTelnetPort = 8010;

    public string Host => "127.0.0.1";
    public int AgwPort { get; private set; }

    public string LocalCallsign => "N0CALL";
    public string UnreachableCallsign => "Q0XYZ";

    /// <summary>BPQ port byte (0-indexed) corresponding to BPQ port 1, the
    /// only routable port in the single-instance config.</summary>
    public int BpqPortIndex => 0;

    private string? _containerId;
    private string? _tempDir;

    public async ValueTask InitializeAsync()
    {
        AgwPort = PickFreeHostPort();

        _tempDir = Path.Combine(Path.GetTempPath(), "dapps-bpq-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bpq32.cfg"), RenderConfig());

        // Direct `docker run` — Testcontainers' readiness checks were flaky
        // against this image (the entrypoint takes a few seconds to bind
        // AGW, by which point Testcontainers had given up).
        var name = "dapps-bpq-" + Guid.NewGuid().ToString("N")[..8];
        var args = $"run -d --name {name} -v {_tempDir}:/data -p {AgwPort}:{InsideAgwPort} {Image}";
        _containerId = await DockerRun(args);

        await WaitForTcp(Host, AgwPort, TimeSpan.FromSeconds(20));
        await Task.Delay(2000);
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId is not null)
        {
            try { await DockerRun($"stop -t 1 {_containerId}"); }
            catch { /* best effort — --rm handles teardown */ }
        }
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            // Files inside are root-owned (created by the container); chown
            // them back to the host user via a throwaway container before
            // deleting from the host.
            try
            {
                await DockerRun($"run --rm -v {_tempDir}:/x debian:bookworm-slim sh -c \"rm -rf /x/* /x/.* 2>/dev/null || true\"");
            }
            catch { /* best effort */ }
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<string> DockerRun(string args)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"docker {args.Split(' ')[0]} failed (exit {p.ExitCode}): {stderr}");
        }
        return stdout.Trim();
    }

    private string RenderConfig() =>
        "SIMPLE=1\n" +
        $"NODECALL={LocalCallsign}\n" +
        "NODEALIAS=NODE\n" +
        "LOCATOR=NONE\n" +
        $"AGWPORT={InsideAgwPort}\n" +
        "AGWSESSIONS=10\n" +
        "AGWMASK=1\n" +
        "\n" +
        "PORT\n" +
        " ID=Telnet\n" +
        " DRIVER=Telnet\n" +
        " CONFIG\n" +
        $" TCPPORT={InsideTelnetPort}\n" +
        " MAXSESSIONS=10\n" +
        $" USER=test,test,{LocalCallsign},,SYSOP\n" +
        "ENDPORT\n";

    private static int PickFreeHostPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitForTcp(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"linbpq AGW port {port} did not open within {timeout} (last error: {last?.Message})");
    }
}

[CollectionDefinition("Linbpq integration", DisableParallelization = true)]
public class LinbpqIntegrationCollection : ICollectionFixture<LinbpqIntegrationFixture> { }
