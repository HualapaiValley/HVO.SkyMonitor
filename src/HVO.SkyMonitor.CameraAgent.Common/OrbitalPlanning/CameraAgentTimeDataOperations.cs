using System.Data.Common;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// The last refresh attempt. <c>Outcome</c> is <c>never</c>, <c>in-progress</c>, <c>interrupted</c> (a stopped process
/// left it in progress), <c>adopted</c>, <c>unchanged</c>, <c>not-newer</c> or <c>failed</c>. A failure here never
/// changes the active pair; whether that pair has expired is reported separately.
/// </summary>
public sealed record CameraAgentTimeDataRefreshStatus(string Outcome, DateTimeOffset? LastAttemptUtc, DateTimeOffset? CompletedUtc,
    string? ReasonCode, string? Detail, DateTimeOffset? LastSuccessUtc, DateTimeOffset? NextPermittedAttemptUtc);

/// <summary>The adopted managed pair, whether or not it is active. <c>State</c> is <c>verified</c> or <c>invalid</c>.</summary>
public sealed record CameraAgentManagedTimePairStatus(string Pair, DateTimeOffset AdoptedUtc, DateTimeOffset RetrievedUtc, string State,
    DateTimeOffset? CoverageEndUtc, string EarthOrientationSha256, string LeapSecondsSha256);

/// <summary>
/// Read-only time-data status for the setup panel, the operations API and <c>hvo-sky</c>. <c>ActiveSource</c> is
/// <c>override</c>, <c>managed</c> or <c>bundled</c>; <c>Problem</c> is <c>none</c> or why no pair is active.
/// <c>Expired</c> means the active pair's planning coverage has ended, which is distinct from a failed refresh.
/// <c>CoversNow</c> is true only when the active pair can evaluate the current time, so planning from now can use it.
/// It is false after expiry and when no verified pair covers the current time yet, for example on the UTC day a pair
/// was retrieved after the provider's daily update when no earlier pair covers that day.
/// </summary>
public sealed record CameraAgentTimeDataStatus(string SchemaVersion, DateTimeOffset ObservedUtc, bool PlanningEnabled,
    bool RefreshEnabled, bool RefreshScheduled, string ActiveSource, string Problem, DateTimeOffset? CoverageEndUtc,
    DateTimeOffset? EarthOrientationEndUtc, DateTimeOffset? LeapSecondsExpireUtc, bool Expired, bool CoversNow, string? EarthOrientationSha256,
    string? LeapSecondsSha256, CameraAgentTimeDataRefreshStatus Refresh, CameraAgentManagedTimePairStatus? ManagedPair)
{
    public const string CurrentVersion = "hvo-time-data-status-v1";
}

/// <summary>Composes the status from the time source, the managed store, the refresh job and the reserved automation.</summary>
public sealed class CameraAgentTimeDataOperations(
    ICameraAgentOrbitalTimeSource timeSource,
    CameraAgentManagedTimeDataStore store,
    CameraAgentTimeDataRefreshJob job,
    IOptionsMonitor<CameraAgentSatellitePlanningOptions> options,
    TimeProvider timeProvider,
    ILocalAutomationStore? automations = null)
{
    /// <summary>The directory of an adopted managed pair, for display on the authenticated setup surface.</summary>
    public string PairDirectory(string pair) => store.PairDirectory(pair);

    public async ValueTask<CameraAgentTimeDataStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = options.CurrentValue;
        var now = timeProvider.GetUtcNow();
        var resolution = await timeSource.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var time = resolution.Data;
        var end = time is null ? (DateTimeOffset?)null : CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(time);
        // An override or disabled planning does not evaluate the managed pair; its last adoption is still reported.
        var pointer = resolution.ManagedPair;
        if (pointer is null && resolution.Managed != CameraAgentManagedTimeDataState.Invalid)
        {
            try
            {
                pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                pointer = null;
            }
        }
        var managed = pointer is null ? null : new CameraAgentManagedTimePairStatus(pointer.Pair, pointer.AdoptedUtc,
            pointer.History[0].RetrievedUtc, resolution.Managed switch
            {
                CameraAgentManagedTimeDataState.Verified => "verified",
                CameraAgentManagedTimeDataState.Invalid => "invalid",
                _ => "not-checked"
            }, resolution.ManagedCoverageEndUtc, pointer.History[0].EarthOrientationSha256, pointer.History[0].LeapSecondsSha256);
        return new(CameraAgentTimeDataStatus.CurrentVersion, now, configuration.Enabled, configuration.TimeDataRefreshEnabled,
            await IsScheduledAsync(cancellationToken).ConfigureAwait(false), Token(resolution.Source), Token(resolution.Problem), end,
            time?.EarthOrientationCoverage?.EndUtc, time?.LeapTableExpiresUtc, end is { } until && now > until,
            time is not null && CameraAgentPinnedOrbitalTimeSource.Covers(time, new(now, now)),
            time?.EarthOrientationSha256, time?.LeapSecondsSha256, await RefreshAsync(cancellationToken).ConfigureAwait(false), managed);
    }

    private async ValueTask<CameraAgentTimeDataRefreshStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        CameraAgentTimeDataRefreshState? state;
        try
        {
            state = await store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new("unreadable", null, null, "time-data.state-unreadable", "the refresh record could not be read", null, null);
        }
        if (state is null) return new("never", null, null, null, null, null, null);
        var outcome = state.Outcome switch
        {
            CameraAgentTimeDataRefreshOutcome.InProgress => job.IsRunning ? "in-progress" : "interrupted",
            CameraAgentTimeDataRefreshOutcome.Adopted => "adopted",
            CameraAgentTimeDataRefreshOutcome.Unchanged => "unchanged",
            CameraAgentTimeDataRefreshOutcome.NotNewer => "not-newer",
            _ => "failed"
        };
        return new(outcome, state.LastAttemptUtc, state.CompletedUtc, state.ReasonCode, state.Detail, state.LastSuccessUtc,
            state.LastAttemptUtc + CameraAgentTimeDataRefreshJob.MinimumAttemptInterval);
    }

    private async ValueTask<bool> IsScheduledAsync(CancellationToken cancellationToken)
    {
        if (automations is null) return false;
        try
        {
            var state = await automations.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return state.Definitions.Any(static item => item.Definition.Enabled &&
                item.Definition.DefinitionId == CameraAgentTimeDataAutomationAdapter.DefinitionId &&
                item.Definition.TaskKind == LocalAutomationTaskKind.TimeDataRefresh);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException or DbException)
        {
            // An unavailable automation store is reported on the Automations page; status stays readable.
            return false;
        }
    }

    private static string Token(CameraAgentOrbitalTimeDataSource source) => source switch
    {
        CameraAgentOrbitalTimeDataSource.Override => "override",
        CameraAgentOrbitalTimeDataSource.Managed => "managed",
        _ => "bundled"
    };

    private static string Token(CameraAgentOrbitalTimeDataProblem problem) => problem switch
    {
        CameraAgentOrbitalTimeDataProblem.None => "none",
        CameraAgentOrbitalTimeDataProblem.Disabled => "disabled",
        CameraAgentOrbitalTimeDataProblem.OverrideIncomplete => "override-incomplete",
        CameraAgentOrbitalTimeDataProblem.OverrideInvalid => "override-invalid",
        _ => "bundled-invalid"
    };
}
