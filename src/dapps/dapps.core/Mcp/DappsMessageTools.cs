using System.ComponentModel;
using dapps.core.Models;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-A - read-only tools covering the message surface:
/// single-message lookup, recent-tail, dropped-message log. The
/// composite "explain why X failed" tool in PR-C will weave these
/// reads with route lookups + recent-events filters into a narrative.
/// </summary>
[McpServerToolType]
public sealed class DappsMessageTools(Database database)
{
    [McpServerTool(Name = "get_message")]
    [Description(
        "Single message by id. Returns null if the row has been delivered and cleaned up, was dropped " +
        "(see list_dropped_messages - soft-deletes still surface there), or never existed. Payload is " +
        "base64-encoded bytes. Use this when an operator asks 'what happened to abc1234?'.")]
    public async Task<DbMessage?> GetMessageAsync(
        [Description("7-character message id, e.g. 'abc1234'.")] string id)
        => await database.GetMessage(id);

    [McpServerTool(Name = "list_recent_messages")]
    [Description(
        "Tail of the messages table - newest first, capped at 50 rows by default. The same data the " +
        "dashboard's queue view shows. Includes both pending forwards and locally-bound messages.")]
    public async Task<IReadOnlyList<DbMessage>> ListRecentMessagesAsync(
        [Description("Max rows to return (1-200, default 50).")] int limit = 50)
    {
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;
        return await database.GetRecentMessages(limit);
    }

    [McpServerTool(Name = "list_dropped_messages")]
    [Description(
        "Recently-dropped messages - TTL-expired, hash-mismatch, or otherwise soft-deleted from the live " +
        "queue. Carries the original payload + headers + a Reason field. Most useful for 'why didn't X ship?' " +
        "diagnostics: a missing message in get_message often turns up here.")]
    public async Task<IReadOnlyList<DbDroppedMessage>> ListDroppedMessagesAsync(
        [Description("Max rows to return (1-200, default 50).")] int limit = 50)
    {
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;
        return await database.GetRecentDroppedMessages(limit);
    }
}
