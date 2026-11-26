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
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = notificationService.GetStatus();

        if (!string.IsNullOrEmpty(status.ConfigurationErrorReasonCode))
        {
            var errorData = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Availability"] = "Degraded",
                ["ReasonCode"] = status.ConfigurationErrorReasonCode,
                ["QueueDepth"] = status.QueueDepth,
                ["SmtpState"] = status.Smtp.State.ToString(),
                ["WebhookState"] = status.Webhook.State.ToString()
            };
            return Task.FromResult(HealthCheckResult.Degraded(
                NotificationReasonCodes.DescribeForOperator(status.ConfigurationErrorReasonCode),
                data: errorData));
        }

        if (!status.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Operator notifications are disabled.",
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Availability"] = "Disabled"
                }));
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
            return Task.FromResult(HealthCheckResult.Degraded(
                "One or more operator notification channels are experiencing delivery failures.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Operator notifications are ready.",
            data: data));
    }
}
