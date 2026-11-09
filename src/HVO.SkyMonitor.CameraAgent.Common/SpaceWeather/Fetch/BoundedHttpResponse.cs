namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>Provider-neutral facts from the shared bounded HTTP transport.</summary>
public sealed record BoundedHttpResponse(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    SwpcTransportFailure Failure, int? StatusCode, bool ValidatorsSent = false, string? MediaType = null,
    string? EntityTag = null, string? LastModified = null, string? RetryAfter = null,
    ReadOnlyMemory<byte> Body = default, bool Oversize = false, bool BodyUndecodable = false);
