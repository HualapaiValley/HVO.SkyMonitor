using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// Replaceable handlers for the orbital operator service, with call counts and the recorded keys, reasons and halt
/// times. Every value it builds is synthetic: no provider bytes, and header values that must never reach the page.
/// </summary>
internal sealed class TestOrbitalUiService : ICameraAgentOrbitalUiService
{
    internal static readonly DateTimeOffset Now = OperatorUiTestData.Now;
    internal static readonly string ManifestSha256 = new('a', 64);
    internal static readonly string PayloadSha256 = new('b', 64);
    internal static readonly string RawSha256 = new('c', 64);
    internal static readonly string RevisionSha256 = new('d', 64);
    internal static readonly string ImplementationSha256 = new('e', 64);
    internal const string SyntheticEntityTag = "synthetic-etag-1";
    internal const string SyntheticLastModified = "Mon, 19 Oct 2026 17:00:00 GMT";
    internal const string SyntheticStoreRoot = "/var/lib/hvo-synthetic/orbital-root";

    /// <summary>The configured refresh options every view carries: refresh is overdue after 12 hours without a retrieval.</summary>
    internal static readonly OrbitalElementsOptions RefreshOptions = new() { StaleAfterHours = 12 };

    internal Func<CancellationToken, ValueTask<OperatorUiResult<OrbitalDataStatusView>>> GetHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<OrbitalDataStatusView>.Success(View()));

    internal Func<string, string, ValueTask<OperatorUiResult<OrbitalCommandView>>> RefreshHandler { get; set; } =
        (_, _) => ValueTask.FromResult(Command(OperatorUiResultKind.Success, OrbitalElementsCommandStatus.Accepted,
            OrbitalElementsOperations.RefreshQueuedReasonCode));

    internal Func<DateTimeOffset, string, string, ValueTask<OperatorUiResult<OrbitalCommandView>>> ResumeHandler { get; set; } =
        (_, _, _) => ValueTask.FromResult(Command(OperatorUiResultKind.Success, OrbitalElementsCommandStatus.Applied,
            "orbital.resumed"));

    internal int Reads { get; private set; }

    internal List<string> RefreshKeys { get; } = [];

    internal List<string> RefreshReasons { get; } = [];

    internal List<(DateTimeOffset HaltSinceUtc, string Key, string Reason)> Resumes { get; } = [];

    public ValueTask<OperatorUiResult<OrbitalDataStatusView>> GetAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return GetHandler(cancellationToken);
    }

    public ValueTask<OperatorUiResult<OrbitalCommandView>> RefreshAsync(
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        RefreshKeys.Add(idempotencyKey);
        RefreshReasons.Add(reason);
        return RefreshHandler(idempotencyKey, reason);
    }

    public ValueTask<OperatorUiResult<OrbitalCommandView>> ResumeAsync(
        DateTimeOffset expectedHaltSinceUtc,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        Resumes.Add((expectedHaltSinceUtc, idempotencyKey, reason));
        return ResumeHandler(expectedHaltSinceUtc, idempotencyKey, reason);
    }

    internal static OperatorUiResult<OrbitalCommandView> Command(
        OperatorUiResultKind kind,
        OrbitalElementsCommandStatus status,
        string reasonCode,
        DateTimeOffset? nextPermitted = null)
        => new(kind, new OrbitalCommandView(status, reasonCode, nextPermitted, null), reasonCode);

    /// <summary>A healthy, enabled read model whose active snapshot was retrieved three hours ago.</summary>
    internal static OrbitalRefreshStatus Status() => new(
        OrbitalRefreshStatus.CurrentVersion,
        Now,
        7,
        Enabled: true,
        ManifestSha256,
        PayloadSha256,
        LastAttemptUtc: Now.AddHours(-3),
        LastRetrievalSuccessUtc: Now.AddHours(-3),
        LastActivationUtc: Now.AddHours(-3),
        NextDueUtc: Now.AddHours(3),
        NextPermittedAttemptUtc: Now.AddHours(-1),
        Automation: new OrbitalRefreshAutomationReference(
            OrbitalElementsAutomationAdapter.DefinitionId, RevisionSha256, "run-synthetic-1"),
        ProviderHalt: null,
        CandidateQuarantine: null,
        Reason: OrbitalElementsOperations.ReadyReasonCode,
        RecoveryAction: OrbitalRefreshRecoveryAction.None);

    /// <summary>The verified manifest of a synthetic snapshot whose newest epoch is <paramref name="newestEpochAge"/> old.</summary>
    internal static OrbitalSnapshotManifestV1 Manifest(TimeSpan? newestEpochAge = null, TimeSpan? oldestEpochAge = null)
    {
        var newest = Now - (newestEpochAge ?? TimeSpan.FromHours(6));
        var oldest = Now - (oldestEpochAge ?? TimeSpan.FromHours(30));
        return new OrbitalSnapshotManifestV1(
            "hvo-orbital-snapshot-synthetic-v1",
            "hvo-orbital-profile-synthetic-v1",
            ManifestSha256,
            PayloadSha256,
            4_096,
            12_345,
            12_340,
            oldest,
            newest,
            new OrbitalPopulationCoverage(
                "Synthetic active group", OrbitalPopulationCompleteness.CompleteForRequestedPopulation, ["synthetic-debris"]),
            [
                new OrbitalSnapshotSource(
                    CelesTrakGpSource.DatasetKey,
                    "Synthetic Provider",
                    "Synthetic lineage",
                    new Uri("https://provider.example.invalid/gp?GROUP=active"),
                    Now.AddHours(-3),
                    Now.AddHours(-4),
                    SyntheticEntityTag,
                    SyntheticLastModified,
                    RawSha256,
                    8_192,
                    new OrbitalSoftwareIdentity("synthetic-parser", "1.0.0", ImplementationSha256),
                    new OrbitalSoftwareIdentity("synthetic-normalizer", "1.0.0", ImplementationSha256),
                    new Uri("https://provider.example.invalid/terms"),
                    "Synthetic terms of use")
            ]);
    }

    internal static OrbitalScheduleSummary Schedule(bool enabled = true) => new(
        true,
        enabled,
        21_600,
        enabled ? Now.AddHours(3) : null,
        2,
        RevisionSha256,
        LocalAutomationRunOutcome.Succeeded,
        "orbital.activated",
        Now.AddHours(-3),
        "run-synthetic-1",
        false);

    /// <summary>A composed view: by default a fresh, healthy snapshot that the operator may refresh.</summary>
    internal static OrbitalDataStatusView View(
        OrbitalRefreshStatus? status = null,
        OrbitalSnapshotManifestV1? manifest = null,
        bool withManifest = true,
        string? manifestUnavailableReason = null,
        OrbitalElementsHealthState health = OrbitalElementsHealthState.Healthy,
        string? healthReason = null,
        OrbitalScheduleSummary? schedule = null,
        bool canMutate = true)
    {
        var resolved = status ?? Status();
        return new OrbitalDataStatusView(
            resolved,
            withManifest && resolved.CurrentManifestSha256 is not null ? manifest ?? Manifest() : null,
            manifestUnavailableReason,
            health,
            healthReason ?? resolved.Reason,
            schedule ?? Schedule(),
            RefreshOptions,
            canMutate);
    }
}
