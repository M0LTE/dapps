using System.Net.Sockets;
using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Server-rendered single-page dashboard (Plan D1 + D2). Reads
/// directly from the queue and config tables; no JS, just a meta-refresh
/// loop. Posts a test message via <see cref="OnPostSendAsync"/>.
/// </summary>
public sealed class IndexModel(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    AirtimeAccountant airtime,
    ILogger<IndexModel> logger) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public double DiscoveryAirtimeConsumedSecondsLastHour { get; private set; }
    public int DiscoveryAirtimeBudgetSecondsPerHour => airtime.BudgetSecondsPerHour;

    public bool BpqAgwReachable { get; private set; }

    public int NeighbourCount { get; private set; }

    public IReadOnlyList<DbNeighbour> Neighbours { get; private set; } = [];

    public int TotalMessages { get; private set; }
    public int PendingOutbound { get; private set; }
    public int UndeliveredLocal { get; private set; }
    public IReadOnlyList<DbMessage> RecentMessages { get; private set; } = [];

    /// <summary>Messages destined for somewhere else, not yet forwarded.
    /// What the OutboundMessageManager loop is working through.</summary>
    public IReadOnlyList<DbMessage> OutboundQueue { get; private set; } = [];

    /// <summary>Messages destined for an app on this node, not yet
    /// ack'd by that app. The "inbox" half of the queue.</summary>
    public IReadOnlyList<DbMessage> LocalInbox { get; private set; } = [];

    public IReadOnlyList<DbDiscoveredPeer> DiscoveredPeers { get; private set; } = [];

    public IReadOnlyList<DbDiscoveryChannel> DiscoveryChannels { get; private set; } = [];

    [BindProperty]
    public SendForm Send { get; set; } = new();

    public string? FlashOk { get; set; }
    public string? FlashError { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        if (string.IsNullOrWhiteSpace(Send.App)) FlashError = "App is required";
        else if (string.IsNullOrWhiteSpace(Send.Destination)) FlashError = "Destination callsign is required";
        else if (string.IsNullOrWhiteSpace(Send.Payload)) FlashError = "Payload is required";

        if (FlashError is null)
        {
            try
            {
                var id = await database.SubmitOutboundMessage(
                    Send.App.Trim(),
                    Send.Destination.Trim().ToUpperInvariant(),
                    Encoding.UTF8.GetBytes(Send.Payload!));
                FlashOk = $"Queued message {id} for {Send.App}@{Send.Destination}.";
                Send = new SendForm();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dashboard test-send failed");
                FlashError = $"Send failed: {ex.Message}";
            }
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Options = options.CurrentValue;

        var neighbours = await database.GetNeighbours();
        Neighbours = neighbours.ToList();
        NeighbourCount = Neighbours.Count;

        TotalMessages = await database.CountMessages();
        PendingOutbound = await database.CountPendingOutbound();
        UndeliveredLocal = await database.CountUndeliveredLocal();
        RecentMessages = await database.GetRecentMessages(50);
        // Split the recent slice into the two operational queues.
        // Done here in C# rather than via separate DB queries because
        // the slice is small (50 rows) and we already have the data.
        var local = Options.Callsign.Split('-')[0];
        OutboundQueue = RecentMessages
            .Where(m => !m.Forwarded
                && !string.Equals(m.Destination.Split('@').Last().Split('-')[0], local, StringComparison.OrdinalIgnoreCase))
            .ToList();
        LocalInbox = RecentMessages
            .Where(m => !m.LocallyDelivered
                && string.Equals(m.Destination.Split('@').Last().Split('-')[0], local, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DiscoveredPeers = await database.GetDiscoveredPeers();
        DiscoveryChannels = await database.GetDiscoveryChannels();
        DiscoveryAirtimeConsumedSecondsLastHour = airtime.ConsumedSecondsLastHour;

        BpqAgwReachable = await ProbeTcp(Options.NodeHost, Options.AgwPort);
    }

    private static async Task<bool> ProbeTcp(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    public sealed class SendForm
    {
        public string App { get; set; } = "";
        public string Destination { get; set; } = "";
        public string? Payload { get; set; }
    }

    public static (string Label, string PillClass) MessageStatus(DbMessage m, string ourCallsign)
    {
        var local = ourCallsign.Split('-')[0];
        var dest = m.Destination.Split('@').Last().Split('-')[0];
        var isLocal = string.Equals(dest, local, StringComparison.OrdinalIgnoreCase);
        if (isLocal && m.LocallyDelivered) return ("delivered", "ok");
        if (isLocal) return ("pending local", "warn");
        if (m.Forwarded) return ("forwarded", "info");
        return ("pending", "");
    }

    public static string Age(DateTime createdAt)
    {
        var span = DateTime.UtcNow - createdAt;
        if (span.TotalSeconds < 90) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 90) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }
}
