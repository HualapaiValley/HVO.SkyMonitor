namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

public sealed record CameraAgentSiloVerificationResult(long Generation, long Sequence, long VerifiedRoles,
    long UnavailableRoles, long ExpiredRoles, long FailedRoles);

/// <summary>Explicit retained-object verification at a fixed catalog cutoff.</summary>
public interface ICameraAgentSiloVerification
{
    bool BindingVerified { get; }

    ValueTask<CameraAgentSiloVerificationResult> VerifyAsync(CancellationToken cancellationToken);
}
