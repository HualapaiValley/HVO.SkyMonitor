namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

public sealed record CameraAgentSiloRolePolicy(string Role, TimeSpan? MaximumAge, bool ExpireUnderPressure, bool AllowLastCopyExpiry);

public sealed record CameraAgentSiloRetentionPolicy(bool ExpiryHeld, IReadOnlyList<CameraAgentSiloRolePolicy> Roles);

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
