using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two m0lte/linbpq containers wired together over AXIP-UDP, with side B
/// configured to bridge an inbound L2 connect through to a TCP listener
/// the test owns on the host. This is the inbound delivery path dapps
/// uses in production but, prior to issue #32, no test in this repo
/// exercised end-to-end.
///
/// Topology:
///
///     SUT-side AGW client ─AGW─ BPQ-A ─AXIP-UDP─ BPQ-B ─ Apps-IF ─► socat
///                                                                    │
///                                                                    ▼
///                                                                  host
///                                                              (the test)
///
/// Side B's bridge: APPLICATION 1 fires `C 1 HOST 0 TRANS S` when an L2
/// SABM arrives addressed to <see cref="ApplCallB"/>. That tells BPQ to
/// dial <c>127.0.0.1:CMDPORT[0]</c> in TRANS (binary-transparent) mode
/// and stay-on-app on disconnect. CMDPORT[0] points at <see cref="InsideAttachPort"/>,
/// where a socat sidecar (running in the same network namespace as both
/// BPQ containers) accepts and forwards to <c>host.docker.internal:&lt;AttachTcpPort&gt;</c>.
/// The test binds a regular TCP listener on the host at <see cref="AttachTcpPort"/>.
///
/// **Why <c>C N HOST &lt;slot&gt; TRANS</c> rather than <c>ATTACH &lt;port&gt; &lt;host&gt; &lt;tcp&gt;</c>:**
/// the docs (linbpq Apps Interface) describe the HOST-slot form; the
/// ATTACH-with-explicit-host form used by some production deployments
/// (e.g. gb7rdg) goes through a different post-attach C-command injection
/// path that, in this test setup, returns "Error - Invalid Command" from
/// the Telnet driver — needs separate investigation. The HOST form is
/// the documented, supported, and reliably-working path; from dapps's
/// point of view (it's reading <c>&lt;callsign&gt;\r\n</c> as the first
/// bytes on a TCP socket) the surface is identical.
///
/// **Why TRANS:** without it, BPQ's Telnet driver applies textual line
/// discipline in both directions — buffering until CR/LF, rewriting
/// CR-NUL → CR-LF, splitting long lines at 255 bytes. dapps speaks a
/// line-oriented text protocol, so non-TRANS would *probably* work, but
/// the binary-transparent TRANS mode is what we want for predictable
/// byte-level assertions in tests and is the safer default for any
/// future binary use of the bridge.
///
/// </summary>
public sealed class TwoInstanceAttachFixture : IAsyncLifetime
{
    private const string Image = "m0lte/linbpq:latest";
    private const string SocatImage = "alpine/socat:latest";

    private const int InsideAgwPortA = 18101;
    private const int InsideAgwPortB = 18102;
    private const int InsideAxipPortA = 19101;
    private const int InsideAxipPortB = 19102;
    /// <summary>The CMDPORT slot port on BPQ-B's Telnet driver. BPQ dials
    /// <c>127.0.0.1:InsideAttachPort</c> when the APPLICATION fires; the
    /// socat sidecar (bound on the same loopback) forwards to the host.</summary>
    private const int InsideAttachPort = 63088;

    public string Host => "127.0.0.1";
    public int AgwPortA { get; private set; }
    public int AgwPortB { get; private set; }

    /// <summary>The host TCP port the socat sidecar forwards inbound bridge
    /// connections to. Tests bind their listener on
    /// <c>0.0.0.0:AttachTcpPort</c> before driving the SUT.</summary>
    public int AttachTcpPort { get; private set; }

    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-9";
    public string ApplCallB => "N0BBB-9";

    /// <summary>AGW port byte for the AXIP carrier port — port 1 is
    /// Telnet, port 2 is AXIP, hence index 1 in 0-indexed AGW addressing.</summary>
    public int AxipPortIndex => 1;

    private string? _containerIdA;
    private string? _containerIdB;
    private string? _containerIdSocat;
    private string? _tempDirA;
    private string? _tempDirB;

    public async ValueTask InitializeAsync()
    {
        AgwPortA = PickFreeHostPort();
        AgwPortB = PickFreeHostPort();
        AttachTcpPort = PickFreeHostPort();

        _tempDirA = Path.Combine(Path.GetTempPath(), "dapps-bpqAtA-" + Guid.NewGuid().ToString("N")[..8]);
        _tempDirB = Path.Combine(Path.GetTempPath(), "dapps-bpqAtB-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDirA);
        Directory.CreateDirectory(_tempDirB);

        // A: vanilla two-instance config — no APPLICATION on this side, since
        // the SUT only originates from A.
        await File.WriteAllTextAsync(Path.Combine(_tempDirA, "bpq32.cfg"),
            RenderConfig(
                nodeCall: CallsignA, nodeAlias: "AAA",
                applCall: ApplCallA, applAlias: "APPLA",
                applCmd: null,                                  // no bridge on A
                cmdPort: 0,
                agwInsidePort: InsideAgwPortA, axipInsidePort: InsideAxipPortA,
                peerCall: CallsignB, peerApplCall: ApplCallB, peerAxipInsidePort: InsideAxipPortB,
                telnetInsidePort: 18111, httpInsidePort: 18121,
                netromInsidePort: 18131, fbbInsidePort: 18141, apiInsidePort: 18151));

        // B: APPLICATION fires `C 1 HOST 0 TRANS S` when an L2 SABM hits
        // ApplCallB. CMDPORT[0] is InsideAttachPort.
        await File.WriteAllTextAsync(Path.Combine(_tempDirB, "bpq32.cfg"),
            RenderConfig(
                nodeCall: CallsignB, nodeAlias: "BBB",
                applCall: ApplCallB, applAlias: "APPLB",
                applCmd: "C 1 HOST 0 TRANS S",
                cmdPort: InsideAttachPort,
                agwInsidePort: InsideAgwPortB, axipInsidePort: InsideAxipPortB,
                peerCall: CallsignA, peerApplCall: ApplCallA, peerAxipInsidePort: InsideAxipPortA,
                telnetInsidePort: 18112, httpInsidePort: 18122,
                netromInsidePort: 18132, fbbInsidePort: 18142, apiInsidePort: 18152));

        var nameA = "dapps-bpq-A-" + Guid.NewGuid().ToString("N")[..8];
        var nameB = "dapps-bpq-B-" + Guid.NewGuid().ToString("N")[..8];
        var nameSocat = "dapps-socat-" + Guid.NewGuid().ToString("N")[..8];

        // Container A publishes both BPQ AGW ports and exposes
        // host.docker.internal:host-gateway so the sidecar (sharing A's
        // netns) can reach the host TCP listener.
        var argsA = $"run -d --name {nameA} " +
                    $"--add-host=host.docker.internal:host-gateway " +
                    $"-v {_tempDirA}:/data " +
                    $"-p {AgwPortA}:{InsideAgwPortA} -p {AgwPortB}:{InsideAgwPortB} " +
                    $"{Image}";
        _containerIdA = await DockerRun(argsA);

        var argsB = $"run -d --name {nameB} " +
                    $"--network=container:{nameA} " +
                    $"-v {_tempDirB}:/data " +
                    $"{Image}";
        _containerIdB = await DockerRun(argsB);

        // socat sidecar in shared netns: listens on InsideAttachPort
        // (BPQ-B's CMDPORT target), forwards every connection to
        // host.docker.internal:AttachTcpPort. fork = one child per
        // accepted connection so multiple test cases can run in series.
        var argsSocat = $"run -d --name {nameSocat} " +
                        $"--network=container:{nameA} " +
                        $"{SocatImage} " +
                        $"-d TCP-LISTEN:{InsideAttachPort},reuseaddr,fork TCP:host.docker.internal:{AttachTcpPort}";
        _containerIdSocat = await DockerRun(argsSocat);

        await WaitForTcp(Host, AgwPortA, TimeSpan.FromSeconds(20));
        await WaitForTcp(Host, AgwPortB, TimeSpan.FromSeconds(20));
        // Grace: BPQ binds AGW immediately, but AXIP UDP and the
        // APPLICATION wiring need a beat. socat is also racing to bind.
        await Task.Delay(2000);
    }

    public async ValueTask DisposeAsync()
    {
        await TryStop(_containerIdSocat);
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
        string? applCmd, int cmdPort,
        int agwInsidePort, int axipInsidePort,
        string peerCall, string peerApplCall, int peerAxipInsidePort,
        int telnetInsidePort, int httpInsidePort,
        int netromInsidePort, int fbbInsidePort, int apiInsidePort)
    {
        // APPLICATION line is conditional — A doesn't need one, B uses
        // it for the bridge under test. Format is
        //   APPLICATION N,<word>,<command>,<call>,<alias>,<quality>
        // with quality 0 = "no NET/ROM advertisement"; we route by
        // explicit AXIP MAP between the two instances.
        var appLine = applCmd is null
            ? ""
            : $"APPLICATION 1,DAPPS,{applCmd},{applCall},{applAlias},0";

        // CMDPORT 0 means "no slots configured" — we omit the directive
        // entirely on side A which has no bridge.
        var cmdPortLine = cmdPort == 0 ? "" : $" CMDPORT {cmdPort}";

        return $"""
            SIMPLE=1
            NODECALL={nodeCall}
            NODEALIAS={nodeAlias}
            LOCATOR=NONE
            NODESINTERVAL=1
            AGWPORT={agwInsidePort}
            AGWSESSIONS=10
            AGWMASK=1
            {appLine}

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
            {cmdPortLine}
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
    }

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

[CollectionDefinition("Linbpq attach-bridge integration", DisableParallelization = true)]
public class TwoInstanceAttachCollection : ICollectionFixture<TwoInstanceAttachFixture> { }
