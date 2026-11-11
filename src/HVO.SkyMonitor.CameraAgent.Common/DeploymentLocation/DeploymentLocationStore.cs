using HVO.SkyMonitor.CameraAgent.Common.Storage;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReadStage = HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.DeploymentLocationReadAttribution.Stage;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

/// <summary>Protects deployment-location history without coupling edge infrastructure to a protection framework.</summary>
public interface IDeploymentLocationProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}

/// <summary>Initial values supplied by validated local configuration.</summary>
public sealed record DeploymentLocationSeed(
    [property: JsonRequired] string LocationId,
    [property: JsonRequired] string Source,
    [property: JsonRequired] double? HorizontalAccuracyMeters,
    [property: JsonRequired] DateTimeOffset? EffectiveFromUtc,
    [property: JsonRequired] DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] ObservatoryLocation Coordinates,
    [property: JsonRequired] DeploymentLocationSourceKind SourceKind = DeploymentLocationSourceKind.Unspecified);

/// <summary>Initializes and resolves protected immutable deployment-location versions.</summary>
public interface IDeploymentLocationStore
{
    DeploymentLocationSnapshot? Active { get; }

    DeploymentLocationSnapshot? Candidate => null;

    DeploymentLocationSnapshot? Staged => null;

    /// <summary>The audited local manual coordinate state, or the unsupported state for stores without the contract.</summary>
    ManualDeploymentLocationState Manual => ManualDeploymentLocationState.Unsupported;

    /// <summary>
    /// Records an operator-entered coordinate change as the governing local seed. The new version is
    /// appended by the next startup reconciliation, so captures in this process lifetime keep the
    /// deployment version they were already stamped with.
    /// </summary>
    ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
        ManualDeploymentLocationRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromException<ManualDeploymentLocationResult>(
            new NotSupportedException("This deployment-location store does not support manual coordinate mutation."));

    /// <summary>
    /// Finds the original resolved coordinates of a retained manual command for partial-request replay.
    /// Applying the command still validates its payload and whether it has been superseded.
    /// </summary>
    ValueTask<ManualDeploymentLocationAuditEntry?> FindManualCommandAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<ManualDeploymentLocationAuditEntry?>(null);

    ValueTask<DeploymentLocationSnapshot> InitializeAsync(
        DeploymentLocationSeed seed,
        CancellationToken cancellationToken);

    ValueTask<DeploymentLocationSnapshot> VerifyInstallationAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        => InitializeAsync(seed, cancellationToken);

    ValueTask<DeploymentLocationSnapshot> LoadCurrentAsync(DeploymentLocationSeed seed, bool verification,
        Func<bool> configurationStillCurrent, CancellationToken cancellationToken)
        => verification ? VerifyInstallationAsync(seed, cancellationToken) : InitializeAsync(seed, cancellationToken);

    ValueTask StageAsync(
        DeploymentLocationSnapshot deployment,
        CancellationToken cancellationToken)
        => ValueTask.FromException(new NotSupportedException("This deployment-location store does not support staging."));

    DeploymentLocationSourceKind ResolveSourceKind(DeploymentLocationSnapshot deployment)
        => DeploymentLocationSourceKind.Unspecified;

    DeploymentLocationSnapshot Resolve(
        CaptureLocationProvenance provenance,
        DateTimeOffset? effectiveUtc = null);
}

public sealed class ProtectedDeploymentLocationStore(
    IOptions<CameraAgentHostOptions> options,
    IDeploymentLocationProtector protector,
    TimeProvider timeProvider,
    ILogger<ProtectedDeploymentLocationStore> logger,
    DeploymentLocationTelemetry? telemetry = null) : IDeploymentLocationStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int CurrentManualSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CameraAgentHostOptions _options = options.Value;
    private readonly IDeploymentLocationProtector _protector = protector;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ProtectedDeploymentLocationStore> _logger = logger;
    internal Action<string>? BeforeStateDirectorySyncForTest { get; set; }
    internal Action? BeforeRetainedManifestValidationForTest { get; set; }
    internal Action<string>? BeforeRetainedProofCommitForTest { get; set; }
    private DeploymentLocationHistory? _history;
    private DeploymentLocationHistory? _bootstrapHistory;
    private RetainedIntegrityLedger? _integrity;
    private bool _disposed;
    private ManualDeploymentLocationRecord? _manual;
    private DeploymentLocationSeed? _configurationSeed;

    public DeploymentLocationSnapshot? Active => Volatile.Read(ref _history)?.Snapshots[^1];

    public DeploymentLocationSnapshot? Candidate => Volatile.Read(ref _history)?.Candidate;

    public DeploymentLocationSnapshot? Staged => Volatile.Read(ref _history)?.Staged;

    public ManualDeploymentLocationState Manual => ProjectManual(
        Volatile.Read(ref _history),
        Volatile.Read(ref _manual),
        Volatile.Read(ref _configurationSeed));

    public DeploymentLocationSourceKind ResolveSourceKind(DeploymentLocationSnapshot deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var history = Volatile.Read(ref _history)
            ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
        var known = history.Snapshots.Any(item => item.CanonicalSha256 == deployment.CanonicalSha256)
            || history.Candidate?.CanonicalSha256 == deployment.CanonicalSha256
            || history.Staged?.CanonicalSha256 == deployment.CanonicalSha256;
        if (!known)
        {
            throw new InvalidDataException("Deployment-location source classification is missing from protected history.");
        }
        return history.SourceKinds.TryGetValue(deployment.Version, out var sourceKind)
            ? sourceKind
            : throw new InvalidDataException(
                "Deployment-location source classification is missing from protected history.");
    }

    public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        => InitializeCoreAsync(seed, strictRetainedValidation: false, fullScrub: true, null, cancellationToken);

    public ValueTask<DeploymentLocationSnapshot> VerifyInstallationAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        => InitializeCoreAsync(seed, strictRetainedValidation: true, fullScrub: false, null, cancellationToken);

    public ValueTask<DeploymentLocationSnapshot> FullVerifyAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        => InitializeCoreAsync(seed, strictRetainedValidation: true, fullScrub: true, null, cancellationToken);

    public ValueTask<DeploymentLocationSnapshot> LoadCurrentAsync(DeploymentLocationSeed seed, bool verification,
        Func<bool> configurationStillCurrent, CancellationToken cancellationToken)
        => InitializeCoreAsync(seed, strictRetainedValidation: verification, fullScrub: !verification, configurationStillCurrent, cancellationToken);

    private async ValueTask<DeploymentLocationSnapshot> InitializeCoreAsync(
        DeploymentLocationSeed seed, bool strictRetainedValidation, bool fullScrub, Func<bool>? configurationStillCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var started = Stopwatch.GetTimestamp();
        var operation = "load";
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity("deployment-location.initialize");
        if (fullScrub) await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!await _gate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
            throw new InstallationVerificationPendingException(requiresScrub: false);
        RetainedIntegrityLedger.Scrub? integrityScrub = null;
        try
        {
            seed = NormalizeSeed(seed);
            var (path, markerPath, manualPath) = EnsureStatePaths();
            var configurationSeed = seed;
            var manual = File.Exists(manualPath)
                ? await ReadManualAsync(manualPath, cancellationToken).ConfigureAwait(false)
                : null;
            var supersedeManual = false;
            if (manual is not null && manual.SupersededAtUtc is null)
            {
                if (GovernsConfiguration(manual, configurationSeed))
                {
                    // The operator's local entry outranks the unchanged startup seed until the
                    // configured seed itself changes, so a restart does not silently revert it.
                    seed = manual.Seed;
                }
                else
                {
                    // Deferred until the protected history is validated so a failed startup mutates nothing.
                    supersedeManual = true;
                }
            }
            var historyExists = File.Exists(path);
            if (!historyExists && _history is not null)
                throw new InvalidDataException("Protected deployment-location history disappeared from the running installation.");
            if (!historyExists && !fullScrub)
                throw new InstallationVerificationPendingException(requiresScrub: true);
            if (!historyExists && (File.Exists(markerPath) || HasLocationBearingEvidence(cancellationToken)))
            {
                throw new InvalidDataException(
                    "Protected deployment-location history is missing for existing capture evidence.");
            }
            var history = historyExists ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
            if (File.Exists(markerPath))
            {
                EnsurePhysicalStatePath(markerPath);
            }
            var marker = File.Exists(markerPath)
                ? await ReadMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false)
                : null;
            ValidateMarker(marker, history);
            var ledgerRequirementAdded = false;
            if (history is not null && fullScrub)
            {
                var ledger = GetIntegrity(history);
                if (ledger is not null && history.RetainedIntegrityNonce is null)
                {
                    history = history with { RetainedIntegrityNonce = ledger.Nonce };
                    ledgerRequirementAdded = true;
                }
                integrityScrub = BeginIntegrityScrub(ledger, history, strictRetainedValidation, cancellationToken);
                using var existingProofs = CreateRetainedIndex(path, history);
                ValidateRetainedEvidence(history, existingProofs, strictRetainedValidation, cancellationToken, integrityScrub);
            }
            ReconciledLocation snapshot;
            var startupUtc = ToMilliseconds(_timeProvider.GetUtcNow());
            if (history?.Staged is not null && history.Staged.IsEffectiveAt(startupUtc))
            {
                var activated = ActivateStaged(history, startupUtc);
                snapshot = Reconcile(activated.History, seed) with { Appended = true };
            }
            else if (history?.Staged?.EffectiveUntilUtc is { } stagedUntil && stagedUntil <= startupUtc)
            {
                var retryable = history with
                {
                    Staged = null
                };
                snapshot = Reconcile(retryable, seed);
            }
            else if (history?.Staged is not null)
            {
                EnsureEffectiveAtStartup(history.Snapshots[^1], startupUtc);
                snapshot = new ReconciledLocation(history, history.Snapshots[^1], Appended: false);
            }
            else if (history is not null
                && history.CentrallyActivatedCanonicalSha256 == history.Snapshots[^1].CanonicalSha256
                && history.ConfigurationSeed == seed)
            {
                EnsureEffectiveAtStartup(history.Snapshots[^1], startupUtc);
                snapshot = new ReconciledLocation(history, history.Snapshots[^1], Appended: false);
            }
            else
            {
                snapshot = Reconcile(history, seed);
            }
            if (!fullScrub)
            {
                // A request never opens/rebuilds the corpus ledger. The host-owned full
                // scrub creates a fresh epoch; request cancellation cannot restart it.
                var ledger = _integrity;
                if (history is null || !ReferenceEquals(snapshot.History, history) || ledger is null)
                    throw new InstallationVerificationPendingException(requiresScrub: true);
                if (!await ledger.VerifyAsync(RetainedIdentity(history), cancellationToken).ConfigureAwait(false))
                    throw new InstallationVerificationPendingException(ledger.RequiresScrub);
            }
            else if (history is null || !ReferenceEquals(snapshot.History, history))
            {
                integrityScrub?.Dispose();
                var ledger = GetIntegrity(snapshot.History);
                if (ledger is not null && snapshot.History.RetainedIntegrityNonce is null)
                {
                    snapshot = snapshot with { History = snapshot.History with { RetainedIntegrityNonce = ledger.Nonce } };
                    ledgerRequirementAdded = true;
                }
                // Registration drains earlier managed publications. Recheck before
                // creating first identity so one cannot appear after the first probe.
                if (history is null && HasLocationBearingEvidence(cancellationToken))
                    throw new InvalidDataException("Protected deployment-location history is missing for existing capture evidence.");
                integrityScrub = BeginIntegrityScrub(ledger, snapshot.History, strictRetainedValidation, cancellationToken);
                using var changedProofs = CreateRetainedIndex(path, snapshot.History);
                ValidateRetainedEvidence(snapshot.History, changedProofs, strictRetainedValidation, cancellationToken, integrityScrub);
            }
            integrityScrub?.Finish(cancellationToken);
            if (configurationStillCurrent is not null && !configurationStillCurrent())
                throw new InstallationVerificationPendingException(requiresScrub: true);
            operation = history is null ? "initialize" : snapshot.Appended ? "change" : "load";
            if (ledgerRequirementAdded || history is null || !ReferenceEquals(snapshot.History, history))
            {
                await WriteAsync(path, snapshot.History, cancellationToken).ConfigureAwait(false);
            }
            if (marker is null || marker.Version != snapshot.Active.Version)
            {
                await WriteMarkerAsync(markerPath, snapshot.Active, cancellationToken).ConfigureAwait(false);
            }
            if (supersedeManual)
            {
                manual = manual! with { SupersededAtUtc = ToMilliseconds(_timeProvider.GetUtcNow()) };
                await WriteManualAsync(manualPath, manual, cancellationToken).ConfigureAwait(false);
                DeploymentLocationLog.ManualOverrideSuperseded(_logger, configurationSeed.LocationId);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Written before the history so any reader that observes the new history also observes the
            // manual state it was reconciled against.
            Volatile.Write(ref _configurationSeed, configurationSeed);
            Volatile.Write(ref _manual, manual);
            Volatile.Write(ref _history, snapshot.History);
            try { integrityScrub?.Publish(); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or Microsoft.Data.Sqlite.SqliteException or JsonException or FormatException)
            {
                DeploymentLocationLog.RetainedIntegrityUnqualified(_logger);
                if (strictRetainedValidation) throw;
            }
            if (snapshot.Appended)
            {
                DeploymentLocationLog.VersionAppended(_logger, snapshot.Active.LocationId, snapshot.Active.Version);
            }
            else
            {
                DeploymentLocationLog.VersionLoaded(_logger, snapshot.Active.LocationId, snapshot.Active.Version);
            }
            telemetry?.Record(operation, snapshot.Appended ? "applied" : "existing", Stopwatch.GetElapsedTime(started));
            activity?.SetTag("operation", operation);
            activity?.SetTag("outcome", snapshot.Appended ? "applied" : "existing");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return snapshot.Active;
        }
        catch
        {
            telemetry?.Record(operation, "failed", Stopwatch.GetElapsedTime(started));
            activity?.SetTag("operation", operation);
            activity?.SetTag("outcome", "failed");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            integrityScrub?.Dispose();
            _gate.Release();
        }
    }

    public async ValueTask StageAsync(
        DeploymentLocationSnapshot deployment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var validation = deployment.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"Acknowledged deployment location is invalid ({validation.ReasonCode}, {validation.FieldPath}).");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = _history
                ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
            var active = history.Snapshots[^1];
            if (deployment.CanonicalSha256 == active.CanonicalSha256)
            {
                return;
            }
            ValidateSuccessor(active, deployment);
            if (history.Candidate is null
                || !string.Equals(
                    history.Candidate.CanonicalSha256,
                    deployment.CanonicalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Acknowledged deployment location does not match the protected local candidate.");
            }
            var updated = history with { Staged = deployment };
            ValidateHistory(updated);
            var (path, _, _) = EnsureStatePaths();
            await WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _history, updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ManualDeploymentLocationAuditEntry?> FindManualCommandAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Search the durable replay window, which is larger than the operator-facing history projection.
            return _manual?.Entries.FirstOrDefault(entry =>
                string.Equals(entry.IdempotencyKey, idempotencyKey.Trim(), StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
        ManualDeploymentLocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity("deployment-location.manual");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = _history
                ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
            var configurationSeed = _configurationSeed
                ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
            var manual = _manual;
            var actor = request.Actor?.Trim() ?? string.Empty;
            var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (string.IsNullOrEmpty(actor) || actor.Length > ManualDeploymentLocationContract.MaximumActorLength ||
                string.IsNullOrEmpty(idempotencyKey) ||
                idempotencyKey.Length > ManualDeploymentLocationContract.MaximumIdempotencyKeyLength ||
                reason is { Length: > ManualDeploymentLocationContract.MaximumReasonLength })
            {
                outcome = "invalid";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Invalid,
                    ManualDeploymentLocationContract.InvalidCommandReasonCode,
                    "manual.command",
                    history,
                    manual,
                    configurationSeed);
            }
            var knownVersion = KnownVersion(history);
            var probe = DeploymentLocationSnapshot.Create(
                history.LocationId,
                knownVersion + 1,
                ManualDeploymentLocationContract.SourceLabel,
                horizontalAccuracyMeters: null,
                ToMilliseconds(_timeProvider.GetUtcNow()),
                null,
                request.LatitudeDegrees,
                request.LongitudeDegrees,
                request.ElevationMeters,
                request.TimeZoneId ?? string.Empty);
            var validation = probe.Validate();
            if (!validation.IsValid)
            {
                outcome = "invalid";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Invalid,
                    validation.ReasonCode,
                    validation.FieldPath,
                    history,
                    manual,
                    configurationSeed);
            }
            if (probe.ElevationMeters < ManualDeploymentLocationContract.MinimumElevationMeters ||
                probe.ElevationMeters > ManualDeploymentLocationContract.MaximumElevationMeters)
            {
                outcome = "invalid";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Invalid,
                    CaptureContractReasonCodes.InvalidLocation,
                    "location.elevationMeters",
                    history,
                    manual,
                    configurationSeed);
            }
            var entries = manual?.Entries ?? [];
            var replay = entries.FirstOrDefault(item =>
                string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            if (replay is not null)
            {
                if (!SameCoordinates(replay, probe))
                {
                    outcome = "conflict";
                    return ManualOutcome(
                        ManualDeploymentLocationStatus.Conflict,
                        ManualDeploymentLocationContract.IdempotencyKeyConflictReasonCode,
                        "manual.idempotencyKey",
                        history,
                        manual,
                        configurationSeed);
                }
                if (!GovernsConfiguration(manual!, configurationSeed)
                    || replay.Sequence <= manual!.SupersededThroughSequence)
                {
                    // The key was recorded, but a configuration change superseded it, so replaying it
                    // would report success for coordinates that will never govern. The watermark keeps
                    // that true after a later entry makes the record govern again.
                    outcome = "conflict";
                    return ManualOutcome(
                        ManualDeploymentLocationStatus.Conflict,
                        ManualDeploymentLocationContract.SupersededEntryReasonCode,
                        "manual.idempotencyKey",
                        history,
                        manual,
                        configurationSeed);
                }
                outcome = "replayed";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Replayed, null, null, history, manual, configurationSeed);
            }
            if (request.ExpectedVersion != knownVersion)
            {
                outcome = "conflict";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Conflict,
                    ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode,
                    "manual.expectedVersion",
                    history,
                    manual,
                    configurationSeed);
            }
            // The history version alone cannot detect a competing pending manual entry, because the
            // command deliberately never touches the history.
            if (request.ExpectedManualSequence != ManualSequence(manual, configurationSeed))
            {
                outcome = "conflict";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Conflict,
                    ManualDeploymentLocationContract.ExpectedManualSequenceConflictReasonCode,
                    "manual.expectedManualSequence",
                    history,
                    manual,
                    configurationSeed);
            }
            var governing = GoverningSeed(history, manual, configurationSeed);
            if (SameCoordinates(governing.Coordinates, probe))
            {
                outcome = "unchanged";
                return ManualOutcome(
                    ManualDeploymentLocationStatus.Unchanged, null, null, history, manual, configurationSeed);
            }
            var record = new ManualDeploymentLocationRecord(
                CurrentManualSchemaVersion,
                history.LocationId,
                NormalizeSeed(new DeploymentLocationSeed(
                    history.LocationId,
                    ManualDeploymentLocationContract.SourceLabel,
                    null,
                    null,
                    null,
                    new ObservatoryLocation(
                        probe.LatitudeDegrees, probe.LongitudeDegrees, probe.ElevationMeters, probe.TimeZoneId),
                    DeploymentLocationSourceKind.Manual)),
                configurationSeed,
                SupersededAtUtc: null,
                manual is null || GovernsConfiguration(manual, configurationSeed)
                    ? manual?.SupersededThroughSequence ?? 0
                    : entries[^1].Sequence,
                [
                    .. entries.TakeLast(ManualDeploymentLocationContract.MaximumRetainedEntries - 1),
                    new ManualDeploymentLocationAuditEntry(
                        entries.Count == 0 ? 1 : entries[^1].Sequence + 1,
                        ToMilliseconds(_timeProvider.GetUtcNow()),
                        actor,
                        reason,
                        idempotencyKey,
                        request.ExpectedVersion,
                        probe.LatitudeDegrees,
                        probe.LongitudeDegrees,
                        probe.ElevationMeters,
                        probe.TimeZoneId)
                ]);
            ValidateManual(record);
            var (_, _, manualPath) = EnsureStatePaths();
            await WriteManualAsync(manualPath, record, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _manual, record);
            DeploymentLocationLog.ManualEntryRecorded(_logger, history.LocationId, record.Entries[^1].Sequence);
            outcome = "applied";
            return ManualOutcome(
                ManualDeploymentLocationStatus.Applied, null, null, history, record, configurationSeed);
        }
        finally
        {
            _gate.Release();
            telemetry?.Record("manual", outcome, Stopwatch.GetElapsedTime(started));
            activity?.SetTag("operation", "manual");
            activity?.SetTag("outcome", outcome);
            activity?.SetStatus(outcome is "applied" or "replayed" or "unchanged"
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error);
        }
    }

    public DeploymentLocationSnapshot Resolve(
        CaptureLocationProvenance provenance,
        DateTimeOffset? effectiveUtc = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var validation = provenance.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Capture deployment-location provenance is invalid.");
        }
        var history = Volatile.Read(ref _history)
            ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
        var snapshot = history.Snapshots.SingleOrDefault(item =>
            string.Equals(item.LocationId, provenance.LocationId, StringComparison.Ordinal) &&
            item.Version == provenance.Version);
        if (snapshot is null || snapshot.ToProvenance() != provenance)
        {
            throw new InvalidDataException("Capture deployment-location provenance does not match protected history.");
        }
        if (effectiveUtc.HasValue && !IsEffective(history, snapshot, ToMilliseconds(effectiveUtc.Value)))
        {
            throw new InvalidDataException("Capture time is outside the protected deployment-location interval.");
        }
        return snapshot;
    }

    private ReconciledLocation Reconcile(DeploymentLocationHistory? history, DeploymentLocationSeed seed)
    {
        if (history is not null && !string.Equals(history.LocationId, seed.LocationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configured deployment-location identity does not match protected history.");
        }

        var latest = history?.Snapshots[^1];
        if (latest is not null
            && history!.CentrallyActivatedCanonicalSha256 == latest.CanonicalSha256
            && history.ConfigurationSeed == seed)
        {
            return new ReconciledLocation(history, latest, Appended: false);
        }
        if (history is not null && history.ConfigurationSeed == seed && history.Candidate is not null)
        {
            return new ReconciledLocation(history, latest!, Appended: false);
        }
        if (latest is not null && SameConfiguredLocation(history!, latest, seed))
        {
            EnsureEffectiveAtStartup(latest, ToMilliseconds(_timeProvider.GetUtcNow()));
            var currentHistory = history!.ConfigurationSeed == seed && history.Candidate is null
                ? history
                : history with
                {
                    ConfigurationSeed = seed,
                    Candidate = null,
                    SourceKinds = history.SourceKinds
                        .Where(pair => history.Snapshots.Any(item => item.Version == pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value)
                };
            ValidateHistory(currentHistory);
            return new ReconciledLocation(currentHistory, latest, Appended: false);
        }

        var now = ToMilliseconds(_timeProvider.GetUtcNow());
        var effectiveFrom = seed.EffectiveFromUtc.HasValue
            ? ToMilliseconds(seed.EffectiveFromUtc.Value)
            : now;
        // The floor must include the instant the latest version was activated, not only its declared
        // effective-from: activating a staged version and appending in the same startup would otherwise
        // produce equal activation and supersession instants and fail the chronology invariant.
        var latestFloor = latest is null
            ? (DateTimeOffset?)null
            : history!.ActivatedAtUtc.TryGetValue(latest.Version, out var latestActivated)
                && latestActivated > latest.EffectiveFromUtc
                    ? latestActivated
                    : latest.EffectiveFromUtc;
        var latestProposedEffectiveFrom = history?.Candidate?.EffectiveFromUtc > latestFloor
            ? history.Candidate.EffectiveFromUtc
            : latestFloor;
        if (!seed.EffectiveFromUtc.HasValue
            && latestProposedEffectiveFrom.HasValue
            && effectiveFrom <= latestProposedEffectiveFrom.Value)
        {
            effectiveFrom = latestProposedEffectiveFrom.Value.AddMilliseconds(1);
        }
        DateTimeOffset? effectiveUntil = seed.EffectiveUntilUtc.HasValue
            ? ToMilliseconds(seed.EffectiveUntilUtc.Value)
            : null;
        var version = Math.Max(latest?.Version ?? 0, history?.Candidate?.Version ?? 0) + 1;
        var snapshot = DeploymentLocationSnapshot.Create(
            seed.LocationId,
            version,
            seed.Source,
            seed.HorizontalAccuracyMeters,
            effectiveFrom,
            effectiveUntil,
            seed.Coordinates.LatitudeDegrees,
            seed.Coordinates.LongitudeDegrees,
            seed.Coordinates.ElevationMeters,
            seed.Coordinates.TimeZoneId);
        var validation = snapshot.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"Configured deployment location is invalid ({validation.ReasonCode}, {validation.FieldPath}).");
        }
        if (latest is not null && (effectiveFrom <= latest.EffectiveFromUtc ||
            latest.EffectiveUntilUtc is { } previousUntil && effectiveFrom < previousUntil))
        {
            throw new InvalidDataException("Configured deployment-location interval overlaps protected history.");
        }

        if (history is not null && _options.CentralIntegration.Mode == CentralIntegrationMode.Enabled)
        {
            var candidateHistory = history with
            {
                Candidate = snapshot,
                Staged = null,
                ConfigurationSeed = seed,
                SourceKinds = history.SourceKinds
                    .Where(pair => history.Snapshots.Any(item => item.Version == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
                    .Append(new KeyValuePair<long, DeploymentLocationSourceKind>(snapshot.Version, seed.SourceKind))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            ValidateHistory(candidateHistory);
            return new ReconciledLocation(candidateHistory, latest!, Appended: false);
        }

        // When the floor above pushed the effective-from past `now`, the reconciler synthesized that
        // instant itself; the startup check exists to reject a declared window that misses startup.
        EnsureEffectiveAtStartup(
            snapshot,
            !seed.EffectiveFromUtc.HasValue && effectiveFrom > now ? effectiveFrom : now);
        var updated = new DeploymentLocationHistory(
            CurrentSchemaVersion,
            seed.LocationId,
            history is null ? [snapshot] : [.. history.Snapshots, snapshot],
            history is null
                ? new Dictionary<long, DateTimeOffset>()
                : new Dictionary<long, DateTimeOffset>(history.SupersededAtUtc)
                {
                    [latest!.Version] = effectiveFrom
                },
            ActivatedAtUtc: history is null
                ? new Dictionary<long, DateTimeOffset> { [snapshot.Version] = effectiveFrom }
                : new Dictionary<long, DateTimeOffset>(history.ActivatedAtUtc)
                {
                    [snapshot.Version] = effectiveFrom
                },
            ConfigurationSeed: seed,
            SourceKinds: history is null
                ? new Dictionary<long, DeploymentLocationSourceKind> { [snapshot.Version] = seed.SourceKind }
                : new Dictionary<long, DeploymentLocationSourceKind>(history.SourceKinds)
                {
                    [snapshot.Version] = seed.SourceKind
                },
            Staged: null,
            CentrallyActivatedCanonicalSha256: null,
            Candidate: null,
            RetainedIntegrityNonce: history?.RetainedIntegrityNonce);
        ValidateHistory(updated);
        return new ReconciledLocation(updated, snapshot, Appended: true);
    }

    private static ReconciledLocation ActivateStaged(
        DeploymentLocationHistory history,
        DateTimeOffset activatedAtUtc)
    {
        var staged = history.Staged!;
        var latest = history.Snapshots[^1];
        ValidateSuccessor(latest, staged);
        EnsureEffectiveAtStartup(staged, activatedAtUtc);
        var updated = history with
        {
            Snapshots = [.. history.Snapshots, staged],
            SupersededAtUtc = new Dictionary<long, DateTimeOffset>(history.SupersededAtUtc)
            {
                [latest.Version] = activatedAtUtc
            },
            ActivatedAtUtc = new Dictionary<long, DateTimeOffset>(history.ActivatedAtUtc)
            {
                [staged.Version] = activatedAtUtc
            },
            Staged = null,
            Candidate = null,
            CentrallyActivatedCanonicalSha256 = staged.CanonicalSha256
        };
        ValidateHistory(updated);
        return new ReconciledLocation(updated, staged, Appended: true);
    }

    private static void ValidateSuccessor(
        DeploymentLocationSnapshot active,
        DeploymentLocationSnapshot successor)
    {
        if (!string.Equals(successor.LocationId, active.LocationId, StringComparison.Ordinal)
            || successor.Version <= active.Version
            || successor.EffectiveFromUtc <= active.EffectiveFromUtc
            || active.EffectiveUntilUtc is { } activeUntil && successor.EffectiveFromUtc < activeUntil)
        {
            throw new InvalidDataException(
                "Acknowledged deployment location does not continue protected history.");
        }
    }

    private static bool SameConfiguredLocation(
        DeploymentLocationHistory history,
        DeploymentLocationSnapshot snapshot,
        DeploymentLocationSeed seed)
        => string.Equals(snapshot.LocationId, seed.LocationId, StringComparison.Ordinal) &&
           string.Equals(snapshot.Source, seed.Source, StringComparison.Ordinal) &&
           history.SourceKinds.TryGetValue(snapshot.Version, out var sourceKind) &&
           sourceKind == seed.SourceKind &&
           snapshot.HorizontalAccuracyMeters == seed.HorizontalAccuracyMeters &&
           snapshot.LatitudeDegrees == seed.Coordinates.LatitudeDegrees &&
           snapshot.LongitudeDegrees == seed.Coordinates.LongitudeDegrees &&
           snapshot.ElevationMeters == seed.Coordinates.ElevationMeters &&
           string.Equals(snapshot.TimeZoneId, seed.Coordinates.TimeZoneId, StringComparison.Ordinal) &&
           (!seed.EffectiveFromUtc.HasValue || snapshot.EffectiveFromUtc == ToMilliseconds(seed.EffectiveFromUtc.Value)) &&
           snapshot.EffectiveUntilUtc == (seed.EffectiveUntilUtc.HasValue
               ? ToMilliseconds(seed.EffectiveUntilUtc.Value)
               : null);

    private static DeploymentLocationSeed NormalizeSeed(DeploymentLocationSeed seed)
        => seed with
        {
            LocationId = seed.LocationId?.Trim() ?? string.Empty,
            Source = seed.Source?.Trim() ?? string.Empty,
            Coordinates = seed.Coordinates with
            {
                LatitudeDegrees = seed.Coordinates.LatitudeDegrees == 0 ? 0 : seed.Coordinates.LatitudeDegrees,
                LongitudeDegrees = seed.Coordinates.LongitudeDegrees == 0 ? 0 : seed.Coordinates.LongitudeDegrees,
                ElevationMeters = seed.Coordinates.ElevationMeters == 0 ? 0 : seed.Coordinates.ElevationMeters,
                TimeZoneId = seed.Coordinates.TimeZoneId?.Trim() ?? string.Empty
            }
        };

    private static void EnsureEffectiveAtStartup(DeploymentLocationSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.EffectiveFromUtc > now || snapshot.EffectiveUntilUtc is { } until && until <= now)
        {
            throw new InvalidDataException("Configured deployment location is not effective at startup.");
        }
    }

    private async ValueTask<DeploymentLocationHistory> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var activity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.read-protected-history");
        using var attribution = DeploymentLocationReadAttribution.Create(activity);
        try
        {
            using (attribution?.Measure(ReadStage.Security))
                RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetDirectoryName(path)!, path);
            byte[] protectedPayload;
            using (attribution?.Measure(ReadStage.Read, synchronous: false))
            {
                protectedPayload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                attribution?.AddBytes(ReadStage.Read, protectedPayload.LongLength);
            }
            byte[] plaintext;
            using (attribution?.Measure(ReadStage.Unprotect))
            {
                plaintext = _protector.Unprotect(protectedPayload);
                attribution?.AddBytes(ReadStage.Unprotect, plaintext.LongLength);
            }
            DeploymentLocationHistory history;
            attribution?.AddBytes(ReadStage.Deserialize, plaintext.LongLength);
            using (attribution?.Measure(ReadStage.Deserialize))
                history = JsonSerializer.Deserialize<DeploymentLocationHistory>(plaintext, SerializerOptions)
                    ?? throw new InvalidDataException("Protected deployment-location history is empty.");
            using (attribution?.Measure(ReadStage.Validate))
                ValidateHistory(history);
            return history;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException("Protected deployment-location history is unreadable.", exception);
        }
    }

    private async ValueTask WriteAsync(
        string path,
        DeploymentLocationHistory history,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(history, SerializerOptions);
        var protectedPayload = _protector.Protect(plaintext);
        EnsureProtectionKeysDurable(path);
        await WriteDurableAsync(path, protectedPayload, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteMarkerAsync(
        string path,
        DeploymentLocationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var marker = new DeploymentLocationMarker(CurrentSchemaVersion, snapshot.LocationId, snapshot.Version);
        await WriteDurableAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(marker, SerializerOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteDurableAsync(string path, byte[] payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        RawIngressFileStore.EnsureNoSymbolicLinks(directory, path);
        var temporaryPath = Path.Combine(directory, string.Concat('.', Path.GetFileName(path), '.', Guid.NewGuid().ToString("N"), ".tmp"));
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                CameraAgentFileSystem.FlushFile(stream);
            }
            RestrictFile(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            CameraAgentFileSystem.PublishPrepared(directory, temporaryPath, path, overwrite: true);
            RestrictFile(path);
            BeforeStateDirectorySyncForTest?.Invoke(directory);
            RawIngressFileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                RetainedFileMutations.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureProtectionKeysDurable(string statePath)
    {
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        var keyDirectory = Path.Combine(stateDirectory, "keys");
        if (!Directory.Exists(keyDirectory))
        {
            return;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(stateDirectory, keyDirectory);
        foreach (var keyFile in Directory.EnumerateFiles(keyDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            RawIngressFileStore.SyncFile(stateDirectory, keyFile);
        }
        RawIngressFileStore.SyncDirectory(keyDirectory);
    }

    private (string StatePath, string MarkerPath, string ManualPath) EnsureStatePaths()
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("CameraAgent data root has no parent directory.");
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        Directory.CreateDirectory(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        RestrictDirectory(root);
        CameraAgentFileSystem.EnsureStorageRootDurable(root);
        var directory = Path.GetFullPath(Path.Combine(root, ".location"));
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        RestrictDirectory(directory);
        BeforeStateDirectorySyncForTest?.Invoke(root);
        RawIngressFileStore.SyncDirectoryHierarchy(root, directory);
        var path = Path.GetFullPath(Path.Combine(directory, "deployment-location.v1.protected"));
        // The manual record is a separate protected file rather than a field of the history document:
        // the baseline ignores this sidecar. Ledger adoption seals the history itself;
        // rollback across that reader boundary restores the paired pre-upgrade files.
        var manualPath = Path.GetFullPath(Path.Combine(directory, "manual-deployment-location.v1.protected"));
        var rootPrefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison) || !manualPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException("Deployment-location state path escapes the CameraAgent data root.");
        }
        return (path, Path.Combine(root, ".deployment-location.v1.identity"), manualPath);
    }

    private static void ValidateHistory(DeploymentLocationHistory history)
    {
        if (history.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(history.LocationId) ||
            history.Snapshots is null || history.Snapshots.Count == 0 || history.SupersededAtUtc is null ||
            history.ConfigurationSeed is null || history.ActivatedAtUtc is null || history.SourceKinds is null)
        {
            throw new InvalidDataException("Protected deployment-location history has an unsupported schema.");
        }
        if (history.RetainedIntegrityNonce is { } nonce && (nonce.Length != 32 || nonce.Any(static c => !char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("Protected retained integrity binding is invalid.");
        if (!string.Equals(history.ConfigurationSeed.LocationId, history.LocationId, StringComparison.Ordinal)
            || history.ConfigurationSeed.Coordinates is null
            || history.ConfigurationSeed != NormalizeSeed(history.ConfigurationSeed)
            || !Enum.IsDefined(history.ConfigurationSeed.SourceKind))
        {
            throw new InvalidDataException("Protected deployment-location configuration seed is invalid.");
        }
        if (history.Staged is { } staged)
        {
            var validation = staged.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidDataException("Protected staged deployment location failed integrity validation.");
            }
            ValidateSuccessor(history.Snapshots[^1], staged);
            if (history.Candidate is null)
            {
                throw new InvalidDataException(
                    "Protected staged deployment location is missing its candidate.");
            }
        }
        if (history.Candidate is { } candidate)
        {
            var validation = candidate.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidDataException("Protected deployment-location candidate failed integrity validation.");
            }
            ValidateSuccessor(history.Snapshots[^1], candidate);
            if (history.Staged is not null
                && !string.Equals(
                    history.Staged.CanonicalSha256,
                    candidate.CanonicalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Protected staged deployment location does not match its candidate.");
            }
        }
        if (history.CentrallyActivatedCanonicalSha256 is { } centralHash
            && (centralHash.Length != 64
                || !string.Equals(centralHash, history.Snapshots[^1].CanonicalSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Protected central deployment activation is inconsistent with history.");
        }
        DeploymentLocationSnapshot? previous = null;
        for (var index = 0; index < history.Snapshots.Count; index++)
        {
            var snapshot = history.Snapshots[index];
            var validation = snapshot.Validate();
            if (!validation.IsValid || !string.Equals(snapshot.LocationId, history.LocationId, StringComparison.Ordinal) ||
                previous is null && snapshot.Version != 1 ||
                previous is not null && snapshot.Version <= previous.Version ||
                previous is not null && snapshot.EffectiveFromUtc <= previous.EffectiveFromUtc)
            {
                throw new InvalidDataException("Protected deployment-location history failed integrity validation.");
            }
            var isLatest = index == history.Snapshots.Count - 1;
            if (isLatest && history.SupersededAtUtc.ContainsKey(snapshot.Version) ||
                !isLatest && (!history.SupersededAtUtc.TryGetValue(snapshot.Version, out var superseded) ||
                    superseded < history.Snapshots[index + 1].EffectiveFromUtc ||
                    snapshot.EffectiveUntilUtc is { } until && until > superseded))
            {
                throw new InvalidDataException("Protected deployment-location history contains overlapping intervals.");
            }
            previous = snapshot;
        }
        var snapshotVersions = history.Snapshots.Select(item => item.Version).ToHashSet();
        if (history.ActivatedAtUtc.Count != snapshotVersions.Count
            || history.ActivatedAtUtc.Keys.Any(version => !snapshotVersions.Contains(version)))
        {
            throw new InvalidDataException("Protected deployment-location activation history is invalid.");
        }
        foreach (var snapshot in history.Snapshots)
        {
            if (!history.ActivatedAtUtc.TryGetValue(snapshot.Version, out var activated)
                || activated < snapshot.EffectiveFromUtc
                || history.SupersededAtUtc.TryGetValue(snapshot.Version, out var superseded)
                && activated >= superseded)
            {
                throw new InvalidDataException(
                    "Protected deployment-location activation chronology is invalid.");
            }
        }
        var supersededVersions = history.Snapshots.Take(history.Snapshots.Count - 1)
            .Select(item => item.Version)
            .ToHashSet();
        if (!supersededVersions.SetEquals(history.SupersededAtUtc.Keys))
        {
            throw new InvalidDataException("Protected deployment-location supersession history is invalid.");
        }
        for (var index = 1; index < history.Snapshots.Count; index++)
        {
            if (history.SupersededAtUtc[history.Snapshots[index - 1].Version]
                != history.ActivatedAtUtc[history.Snapshots[index].Version])
            {
                throw new InvalidDataException(
                    "Protected deployment-location activation does not match its supersession boundary.");
            }
        }
        var knownVersions = snapshotVersions
            .Concat(history.Candidate is null ? [] : [history.Candidate.Version])
            .Concat(history.Staged is null ? [] : [history.Staged.Version])
            .ToHashSet();
        if (history.SourceKinds.Count != knownVersions.Count
            || history.SourceKinds.Any(pair => !knownVersions.Contains(pair.Key) || !Enum.IsDefined(pair.Value)))
        {
            throw new InvalidDataException("Protected deployment-location source classifications are invalid.");
        }
        var configuredSnapshot = history.Candidate ?? history.Snapshots[^1];
        if (!SameConfiguredLocation(history, configuredSnapshot, history.ConfigurationSeed))
        {
            throw new InvalidDataException(
                "Protected deployment-location configuration seed does not match protected history.");
        }
    }

    private static bool IsEffective(
        DeploymentLocationHistory history,
        DeploymentLocationSnapshot snapshot,
        DateTimeOffset utc)
    {
        var effectiveUntil = snapshot.EffectiveUntilUtc;
        if (history.SupersededAtUtc.TryGetValue(snapshot.Version, out var superseded) &&
            (!effectiveUntil.HasValue || superseded < effectiveUntil.Value))
        {
            effectiveUntil = superseded;
        }
        var effectiveFrom = snapshot.EffectiveFromUtc;
        if (history.ActivatedAtUtc.TryGetValue(snapshot.Version, out var activated) && activated > effectiveFrom)
        {
            effectiveFrom = activated;
        }
        return utc >= effectiveFrom && (!effectiveUntil.HasValue || utc < effectiveUntil.Value);
    }

    private static async ValueTask<DeploymentLocationMarker> ReadMarkerAsync(
        string path,
        CancellationToken cancellationToken)
        => JsonSerializer.Deserialize<DeploymentLocationMarker>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), SerializerOptions)
           ?? throw new InvalidDataException("Deployment-location identity marker is invalid.");

    private static void ValidateMarker(
        DeploymentLocationMarker? marker,
        DeploymentLocationHistory? history)
    {
        if (marker is null)
        {
            return;
        }
        if (marker.SchemaVersion != CurrentSchemaVersion || history is null ||
            !string.Equals(marker.LocationId, history.LocationId, StringComparison.Ordinal) ||
            history.Snapshots.All(snapshot => snapshot.Version != marker.Version))
        {
            throw new InvalidDataException("Deployment-location identity marker does not match protected history.");
        }
    }

    private static byte[] RetainedIdentity(DeploymentLocationHistory history)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(history, SerializerOptions));
        hash.AppendData(typeof(ProtectedDeploymentLocationStore).Module.ModuleVersionId.ToByteArray());
        hash.AppendData(typeof(CaptureContractJson).Module.ModuleVersionId.ToByteArray());
        hash.AppendData(typeof(object).Module.ModuleVersionId.ToByteArray());
        return hash.GetHashAndReset();
    }

    private RetainedManifestValidationIndex CreateRetainedIndex(string historyPath, DeploymentLocationHistory history)
    {
        CleanupRetainedTemporaryFiles(Path.GetDirectoryName(historyPath)!);
        return new RetainedManifestValidationIndex(Path.GetDirectoryName(historyPath)!, RetainedIdentity(history),
            _protector, _timeProvider.GetUtcNow(), () => DeploymentLocationLog.RetainedProofCacheUnavailable(_logger),
            BeforeRetainedProofCommitForTest);
    }

    private RetainedIntegrityLedger.Scrub? BeginIntegrityScrub(RetainedIntegrityLedger? ledger,
        DeploymentLocationHistory history, bool strict, CancellationToken cancellationToken)
    {
        if (ledger is null && strict) throw new InvalidDataException("Retained integrity ledger is unavailable for qualification.");
        try { return ledger?.BeginScrub(RetainedIdentity(history), key => ReadIntegrityEntry(key, history), strict, cancellationToken); }
        catch (Exception exception) when (!strict && exception is (InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or Microsoft.Data.Sqlite.SqliteException or JsonException))
        {
            // A damaged ledger never qualifies, but does not close the recovery UI.
            DeploymentLocationLog.RetainedIntegrityUnqualified(_logger);
            return null;
        }
    }

    private RetainedIntegrityLedger? GetIntegrity(DeploymentLocationHistory history)
    {
        if (_integrity is not null)
        {
            if (history.RetainedIntegrityNonce is null || string.Equals(history.RetainedIntegrityNonce, _integrity.Nonce, StringComparison.Ordinal))
                return _integrity;
            DeploymentLocationLog.RetainedIntegrityUnqualified(_logger);
            return null;
        }
        _bootstrapHistory = history;
        try
        {
            _integrity = new RetainedIntegrityLedger(_options.RawIngressRoot, _protector,
                key => ReadIntegrityEntry(key, Volatile.Read(ref _history) ?? _bootstrapHistory),
                () => DeploymentLocationLog.RetainedIntegrityUnqualified(_logger), _timeProvider, history.RetainedIntegrityNonce);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or Microsoft.Data.Sqlite.SqliteException or JsonException or FormatException)
        {
            DeploymentLocationLog.RetainedIntegrityUnqualified(_logger);
        }
        return _integrity;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Both query branches are fixed SQL selected by a local schema capability; the capture key is parameterized.")]
    private RetainedIntegrityLedger.Entry? ReadIntegrityEntry(string key, DeploymentLocationHistory history)
    {
        byte[]? bytes;
        var frame = false;
        if (key.StartsWith("F|", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(_options.RawIngressRoot, key[2..]));
            RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, path);
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
            frame = IsFrameSidecar(_options.RawIngressRoot, path);
        }
        else
        {
            var path = Path.Combine(_options.RawIngressRoot, "journal", "raw-ingress.db");
            if (!File.Exists(path)) return null;
            RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, path);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='raw_captures';";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return null;
            command.CommandText = JournalHasCaptureId(connection)
                ? "SELECT manifest_json FROM raw_captures WHERE capture_id=$capture;"
                : "SELECT manifest_json FROM raw_captures WHERE rowid=$capture;";
            command.Parameters.AddWithValue("$capture", key[2..]);
            bytes = command.ExecuteScalar() as byte[];
            if (bytes is null) return null;
            frame = true;
        }
        var (manifest, other) = ParseRetainedManifest(bytes, frame, strict: false);
        var kind = manifest is not null ? 1 : other ? 2 : 3;
        if (manifest?.Descriptor.Location is { } provenance)
        {
            var snapshot = history.Snapshots.SingleOrDefault(item => item.LocationId == provenance.LocationId && item.Version == provenance.Version);
            if (snapshot is null || snapshot.ToProvenance() != provenance || !IsEffective(history, snapshot, manifest.Descriptor.Timing.ExposureStartedUtc)) kind = 3;
        }
        return new RetainedIntegrityLedger.Entry(key, SHA256.HashData(bytes), kind);
    }

    private static bool JournalHasCaptureId(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(raw_captures);";
        using var reader = columns.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1) == "capture_id") return true;
        return false;
    }

    private void CleanupRetainedTemporaryFiles(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, ".retained-validation.v1.protected.*.tmp").Take(8))
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(directory, file);
                RetainedFileMutations.Delete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeploymentLocationLog.RetainedProofCacheUnavailable(_logger);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The nullable observation lease is always disposed by its using declaration.")]
    private void ValidateRetainedEvidence(DeploymentLocationHistory history,
        RetainedManifestValidationIndex summary, bool strict, CancellationToken cancellationToken, RetainedIntegrityLedger.Scrub? scrub = null)
    {
        using var activity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.validate-retained");
        var root = Path.GetFullPath(_options.RawIngressRoot);
        if (!Directory.Exists(root))
        {
            return;
        }
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        using (var fileActivity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.validate-retained-files"))
        {
            using var attribution = DeploymentLocationReadAttribution.Create(fileActivity);
            IEnumerator<string> paths;
            using (attribution?.Measure(ReadStage.Enumeration))
                paths = Directory.EnumerateFiles(root, "*.json", enumeration).GetEnumerator();
            using var retainedPaths = paths;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext;
                string? path;
                using (attribution?.Measure(ReadStage.Enumeration))
                {
                    hasNext = retainedPaths.MoveNext();
                    path = hasNext ? retainedPaths.Current : null;
                }
                if (!hasNext) break;
                using var observation = scrub?.Observe(cancellationToken);
                try
                {
                    using (attribution?.Measure(ReadStage.Security))
                        RawIngressFileStore.EnsureNoSymbolicLinks(root, path!);
                    byte[] manifestJson;
                    using (attribution?.Measure(ReadStage.Read))
                    {
                        manifestJson = File.ReadAllBytes(path!);
                        attribution?.AddBytes(ReadStage.Read, manifestJson.LongLength);
                    }
                    BeforeRetainedManifestValidationForTest?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ValidateRetainedManifest(history, manifestJson, summary, attribution,
                        requireManifest: IsFrameSidecar(root, path!), strict);
                    scrub?.Record(new RetainedIntegrityLedger.Entry(RetainedIntegrityLedger.FileKey(root, path!), result.Digest, result.Kind));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        "Existing capture evidence could not be inspected for location provenance.", exception);
                }
            }
        }
        ValidateJournalEvidence(root, history, summary, strict, cancellationToken, scrub);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsFrameSidecar(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0] == "frames";

    private static (byte[] Digest, int Kind) ValidateRetainedManifest(DeploymentLocationHistory history, byte[] manifestJson,
        RetainedManifestValidationIndex summary, DeploymentLocationReadAttribution? attribution, bool requireManifest, bool strict)
    {
        attribution?.AddManifest(manifestJson.LongLength);
        byte[] digest;
        using (attribution?.Measure(ReadStage.Hash)) digest = RetainedManifestValidationIndex.Hash(manifestJson);
        var known = summary.Find(digest);
        if (known is not null)
        {
            attribution?.AddCacheHit();
            if (requireManifest && known == RetainedManifestValidationIndex.ProofKind.OtherJson)
            {
                if (strict) throw new InvalidDataException("Retained capture manifest failed schema or contract validation.");
                return (digest, 3);
            }
            return (digest, (int)known.Value);
        }
        ArtifactManifestV2? manifest;
        bool otherJson;
        attribution?.AddBytes(ReadStage.Parse, manifestJson.LongLength);
        using (attribution?.Measure(ReadStage.Parse))
            (manifest, otherJson) = ParseRetainedManifest(manifestJson, requireManifest, strict);
        if (manifest is null)
        {
            if (otherJson) summary.Record(digest, RetainedManifestValidationIndex.ProofKind.OtherJson);
            return (digest, otherJson ? 2 : 3);
        }
        using var provenanceStage = attribution?.Measure(ReadStage.Provenance);
        var provenance = manifest.Descriptor.Location;
        if (provenance is not null)
        {
            var snapshot = history.Snapshots.SingleOrDefault(item =>
                string.Equals(item.LocationId, provenance.LocationId, StringComparison.Ordinal) && item.Version == provenance.Version);
            if (snapshot is null || snapshot.ToProvenance() != provenance ||
                !IsEffective(history, snapshot, manifest.Descriptor.Timing.ExposureStartedUtc))
                throw new InvalidDataException("Retained capture location provenance does not match protected deployment-location history.");
        }
        summary.Record(digest, RetainedManifestValidationIndex.ProofKind.Artifact);
        return (digest, 1);
    }

    private static (ArtifactManifestV2? Manifest, bool OtherJson) ParseRetainedManifest(byte[] bytes, bool requireManifest, bool strict)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var claimsManifest = root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.String &&
                    schema.ValueEquals(ArtifactManifestV2.CurrentSchemaVersion) ||
                 root.TryGetProperty("descriptor", out _) && root.TryGetProperty("relativeArtifactPath", out _));
            if (!requireManifest && !claimsManifest) return (null, true);
            var manifest = CaptureContractJson.ParseManifest(bytes).Document?.Manifest;
            if (manifest is not null) return (manifest, false);
        }
        catch (JsonException) { }
        // Startup lets reconciliation and recovery UI inspect invalid/torn sidecars. The protected
        // endpoint uses strict mode and cannot return installation identity for those same bytes.
        if (strict) throw new InvalidDataException("Retained capture manifest failed schema or contract validation.");
        return (null, false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The nullable observation lease is always disposed by its using declaration.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Both branches are fixed internal schema queries with no supplied SQL.")]
    private void ValidateJournalEvidence(string root, DeploymentLocationHistory history,
        RetainedManifestValidationIndex summary, bool strict, CancellationToken cancellationToken, RetainedIntegrityLedger.Scrub? scrub = null)
    {
        using var activity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.validate-retained-journal");
        using var attribution = DeploymentLocationReadAttribution.Create(activity);
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path))
        {
            return;
        }
        using (attribution?.Measure(ReadStage.Security))
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        using (attribution?.Measure(ReadStage.Open))
            connection.Open();
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'raw_captures';";
        long tableCount;
        using (attribution?.Measure(ReadStage.Query))
            tableCount = Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (tableCount == 0)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = JournalHasCaptureId(connection) ? "SELECT capture_id,manifest_json FROM raw_captures;" : "SELECT CAST(rowid AS TEXT),manifest_json FROM raw_captures;";
        Microsoft.Data.Sqlite.SqliteDataReader reader;
        using (attribution?.Measure(ReadStage.Query))
            reader = command.ExecuteReader();
        using var retainedReader = reader;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var observation = scrub?.Observe(cancellationToken);
            bool hasNext;
            byte[]? manifestJson;
            string? capture;
            using (attribution?.Measure(ReadStage.Read))
            {
                hasNext = retainedReader.Read();
                capture = hasNext ? retainedReader.GetString(0) : null;
                manifestJson = hasNext ? (byte[])retainedReader.GetValue(1) : null;
                if (manifestJson is not null) attribution?.AddBytes(ReadStage.Read, manifestJson.LongLength);
            }
            if (!hasNext) break;
            BeforeRetainedManifestValidationForTest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var result = ValidateRetainedManifest(history, manifestJson!, summary, attribution, requireManifest: true, strict);
            scrub?.Record(new RetainedIntegrityLedger.Entry(RetainedIntegrityLedger.JournalKey(capture!), result.Digest, result.Kind));
        }
    }

    private bool HasLocationBearingEvidence(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        if (!Directory.Exists(root))
        {
            return false;
        }
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*.json", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
                if (parsed.Document?.Manifest?.Descriptor.Location is not null)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "Existing capture evidence could not be inspected for location provenance.", exception);
            }
        }
        return JournalContainsLocationEvidence(root);
    }

    private static bool JournalContainsLocationEvidence(string root)
    {
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path))
        {
            return false;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'raw_captures';";
        if (Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM raw_captures WHERE CAST(manifest_json AS TEXT) LIKE '%\"location\"%' LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static long KnownVersion(DeploymentLocationHistory history)
        => Math.Max(
            history.Snapshots[^1].Version,
            Math.Max(history.Candidate?.Version ?? 0, history.Staged?.Version ?? 0));

    private static bool GovernsConfiguration(
        ManualDeploymentLocationRecord record,
        DeploymentLocationSeed configurationSeed)
        => record.SupersededAtUtc is null
           && record.SchemaVersion == CurrentManualSchemaVersion
           && string.Equals(record.LocationId, configurationSeed.LocationId, StringComparison.Ordinal)
           && record.BaselineConfigurationSeed == configurationSeed;

    private static long ManualSequence(
        ManualDeploymentLocationRecord? manual,
        DeploymentLocationSeed? configurationSeed)
        => manual is not null
           && configurationSeed is not null
           && GovernsConfiguration(manual, configurationSeed)
           && manual.Entries.Count > 0
            ? manual.Entries[^1].Sequence
            : 0;

    private static DeploymentLocationSeed GoverningSeed(
        DeploymentLocationHistory history,
        ManualDeploymentLocationRecord? manual,
        DeploymentLocationSeed configurationSeed)
        => manual is not null && GovernsConfiguration(manual, configurationSeed)
            ? manual.Seed
            : history.ConfigurationSeed;

    private static bool SameCoordinates(ObservatoryLocation coordinates, DeploymentLocationSnapshot snapshot)
        => coordinates.LatitudeDegrees == snapshot.LatitudeDegrees
           && coordinates.LongitudeDegrees == snapshot.LongitudeDegrees
           && coordinates.ElevationMeters == snapshot.ElevationMeters
           && string.Equals(coordinates.TimeZoneId, snapshot.TimeZoneId, StringComparison.Ordinal);

    private static bool SameCoordinates(
        ManualDeploymentLocationAuditEntry entry,
        DeploymentLocationSnapshot snapshot)
        => entry.LatitudeDegrees == snapshot.LatitudeDegrees
           && entry.LongitudeDegrees == snapshot.LongitudeDegrees
           && entry.ElevationMeters == snapshot.ElevationMeters
           && string.Equals(entry.TimeZoneId, snapshot.TimeZoneId, StringComparison.Ordinal);

    private ManualDeploymentLocationResult ManualOutcome(
        ManualDeploymentLocationStatus status,
        string? reasonCode,
        string? fieldPath,
        DeploymentLocationHistory history,
        ManualDeploymentLocationRecord? manual,
        DeploymentLocationSeed configurationSeed)
        => new(status, reasonCode, fieldPath, ProjectManual(history, manual, configurationSeed));

    private ManualDeploymentLocationState ProjectManual(
        DeploymentLocationHistory? history,
        ManualDeploymentLocationRecord? manual,
        DeploymentLocationSeed? configurationSeed)
    {
        var centralAcknowledgementRequired = _options.CentralIntegration.Mode == CentralIntegrationMode.Enabled;
        if (history is null)
        {
            // No history means no version to append to, so reporting the contract as available would
            // offer an entry form whose every command fails.
            return ManualDeploymentLocationState.Unsupported;
        }
        var governs = manual is not null
            && configurationSeed is not null
            && GovernsConfiguration(manual, configurationSeed);
        ManualDeploymentLocationOverride? governing = null;
        if (governs && manual!.Entries.Count > 0)
        {
            var latest = manual.Entries[^1];
            governing = new ManualDeploymentLocationOverride(
                latest.LatitudeDegrees,
                latest.LongitudeDegrees,
                latest.ElevationMeters,
                latest.TimeZoneId,
                !SameCoordinates(manual.Seed.Coordinates, history.Snapshots[^1]),
                latest.RecordedAtUtc,
                latest.Actor,
                latest.Reason);
        }
        var knownVersion = KnownVersion(history);
        return new ManualDeploymentLocationState(
            Supported: true,
            LocationId: history.LocationId,
            ActiveVersion: history.Snapshots[^1].Version,
            KnownVersion: knownVersion,
            NextVersion: knownVersion + 1,
            // Only the candidate or staged snapshot that carries this manual entry names its version.
            // A candidate created before the newest entry describes a different one.
            PendingVersion: (history.Staged ?? history.Candidate) is { } pendingSnapshot
                && governs
                && SameCoordinates(manual!.Seed.Coordinates, pendingSnapshot)
                    ? pendingSnapshot.Version
                    : null,
            ManualSequence: ManualSequence(manual, configurationSeed),
            CentralAcknowledgementRequired: centralAcknowledgementRequired,
            StagedAcknowledgementPending: history.Staged is not null,
            CandidateAwaitingAcknowledgement: history.Candidate is not null,
            Override: governing,
            OverrideSupersededAtUtc: manual?.SupersededAtUtc,
            History: manual is null
                ? []
                : [.. manual.Entries
                    .Reverse()
                    .Take(ManualDeploymentLocationContract.MaximumProjectedEntries)]);
    }

    private async ValueTask<ManualDeploymentLocationRecord> ReadManualAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var activity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.read-protected-manual");
        using var attribution = DeploymentLocationReadAttribution.Create(activity);
        try
        {
            using (attribution?.Measure(ReadStage.Security))
                RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetDirectoryName(path)!, path);
            byte[] protectedPayload;
            using (attribution?.Measure(ReadStage.Read, synchronous: false))
            {
                protectedPayload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                attribution?.AddBytes(ReadStage.Read, protectedPayload.LongLength);
            }
            byte[] plaintext;
            using (attribution?.Measure(ReadStage.Unprotect))
            {
                plaintext = _protector.Unprotect(protectedPayload);
                attribution?.AddBytes(ReadStage.Unprotect, plaintext.LongLength);
            }
            ManualDeploymentLocationRecord record;
            attribution?.AddBytes(ReadStage.Deserialize, plaintext.LongLength);
            using (attribution?.Measure(ReadStage.Deserialize))
                record = JsonSerializer.Deserialize<ManualDeploymentLocationRecord>(plaintext, SerializerOptions)
                    ?? throw new InvalidDataException("Protected manual deployment-location state is empty.");
            using (attribution?.Measure(ReadStage.Validate))
                ValidateManual(record);
            return record;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException("Protected manual deployment-location state is unreadable.", exception);
        }
    }

    private async ValueTask WriteManualAsync(
        string path,
        ManualDeploymentLocationRecord record,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        var protectedPayload = _protector.Protect(plaintext);
        EnsureProtectionKeysDurable(path);
        await WriteDurableAsync(path, protectedPayload, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateManual(ManualDeploymentLocationRecord record)
    {
        if (record.SchemaVersion != CurrentManualSchemaVersion || string.IsNullOrWhiteSpace(record.LocationId) ||
            record.Seed is null || record.BaselineConfigurationSeed is null ||
            record.Entries is null || record.Entries.Count == 0)
        {
            throw new InvalidDataException("Protected manual deployment-location state has an unsupported schema.");
        }
        if (record.Seed != NormalizeSeed(record.Seed) ||
            record.BaselineConfigurationSeed != NormalizeSeed(record.BaselineConfigurationSeed) ||
            record.Seed.Coordinates is null || record.BaselineConfigurationSeed.Coordinates is null ||
            !string.Equals(record.Seed.LocationId, record.LocationId, StringComparison.Ordinal) ||
            !string.Equals(record.BaselineConfigurationSeed.LocationId, record.LocationId, StringComparison.Ordinal) ||
            record.Seed.SourceKind != DeploymentLocationSourceKind.Manual ||
            !Enum.IsDefined(record.BaselineConfigurationSeed.SourceKind) ||
            !string.Equals(
                record.Seed.Source, ManualDeploymentLocationContract.SourceLabel, StringComparison.Ordinal) ||
            record.Seed.HorizontalAccuracyMeters is not null ||
            record.Seed.EffectiveFromUtc is not null || record.Seed.EffectiveUntilUtc is not null ||
            record.SupersededAtUtc is { } superseded && superseded.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Protected manual deployment-location state failed integrity validation.");
        }
        if (record.SupersededThroughSequence < 0 ||
            record.SupersededThroughSequence > record.Entries[^1].Sequence)
        {
            throw new InvalidDataException(
                "Protected manual deployment-location supersession watermark is invalid.");
        }
        if (record.Entries.Count > ManualDeploymentLocationContract.MaximumRetainedEntries)
        {
            throw new InvalidDataException(
                "Protected manual deployment-location audit history exceeds its retained bound.");
        }
        var previous = 0L;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in record.Entries)
        {
            if (entry.Sequence <= previous || !keys.Add(entry.IdempotencyKey) || !IsValidManualEntry(entry))
            {
                throw new InvalidDataException(
                    "Protected manual deployment-location audit history failed integrity validation.");
            }
            previous = entry.Sequence;
        }
        if (!SameCoordinates(record.Seed.Coordinates, record.Entries[^1]))
        {
            throw new InvalidDataException(
                "Protected manual deployment-location state does not match its audit history.");
        }
    }

    private static bool SameCoordinates(
        ObservatoryLocation coordinates,
        ManualDeploymentLocationAuditEntry entry)
        => coordinates.LatitudeDegrees == entry.LatitudeDegrees
           && coordinates.LongitudeDegrees == entry.LongitudeDegrees
           && coordinates.ElevationMeters == entry.ElevationMeters
           && string.Equals(coordinates.TimeZoneId, entry.TimeZoneId, StringComparison.Ordinal);

    private static bool IsValidManualEntry(ManualDeploymentLocationAuditEntry entry)
        => entry.Sequence >= 1
           && entry.RecordedAtUtc.Offset == TimeSpan.Zero
           && entry.ExpectedVersion >= 1
           && !string.IsNullOrWhiteSpace(entry.Actor)
           && entry.Actor.Length <= ManualDeploymentLocationContract.MaximumActorLength
           && !string.IsNullOrWhiteSpace(entry.IdempotencyKey)
           && entry.IdempotencyKey.Length <= ManualDeploymentLocationContract.MaximumIdempotencyKeyLength
           && entry.Reason is null or { Length: <= ManualDeploymentLocationContract.MaximumReasonLength }
           && double.IsFinite(entry.LatitudeDegrees) && entry.LatitudeDegrees is >= -90 and <= 90
           && double.IsFinite(entry.LongitudeDegrees) && entry.LongitudeDegrees is >= -180 and <= 180
           // The manual elevation bound is deliberately not applied here: it is command policy, and
           // enforcing it on the read path would make an existing record unreadable if it ever tightened.
           && double.IsFinite(entry.ElevationMeters)
           && DeploymentLocationSnapshot.IsPortableTimeZoneId(entry.TimeZoneId);

    private void EnsurePhysicalStatePath(string path)
        => RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetFullPath(_options.RawIngressRoot), path);

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _integrity?.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record DeploymentLocationHistory(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string LocationId,
        [property: JsonRequired] IReadOnlyList<DeploymentLocationSnapshot> Snapshots,
        [property: JsonRequired] IReadOnlyDictionary<long, DateTimeOffset> SupersededAtUtc,
        [property: JsonRequired] DeploymentLocationSnapshot? Staged = null,
        [property: JsonRequired] string? CentrallyActivatedCanonicalSha256 = null,
        [property: JsonRequired] DeploymentLocationSnapshot? Candidate = null,
        [property: JsonRequired] DeploymentLocationSeed ConfigurationSeed = null!,
        [property: JsonRequired] IReadOnlyDictionary<long, DateTimeOffset> ActivatedAtUtc = null!,
        [property: JsonRequired] Dictionary<long, DeploymentLocationSourceKind> SourceKinds = null!,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RetainedIntegrityNonce = null);

    private sealed record DeploymentLocationMarker(int SchemaVersion, string LocationId, long Version);

    private sealed record ManualDeploymentLocationRecord(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string LocationId,
        [property: JsonRequired] DeploymentLocationSeed Seed,
        [property: JsonRequired] DeploymentLocationSeed BaselineConfigurationSeed,
        [property: JsonRequired] DateTimeOffset? SupersededAtUtc,
        // Every entry at or below this sequence was superseded by a configuration change and can never
        // govern again, even after a later entry makes the record itself governing.
        [property: JsonRequired] long SupersededThroughSequence,
        [property: JsonRequired] IReadOnlyList<ManualDeploymentLocationAuditEntry> Entries);

    private sealed record ReconciledLocation(
        DeploymentLocationHistory History,
        DeploymentLocationSnapshot Active,
        bool Appended);
}

internal static partial class DeploymentLocationLog
{
    [LoggerMessage(7308, LogLevel.Warning, "Retained installation integrity requires a complete scrub before protected verification")]
    internal static partial void RetainedIntegrityUnqualified(ILogger logger);

    [LoggerMessage(7307, LogLevel.Warning, "Optional retained validation index unavailable; verification will inspect current bytes without cached proofs")]
    internal static partial void RetainedProofCacheUnavailable(ILogger logger);

    [LoggerMessage(7301, LogLevel.Information,
        "Deployment location {LocationId} version {Version} appended to protected history")]
    internal static partial void VersionAppended(ILogger logger, string locationId, long version);

    [LoggerMessage(7302, LogLevel.Information,
        "Deployment location {LocationId} version {Version} loaded from protected history")]
    internal static partial void VersionLoaded(ILogger logger, string locationId, long version);

    [LoggerMessage(7305, LogLevel.Information,
        "Deployment location {LocationId} manual coordinate entry {Sequence} recorded; the next start appends its deployment version")]
    internal static partial void ManualEntryRecorded(ILogger logger, string locationId, long sequence);

    [LoggerMessage(7306, LogLevel.Information,
        "Deployment location {LocationId} manual override superseded by a changed startup configuration seed")]
    internal static partial void ManualOverrideSuperseded(ILogger logger, string locationId);
}
