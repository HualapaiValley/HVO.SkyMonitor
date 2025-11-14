namespace HVO.SkyMonitor.CameraAgent.ZWO.Models.Sample;

/// <summary>
/// Represents the status payload returned by the sample service.
/// </summary>
public sealed record SampleStatusResponse(string Message, DateTimeOffset RetrievedAtUtc);
