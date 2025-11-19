using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Controls dashboard preview refresh behavior.
/// </summary>
public sealed class CapturePreviewOptions
{
    private const int DefaultPollingSeconds = 2;

    /// <summary>
    /// Gets or sets the refresh cadence (in seconds) for telemetry and preview polling.
    /// </summary>
    [Range(1, 300)]
    public int PollingIntervalSeconds { get; init; } = DefaultPollingSeconds;
}
