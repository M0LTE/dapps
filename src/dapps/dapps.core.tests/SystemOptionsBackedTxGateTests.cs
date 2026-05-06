using AwesomeAssertions;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;

namespace dapps.core.tests;

/// <summary>
/// PR 2 wiring of the production gate. Confirms the gate composes
/// the local SystemOptions toggle and the remote signal correctly,
/// and that flipping IOptionsMonitor.CurrentValue is reflected on
/// the next read (no caching).
/// </summary>
public class SystemOptionsBackedTxGateTests
{
    private sealed class StubOptions : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions Value { get; set; } = new() { TxEnabled = true };
        public SystemOptions CurrentValue => Value;
        public SystemOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }

    private sealed class StubRemote : ITxKillSwitchSignal
    {
        public bool RemoteAllowed { get; set; } = true;
        public string? RemoteBlockReason { get; set; }
    }

    [Fact]
    public void TxAllowed_True_WhenBothSignalsAllow()
    {
        var opts = new StubOptions();
        var rem = new StubRemote();
        var gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeTrue();
        gate.LocalAllowed.Should().BeTrue();
        gate.RemoteAllowed.Should().BeTrue();
        gate.BlockReason.Should().BeNull();
    }

    [Fact]
    public void TxAllowed_False_WhenLocalBlocks()
    {
        var opts = new StubOptions { Value = new SystemOptions { TxEnabled = false } };
        var rem = new StubRemote();
        var gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeFalse();
        gate.LocalAllowed.Should().BeFalse();
        gate.RemoteAllowed.Should().BeTrue();
        gate.BlockReason.Should().Contain("master TX-stop");
    }

    [Fact]
    public void TxAllowed_False_WhenRemoteBlocks()
    {
        var opts = new StubOptions();
        var rem = new StubRemote { RemoteAllowed = false, RemoteBlockReason = "external-kill-switch fired" };
        var gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeFalse();
        gate.LocalAllowed.Should().BeTrue();
        gate.RemoteAllowed.Should().BeFalse();
        gate.BlockReason.Should().Contain("external-kill-switch fired");
    }

    [Fact]
    public void BlockReason_ComposesBothWhenBothBlock()
    {
        var opts = new StubOptions { Value = new SystemOptions { TxEnabled = false } };
        var rem = new StubRemote { RemoteAllowed = false, RemoteBlockReason = "remote stop" };
        var gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeFalse();
        gate.BlockReason.Should().Contain("master TX-stop");
        gate.BlockReason.Should().Contain("remote stop");
    }

    [Fact]
    public void TxAllowed_LiveReadsMonitorEachCall()
    {
        var opts = new StubOptions();
        var rem = new StubRemote();
        var gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeTrue();

        opts.Value = new SystemOptions { TxEnabled = false };
        gate.TxAllowed.Should().BeFalse(
            "the gate must read IOptionsMonitor.CurrentValue per call so a /Config flip takes effect immediately");

        opts.Value = new SystemOptions { TxEnabled = true };
        gate.TxAllowed.Should().BeTrue();
    }

    [Fact]
    public void OpenTxKillSwitchSignal_AlwaysAllows()
    {
        var sig = new OpenTxKillSwitchSignal();
        sig.RemoteAllowed.Should().BeTrue();
        sig.RemoteBlockReason.Should().BeNull();
    }

    [Fact]
    public void GateIsAlsoIDappsTxGate()
    {
        var opts = new StubOptions();
        var rem = new StubRemote();
        IDappsTxGate gate = new SystemOptionsBackedTxGate(opts, rem);

        gate.TxAllowed.Should().BeTrue();
        gate.BlockReason.Should().BeNull();
    }
}
