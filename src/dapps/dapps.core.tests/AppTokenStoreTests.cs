using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests the per-app credential store (Plan A4). Tokens are PBKDF2-
/// hashed at rest and never returned after the initial creation;
/// verification is constant-time-equality on the derived bytes.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AppTokenStoreTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppTokenStore store = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-tokens-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbAppToken>();
        }
        store = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateOrRotate_ReturnsPlaintextThatVerifiesToTheRightApp()
    {
        var plaintext = await store.CreateOrRotateAsync("myapp");

        plaintext.Should().NotBeNullOrEmpty();
        (await store.VerifyAsync(plaintext)).Should().Be("myapp");
    }

    [Fact]
    public async Task CreateOrRotate_RotateInvalidatesPriorToken()
    {
        var first = await store.CreateOrRotateAsync("myapp");
        var second = await store.CreateOrRotateAsync("myapp");

        first.Should().NotBe(second, "rotation MUST regenerate fresh entropy");
        (await store.VerifyAsync(first)).Should().BeNull("the prior token must stop verifying after rotation");
        (await store.VerifyAsync(second)).Should().Be("myapp");
    }

    [Fact]
    public async Task VerifyAsync_UnknownToken_ReturnsNull()
    {
        await store.CreateOrRotateAsync("a");
        (await store.VerifyAsync("not-a-real-token-string")).Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_EmptyToken_ReturnsNull()
    {
        await store.CreateOrRotateAsync("a");
        (await store.VerifyAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_RemovesTheRow()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var deleted = await store.RevokeAsync("myapp");

        deleted.Should().BeTrue();
        (await store.VerifyAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_AbsentApp_ReturnsFalse()
    {
        var deleted = await store.RevokeAsync("nope");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsAppNamesNotSecrets()
    {
        await store.CreateOrRotateAsync("alpha");
        await store.CreateOrRotateAsync("beta");

        var entries = await store.ListAsync();

        entries.Should().HaveCount(2);
        entries.Select(e => e.AppName).Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task StoredHash_NotPlaintext()
    {
        var plaintext = await store.CreateOrRotateAsync("myapp");

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbAppToken>("myapp")!;
        row.TokenHash.Should().NotContain(plaintext, "the plaintext must not appear in the hash column");
        row.Salt.Should().HaveLength(32, "salt is 16 random bytes hex-encoded");
        row.TokenHash.Should().HaveLength(64, "hash is 32 derived bytes hex-encoded");
    }
}
