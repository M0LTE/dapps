using System.Globalization;
using System.Text;
using dapps.client;

namespace dapps.core.Services;

/// <summary>
/// Pure parsed-and-validated representation of an `ihave` line. Constructed
/// only by <see cref="IHaveValidator.Validate"/>; the receiver promises
/// every constraint the spec mandates is already checked here, so callers
/// can act on the fields without re-validating.
/// </summary>
public sealed record IHaveOffer(
    string Id,
    int Length,
    string Format,                          // "p" or "d"
    long? Salt,
    int? CompressedLength,
    string Destination,
    Dictionary<string, string> AdditionalHeaders);

public sealed record OfferValidationResult
{
    public string? Id { get; init; }
    public IHaveOffer? Offer { get; init; }
    public string? Error { get; init; }

    public bool IsValid => Error is null && Offer is not null;

    public static OfferValidationResult Success(IHaveOffer offer)
        => new() { Id = offer.Id, Offer = offer };

    public static OfferValidationResult Fail(string? id, string error)
        => new() { Id = id, Error = error };
}

/// <summary>
/// Pure parser/validator for the `ihave` command line. Decoupled from I/O,
/// the database, and any framework concerns so the rejection paths can be
/// exercised by unit tests without standing up a TCP listener.
/// </summary>
public static class IHaveValidator
{
    private const string ChkSuffixPrefix = " chk=";
    private const int ChkValueLength = 4;

    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
        { "len", "fmt", "s", "clen", "dst", "chk" };

    public static OfferValidationResult Validate(string ihaveCommand)
    {
        var parts = ihaveCommand.Split(' ');
        if (parts.Length < 2 || parts[0] != "ihave")
        {
            return OfferValidationResult.Fail(null, "not an ihave command");
        }

        var id = parts[1];
        if (string.IsNullOrEmpty(id))
        {
            return OfferValidationResult.Fail(null, "missing message id");
        }

        // Parse remaining tokens as KVs. Spec forbids `=` and space inside
        // either side, so a token's first `=` is always the separator.
        var kvps = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var token = parts[i];
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
            {
                return OfferValidationResult.Fail(id, $"malformed key=value token: '{token}'");
            }
            kvps[token[..eq]] = token[(eq + 1)..];
        }

        // chk: when present, validates the rest of the line. Run this first
        // — if the line is corrupt, downstream field parsing is meaningless.
        if (kvps.TryGetValue("chk", out var chk))
        {
            var chkError = ValidateChecksum(ihaveCommand, chk);
            if (chkError != null) return OfferValidationResult.Fail(id, chkError);
        }

        if (!kvps.TryGetValue("len", out var lenStr))
            return OfferValidationResult.Fail(id, "len= is required");
        if (!int.TryParse(lenStr, NumberStyles.None, CultureInfo.InvariantCulture, out var len) || len < 0)
            return OfferValidationResult.Fail(id, "len= must be a non-negative integer");

        if (!kvps.TryGetValue("dst", out var dst))
            return OfferValidationResult.Fail(id, "dst= is required");

        var fmt = kvps.TryGetValue("fmt", out var fmtVal) ? fmtVal : "p";
        if (fmt != "p" && fmt != "d")
            return OfferValidationResult.Fail(id, $"unknown fmt={fmt}");

        var hasClen = kvps.TryGetValue("clen", out var clenStr);
        if (fmt == "d" && !hasClen)
            return OfferValidationResult.Fail(id, "clen= is required when fmt=d");
        if (fmt == "p" && hasClen)
            return OfferValidationResult.Fail(id, "clen= MUST NOT be supplied when fmt=p");
        int? clen = null;
        if (hasClen)
        {
            if (!int.TryParse(clenStr, NumberStyles.None, CultureInfo.InvariantCulture, out var clenValue) || clenValue < 0)
                return OfferValidationResult.Fail(id, "clen= must be a non-negative integer");
            clen = clenValue;
        }

        long? salt = null;
        if (kvps.TryGetValue("s", out var sStr))
        {
            if (!long.TryParse(sStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s))
                return OfferValidationResult.Fail(id, "s= must be a 64-bit signed integer");
            salt = s;
        }

        var headers = kvps
            .Where(kv => !ReservedKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return OfferValidationResult.Success(new IHaveOffer(id, len, fmt, salt, clen, dst, headers));
    }

    /// <summary>
    /// Validate that `chk=NNNN` is the last KV, that no other `chk=` appears
    /// in the line, and that the CRC-16/CCITT-FALSE of the bytes-before-chk
    /// matches. Returns null on success, error string on any failure.
    /// </summary>
    private static string? ValidateChecksum(string ihaveCommand, string providedChk)
    {
        var prefixIndex = ihaveCommand.LastIndexOf(ChkSuffixPrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return "chk parsed from KVs but not present in line as ' chk=' suffix";
        }

        if (prefixIndex + ChkSuffixPrefix.Length + ChkValueLength != ihaveCommand.Length)
        {
            return "chk MUST be the last KV on the line";
        }

        var firstChkIndex = ihaveCommand.IndexOf("chk=", StringComparison.Ordinal);
        if (firstChkIndex != prefixIndex + 1)
        {
            return "chk= must appear only as the last KV";
        }

        if (!ushort.TryParse(providedChk, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var providedValue)
            || providedChk.Length != ChkValueLength)
        {
            return "chk value is not 4 hex characters";
        }

        var coveredBytes = Encoding.UTF8.GetBytes(ihaveCommand[..prefixIndex]);
        var expected = Crc16CcittFalse.Compute(coveredBytes);

        return expected == providedValue ? null : "chk mismatch (line corruption?)";
    }
}
