using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Validates basic TCP connectivity to the configured SMTP relay.
/// </summary>
internal sealed class SmtpHealthCheck : IHealthCheck
{
    private readonly SmtpOptions _options;

    public SmtpHealthCheck(IOptions<SmtpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            return HealthCheckResult.Unhealthy("SMTP host is not configured.");
        }

        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(_options.Host, _options.Port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                return HealthCheckResult.Unhealthy("SMTP connection timed out.");
            }

            await connectTask.ConfigureAwait(false);
            return HealthCheckResult.Healthy("SMTP endpoint accepted a TCP connection.");
        }
        catch (SocketException ex)
        {
            return HealthCheckResult.Unhealthy("SMTP connection failed.", ex);
        }
        catch (OperationCanceledException ex)
        {
            return HealthCheckResult.Unhealthy("SMTP health probe was cancelled.", ex);
        }
    }
}
