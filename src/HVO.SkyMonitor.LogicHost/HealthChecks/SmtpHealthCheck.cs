using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Validates that the configured SMTP relay returns its protocol greeting.
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcpClient.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var bytesRead = await tcpClient.GetStream().ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var greeting = Encoding.ASCII.GetString(buffer, 0, bytesRead);
            return greeting.StartsWith("220", StringComparison.Ordinal)
                ? HealthCheckResult.Healthy("SMTP endpoint returned a ready greeting.")
                : HealthCheckResult.Unhealthy("SMTP endpoint returned an invalid greeting.");
        }
        catch (SocketException ex)
        {
            return HealthCheckResult.Unhealthy("SMTP connection failed.", ex);
        }
        catch (OperationCanceledException ex)
        {
            return HealthCheckResult.Unhealthy("SMTP health probe timed out or was cancelled.", ex);
        }
    }
}
