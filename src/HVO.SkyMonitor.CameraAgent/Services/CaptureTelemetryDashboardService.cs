using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Services;

public sealed class CaptureTelemetryDashboardService
{
    private readonly ICaptureTelemetryProvider _provider;
    private readonly ILogger<CaptureTelemetryDashboardService> _logger;

    public CaptureTelemetryDashboardService(
        ICaptureTelemetryProvider provider,
        ILogger<CaptureTelemetryDashboardService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public ValueTask<CaptureTelemetrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = _provider.GetSnapshot();
            return ValueTask.FromResult(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve capture telemetry snapshot.");
            throw;
        }
    }
}
