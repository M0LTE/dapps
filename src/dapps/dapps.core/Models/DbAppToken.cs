using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per app that has a credential issued. The token is never
/// stored — only its PBKDF2-HMAC-SHA256 hash plus the per-row salt that
/// derived it. Operators see the plaintext exactly once at creation
/// time and store it elsewhere.
/// </summary>
[Table("apptokens")]
public sealed class DbAppToken
{
    [PrimaryKey]
    public string AppName { get; set; } = "";

    /// <summary>Hex-encoded PBKDF2-HMAC-SHA256 output (32 bytes → 64 hex chars).</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>Hex-encoded random salt fed into the KDF (16 bytes → 32 hex chars).</summary>
    public string Salt { get; set; } = "";

    /// <summary>UTC instant the token was issued. Useful for audit and rotation policies.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
