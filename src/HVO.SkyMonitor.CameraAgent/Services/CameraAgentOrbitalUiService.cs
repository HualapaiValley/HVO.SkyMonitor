using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The operator surface of CameraAgent-local orbital elements: one composed status read, and the refresh and resume
/// commands of <see cref="OrbitalElementsOperations"/>. Nothing here downloads, schedules or reads the payload.
/// </summary>
internal interface ICameraAgentOrbitalUiService
{
    ValueTask<OperatorUiResult<OrbitalDataStatusView>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<OrbitalCommandView>> RefreshAsync(
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<OrbitalCommandView>> ResumeAsync(
        DateTimeOffset expectedHaltSinceUtc,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken);
}

/// <summary>
/// Composes the refresh read model, the small verified manifest of the active snapshot, the health evaluation and the
/// reserved automation definition into one view. Reads need <c>OperationsReadV1</c>; commands need
/// <c>OperationsMutateV1</c> and an owner identity, which becomes the recorded actor. Logs carry exception type names
/// and reason codes only, because storage exception messages can name paths.
/// </summary>
internal sealed class CameraAgentOrbitalUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    OrbitalElementsOperations operations,
    IOrbitalSnapshotManifestReader manifestReader,
    OrbitalElementsHealthEvaluator healthEvaluator,
    ILocalAutomationStore automationStore,
    IOptions<CameraAgentHostOptions> options,
    ILogger<CameraAgentOrbitalUiService> logger) : ICameraAgentOrbitalUiService
{
    private const int MaximumActorLength = 128;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<OrbitalDataStatusView>> GetAsync(CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is null)
        {
            return OperatorUiResult<OrbitalDataStatusView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        var canMutate = ActorOf(await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)) is not null;
        OrbitalRefreshStatus status;
        try
        {
            status = await operations.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent orbital status read failed ({FailureType}, {ReasonCode}).",
                exception.GetType().Name, ReasonCodeOf(exception));
            return OperatorUiResult<OrbitalDataStatusView>.Failure(
                OperatorUiResultKind.Unavailable, "Orbital data status is unavailable.");
        }
        var (manifest, manifestUnavailableReason) = await ReadManifestAsync(status, cancellationToken).ConfigureAwait(false);
        var (healthState, healthReason) = await EvaluateHealthAsync(cancellationToken).ConfigureAwait(false);
        var schedule = await ReadScheduleAsync(cancellationToken).ConfigureAwait(false);
        return OperatorUiResult<OrbitalDataStatusView>.Success(new OrbitalDataStatusView(
            status,
            manifest,
            manifestUnavailableReason,
            healthState,
            healthReason ?? status.Reason,
            schedule,
            options.Value.OrbitalElements.StaleAfterHours,
            canMutate));
    }

    public ValueTask<OperatorUiResult<OrbitalCommandView>> RefreshAsync(
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(reason);
        return ExecuteAsync(
            actor => operations.RefreshAsync(
                new OrbitalElementsRefreshRequest(idempotencyKey, actor, reason), cancellationToken),
            cancellationToken);
    }

    public ValueTask<OperatorUiResult<OrbitalCommandView>> ResumeAsync(
        DateTimeOffset expectedHaltSinceUtc,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(reason);
        return ExecuteAsync(
            actor => operations.ResumeAsync(
                new OrbitalResumeRequest(expectedHaltSinceUtc, idempotencyKey, actor, reason), cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Authorizes before the command runs, then maps its disposition: Accepted, Applied and Replayed are Success; a
    /// rejection keeps its reason code, next permitted attempt and the read model after the command as the value.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<OrbitalCommandView>> ExecuteAsync(
        Func<string, ValueTask<OrbitalElementsCommandResult>> command,
        CancellationToken cancellationToken)
    {
        var actor = ActorOf(await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false));
        if (actor is null)
        {
            return OperatorUiResult<OrbitalCommandView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        OrbitalElementsCommandResult result;
        try
        {
            result = await command(actor).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent orbital command failed ({FailureType}, {ReasonCode}).",
                exception.GetType().Name, ReasonCodeOf(exception));
            return OperatorUiResult<OrbitalCommandView>.Failure(
                OperatorUiResultKind.Unavailable, "The orbital command could not be completed.");
        }
        var kind = result.Status switch
        {
            OrbitalElementsCommandStatus.Accepted or OrbitalElementsCommandStatus.Applied or
                OrbitalElementsCommandStatus.Replayed => OperatorUiResultKind.Success,
            OrbitalElementsCommandStatus.Conflict => OperatorUiResultKind.Conflict,
            OrbitalElementsCommandStatus.Invalid => OperatorUiResultKind.Invalid,
            _ => OperatorUiResultKind.Unavailable
        };
        var nextText = result.NextPermittedAttemptUtc is { } next
            ? next.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : null;
        return new OperatorUiResult<OrbitalCommandView>(
            kind,
            new OrbitalCommandView(result.Status, result.ReasonCode, result.NextPermittedAttemptUtc, result.Refresh),
            OrbitalOperatorText.Describe(result.ReasonCode, nextText));
    }

    /// <summary>A manifest that cannot be read never hides the status; the view carries its sanitized reason instead.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A manifest failure is reported as a bounded reason code beside the status.")]
    private async ValueTask<(OrbitalSnapshotManifestV1? Manifest, string? UnavailableReason)> ReadManifestAsync(
        OrbitalRefreshStatus status,
        CancellationToken cancellationToken)
    {
        if (status.CurrentManifestSha256 is not { } manifestSha256)
        {
            return (null, null);
        }
        try
        {
            return await manifestReader.ReadManifestAsync(manifestSha256, cancellationToken).ConfigureAwait(false)
                is { } manifest
                ? (manifest, null)
                : (null, OrbitalOperatorText.ManifestRetiredReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var reasonCode = ReasonCodeOf(exception) ?? OrbitalElementsAvailability.StoreUnavailableReasonCode;
            logger.LogWarning("CameraAgent orbital manifest read failed ({FailureType}, {ReasonCode}).",
                exception.GetType().Name, reasonCode);
            return (null, reasonCode);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Health is advisory here; a failure reads as an unavailable store.")]
    private async ValueTask<(OrbitalElementsHealthState State, string? Reason)> EvaluateHealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await healthEvaluator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
            return (health.State, health.Data.TryGetValue("Reason", out var reason) ? reason as string : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent orbital health evaluation failed ({FailureType}).", exception.GetType().Name);
            return (OrbitalElementsHealthState.Unhealthy, OrbitalElementsAvailability.StoreUnavailableReasonCode);
        }
    }

    /// <summary>The reserved definition as the automation store holds it: one schedule identity, read-only here.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The schedule summary is optional; the status still shows without it.")]
    private async ValueTask<OrbitalScheduleSummary?> ReadScheduleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await automationStore.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return OrbitalScheduleSummary.From(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent orbital schedule read failed ({FailureType}).", exception.GetType().Name);
            return null;
        }
    }

    private static string? ReasonCodeOf(Exception exception)
        => exception is OrbitalStorageException storage ? storage.ReasonCode : null;

    private static string? ActorOf(ClaimsPrincipal? principal)
        => principal is not null &&
            CameraAgentCredentialAccess.GetOwnerId(principal) is { Length: > 0 and <= MaximumActorLength } actor &&
            !string.IsNullOrWhiteSpace(actor)
                ? actor
                : null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}

/// <summary>
/// The single operator-facing state word of the orbital data. <see cref="Stale"/> is about the elements themselves (how
/// old the newest epoch is); <see cref="RefreshOverdue"/> is about retrieval (how long since the last successful
/// download). The two are deliberately separate: a fresh download can carry old elements, and current elements can sit
/// behind an overdue refresh.
/// </summary>
public enum OrbitalDataState
{
    Fresh,
    Stale,
    RefreshOverdue,
    NoData,
    Disabled,
    Halted,
    Quarantined,
    NeedsRepair,
    Unavailable
}

/// <summary>The result of one refresh or resume command, including the read model after it when the store answered.</summary>
internal sealed record OrbitalCommandView(
    OrbitalElementsCommandStatus Status,
    string ReasonCode,
    DateTimeOffset? NextPermittedAttemptUtc,
    OrbitalRefreshStatus? Refresh);

/// <summary>
/// The reserved <c>orbital-elements-refresh</c> automation definition as the automation store reports it. The last run
/// carries only its leading reason code, never the free-text part of its detail.
/// </summary>
public sealed record OrbitalScheduleSummary(
    bool Exists,
    bool Enabled,
    int? IntervalSeconds,
    DateTimeOffset? NextRunUtc,
    long? Version,
    string? RevisionSha256,
    LocalAutomationRunOutcome? LastRunOutcome,
    string? LastRunReasonCode,
    DateTimeOffset? LastRunCompletedUtc,
    string? LastRunKey,
    bool ActiveRun)
{
    internal static OrbitalScheduleSummary Missing { get; } = new(
        false, false, null, null, null, null, null, null, null, null, false);

    /// <summary>The first twelve hex digits of the revision, for display beside the full value in diagnostics.</summary>
    internal string? RevisionShort => RevisionSha256 is { Length: > 12 } revision ? revision[..12] : RevisionSha256;

    internal static OrbitalScheduleSummary From(LocalAutomationOperatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var definition = state.Definitions.SingleOrDefault(static item =>
            string.Equals(item.Definition.DefinitionId, OrbitalElementsAutomationAdapter.DefinitionId, StringComparison.Ordinal) &&
            item.Definition.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh);
        if (definition is null)
        {
            return Missing;
        }
        var active = state.Runs.Any(static run =>
            string.Equals(run.DefinitionId, OrbitalElementsAutomationAdapter.DefinitionId, StringComparison.Ordinal) &&
            run.Outcome is LocalAutomationRunOutcome.Running or LocalAutomationRunOutcome.Queued) ||
            definition.LastRun?.Outcome is LocalAutomationRunOutcome.Running or LocalAutomationRunOutcome.Queued;
        var lastRun = definition.LastRun;
        return new OrbitalScheduleSummary(
            true,
            definition.Definition.Enabled,
            definition.Definition.TriggerInterval,
            definition.NextRunUtc,
            definition.Version,
            definition.RevisionSha256,
            lastRun?.Outcome,
            OrbitalOperatorText.LeadingReasonCode(lastRun?.Detail),
            lastRun?.CompletedAtUtc,
            lastRun?.RunKey,
            active);
    }
}

/// <summary>
/// The composed operator view of the installed orbital data. Command availability is derived from the status and its
/// recovery action, never from a separate rule set, and the service and the store re-check every command. Freshness is
/// derived from facts (element epochs and retrieval times), not from the health reason code, so it does not depend on
/// which single degrading reason health chose to report. The record is public only because Blazor component parameters
/// must be; its derived members stay internal to the host.
/// </summary>
public sealed record OrbitalDataStatusView(
    OrbitalRefreshStatus Status,
    OrbitalSnapshotManifestV1? Manifest,
    string? ManifestUnavailableReason,
    OrbitalElementsHealthState HealthState,
    string HealthReason,
    OrbitalScheduleSummary? Schedule,
    int StaleAfterHours,
    bool CanMutate)
{
    internal DateTimeOffset ObservedUtc => Status.ObservedUtc;

    internal bool HasData => Status.CurrentManifestSha256 is not null;

    internal bool IsHalted => Status.ProviderHalt is not null;

    /// <summary>
    /// Health is unhealthy only when the store is unusable. Disabled refresh skips health, but a disabled, readable
    /// store reports <see cref="OrbitalRefreshRecoveryAction.Configure"/>, so repair while disabled means unavailable.
    /// </summary>
    internal bool StoreUnavailable => HealthState == OrbitalElementsHealthState.Unhealthy ||
        (!Status.Enabled && Status.RecoveryAction == OrbitalRefreshRecoveryAction.RepairStorage);

    internal bool CanResume => IsHalted;

    internal bool CanRefresh => RefreshBlockedReasonCode is null;

    /// <summary>The first reason Refresh would be rejected, in the order the operation checks them, or null.</summary>
    internal string? RefreshBlockedReasonCode
    {
        get
        {
            if (StoreUnavailable)
            {
                return Status.Reason;
            }
            if (!Status.Enabled)
            {
                return OrbitalElementsOperations.DisabledReasonCode;
            }
            if (Status.Automation is null || Schedule is { Exists: false })
            {
                return OrbitalElementsOperations.DefinitionMissingReasonCode;
            }
            if (Schedule is { Enabled: false })
            {
                return OrbitalElementsOperations.DefinitionDisabledReasonCode;
            }
            if (IsHalted)
            {
                return OrbitalElementsOperations.ProviderHaltedReasonCode;
            }
            return Status.NextPermittedAttemptUtc is { } next && ObservedUtc < next
                ? OrbitalElementsOperations.NotYetPermittedReasonCode
                : null;
        }
    }

    /// <summary>
    /// How old the newest element epoch may be before the elements read as stale. This mirrors the element-age rule the
    /// orbital health evaluation adopts for <c>orbital.snapshot-stale</c>; it is a display rule only.
    /// </summary>
    internal static readonly TimeSpan ElementStaleAfter = TimeSpan.FromHours(48);

    /// <summary>The configured <c>StaleAfterHours</c>: how long since the last successful retrieval before refresh is overdue.</summary>
    internal TimeSpan RefreshOverdueAfter => TimeSpan.FromHours(StaleAfterHours);

    /// <summary>The age of the newest element epoch: the scientific age, independent of when it was downloaded.</summary>
    internal TimeSpan? NewestElementAge => Manifest?.MaximumEpochUtc is { } newest ? Positive(ObservedUtc - newest) : null;

    internal TimeSpan? OldestElementAge => Manifest?.MinimumEpochUtc is { } oldest ? Positive(ObservedUtc - oldest) : null;

    internal TimeSpan? ActivationAge => Status.LastActivationUtc is { } activated ? Positive(ObservedUtc - activated) : null;

    /// <summary>The time since the last successful retrieval (an activation counts when no retrieval is recorded).</summary>
    internal TimeSpan? RetrievalAge => (Status.LastRetrievalSuccessUtc ?? Status.LastActivationUtc) is { } retrieved
        ? Positive(ObservedUtc - retrieved)
        : null;

    /// <summary>Element age: even the newest epoch is older than <see cref="ElementStaleAfter"/>, however recent the download.</summary>
    internal bool ElementsStale => HasData && NewestElementAge > ElementStaleAfter;

    /// <summary>
    /// Retrieval time: refresh is on and no successful retrieval happened within <see cref="RefreshOverdueAfter"/>. This
    /// is computed here because health reports only its first degrading reason, so a halt would otherwise hide it.
    /// </summary>
    internal bool RefreshOverdue => Status.Enabled && RetrievalAge > RefreshOverdueAfter;

    internal OrbitalDataState State
    {
        get
        {
            if (StoreUnavailable)
            {
                return OrbitalDataState.Unavailable;
            }
            if (IsHalted)
            {
                return OrbitalDataState.Halted;
            }
            if (Status.RecoveryAction == OrbitalRefreshRecoveryAction.InspectQuarantine)
            {
                return OrbitalDataState.Quarantined;
            }
            if (Status.RecoveryAction == OrbitalRefreshRecoveryAction.RepairStorage)
            {
                return OrbitalDataState.NeedsRepair;
            }
            if (!HasData)
            {
                return Status.Enabled ? OrbitalDataState.NoData : OrbitalDataState.Disabled;
            }
            if (ElementsStale)
            {
                return OrbitalDataState.Stale;
            }
            if (RefreshOverdue)
            {
                return OrbitalDataState.RefreshOverdue;
            }
            return Status.Enabled ? OrbitalDataState.Fresh : OrbitalDataState.Disabled;
        }
    }

    private static TimeSpan Positive(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

/// <summary>
/// Plain operator text for every <c>orbital.*</c> reason code, the automation codes an orbital command can return, and
/// each recovery action. Shared by the Software &amp; Catalog panel, the data notice and Automations. An unknown code is
/// shown as itself rather than guessed at.
/// </summary>
internal static class OrbitalOperatorText
{
    internal const string ManifestRetiredReasonCode = "orbital.manifest-retired";
    internal const string NoDataText =
        "No orbital data installed. Satellite predictions are unavailable; this does not mean no satellites are overhead.";
    internal const string ValidityText = "Unknown: the source supplies no validity window";
    internal const string NoHttpStatusText = "No valid HTTP status recorded";

    /// <summary>A halt whose provider outcome was never durably recorded; it carries no HTTP status.</summary>
    internal const string ProviderOutcomeUnknownReasonCode = "orbital.provider-outcome-unknown";

    /// <summary>
    /// The planned health code for "no successful retrieval within <c>StaleAfterHours</c>". Until health emits it, it
    /// is simply never seen; if it ships under another name, that name falls back to showing the code itself.
    /// </summary>
    internal const string RefreshOverdueReasonCode = "orbital.refresh-overdue";

    /// <summary>The fixed fallback for recorded free text, which is never rendered.</summary>
    internal const string UnrecordedReasonText = "The recorded reason is not a reason code, so it is not shown.";
    private const string ProviderHttpPrefix = "orbital.provider-http-";

    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        // Operator status and commands.
        [OrbitalElementsOperations.DisabledReasonCode] =
            "Orbital refresh is turned off in this CameraAgent's settings. Any installed snapshot stays readable.",
        [OrbitalElementsOperations.DefinitionMissingReasonCode] =
            "No refresh schedule exists. Create the orbital-elements-refresh automation to schedule refreshes.",
        [OrbitalElementsOperations.DefinitionDisabledReasonCode] =
            "The refresh schedule is disabled. Enable the orbital-elements-refresh automation to refresh.",
        [OrbitalElementsOperations.ProviderHaltedReasonCode] =
            "The provider refused a request, so every refresh is halted until an operator resumes the provider.",
        [OrbitalElementsOperations.RefreshQueuedReasonCode] =
            "Refresh queued. The automation runner makes the request on its next poll, and the provider floor and halt still apply.",
        [OrbitalElementsOperations.RefreshReplayedReasonCode] =
            "Already queued. This refresh request was recorded before, so nothing new was queued.",
        [OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode] = "The refresh request identifier was not valid. Try again.",
        [OrbitalElementsOperations.InvalidActorReasonCode] = "The signed-in identity cannot be recorded as the requester.",
        [OrbitalElementsOperations.InvalidReasonReasonCode] =
            "The refresh reason must be a single line of at most 512 characters.",
        [OrbitalElementsOperations.AttemptUnsettledReasonCode] =
            "An attempt is still recorded as in progress although nothing is running, usually after a restart. Restart CameraAgent so startup can settle it.",
        [OrbitalElementsOperations.StorageFailedReasonCode] =
            "The last attempt could not be stored durably. The last-known-good snapshot remains active.",
        [OrbitalElementsOperations.CandidateQuarantinedReasonCode] =
            "The last download failed validation and was quarantined. The last-known-good snapshot remains active.",
        [OrbitalElementsOperations.NoActiveSnapshotReasonCode] = NoDataText,
        [OrbitalElementsOperations.ReadyReasonCode] = "The active snapshot is current.",
        [ManifestRetiredReasonCode] = "That snapshot was retired by retention. Reload to see the current snapshot.",

        // Health.
        [OrbitalElementsHealthEvaluator.SnapshotStaleReasonCode] =
            "The orbital elements are older than the freshness limit. They stay in use, but predictions from them are less accurate.",
        [RefreshOverdueReasonCode] =
            "No successful retrieval happened within the configured stale period. The installed elements stay in use.",
        [OrbitalElementsHealthEvaluator.FirstSnapshotOverdueReasonCode] =
            "The first snapshot is overdue, so no orbital data is installed yet.",

        // Run outcomes.
        ["orbital.activated"] = "A new snapshot was validated and activated.",
        ["orbital.unchanged-payload"] =
            "The provider returned the same elements as the active snapshot; a new snapshot record was activated without new data.",
        ["orbital.retrieved"] = "The provider response was retrieved.",
        ["orbital.attempt-cancelled"] = "The attempt was cancelled before it completed.",
        ["orbital.attempt-in-flight"] = "Another attempt was already running, so this occurrence was skipped.",
        ["orbital.attempt-interrupted"] = "CameraAgent stopped before the attempt completed; startup recorded it as interrupted.",
        ["orbital.floor-not-elapsed"] = "Skipped because the provider's two-hour floor had not elapsed.",
        ["orbital.manual-request-expired"] =
            "A manual refresh waited more than 15 minutes without running and expired. Check that the automation runner is on.",
        ["orbital.candidate-rejected"] = "The provider response failed validation and was rejected.",
        ["orbital.internal-error"] = "The attempt failed unexpectedly and was closed.",
        ["orbital.non-200-response"] = "The provider returned a status other than 200 OK.",
        ["orbital.invalid-http-status"] =
            "The provider's response status was outside the valid HTTP range of 100 to 599.",
        [ProviderOutcomeUnknownReasonCode] =
            "An attempt may have reached the provider, but its outcome was not recorded (for example after a crash, a storage failure or a shutdown during the request). Refresh stays halted until an operator resumes the provider.",

        // Response checks.
        ["orbital.media-type"] = "The provider response was not JSON.",
        ["orbital.content-encoding"] = "The provider response used an unsupported content encoding.",
        ["orbital.gzip-invalid"] = "The compressed provider response was invalid.",
        ["orbital.length-mismatch"] = "The provider response length did not match its declared length.",
        ["orbital.wire-bound"] = "The provider response exceeded the download size limit.",
        ["orbital.raw-bound"] = "The decoded provider response exceeded the size limit.",
        ["orbital.empty-body"] = "The provider response was empty.",
        ["orbital.raw-integrity"] = "The stored provider response failed integrity verification.",

        // Parser and normalizer.
        ["orbital.json-root"] = "The provider response was not a JSON array of records.",
        ["orbital.json-syntax"] = "The provider response was not valid JSON.",
        ["orbital.missing-member"] = "A record was missing a required field.",
        ["orbital.unknown-member"] = "A record had an unexpected field.",
        ["orbital.duplicate-member"] = "A record repeated a field.",
        ["orbital.member-type"] = "A record field had the wrong type.",
        ["orbital.invalid-number"] = "A record had an invalid number.",
        ["orbital.invalid-text"] = "A record had invalid text.",
        ["orbital.invalid-epoch"] = "A record had an invalid element epoch.",
        ["orbital.invalid-object-identity"] = "A record had an invalid object identity.",
        ["orbital.epoch-range"] = "A record's element epoch was outside the accepted range.",
        ["orbital.record-shape"] = "A record did not have the expected shape.",
        ["orbital.record-bound"] = "The provider response held more records than allowed.",
        ["orbital.record-contract"] = "A record failed the orbital-elements contract.",
        ["orbital.snapshot-contract"] = "The snapshot failed the orbital-elements contract.",
        ["orbital.empty-payload"] = "The provider response held no records.",
        ["orbital.duplicate-record"] = "The provider response repeated a record.",
        ["orbital.conflicting-metadata"] = "Records disagreed about shared metadata.",
        ["orbital.unsupported-classification"] = "A record had an unsupported classification.",
        ["orbital.unsupported-ephemeris-type"] = "A record had an unsupported ephemeris type.",

        // Transport.
        ["orbital.request-timeout"] = "The request to the provider timed out.",
        ["orbital.http-client-timeout"] = "The HTTP client timed out waiting for the provider.",
        ["orbital.transport-authentication"] = "The provider's TLS identity could not be verified.",
        ["orbital.transport-tls"] = "A TLS error interrupted the request.",
        ["orbital.transport-connection"] = "The provider could not be reached.",
        ["orbital.transport-protocol"] = "The provider response broke the HTTP protocol.",
        ["orbital.transport-io"] = "A network error interrupted the request.",
        ["orbital.transport-limit"] = "The provider response exceeded a transport limit.",
        ["orbital.transport-cancelled"] = "The request was cancelled.",
        ["orbital.transport-failure"] = "The request to the provider failed.",
        ["orbital.client-error"] = "The request could not be sent.",

        // Storage.
        ["orbital.storage-no-space"] = "The orbital store ran out of disk space.",
        ["orbital.storage-database"] = "The orbital database reported an error.",
        ["orbital.storage-permission"] = "CameraAgent does not have permission to use the orbital store.",
        ["orbital.storage-containment"] = "A file in the orbital store was a link or outside the store.",
        [OrbitalElementsAvailability.IntegrityFailureReasonCode] = "The orbital store failed integrity verification.",
        ["orbital.storage-io"] = "A disk error affected the orbital store.",
        ["orbital.storage-locked"] = "Another process holds the orbital store.",
        [OrbitalElementsAvailability.StoreUnavailableReasonCode] = "The orbital store is unavailable.",

        // Resume.
        ["orbital.resumed"] =
            "Provider resumed. The next permitted occurrence makes the next request; the two-hour floor did not move.",
        ["orbital.resume-replayed"] = "This resume was already applied.",
        ["orbital.no-halt"] = "There is no provider halt to resume. The current state is shown.",
        ["orbital.halt-mismatch"] = "The halt changed after this page read it. Review the current halt before resuming.",
        ["orbital.idempotency-conflict"] =
            "This request identifier was already used with different content. Reload and try again.",
        ["orbital.resume-invalid-halt-time"] = "The halt time must be the UTC time shown for the current halt.",
        ["orbital.resume-invalid-idempotency-key"] = "The resume request identifier was not valid. Try again.",
        ["orbital.resume-invalid-actor"] = "The signed-in identity cannot be recorded as the operator.",
        ["orbital.resume-invalid-reason"] = "The resume reason must be a single line of at most 512 characters.",
        ["orbital.resume-rejected"] = "The resume was rejected.",

        // Schedule validation.
        [OrbitalElementsAutomationAdapter.ReservedDefinitionIdReasonCode] =
            "The orbital refresh task accepts only the identifier orbital-elements-refresh.",
        [OrbitalElementsAutomationAdapter.IntervalBelowFloorReasonCode] =
            "The interval must be at least 7,200 seconds; the provider allows at most one attempt every two hours.",
        [OrbitalElementsAutomationAdapter.IntervalOutOfRangeReasonCode] = "The interval must be at most 86,400 seconds.",

        // Automation codes an orbital refresh can return.
        [LocalAutomationContract.RunNowPendingReasonCode] = "A refresh is already queued or running.",
        [LocalAutomationContract.RunNowUnavailableReasonCode] =
            "The refresh schedule cannot run now; it is disabled or no longer valid.",
        [LocalAutomationContract.InvalidCommandReasonCode] = "The command was rejected as invalid.",
    };

    /// <summary>Whether <paramref name="reasonCode"/> has fixed operator text rather than falling back to itself.</summary>
    internal static bool IsKnown(string reasonCode)
        => Texts.ContainsKey(reasonCode) || ProviderHttpStatus(reasonCode) is not null ||
            string.Equals(reasonCode, OrbitalElementsOperations.NotYetPermittedReasonCode, StringComparison.Ordinal);

    /// <summary>
    /// Operator text for <paramref name="reasonCode"/>. <paramref name="nextPermitted"/> is the caller's formatted next
    /// permitted attempt, used only by <c>orbital.not-yet-permitted</c>.
    /// </summary>
    internal static string Describe(string? reasonCode, string? nextPermitted = null)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return "No reason was recorded.";
        }
        if (string.Equals(reasonCode, OrbitalElementsOperations.NotYetPermittedReasonCode, StringComparison.Ordinal))
        {
            return nextPermitted is null
                ? "The provider allows one attempt every two hours, and the next attempt is not permitted yet."
                : string.Concat("The provider allows one attempt every two hours. The next attempt is permitted at ",
                    nextPermitted, ".");
        }
        if (ProviderHttpStatus(reasonCode) is { } status)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The provider returned HTTP {status}, so every refresh is halted until an operator resumes the provider.");
        }
        return Texts.TryGetValue(reasonCode, out var text) ? text : reasonCode;
    }

    /// <summary>
    /// The HTTP status of a provider halt: <c>HTTP 403</c> for a recorded status, and an explicit statement when no
    /// valid status was recorded. Every halt status renders through here so a missing status is never shown as a number.
    /// </summary>
    internal static string HaltStatus(int? httpStatusCode)
        => httpStatusCode is { } status and >= 100 and <= 599
            ? string.Create(CultureInfo.InvariantCulture, $"HTTP {status}")
            : NoHttpStatusText;

    /// <summary>
    /// Operator text for a recorded halt, quarantine or run reason. A reason code (or a leading code) is described; any
    /// other recorded text is replaced by a fixed sentence, so free text never reaches the page.
    /// </summary>
    internal static string ReasonText(string? recorded)
        => string.IsNullOrWhiteSpace(recorded)
            ? Describe(null)
            : LeadingReasonCode(recorded) is { } code
                ? Describe(code)
                : UnrecordedReasonText;

    internal static string Recovery(OrbitalRefreshRecoveryAction action) => action switch
    {
        OrbitalRefreshRecoveryAction.None => "No action needed.",
        OrbitalRefreshRecoveryAction.Configure =>
            "Turn refresh on in this CameraAgent's settings and enable the orbital-elements-refresh schedule in Automations.",
        OrbitalRefreshRecoveryAction.WaitForPermittedAttempt =>
            "Wait for the next permitted attempt; the provider allows one every two hours.",
        OrbitalRefreshRecoveryAction.ResumeProvider =>
            "Check why the provider refused the request, then resume the provider from this page.",
        OrbitalRefreshRecoveryAction.InspectQuarantine =>
            "Inspect the quarantined response as the orbital refresh runbook describes. The next successful refresh clears it.",
        OrbitalRefreshRecoveryAction.RepairStorage =>
            "Check this CameraAgent's orbital storage and restart CameraAgent, as the orbital refresh runbook describes.",
        _ => action.ToString()
    };

    internal static string StateLabel(OrbitalDataState state) => state switch
    {
        OrbitalDataState.Fresh => "Fresh",
        OrbitalDataState.Stale => "Stale",
        OrbitalDataState.RefreshOverdue => "Refresh overdue",
        OrbitalDataState.NoData => "No data",
        OrbitalDataState.Disabled => "Disabled",
        OrbitalDataState.Halted => "Halted",
        OrbitalDataState.Quarantined => "Quarantined",
        OrbitalDataState.NeedsRepair => "Needs repair",
        _ => "Unavailable"
    };

    /// <summary>The shared <c>.state-chip</c> tone of a state.</summary>
    internal static string StateTone(OrbitalDataState state) => state switch
    {
        OrbitalDataState.Fresh => "success",
        OrbitalDataState.Stale or OrbitalDataState.RefreshOverdue or OrbitalDataState.Quarantined => "warning",
        OrbitalDataState.NoData => "pending",
        OrbitalDataState.Disabled => "neutral",
        _ => "failure"
    };

    internal static string Completeness(OrbitalPopulationCompleteness completeness) => completeness switch
    {
        OrbitalPopulationCompleteness.CompleteForRequestedPopulation => "Complete for the requested population",
        OrbitalPopulationCompleteness.Partial => "Partial for the requested population",
        _ => "Unknown for the requested population"
    };

    /// <summary>
    /// The leading <c>orbital.*</c> or <c>automation.*</c> code of a run detail (<c>{code}: {text}</c>), or null. The
    /// free-text part is never surfaced.
    /// </summary>
    internal static string? LeadingReasonCode(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }
        var end = detail.IndexOf(':', StringComparison.Ordinal);
        var code = (end < 0 ? detail : detail[..end]).Trim();
        return code.Length is > 0 and <= 96 &&
            (code.StartsWith("orbital.", StringComparison.Ordinal) || code.StartsWith("automation.", StringComparison.Ordinal)) &&
            code.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
                ? code
                : null;
    }

    private static int? ProviderHttpStatus(string reasonCode)
        => reasonCode.StartsWith(ProviderHttpPrefix, StringComparison.Ordinal) &&
            int.TryParse(reasonCode.AsSpan(ProviderHttpPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out var status) && status is >= 100 and <= 599
                ? status
                : null;
}
