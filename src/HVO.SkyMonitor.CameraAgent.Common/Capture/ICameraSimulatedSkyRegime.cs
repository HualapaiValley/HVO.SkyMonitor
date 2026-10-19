using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>
/// A camera module whose rendered sky need not follow the real Sun. When <see cref="SimulatedSolarRegime"/> is set,
/// capture control uses it for image-facing decisions (setpoint defaults, regime-change resets, and the regime recorded
/// in capture evidence); the capture schedule keeps following the real Sun.
/// </summary>
internal interface ICameraSimulatedSkyRegime
{
    /// <summary>Gets the regime of the rendered sky, or <see langword="null"/> when it follows the real Sun.</summary>
    CaptureSolarRegime? SimulatedSolarRegime { get; }
}
