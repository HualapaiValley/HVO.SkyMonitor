namespace HVO.SkyMonitor.CameraAgent.ZWO.Models.Sample;

/// <summary>
/// Represents the authenticated diagnostic payload returned by the sample API.
/// </summary>
public sealed record SampleAuthenticatedResponse(string Message, string UserName, string AuthenticationType, string AccessLevel);
