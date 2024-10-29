using System.Text.Json.Nodes;

namespace dapps.core.Models;

readonly record struct DappsMessage
{
    public DateTime Ttl { get; init; }
    public string SourceNode { get; init; }
    public string SourceCall { get; init; }
    public string DestNode { get; init; }
    public string DestTopic { get; init; }
    public JsonObject Data { get; init; }
}
