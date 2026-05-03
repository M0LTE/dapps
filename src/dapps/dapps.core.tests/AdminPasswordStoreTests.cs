using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

[Collection(SqliteOverridePathCollection.Name)]
public sealed class AdminPasswordStoreTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-adminpwd-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbSystemOption>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FreshStore_IsNotConfigured()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        (await store.IsConfiguredAsync()).Should().BeFalse();
        (await store.VerifyAsync("anything")).Should().BeFalse(
            "verify against an unset store always returns false - never throws");
    }

    [Fact]
    public async Task SetAndVerify_RoundTripsTheCorrectPassword()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("hunter2");

        (await store.IsConfiguredAsync()).Should().BeTrue();
        (await store.VerifyAsync("hunter2")).Should().BeTrue();
        (await store.VerifyAsync("hunter3")).Should().BeFalse();
        (await store.VerifyAsync("")).Should().BeFalse();
        (await store.VerifyAsync(null)).Should().BeFalse();
    }

    [Fact]
    public async Task SetTwice_RotatesAndOldPasswordStopsWorking()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("first");
        await store.SetAsync("second");

        (await store.VerifyAsync("first")).Should().BeFalse();
        (await store.VerifyAsync("second")).Should().BeTrue();
    }

    [Fact]
    public async Task SetEmpty_ClearsTheStore()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("temp");
        await store.SetAsync("");

        (await store.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task EachSet_UsesADifferentSalt()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("same-password");
        var hash1 = ReadHash();
        await store.SetAsync("same-password");
        var hash2 = ReadHash();

        hash1.Should().NotBe(hash2,
            "fresh salt every set; same plaintext shouldn't produce the same hash");
    }

    private static string ReadHash()
    {
        using var c = DbInfo.GetConnection();
        return c.Find<DbSystemOption>("AdminPasswordHash")!.Value;
    }
}
