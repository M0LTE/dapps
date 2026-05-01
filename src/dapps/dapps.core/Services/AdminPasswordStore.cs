using System.Security.Cryptography;
using System.Text;
using dapps.core.Models;

namespace dapps.core.Services;

/// <summary>
/// Single-sysop admin password for the dashboard and admin endpoints
/// (<c>/Config</c>, <c>/Neighbours</c>, <c>/AppTokens</c>). Hashed at
/// rest with the same PBKDF2-HMAC-SHA256 + 16-byte-salt + 100k-iter
/// scheme as <see cref="AppTokenStore"/> — same threat model (a
/// hostile peer on the LAN), same cost ceiling.
///
/// Bootstrap path: <c>DAPPS_ADMIN_PASSWORD</c> env var on first start
/// (handled by <see cref="DbStartup"/>). After that the hash row in
/// <c>systemoptions</c> is authoritative; rotating happens via
/// <see cref="SetAsync"/> from <c>/Config</c>.
/// </summary>
public sealed class AdminPasswordStore(ILogger<AdminPasswordStore> logger)
{
    private const string HashOption = "AdminPasswordHash";
    private const string SaltOption = "AdminPasswordSalt";

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    public async Task<bool> IsConfiguredAsync()
    {
        var (hashHex, saltHex) = await ReadAsync();
        return !string.IsNullOrEmpty(hashHex) && !string.IsNullOrEmpty(saltHex);
    }

    /// <summary>
    /// Constant-time check of a plaintext attempt against the stored
    /// hash. Returns false (not throws) when no password is configured —
    /// callers gate that case via <see cref="IsConfiguredAsync"/>.
    /// </summary>
    public async Task<bool> VerifyAsync(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return false;
        var (hashHex, saltHex) = await ReadAsync();
        if (string.IsNullOrEmpty(hashHex) || string.IsNullOrEmpty(saltHex)) return false;

        var stored = Convert.FromHexString(hashHex);
        var salt = Convert.FromHexString(saltHex);
        var attempt = Pbkdf2(plaintext, salt);
        return CryptographicOperations.FixedTimeEquals(stored, attempt);
    }

    /// <summary>Set or rotate. Empty/null plaintext clears the password
    /// (auth then off until set again).</summary>
    public async Task SetAsync(string? plaintext)
    {
        var connection = DbInfo.GetAsyncConnection();
        if (string.IsNullOrEmpty(plaintext))
        {
            await connection.ExecuteAsync(
                "delete from systemoptions where option in (?, ?)",
                HashOption, SaltOption);
            logger.LogWarning("Admin password cleared — dashboard now unauthenticated");
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(plaintext, salt);
        await Upsert(connection, HashOption, Convert.ToHexString(hash));
        await Upsert(connection, SaltOption, Convert.ToHexString(salt));
        logger.LogInformation("Admin password updated");
    }

    private static async Task Upsert(SQLite.SQLiteAsyncConnection c, string option, string value)
    {
        var existing = await c.FindAsync<DbSystemOption>(option);
        if (existing != null)
        {
            await c.ExecuteAsync("update systemoptions set value=? where option=?", value, option);
        }
        else
        {
            await c.InsertAsync(new DbSystemOption { Option = option, Value = value });
        }
    }

    private async Task<(string? hashHex, string? saltHex)> ReadAsync()
    {
        var c = DbInfo.GetAsyncConnection();
        var hashRow = await c.FindAsync<DbSystemOption>(HashOption);
        var saltRow = await c.FindAsync<DbSystemOption>(SaltOption);
        return (hashRow?.Value, saltRow?.Value);
    }

    private static byte[] Pbkdf2(string plaintext, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(plaintext), salt, Iterations, Hash, HashBytes);
}
