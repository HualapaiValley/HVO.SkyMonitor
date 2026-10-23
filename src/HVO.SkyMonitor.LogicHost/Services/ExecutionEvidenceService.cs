using System.Collections.Immutable;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService(
    ExecutionEvidenceDatabase database,
    IOptions<ExecutionEvidenceOptions> options,
    TimeProvider timeProvider,
    ExecutionEvidenceTelemetry telemetry,
    IDataProtectionProvider protection)
{
    private readonly ApplicationDbContext _db = database.Context;
    private readonly ExecutionEvidenceOptions _options = options.Value;

    public async Task<ExecutionEvidenceBindingView?> CreateBindingAsync(ExecutionEvidenceBindingRequest request,
        string actorUserId, Guid? credentialObservatoryId, CancellationToken cancellationToken = default)
    {
        if (request.OriginInstallationId == Guid.Empty || request.AgentInstanceId == Guid.Empty)
        {
            throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.InvalidOrigin);
        }
        if (credentialObservatoryId is { } scope && scope != request.ObservatoryId ||
            !await ObservatoryMembershipAccess.ForOwner(_db, actorUserId)
                .AnyAsync(m => m.ObservatoryId == request.ObservatoryId, cancellationToken).ConfigureAwait(false)) { return null; }

        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        if (!await ObservatoryMembershipAccess.ForOwner(_db, actorUserId)
            .AnyAsync(m => m.ObservatoryId == request.ObservatoryId, cancellationToken).ConfigureAwait(false)) { return null; }
        var registration = await _db.DeviceRegistrations
            .FromSqlInterpolated($"SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.RegistrationId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var installation = await _db.LogicalCameraInstallations.AsNoTracking().Include(i => i.LogicalCamera)
            .SingleOrDefaultAsync(i => i.Id == request.LogicalCameraInstallationId, cancellationToken).ConfigureAwait(false);
        if (registration is null || registration.ObservatoryId != request.ObservatoryId ||
            registration.Status != DeviceRegistrationStatus.Active || registration.DevicePublicId is null ||
            installation is null || installation.RegistrationId != registration.Id ||
            installation.LogicalCamera!.ObservatoryId != request.ObservatoryId ||
            installation.InstallationPublicId != registration.DevicePublicId)
        {
            return null;
        }
        var existing = await _db.Set<ExecutionEvidenceBinding>().AsNoTracking().SingleOrDefaultAsync(b =>
            b.OriginInstallationId == request.OriginInstallationId && b.AgentInstanceId == request.AgentInstanceId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.RegistrationId == request.RegistrationId && existing.ObservatoryId == request.ObservatoryId &&
                existing.LogicalCameraInstallationId == request.LogicalCameraInstallationId ? View(existing) : null;
        }
        if (await _db.Set<ExecutionEvidenceBinding>().CountAsync(b => b.RegistrationId == request.RegistrationId, cancellationToken)
            .ConfigureAwait(false) >= _options.MaximumBindingsPerRegistration) { throw new ExecutionEvidencePressureException(); }
        var binding = new ExecutionEvidenceBinding
        {
            ObservatoryId = request.ObservatoryId,
            RegistrationId = request.RegistrationId,
            LogicalCameraInstallationId = request.LogicalCameraInstallationId,
            InstallationPublicId = registration.DevicePublicId.Value,
            OriginInstallationId = request.OriginInstallationId,
            AgentInstanceId = request.AgentInstanceId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            CreatedByUserId = actorUserId
        };
        _db.Add(binding);
        _db.Add(new ExecutionEvidenceBindingUsage { BindingId = binding.Id });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(binding);
    }

    public async Task<ExecutionEvidenceNegotiationResponseV1> NegotiateAsync(string deviceId, string deviceKey,
        ExecutionEvidenceNegotiationRequestV1 request, CancellationToken cancellationToken = default)
    {
        var validation = GraphExecutionEvidenceJson.Validate(request);
        if (!validation.IsValid) { throw new ExecutionEvidenceInvalidException(validation.ReasonCode!); }
        _ = await AuthenticateAsync(deviceId, deviceKey, request.Origin, cancellationToken).ConfigureAwait(false);
        return GraphExecutionEvidenceJson.Negotiate(request, timeProvider.GetUtcNow()) with
        {
            Limits = ExecutionEvidenceLimitsV1.Current with { MaximumResyncUnits = ExecutionEvidenceOptions.MaximumBatchUnits }
        };
    }

    public async Task<ExecutionEvidenceFeedbackV1> ReceiveAsync(string deviceId, string deviceKey,
        string originIdentitySha256, IReadOnlyList<ReadOnlyMemory<byte>> units, CancellationToken cancellationToken = default)
    {
        using var activity = ExecutionEvidenceTelemetry.StartActivity("execution-evidence.receive");
        if (units.Count is < 1 or > ExecutionEvidenceOptions.MaximumBatchUnits || !ExecutionEvidencePlanValidator.IsHash(originIdentitySha256) ||
            units.Sum(x => (long)x.Length + 1) > ExecutionEvidenceOptions.MaximumRequestBytes)
        { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.LimitExceeded); }
        // Credentials are checked before parsing an untrusted document or looking up any stored hash.
        _ = await new DeviceCredentialValidator(_db, timeProvider).ValidateAsync(deviceId, deviceKey, cancellationToken).ConfigureAwait(false);
        var parsed = new List<ExecutionEvidenceEnvelopeV1>(units.Count);
        ExecutionEvidenceBinding? binding = null;
        foreach (var bytes in units)
        {
            var result = GraphExecutionEvidenceJson.ParseEnvelope(bytes);
            if (result.Value is not { } envelope || !bytes.Span.SequenceEqual(GraphExecutionEvidenceJson.Serialize(envelope)))
            { throw new ExecutionEvidenceInvalidException(result.Validation.ReasonCode ?? GraphExecutionEvidenceReasonCodes.InvalidJson); }
            if (envelope.Origin.IdentitySha256 != originIdentitySha256) { throw new ExecutionEvidenceDeniedException(); }
            binding ??= await AuthenticateAsync(deviceId, deviceKey, envelope.Origin, cancellationToken).ConfigureAwait(false);
            parsed.Add(envelope);
        }
        var facts = ImmutableArray.CreateBuilder<ExecutionEvidenceFactV1>();
        long originId = 0;
        for (var i = 0; i < parsed.Count; i++)
        {
            var result = await ReceiveUnitAsync(deviceId, deviceKey, binding!, parsed[i], units[i], cancellationToken).ConfigureAwait(false);
            originId = result.OriginId;
            facts.AddRange(result.Facts);
        }
        return await FeedbackAsync(originId, facts.ToImmutable(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionEvidenceBinding> AuthenticateAsync(string deviceId, string deviceKey,
        ExecutionEvidenceOriginV1 origin, CancellationToken cancellationToken)
    {
        var registration = await new DeviceCredentialValidator(_db, timeProvider).ValidateAsync(deviceId, deviceKey, cancellationToken).ConfigureAwait(false);
        var binding = await _db.Set<ExecutionEvidenceBinding>().AsNoTracking().SingleOrDefaultAsync(b =>
            b.OriginInstallationId == origin.OriginInstallationId && b.AgentInstanceId == origin.AgentInstanceId,
            cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.RegistrationId != registration.Id || binding.ObservatoryId != registration.ObservatoryId ||
            binding.InstallationPublicId != registration.DevicePublicId ||
            origin.ObservatoryId is { } observatory && observatory != binding.ObservatoryId ||
            origin.LogicalCameraInstallationId is { } installation && installation != binding.LogicalCameraInstallationId ||
            origin.InstallationPublicId is { } publicId && publicId != binding.InstallationPublicId)
        { throw new ExecutionEvidenceDeniedException(); }
        return binding;
    }

    private async Task<(long OriginId, ImmutableArray<ExecutionEvidenceFactV1> Facts)> ReceiveUnitAsync(
        string deviceId, string deviceKey, ExecutionEvidenceBinding binding, ExecutionEvidenceEnvelopeV1 envelope,
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Hold a shared registration lock through this short SQL-only transaction: revocation cannot race an ack.
        _ = await _db.DeviceRegistrations.FromSqlInterpolated(
            $"SELECT * FROM [DeviceRegistrations] WITH (HOLDLOCK) WHERE [Id] = {binding.RegistrationId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        _ = await AuthenticateAsync(deviceId, deviceKey, envelope.Origin, cancellationToken).ConfigureAwait(false);
        var usage = await LockUsageAsync(binding.Id, cancellationToken).ConfigureAwait(false);
        var origin = await _db.Set<ImportedExecutionEvidenceOrigin>().SingleOrDefaultAsync(
            o => o.IdentitySha256 == envelope.Origin.IdentitySha256, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (origin is null)
        {
            if (usage.OriginCount >= _options.MaximumOriginsPerBinding) { throw new ExecutionEvidencePressureException(); }
            origin = new ImportedExecutionEvidenceOrigin
            {
                BindingId = binding.Id,
                IdentitySha256 = envelope.Origin.IdentitySha256,
                BootSessionId = envelope.Origin.BootSessionId,
                SoftwareVersion = envelope.Origin.SoftwareVersion,
                FirstReceivedAtUtc = now
            };
            _db.Add(origin);
            usage.OriginCount++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        origin.HighestSeenSequence = Math.Max(origin.HighestSeenSequence, envelope.OriginSequence);
        var stored = await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().SingleOrDefaultAsync(
            u => u.OriginId == origin.Id && u.OriginSequence == envelope.OriginSequence, cancellationToken).ConfigureAwait(false);
        ImmutableArray<ExecutionEvidenceFactV1> facts;
        string outcome;
        if (stored is not null)
        {
            if (stored.PayloadSha256 != envelope.PayloadSha256)
            {
                var conflict = await _db.Set<ImportedExecutionEvidenceConflict>().SingleOrDefaultAsync(c =>
                    c.UnitId == stored.Id && c.SubmittedPayloadSha256 == envelope.PayloadSha256, cancellationToken).ConfigureAwait(false);
                if (conflict is null)
                {
                    RequireCapacity(usage, 512, conflict: true);
                    conflict = new ImportedExecutionEvidenceConflict
                    {
                        BindingId = binding.Id,
                        UnitId = stored.Id,
                        SubmittedEvidenceId = envelope.EvidenceId,
                        SubmittedPayloadSha256 = envelope.PayloadSha256,
                        OccurredAtUtc = now
                    };
                    _db.Add(conflict);
                    usage.ConflictCount++;
                    usage.StoredBytes += 512;
                }
                facts = GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, stored.PayloadSha256, conflict.OccurredAtUtc);
                outcome = "conflict";
            }
            else
            {
                facts = StoredFacts(stored, envelope, duplicate: true);
                outcome = "duplicate";
            }
        }
        else
        {
            var validation = await ValidateContextAsync(binding, origin.Id, envelope, cancellationToken).ConfigureAwait(false);
            if (validation.ReasonCode is GraphExecutionEvidenceReasonCodes.UnknownRevision or GraphExecutionEvidenceReasonCodes.UnknownPredecessor)
            {
                // No terminal rejection: the existing sender quarantines on any Rejected fact. Its pending rows
                // remain retryable, and HighestSeenSequence makes the absent dependency visible in bounded resync.
                facts = [];
                outcome = "dependency";
            }
            else
            {
                var references = validation.IsValid ? ArtifactReferences(envelope).Distinct().ToArray() : [];
                var cost = bytes.Length + 1024L + references.Length * 1024L;
                RequireCapacity(usage, cost);
                stored = new ImportedExecutionEvidenceUnit
                {
                    BindingId = binding.Id,
                    OriginId = origin.Id,
                    OriginSequence = envelope.OriginSequence,
                    EvidenceId = envelope.EvidenceId,
                    PayloadSha256 = envelope.PayloadSha256,
                    Kind = envelope.Kind.ToString(),
                    RevisionId = envelope.GraphRevision?.RevisionId ?? envelope.Execution?.GraphRevisionId,
                    ExecutionId = envelope.Execution?.ExecutionId ?? envelope.Availability?.ExecutionId,
                    CaptureId = envelope.Execution?.CaptureId,
                    CorrectsEvidenceId = envelope.Correction?.CorrectsEvidenceId,
                    CorrectsOriginSequence = envelope.Correction?.CorrectsOriginSequence,
                    ProducedAtUtc = envelope.ProducedAtUtc,
                    ReceivedAtUtc = now,
                    CanonicalBytes = bytes.ToArray(),
                    Accepted = validation.IsValid,
                    RejectionReasonCode = validation.ReasonCode,
                    RejectionFieldPath = validation.FieldPath
                };
                _db.Add(stored);
                usage.UnitCount++;
                usage.StoredBytes += cost;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (validation.IsValid)
                {
                    await AddReferencesAsync(binding.Id, stored.Id, references, now, cancellationToken).ConfigureAwait(false);
                    await AddRangeAsync(origin, envelope.OriginSequence, cancellationToken).ConfigureAwait(false);
                }
                facts = StoredFacts(stored, envelope, duplicate: false);
                outcome = validation.IsValid ? "accepted" : "rejected";
            }
        }
        origin.GapDetectedAtUtc = origin.ContiguousThroughSequence == origin.HighestSeenSequence
            ? null : origin.GapDetectedAtUtc ?? now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.Unit(outcome);
        return (origin.Id, facts);
    }

    internal Task<ExecutionEvidenceBindingUsage> LockUsageAsync(Guid bindingId, CancellationToken cancellationToken)
        => _db.Set<ExecutionEvidenceBindingUsage>().FromSqlInterpolated(
            $"SELECT * FROM [ExecutionEvidenceBindingUsage] WITH (UPDLOCK, HOLDLOCK) WHERE [BindingId] = {bindingId}")
            .SingleAsync(cancellationToken);

    private void RequireCapacity(ExecutionEvidenceBindingUsage usage, long bytes, bool conflict = false)
    {
        if (usage.StoredBytes > _options.MaximumStoredBytesPerBinding - bytes ||
            (conflict ? usage.ConflictCount >= _options.MaximumConflictsPerBinding : usage.UnitCount >= _options.MaximumUnitsPerBinding))
        { throw new ExecutionEvidencePressureException(); }
    }

    private static ImmutableArray<ExecutionEvidenceFactV1> StoredFacts(ImportedExecutionEvidenceUnit stored,
        ExecutionEvidenceEnvelopeV1 envelope, bool duplicate)
        => stored.Accepted ? GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, duplicate ? stored.PayloadSha256 : null, stored.ReceivedAtUtc)
            : [new(ExecutionEvidenceFactV1.CurrentSchemaVersion, ExecutionEvidenceFactKind.Received, envelope.EvidenceId,
                    envelope.OriginSequence, envelope.PayloadSha256, stored.ReceivedAtUtc, duplicate),
                new(ExecutionEvidenceFactV1.CurrentSchemaVersion, ExecutionEvidenceFactKind.Rejected, envelope.EvidenceId,
                    envelope.OriginSequence, envelope.PayloadSha256, stored.ReceivedAtUtc, false, stored.RejectionReasonCode, stored.RejectionFieldPath)];

    private static ExecutionEvidenceBindingView View(ExecutionEvidenceBinding b)
        => new(b.Id, b.ObservatoryId, b.RegistrationId, b.LogicalCameraInstallationId, b.InstallationPublicId,
            b.OriginInstallationId, b.AgentInstanceId, b.CreatedAtUtc);
}
