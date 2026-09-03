using System.Collections.Immutable;
using System.Data;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralProcessingGraphRevisionView(
    Guid Id,
    string Name,
    string Revision,
    CentralProcessingGraphLifecycle Lifecycle,
    string DefinitionIdentitySha256,
    string PortablePlanIdentitySha256,
    string? EdgePlanIdentitySha256,
    string? CentralPlanIdentitySha256,
    JsonElement Definition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? RetiredAtUtc);

internal sealed record CentralProcessingGraphAssignmentView(
    Guid Id,
    Guid RevisionId,
    CentralProcessingGraphTargetHost TargetHost,
    CentralProcessingGraphAssignmentScope Scope,
    Guid? ObservatoryId,
    Guid? LogicalCameraId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    DateTimeOffset CreatedAtUtc,
    string ReasonCode,
    CentralProcessingGraphRevisionView Revision);

internal sealed record CentralProcessingGraphAssignmentRequest(
    Guid RevisionId,
    CentralProcessingGraphTargetHost TargetHost,
    CentralProcessingGraphAssignmentScope Scope,
    Guid? ObservatoryId,
    Guid? LogicalCameraId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    string ReasonCode);

internal enum CentralProcessingGraphMutationOutcome
{
    Applied,
    Unchanged,
    Invalid,
    Conflict,
    NotFoundOrDenied
}

internal sealed record CentralProcessingGraphMutationResult<T>(
    CentralProcessingGraphMutationOutcome Outcome,
    T? Value = default,
    string? ReasonCode = null);

internal interface IProcessingGraphCatalogService
{
    Task<IReadOnlyList<CentralProcessingGraphRevisionView>> ListRevisionsAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> CreateRevisionAsync(
        ProcessingGraphDefinition definition,
        string actorUserId,
        bool isPlatformEditor,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> PublishRevisionAsync(
        Guid revisionId,
        string actorUserId,
        bool isPlatformEditor,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> RetireRevisionAsync(
        Guid revisionId,
        string actorUserId,
        string reasonCode,
        bool isPlatformEditor,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView>> AssignAsync(
        CentralProcessingGraphAssignmentRequest request,
        string actorUserId,
        bool isPlatformEditor,
        Guid? credentialObservatoryId,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphAssignmentView?> ResolveForUserAsync(
        CentralProcessingGraphTargetHost targetHost,
        Guid observatoryId,
        Guid? logicalCameraId,
        DateTimeOffset effectiveAtUtc,
        string actorUserId,
        bool isPlatformEditor,
        Guid? credentialObservatoryId,
        CancellationToken cancellationToken);
}

internal sealed partial class ProcessingGraphCatalogService(
    ApplicationDbContext dbContext,
    ICentralProcessingGraphNodeRegistry nodeRegistry,
    TimeProvider timeProvider,
    ProcessingGraphCatalogTelemetry telemetry,
    ILogger<ProcessingGraphCatalogService> logger) : IProcessingGraphCatalogService
{
    public async Task<IReadOnlyList<CentralProcessingGraphRevisionView>> ListRevisionsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        return (await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .Take(maximumCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToView)
            .ToArray();
    }

    public async Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> CreateRevisionAsync(
        ProcessingGraphDefinition definition,
        string actorUserId,
        bool isPlatformEditor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (!isPlatformEditor)
        {
            return new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied);
        }
        var started = timeProvider.GetTimestamp();
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity("processing-graph.catalog");
        var portable = ProcessingGraphCompiler.Compile(definition);
        if (!portable.IsValid || ContainsSecretMaterial(definition))
        {
            telemetry.Record("catalog", "invalid", timeProvider.GetElapsedTime(started));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "invalid-definition");
            return new(CentralProcessingGraphMutationOutcome.Invalid, ReasonCode: "invalid-definition");
        }
        var portablePlan = portable.Plan
            ?? throw new InvalidOperationException("A valid processing graph did not produce a portable plan.");
        var canonical = ProcessingGraphJson.SerializeCanonical(definition);
        var existing = await dbContext.CentralProcessingGraphRevisions.SingleOrDefaultAsync(
            item => item.Name == definition.Name && item.Revision == definition.Revision,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var same = string.Equals(existing.DefinitionIdentitySha256,
                portablePlan.DefinitionIdentitySha256, StringComparison.Ordinal);
            telemetry.Record("catalog", same ? "unchanged" : "conflict", timeProvider.GetElapsedTime(started));
            return new(
                same ? CentralProcessingGraphMutationOutcome.Unchanged : CentralProcessingGraphMutationOutcome.Conflict,
                same ? ToView(existing) : null,
                same ? null : "revision-name-conflict");
        }

        var capabilities = nodeRegistry.Capabilities;
        var edge = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.CameraAgent, capabilities));
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, capabilities));
        var centralHandlersValid = central.Plan is not null && nodeRegistry.Validate(central.Plan);
        if ((!edge.IsValid && !central.IsValid) || (central.IsValid && !centralHandlersValid))
        {
            telemetry.Record("catalog", "invalid", timeProvider.GetElapsedTime(started));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "host-incompatible");
            return new(CentralProcessingGraphMutationOutcome.Invalid, ReasonCode: "host-incompatible");
        }
        var entity = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(canonical),
            DefinitionIdentitySha256 = portablePlan.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portablePlan.PlanIdentitySha256,
            EdgePlanIdentitySha256 = edge.IsValid ? edge.Plan!.PlanIdentitySha256 : null,
            CentralPlanIdentitySha256 = central.IsValid && centralHandlersValid
                ? central.Plan!.PlanIdentitySha256
                : null,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            CreatedByUserId = actorUserId
        };
        dbContext.CentralProcessingGraphRevisions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.Record("catalog", "created", timeProvider.GetElapsedTime(started));
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        Log.CatalogMutation(logger, "create", "applied", entity.Id);
        return new(CentralProcessingGraphMutationOutcome.Applied, ToView(entity));
    }

    public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> PublishRevisionAsync(
        Guid revisionId,
        string actorUserId,
        bool isPlatformEditor,
        CancellationToken cancellationToken)
        => TransitionAsync(revisionId, actorUserId, null, publish: true, isPlatformEditor, cancellationToken);

    public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> RetireRevisionAsync(
        Guid revisionId,
        string actorUserId,
        string reasonCode,
        bool isPlatformEditor,
        CancellationToken cancellationToken)
        => TransitionAsync(revisionId, actorUserId, reasonCode, publish: false, isPlatformEditor, cancellationToken);

    public async Task<CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView>> AssignAsync(
        CentralProcessingGraphAssignmentRequest request,
        string actorUserId,
        bool isPlatformEditor,
        Guid? credentialObservatoryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var started = timeProvider.GetTimestamp();
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity("processing-graph.assign");
        activity?.SetTag("processing_graph.target_host", request.TargetHost.ToString());
        activity?.SetTag("processing_graph.scope", request.Scope.ToString());
        var reason = request.ReasonCode?.Trim() ?? string.Empty;
        var scopeValid = request.Scope switch
        {
            CentralProcessingGraphAssignmentScope.GlobalDefault =>
                request.ObservatoryId is null && request.LogicalCameraId is null,
            CentralProcessingGraphAssignmentScope.Observatory =>
                request.ObservatoryId is not null && request.LogicalCameraId is null,
            CentralProcessingGraphAssignmentScope.LogicalCamera =>
                request.ObservatoryId is not null && request.LogicalCameraId is not null,
            _ => false
        };
        if (!Enum.IsDefined(request.TargetHost) || !scopeValid ||
            request.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            request.EffectiveUntilUtc is { } until && until.Offset != TimeSpan.Zero ||
            request.EffectiveUntilUtc <= request.EffectiveFromUtc ||
            reason.Length is < 1 or > 128)
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.Invalid, ReasonCode: "invalid-assignment"));
        }
        if (credentialObservatoryId is { } scoped && request.ObservatoryId != scoped)
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        if (request.Scope == CentralProcessingGraphAssignmentScope.GlobalDefault)
        {
            if (!isPlatformEditor)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
            }
        }
        else if (!isPlatformEditor && !await ObservatoryMembershipAccess.ForManager(dbContext, actorUserId)
                     .AnyAsync(item => item.ObservatoryId == request.ObservatoryId, cancellationToken)
                     .ConfigureAwait(false))
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        if (request.ObservatoryId is { } observatoryId && !await dbContext.Observatories.AsNoTracking()
                .AnyAsync(item => item.Id == observatoryId && item.IsActive, cancellationToken).ConfigureAwait(false))
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        if (request.LogicalCameraId is { } cameraId && !await dbContext.LogicalCameras.AsNoTracking()
                .AnyAsync(item => item.Id == cameraId && item.ObservatoryId == request.ObservatoryId &&
                    item.DeactivatedAtUtc == null, cancellationToken).ConfigureAwait(false))
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (dbContext.Database.IsSqlServer())
        {
            _ = await CentralProcessingGraphAssignmentLock.AcquireRevisionAsync(
                dbContext, request.RevisionId, cancellationToken).ConfigureAwait(false);
            _ = await CentralProcessingGraphAssignmentLock.AcquireScopeAsync(
                dbContext, request.TargetHost, request.Scope, cancellationToken).ConfigureAwait(false);
        }
        var revision = await dbContext.CentralProcessingGraphRevisions.SingleOrDefaultAsync(
            item => item.Id == request.RevisionId, cancellationToken).ConfigureAwait(false);
        if (revision is null || revision.Lifecycle != CentralProcessingGraphLifecycle.Published ||
            request.TargetHost == CentralProcessingGraphTargetHost.Edge && revision.EdgePlanIdentitySha256 is null ||
            request.TargetHost == CentralProcessingGraphTargetHost.Central && revision.CentralPlanIdentitySha256 is null)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return Complete(new(CentralProcessingGraphMutationOutcome.Conflict, ReasonCode: "revision-not-assignable"));
        }

        var duplicate = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .Include(item => item.Revision)
            .SingleOrDefaultAsync(item => item.RevisionId == request.RevisionId &&
                item.TargetHost == request.TargetHost && item.Scope == request.Scope &&
                item.ObservatoryId == request.ObservatoryId && item.LogicalCameraId == request.LogicalCameraId &&
                item.EffectiveFromUtc == request.EffectiveFromUtc &&
                item.EffectiveUntilUtc == request.EffectiveUntilUtc && item.ReasonCode == reason,
                cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return Complete(new(CentralProcessingGraphMutationOutcome.Unchanged, ToView(duplicate)));
        }
        var now = timeProvider.GetUtcNow();
        if (request.EffectiveFromUtc < now)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return Complete(new(CentralProcessingGraphMutationOutcome.Invalid, ReasonCode: "backdated-assignment"));
        }
        var conflictingDecision = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .AnyAsync(item => item.TargetHost == request.TargetHost && item.Scope == request.Scope &&
                item.ObservatoryId == request.ObservatoryId && item.LogicalCameraId == request.LogicalCameraId &&
                item.EffectiveFromUtc == request.EffectiveFromUtc,
                cancellationToken).ConfigureAwait(false);
        if (conflictingDecision)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return Complete(new(CentralProcessingGraphMutationOutcome.Conflict, ReasonCode: "assignment-decision-conflict"));
        }

        var entity = new CentralProcessingGraphAssignment
        {
            RevisionId = revision.Id,
            Revision = revision,
            TargetHost = request.TargetHost,
            Scope = request.Scope,
            ObservatoryId = request.ObservatoryId,
            LogicalCameraId = request.LogicalCameraId,
            EffectiveFromUtc = request.EffectiveFromUtc,
            EffectiveUntilUtc = request.EffectiveUntilUtc,
            CreatedAtUtc = now,
            ActorUserId = actorUserId,
            ReasonCode = reason
        };
        dbContext.CentralProcessingGraphAssignments.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        Log.Assignment(logger, entity.Scope.ToString(), entity.TargetHost.ToString(), entity.Id);
        return Complete(new(CentralProcessingGraphMutationOutcome.Applied, ToView(entity)));

        CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView> Complete(
            CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView> result)
        {
            telemetry.Record("assign", result.Outcome.ToString(), timeProvider.GetElapsedTime(started));
            activity?.SetStatus(result.Outcome is CentralProcessingGraphMutationOutcome.Applied or
                CentralProcessingGraphMutationOutcome.Unchanged
                    ? System.Diagnostics.ActivityStatusCode.Ok
                    : System.Diagnostics.ActivityStatusCode.Error,
                result.ReasonCode);
            return result;
        }
    }

    public async Task<CentralProcessingGraphAssignmentView?> ResolveForUserAsync(
        CentralProcessingGraphTargetHost targetHost,
        Guid observatoryId,
        Guid? logicalCameraId,
        DateTimeOffset effectiveAtUtc,
        string actorUserId,
        bool isPlatformEditor,
        Guid? credentialObservatoryId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (!Enum.IsDefined(targetHost) || effectiveAtUtc.Offset != TimeSpan.Zero ||
            credentialObservatoryId is { } scoped && scoped != observatoryId ||
            !isPlatformEditor && !await ObservatoryMembershipAccess.ForUser(dbContext, actorUserId)
                .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (logicalCameraId is { } cameraId && !await dbContext.LogicalCameras.AsNoTracking()
                .AnyAsync(item => item.Id == cameraId && item.ObservatoryId == observatoryId,
                    cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var assignment = await ResolveAsync(
            targetHost, observatoryId, logicalCameraId, effectiveAtUtc, cancellationToken).ConfigureAwait(false);
        return assignment is null ? null : ToView(assignment);
    }

    internal async Task<CentralProcessingGraphAssignment?> ResolveAsync(
        CentralProcessingGraphTargetHost targetHost,
        Guid observatoryId,
        Guid? logicalCameraId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity("processing-graph.resolve");
        var candidates = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .Include(item => item.Revision)
            .Where(item => item.TargetHost == targetHost &&
                item.EffectiveFromUtc <= effectiveAtUtc &&
                (item.EffectiveUntilUtc == null || effectiveAtUtc < item.EffectiveUntilUtc) &&
                item.Revision!.PublishedAtUtc <= effectiveAtUtc &&
                (item.Scope == CentralProcessingGraphAssignmentScope.GlobalDefault ||
                 item.Scope == CentralProcessingGraphAssignmentScope.Observatory && item.ObservatoryId == observatoryId ||
                 item.Scope == CentralProcessingGraphAssignmentScope.LogicalCamera && item.ObservatoryId == observatoryId &&
                 item.LogicalCameraId == logicalCameraId))
            .OrderByDescending(item => item.Scope == CentralProcessingGraphAssignmentScope.LogicalCamera
                ? 2
                : item.Scope == CentralProcessingGraphAssignmentScope.Observatory ? 1 : 0)
            .ThenByDescending(item => item.EffectiveFromUtc)
            .ThenByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var assignment = candidates.SingleOrDefault();
        telemetry.Record("resolve", assignment is null ? "none" : "resolved", timeProvider.GetElapsedTime(started));
        activity?.SetTag("processing_graph.target_host", targetHost.ToString());
        activity?.SetTag("processing_graph.scope", assignment?.Scope.ToString() ?? "none");
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return assignment;
    }

    private async Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> TransitionAsync(
        Guid revisionId,
        string actorUserId,
        string? reasonCode,
        bool publish,
        bool isPlatformEditor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var started = timeProvider.GetTimestamp();
        var operation = publish ? "publish" : "retire";
        using var activity = ProcessingGraphCatalogTelemetry.ActivitySource.StartActivity(
            string.Concat("processing-graph.", operation));
        activity?.SetTag("processing_graph.revision_id", revisionId);
        var reason = reasonCode?.Trim();
        if (!isPlatformEditor)
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        if (!publish && (reason?.Length is not > 0 or > 128))
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.Invalid, ReasonCode: "invalid-reason"));
        }
        var entity = await dbContext.CentralProcessingGraphRevisions.SingleOrDefaultAsync(
            item => item.Id == revisionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Complete(new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied));
        }
        var now = timeProvider.GetUtcNow();
        if (publish)
        {
            if (entity.Lifecycle == CentralProcessingGraphLifecycle.Retired)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.Conflict, ReasonCode: "revision-retired"));
            }
            if (entity.PublishedAtUtc is not null)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.Unchanged, ToView(entity)));
            }
            if (entity.EdgePlanIdentitySha256 is null && entity.CentralPlanIdentitySha256 is null)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.Conflict, ReasonCode: "revision-not-assignable"));
            }
            entity.PublishedAtUtc = now;
            entity.PublishedByUserId = actorUserId;
        }
        else
        {
            if (entity.Lifecycle == CentralProcessingGraphLifecycle.Draft)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.Conflict, ReasonCode: "revision-not-published"));
            }
            if (entity.RetiredAtUtc is not null)
            {
                return Complete(new(CentralProcessingGraphMutationOutcome.Unchanged, ToView(entity)));
            }
            entity.RetiredAtUtc = now;
            entity.RetiredByUserId = actorUserId;
            entity.RetirementReasonCode = reason;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Log.CatalogMutation(logger, operation, "applied", entity.Id);
        return Complete(new(CentralProcessingGraphMutationOutcome.Applied, ToView(entity)));

        CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView> Complete(
            CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView> result)
        {
            telemetry.Record(operation, result.Outcome.ToString(), timeProvider.GetElapsedTime(started));
            activity?.SetStatus(result.Outcome is CentralProcessingGraphMutationOutcome.Applied or
                CentralProcessingGraphMutationOutcome.Unchanged
                    ? System.Diagnostics.ActivityStatusCode.Ok
                    : System.Diagnostics.ActivityStatusCode.Error,
                result.ReasonCode);
            return result;
        }
    }

    private static bool ContainsSecretMaterial(ProcessingGraphDefinition definition)
    {
        foreach (var node in definition.Nodes)
        {
            if (ContainsSecretProperty(node.EffectiveOptions, null))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsSecretProperty(JsonElement element, string? propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsSensitiveName(property.Name) ||
                    ContainsSecretProperty(property.Value, property.Name))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(item => ContainsSecretProperty(item, propertyName));
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            return ContainsSecretScalar(element.GetString(), propertyName);
        }
        return false;
    }

    internal static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "password" or "pwd" or "secret" or "token" or "apikey" or "privatekey" or
                "devicekey" or "accesskey" or "credential" or "authorization" or "connectionstring" ||
            normalized.EndsWith("password", StringComparison.Ordinal) ||
            normalized.EndsWith("secret", StringComparison.Ordinal) ||
            normalized.EndsWith("token", StringComparison.Ordinal) ||
            normalized.EndsWith("apikey", StringComparison.Ordinal) ||
            normalized.EndsWith("privatekey", StringComparison.Ordinal) ||
            normalized.EndsWith("accesskey", StringComparison.Ordinal) ||
            normalized.EndsWith("credential", StringComparison.Ordinal) ||
            normalized.EndsWith("authorization", StringComparison.Ordinal) ||
            normalized.EndsWith("connectionstring", StringComparison.Ordinal);
    }

    internal static bool ContainsSecretScalar(string? value, string? propertyName)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        if (candidate.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
            candidate.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal) ||
            candidate.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal) ||
            candidate.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal) ||
            candidate.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return true;
        }
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || QueryContainsSecret(uri.Query))
            {
                return true;
            }
            return false;
        }
        if (ConnectionStringContainsSecret(candidate) || LooksLikeJwt(candidate) || HasKnownTokenPrefix(candidate))
        {
            return true;
        }
        return !IsOpaqueValueExemptName(propertyName) && LooksLikeOpaqueSecret(candidate);
    }

    internal static bool QueryContainsSecret(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            try
            {
                key = Uri.UnescapeDataString(key.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return true;
            }
            if (IsSensitiveName(key) || key.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("sig", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("signature", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ConnectionStringContainsSecret(string value)
    {
        var pairs = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pairs.Length < 2 || pairs.Any(static pair => !pair.Contains('=', StringComparison.Ordinal)))
        {
            return false;
        }
        return pairs.Any(pair => IsSensitiveName(pair[..pair.IndexOf('=', StringComparison.Ordinal)]) ||
            pair[..pair.IndexOf('=', StringComparison.Ordinal)].Trim().Equals("uid", StringComparison.OrdinalIgnoreCase) ||
            pair[..pair.IndexOf('=', StringComparison.Ordinal)].Trim().Equals("user id", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool LooksLikeJwt(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts[0].StartsWith("eyJ", StringComparison.Ordinal) &&
            parts.All(static part => part.Length >= 8 && part.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    internal static bool HasKnownTokenPrefix(string value)
    {
        return value.StartsWith("AKIA", StringComparison.Ordinal) ||
            value.StartsWith("ASIA", StringComparison.Ordinal) ||
            value.StartsWith("ghp_", StringComparison.Ordinal) ||
            value.StartsWith("github_pat_", StringComparison.Ordinal) ||
            value.StartsWith("sk_live_", StringComparison.Ordinal);
    }

    internal static bool IsOpaqueValueExemptName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var explicitIdentifier = name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Id", StringComparison.Ordinal) ||
            name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-id", StringComparison.OrdinalIgnoreCase);
        return explicitIdentifier || normalized is "hash" or "checksum" or "digest" or "identity" or "sha256" ||
            normalized.Contains("hash", StringComparison.Ordinal) ||
            normalized.Contains("checksum", StringComparison.Ordinal) ||
            normalized.Contains("digest", StringComparison.Ordinal) ||
            normalized.Contains("identity", StringComparison.Ordinal) ||
            normalized.EndsWith("sha256", StringComparison.Ordinal);
    }

    internal static bool LooksLikeOpaqueSecret(string value)
    {
        if (value.Length < 32 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (value.All(Uri.IsHexDigit))
        {
            return value.Length >= 48 || HasHighHexEntropy(value);
        }
        if (!value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '/' or '='))
        {
            return false;
        }
        var characterClasses = Convert.ToInt32(value.Any(char.IsAsciiLetterLower)) +
            Convert.ToInt32(value.Any(char.IsAsciiLetterUpper)) +
            Convert.ToInt32(value.Any(char.IsDigit)) +
            Convert.ToInt32(value.Any(static character => character is '-' or '_' or '+' or '/' or '='));
        return characterClasses >= 2 && value.Distinct().Take(12).Count() == 12;
    }

    internal static bool HasHighHexEntropy(string value)
    {
        Span<int> frequencies = stackalloc int[16];
        foreach (var character in value)
        {
            var index = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                _ => character - 'A' + 10
            };
            frequencies[index]++;
        }
        var entropy = 0d;
        foreach (var frequency in frequencies)
        {
            if (frequency == 0)
            {
                continue;
            }
            var probability = frequency / (double)value.Length;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy >= 3;
    }

    private static CentralProcessingGraphRevisionView ToView(CentralProcessingGraphRevision entity)
    {
        var parse = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(entity.DefinitionJson));
        if (!parse.IsValid)
        {
            throw new InvalidDataException("The stored processing graph definition is invalid.");
        }
        using var document = JsonDocument.Parse(ProcessingGraphJson.SerializeCanonical(parse.Definition!));
        return new(
            entity.Id,
            entity.Name,
            entity.Revision,
            entity.Lifecycle,
            entity.DefinitionIdentitySha256,
            entity.PortablePlanIdentitySha256,
            entity.EdgePlanIdentitySha256,
            entity.CentralPlanIdentitySha256,
            document.RootElement.Clone(),
            entity.CreatedAtUtc,
            entity.PublishedAtUtc,
            entity.RetiredAtUtc);
    }

    private static CentralProcessingGraphAssignmentView ToView(CentralProcessingGraphAssignment entity)
        => new(
            entity.Id,
            entity.RevisionId,
            entity.TargetHost,
            entity.Scope,
            entity.ObservatoryId,
            entity.LogicalCameraId,
            entity.EffectiveFromUtc,
            entity.EffectiveUntilUtc,
            entity.CreatedAtUtc,
            entity.ReasonCode,
            ToView(entity.Revision ?? throw new InvalidDataException("The processing graph assignment revision is unavailable.")));

    private static partial class Log
    {
        [LoggerMessage(2190, LogLevel.Information,
            "Processing graph catalog mutation {Operation} completed with {Outcome}: RevisionId={RevisionId}")]
        internal static partial void CatalogMutation(ILogger logger, string operation, string outcome, Guid revisionId);

        [LoggerMessage(2191, LogLevel.Information,
            "Processing graph {Scope} assignment for {TargetHost} created: AssignmentId={AssignmentId}")]
        internal static partial void Assignment(ILogger logger, string scope, string targetHost, Guid assignmentId);
    }
}

internal static class CentralProcessingGraphAssignmentLock
{
    internal static Task<int> AcquireRevisionAsync(
        ApplicationDbContext dbContext,
        Guid revisionId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralProcessingGraphRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {revisionId}")
            .SingleOrDefaultAsync(cancellationToken);

    internal static Task<int> AcquireScopeAsync(
        ApplicationDbContext dbContext,
        CentralProcessingGraphTargetHost targetHost,
        CentralProcessingGraphAssignmentScope scope,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT TOP(1) CAST(1 AS int) AS [Value] FROM [CentralProcessingGraphAssignments] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetHost] = {targetHost.ToString()} AND [Scope] = {scope.ToString()} ORDER BY [Id]")
            .SingleOrDefaultAsync(cancellationToken);
}
