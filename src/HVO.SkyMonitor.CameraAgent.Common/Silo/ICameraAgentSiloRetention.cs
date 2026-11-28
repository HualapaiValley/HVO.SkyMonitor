using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

public sealed record CameraAgentSiloRolePolicy(string Role, TimeSpan? MaximumAge, bool ExpireUnderPressure, bool AllowLastCopyExpiry);

public sealed record CameraAgentSiloCaptureVariantRetention(string Role, [property: JsonRequired] TimeSpan MaximumAge,
    string? MediaType = null, string? Variant = null);

public sealed record CameraAgentSiloCaptureRetentionProfile(string Name,
    IReadOnlyList<CameraAgentSiloCaptureVariantRetention> Variants,
    IReadOnlyList<string>? WindowIds = null, IReadOnlyList<string>? SkyConditions = null);

public sealed record CameraAgentSiloRetentionPolicy(bool ExpiryHeld, IReadOnlyList<CameraAgentSiloRolePolicy> Roles,
    IReadOnlyList<CameraAgentSiloCaptureRetentionProfile>? CaptureProfiles = null);

public sealed record CameraAgentSiloPin(string PinId, string Kind, string ArtifactId, string Revision, bool Active, string Reason);

public sealed record CameraAgentSiloRetentionCandidate(string Key, string Version, string Decision,
    IReadOnlyList<string> Roles, long LogicalBytes);

public interface ICameraAgentSiloRetention
{
    bool Ready { get; }

    ValueTask SetPolicyAsync(CameraAgentSiloRetentionPolicy policy, CancellationToken cancellationToken);

    ValueTask SetPinAsync(CameraAgentSiloPin pin, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CameraAgentSiloRetentionCandidate>> DryRunAsync(
        DateTimeOffset asOfUtc, bool underPressure, CancellationToken cancellationToken);
}
