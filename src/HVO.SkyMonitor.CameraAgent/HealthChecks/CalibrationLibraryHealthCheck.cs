using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class CalibrationLibraryHealthCheck(
    SqliteCalibrationLibraryStore store,
    ICameraAgentConfigurationAccessor configurationAccessor) : IHealthCheck
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The anonymous health boundary returns a fixed failure without disclosing durable-state details.")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!configurationAccessor.IsConfigured)
        {
            return HealthCheckResult.Degraded(
                "Waiting for calibration configuration.",
                data: CreateData(required: false, status: null));
        }

        try
        {
            var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);
            var required = IsLibraryRequired(configuration);
            var status = await store.GetOperationsStatusAsync(cancellationToken).ConfigureAwait(false);
            var data = CreateData(required, status);
            if (status.State.ActiveBundle is { } active)
            {
                var validation = store.ActiveBundleValidation;
                if (!string.Equals(
                        validation.BundleIdentitySha256,
                        active.BundleIdentitySha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return HealthCheckResult.Degraded(
                        "The active calibration bundle has not completed validation.", data: data);
                }
                if (string.Equals(
                        validation.ReasonCode, "calibration.library.validation-pending", StringComparison.Ordinal))
                {
                    return HealthCheckResult.Degraded(
                        "The active calibration bundle has not completed startup validation.", data: data);
                }
                if (!string.Equals(validation.ReasonCode, "calibration.library.selected", StringComparison.Ordinal))
                {
                    return HealthCheckResult.Unhealthy(
                        "The active calibration bundle failed integrity validation.", data: data);
                }
                if (!string.Equals(active.PublicationState, "published", StringComparison.Ordinal))
                {
                    return HealthCheckResult.Unhealthy(
                        "The active calibration bundle is not completely published.", data: data);
                }
            }
            else if (required)
            {
                return HealthCheckResult.Degraded(
                    "Calibration is required but no bundle is active.", data: data);
            }

            if (status.PendingAcquisition is not null ||
                string.Equals(
                    status.State.LastReconciliationReason,
                    "calibration.library.reconciliation-failed",
                    StringComparison.Ordinal))
            {
                return HealthCheckResult.Degraded(
                    "Calibration acquisition or reconciliation requires attention.", data: data);
            }

            return HealthCheckResult.Healthy(
                required
                    ? "The required calibration library has a valid active bundle."
                    : "Calibration library use is not required by the active processing graph.",
                data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Calibration library durable state is unavailable.");
        }
    }

    private static bool IsLibraryRequired(HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration)
        => configuration.ResolveProcessingSteps().Any(step =>
        {
            if (!step.Required || !string.Equals(step.Type, "Calibration", StringComparison.Ordinal))
            {
                return false;
            }
            var options = step.Options?.Deserialize<CalibrationProcessingStepOptions>(SerializerOptions)
                ?? new CalibrationProcessingStepOptions();
            return options.Enabled && string.Equals(options.Strategy, "CalibrationLibrary", StringComparison.Ordinal);
        });

    private static Dictionary<string, object> CreateData(
        bool required,
        CalibrationLibraryOperationsStatus? status)
        => new()
        {
            ["Required"] = required,
            ["Active"] = status?.State.ActiveBundle is not null,
            ["ActiveState"] = status?.State.ActiveBundle?.PublicationState ?? "inactive",
            ["StateVersion"] = status?.State.Version ?? 0,
            ["PendingAcquisitionState"] = status?.PendingAcquisition?.State ?? "none",
            ["LastSelectionReason"] = CalibrationTelemetry.NormalizeReason(status?.State.LastSelectionReason),
            ["LastSelectionUtc"] = status?.State.LastSelectionUtc?.ToString("O") ?? "none",
            ["BundleCount"] = status?.PublishedBundleCount ?? 0,
            ["QuarantineCount"] = status?.QuarantineCount ?? 0,
            ["LastActivationResult"] = status?.LastActivation?.CommandKind ?? "none",
            ["LastActivationUtc"] = status?.LastActivation?.ActivatedUtc.ToString("O") ?? "none",
            ["LastReconciliationResult"] = CalibrationTelemetry.NormalizeReason(
                status?.State.LastReconciliationReason),
            ["LastReconciliationUtc"] = status?.State.LastReconciliationUtc?.ToString("O") ?? "none"
        };
}
