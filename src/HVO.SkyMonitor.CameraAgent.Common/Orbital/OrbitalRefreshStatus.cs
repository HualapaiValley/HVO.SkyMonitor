using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>References the existing automation identity; it does not define another job or schedule.</summary>
public sealed record OrbitalRefreshAutomationReference(string DefinitionId, string RevisionSha256, string? RunKey);
public enum OrbitalRefreshRecoveryAction { None, Configure, WaitForPermittedAttempt, ResumeProvider, InspectQuarantine, RepairStorage }
public sealed record OrbitalProviderHalt(DateTimeOffset SinceUtc, int HttpStatusCode, string Reason);
public sealed record OrbitalCandidateQuarantine(DateTimeOffset ObservedUtc, string? RawSha256, string Reason);

/// <summary>
/// Host-owned operational read model for Software & Catalog. Scientific record age belongs in Astronomy;
/// disabled/halted refresh can coexist with a readable active manifest. No refresh commands or persistence here.
/// </summary>
public sealed record OrbitalRefreshStatus(string SchemaVersion, DateTimeOffset ObservedUtc, long Revision,
    bool Enabled, string? CurrentManifestSha256, string? CurrentPayloadSha256,
    DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastRetrievalSuccessUtc, DateTimeOffset? LastActivationUtc,
    DateTimeOffset? NextDueUtc, DateTimeOffset? NextPermittedAttemptUtc,
    OrbitalRefreshAutomationReference? Automation, OrbitalProviderHalt? ProviderHalt,
    OrbitalCandidateQuarantine? CandidateQuarantine, string Reason, OrbitalRefreshRecoveryAction RecoveryAction)
{
    public const string CurrentVersion = "hvo-orbital-refresh-status-v1";

    public void Validate()
    {
        if (SchemaVersion != CurrentVersion || ObservedUtc.Offset != TimeSpan.Zero || Revision < 0 ||
            !Enum.IsDefined(RecoveryAction) || string.IsNullOrWhiteSpace(Reason) || Reason.Length > 512 ||
            (CurrentManifestSha256 is null) != (CurrentPayloadSha256 is null) ||
            CurrentManifestSha256 is not null && !OrbitalContract.IsSha256(CurrentManifestSha256) ||
            CurrentPayloadSha256 is not null && !OrbitalContract.IsSha256(CurrentPayloadSha256) ||
            new[] { LastAttemptUtc, LastRetrievalSuccessUtc, LastActivationUtc, NextDueUtc, NextPermittedAttemptUtc }
                .Any(static value => value is { Offset.Ticks: not 0 }) ||
            Automation is not null && (string.IsNullOrWhiteSpace(Automation.DefinitionId) || Automation.DefinitionId.Length > 64 ||
                !OrbitalContract.IsSha256(Automation.RevisionSha256) || Automation.RunKey is not null && (string.IsNullOrWhiteSpace(Automation.RunKey) || Automation.RunKey.Length > 128)) ||
            ProviderHalt is not null && (ProviderHalt.SinceUtc.Offset != TimeSpan.Zero || ProviderHalt.HttpStatusCode is < 100 or > 599 or 200 ||
                string.IsNullOrWhiteSpace(ProviderHalt.Reason) || ProviderHalt.Reason.Length > 512) ||
            CandidateQuarantine is not null && (CandidateQuarantine.ObservedUtc.Offset != TimeSpan.Zero ||
                CandidateQuarantine.RawSha256 is not null && !OrbitalContract.IsSha256(CandidateQuarantine.RawSha256) ||
                string.IsNullOrWhiteSpace(CandidateQuarantine.Reason) || CandidateQuarantine.Reason.Length > 512))
            throw new ArgumentException("Invalid orbital refresh read model.");
    }
}
