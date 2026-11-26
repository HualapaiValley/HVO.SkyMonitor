using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>
/// Sanitized health check for optional local operator notifications. Reports Healthy with Availability=Disabled when
/// notifications are opted out (default), Healthy when active channels are ready, and Degraded (never Unhealthy) when
/// configuration/store corruption or outbound provider failures occur so notification faults never fail host readiness.
/// </summary>
internal sealed class OperatorNotificationsHealthCheck(
    IOperatorNotificationService notificationService) : IHealthCheck
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Health check must isolate notification store/config faults as Degraded and never fail host readiness.")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string? probeErrorReasonCode = null;
        try
        {
            // Refresh store state so runtime filesystem damage (e.g. chmod 0000 or directory replacement)
            // surfaces immediately as Degraded instead of remaining green until the next delivery.
            var snapshot = await notificationService.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(snapshot.StoreStatusReasonCode))
            {
                probeErrorReasonCode = snapshot.StoreStatusReasonCode;
            }
            else if (snapshot.StoreCorrupt)
            {
                probeErrorReasonCode = NotificationReasonCodes.StoreCorrupt;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            probeErrorReasonCode = NotificationReasonCodes.StoreUnreadable;
        }

        var status = notificationService.GetStatus();
        var effectiveErrorReasonCode = probeErrorReasonCode ?? status.ConfigurationErrorReasonCode;

        if (!string.IsNullOrEmpty(effectiveErrorReasonCode))
        {
            var errorData = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Availability"] = "Degraded",
                ["ReasonCode"] = effectiveErrorReasonCode,
                ["QueueDepth"] = status.QueueDepth,
                ["SmtpState"] = status.Smtp.State.ToString(),
                ["WebhookState"] = status.Webhook.State.ToString()
            };
            return HealthCheckResult.Degraded(
                NotificationReasonCodes.DescribeForOperator(effectiveErrorReasonCode),
                data: errorData);
        }

        if (!status.Enabled)
        {
            return HealthCheckResult.Healthy(
                "Operator notifications are disabled.",
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Availability"] = "Disabled"
                });
        }

        var smtpDegraded = status.Smtp.Enabled &&
            status.Smtp.State is NotificationChannelHealthState.Degraded or NotificationChannelHealthState.OutageBackoff;
        var webhookDegraded = status.Webhook.Enabled &&
            status.Webhook.State is NotificationChannelHealthState.Degraded or NotificationChannelHealthState.OutageBackoff;

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Availability"] = smtpDegraded || webhookDegraded ? "Degraded" : "Available",
            ["QueueDepth"] = status.QueueDepth,
            ["SmtpState"] = status.Smtp.State.ToString(),
            ["WebhookState"] = status.Webhook.State.ToString()
        };

        if (smtpDegraded || webhookDegraded)
        {
            return HealthCheckResult.Degraded(
                "One or more operator notification channels are experiencing delivery failures.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            "Operator notifications are ready.",
            data: data);
    }
}
