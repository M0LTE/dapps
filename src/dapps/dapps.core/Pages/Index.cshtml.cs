using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Overview page (/) - lean first paint plus a JS layer driven by
/// <c>/Operational?full=true</c>. The model only needs SystemOptions
/// and the TX-gate state for the page header; live data (queue counts,
/// node reachability, decisions ring, airtime, update card) all come
/// from the snapshot the layout polls.
/// </summary>
public sealed class IndexModel(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    SystemOptionsBackedTxGate txGate,
    ILogger<IndexModel> logger) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public bool TxAllowed => txGate.TxAllowed;
    public string? TxBlockReason => txGate.BlockReason;

    [BindProperty]
    public SendForm Send { get; set; } = new();

    public string? FlashOk { get; set; }
    public string? FlashError { get; set; }

    public Task OnGetAsync()
    {
        Options = options.CurrentValue;
        return Task.CompletedTask;
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        Options = options.CurrentValue;

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
                logger.LogError(ex, "Overview test-send failed");
                FlashError = $"Send failed: {ex.Message}";
            }
        }

        return Page();
    }

    public sealed class SendForm
    {
        public string App { get; set; } = "";
        public string Destination { get; set; } = "";
        public string? Payload { get; set; }
    }
}
