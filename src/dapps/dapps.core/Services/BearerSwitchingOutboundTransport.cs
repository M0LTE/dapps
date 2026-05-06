using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Transport.Rhp;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// <see cref="IDappsOutboundTransport"/> facade that chooses between
/// AGW and RHPv2 at the moment of each <see cref="ConnectAsync"/>
/// based on the live <see cref="SystemOptions.NodeBearer"/> value.
///
/// Built so a /Config save that flips the bearer takes effect on the
/// very next outbound forward - no service restart, no factory
/// re-resolution. The forwarder loop opens fresh connections per
/// outbound anyway, so picking the impl at call-time is essentially
/// free.
/// </summary>
public sealed class BearerSwitchingOutboundTransport(
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    IDappsTxGate txGate) : IDappsOutboundTransport
{
    public Task<IDappsConnection> ConnectAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        if (string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase))
        {
            var port = opts.RhpPort > 0 ? opts.RhpPort : 9000;
            var user = string.IsNullOrEmpty(opts.RhpUser) ? null : opts.RhpUser;
            var pass = string.IsNullOrEmpty(opts.RhpPass) ? null : opts.RhpPass;
            var rhp = new Rhpv2OutboundTransport(
                opts.NodeHost, port,
                loggerFactory.CreateLogger<Rhpv2OutboundTransport>(),
                user, pass,
                txGate);
            return rhp.ConnectAsync(localCallsign, remoteCallsign, bearerPort, stoppingToken);
        }

        var agw = new AgwOutboundTransport(opts.NodeHost, opts.AgwPort, loggerFactory, txGate);
        return agw.ConnectAsync(localCallsign, remoteCallsign, bearerPort, stoppingToken);
    }
}
