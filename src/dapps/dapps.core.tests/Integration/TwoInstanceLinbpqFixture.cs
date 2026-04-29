using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two m0lte/linbpq containers wired together over AXIP-UDP.
///
/// Topology (shared with the linbpq native two-instance test pattern):
///
///     SUT-side AGW client ─AGW─ BPQ-A ─AXIP-UDP─ BPQ-B ─AGW─ Receiver
///
/// Both BPQs are configured with an APPLICATION binding so the dialled
/// callsign (APPL1CALL) is dispatched to a registered AGW listener on
/// the receiving side. AXIP MAP entries on each side route SABMs for
/// the peer's NODECALL and APPL1CALL over UDP to the peer.
///
/// Networking: both containers share container A's network namespace
/// (<c>--network=container:&lt;a&gt;</c>) so they reach each other on the
/// same loopback — equivalent to the two-process-on-127.0.0.1 setup
/// the upstream linbpq test runs on natively. Bridge networking with
/// distinct container IPs has not been verified to work for AGW
/// dispatch and is avoided.
/// </summary>
public sealed class TwoInstanceLinbpqFixture : IAsyncLifetime
{
    private const string Image = "m0lte/linbpq:latest";

    private const int InsideAgwPortA = 18001;
    private const int InsideAgwPortB = 18002;
    private const int InsideAxipPortA = 19001;
    private const int InsideAxipPortB = 19002;

    public string Host => "127.0.0.1";
    public int AgwPortA { get; private set; }
    public int AgwPortB { get; private set; }

    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-9";
    public string ApplCallB => "N0BBB-9";

    /// <summary>AGW port byte (0-indexed) that points at the AXIP carrier
    /// port on either BPQ — port 1 is Telnet, port 2 is AXIP, hence index 1.
    /// Use this when the SUT issues a connect that needs to reach the
    /// peer instance.</summary>
    public int AxipPortIndex => 1;

    private string? _containerIdA;
    private string? _containerIdB;
    private string? _tempDirA;
    private string? _tempDirB;

    public async ValueTask InitializeAsync()
    {
        AgwPortA = PickFreeHostPort();
        AgwPortB = PickFreeHostPort();

        _tempDirA = Path.Combine(Path.GetTempPath(), "dapps-bpqA-" + Guid.NewGuid().ToString("N")[..8]);
        _tempDirB = Path.Combine(Path.GetTempPath(), "dapps-bpqB-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDirA);
        Directory.CreateDirectory(_tempDirB);

        await File.WriteAllTextAsync(Path.Combine(_tempDirA, "bpq32.cfg"),
            RenderConfig(
                nodeCall: CallsignA, nodeAlias: "AAA",
                applCall: ApplCallA, applAlias: "APPLA",
                agwInsidePort: InsideAgwPortA, axipInsidePort: InsideAxipPortA,
                peerCall: CallsignB, peerApplCall: ApplCallB, peerAxipInsidePort: InsideAxipPortB,
                telnetInsidePort: 18011, httpInsidePort: 18021,
                netromInsidePort: 18031, fbbInsidePort: 18041, apiInsidePort: 18051));

        await File.WriteAllTextAsync(Path.Combine(_tempDirB, "bpq32.cfg"),
            RenderConfig(
                nodeCall: CallsignB, nodeAlias: "BBB",
                applCall: ApplCallB, applAlias: "APPLB",
                agwInsidePort: InsideAgwPortB, axipInsidePort: InsideAxipPortB,
                peerCall: CallsignA, peerApplCall: ApplCallA, peerAxipInsidePort: InsideAxipPortA,
                telnetInsidePort: 18012, httpInsidePort: 18022,
                netromInsidePort: 18032, fbbInsidePort: 18042, apiInsidePort: 18052));

        var nameA = "dapps-bpq-A-" + Guid.NewGuid().ToString("N")[..8];
        var nameB = "dapps-bpq-B-" + Guid.NewGuid().ToString("N")[..8];

        // A publishes both AGW ports (B shares A's netns, so its AGW port
        // also reaches the host through A's port mapping).
        var argsA = $"run -d --name {nameA} " +
                    $"-v {_tempDirA}:/data " +
                    $"-p {AgwPortA}:{InsideAgwPortA} -p {AgwPortB}:{InsideAgwPortB} " +
                    $"{Image}";
        _containerIdA = await DockerRun(argsA);

        var argsB = $"run -d --name {nameB} " +
                    $"--network=container:{nameA} " +
                    $"-v {_tempDirB}:/data " +
                    $"{Image}";
        _containerIdB = await DockerRun(argsB);

        await WaitForTcp(Host, AgwPortA, TimeSpan.FromSeconds(20));
        await WaitForTcp(Host, AgwPortB, TimeSpan.FromSeconds(20));
        // Tiny grace period: linbpq's AGW listener accepts immediately but
        // the AXIP UDP socket binds a moment later.
        await Task.Delay(2000);
    }

    public async ValueTask DisposeAsync()
    {
        await TryStop(_containerIdB);
        await TryStop(_containerIdA);
        await TryCleanupTemp(_tempDirA);
        await TryCleanupTemp(_tempDirB);
    }

    private static async Task TryStop(string? containerId)
    {
        if (containerId is null) return;
        try { await DockerRun($"stop -t 1 {containerId}"); }
        catch { /* best effort */ }
        try { await DockerRun($"rm -f {containerId}"); }
        catch { /* best effort */ }
    }

    private static async Task TryCleanupTemp(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            // Files are root-owned (created inside the container); chown
            // them back via a throwaway container before deleting.
            await DockerRun($"run --rm -v {dir}:/x debian:bookworm-slim sh -c \"rm -rf /x/* /x/.* 2>/dev/null || true\"");
        }
        catch { /* best effort */ }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
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

    private static string RenderConfig(
        string nodeCall, string nodeAlias,
        string applCall, string applAlias,
        int agwInsidePort, int axipInsidePort,
        string peerCall, string peerApplCall, int peerAxipInsidePort,
        int telnetInsidePort, int httpInsidePort,
        int netromInsidePort, int fbbInsidePort, int apiInsidePort) =>
        $"""
        SIMPLE=1
        NODECALL={nodeCall}
        NODEALIAS={nodeAlias}
        LOCATOR=NONE
        NODESINTERVAL=1
        AGWPORT={agwInsidePort}
        AGWSESSIONS=10
        AGWMASK=1
        APPLICATIONS={applAlias}
        APPL1CALL={applCall}
        APPL1ALIAS={applAlias}

        PORT
         ID=Telnet
         DRIVER=Telnet
         CONFIG
         TCPPORT={telnetInsidePort}
         HTTPPORT={httpInsidePort}
         NETROMPORT={netromInsidePort}
         FBBPORT={fbbInsidePort}
         APIPORT={apiInsidePort}
         MAXSESSIONS=20
         USER=test,test,{nodeCall},,SYSOP
        ENDPORT

        PORT
         ID=AXIP
         DRIVER=BPQAXIP
         QUALITY=200
         MINQUAL=1
         CONFIG
         UDP {axipInsidePort}
         BROADCAST NODES
         MAP {peerCall} 127.0.0.1 UDP {peerAxipInsidePort} B
         MAP {peerApplCall} 127.0.0.1 UDP {peerAxipInsidePort} B
        ENDPORT

        ROUTES:
        {peerCall},200,2
        ***

        """;

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

[CollectionDefinition("Linbpq two-instance integration", DisableParallelization = true)]
public class TwoInstanceLinbpqCollection : ICollectionFixture<TwoInstanceLinbpqFixture> { }
