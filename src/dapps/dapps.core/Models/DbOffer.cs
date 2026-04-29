using SQLite;

namespace dapps.core.Models;

[Table("offers")]
internal sealed class DbOffer
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public int Length { get; init; }
    [NotNull]
    public string Format { get; init; } = "";
    public long? Salt { get; init; }
    public int? CompressedLength { get; init; }
    public string Destination { get; init; } = "";
    public string AdditionalProperties { get; set; } = "{}";
}
