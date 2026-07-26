using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed class CalibrationLibraryValidationService(
    SqliteCalibrationLibraryStore store,
    ICameraAgentConfigurationAccessor configurationAccessor,
    CalibrationTelemetry telemetry) : BackgroundService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Calibration validation must not stop raw acquisition when optional library state is unavailable.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = await configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await store.ValidateActiveBundleAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                telemetry.RecordFailure("initialize", "other");
                telemetry.RecordLibraryOperation("initialize", "failure", failed: 1);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
