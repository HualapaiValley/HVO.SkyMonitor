using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentOutboxOperationsEndpoints
{
    internal static IServiceCollection AddCameraAgentOutboxOperations(this IServiceCollection services)
    {
        services.AddSingleton<OutboxOperationsTokenService>();
        return services;
    }

    internal static IEndpointRouteBuilder MapCameraAgentOutboxOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/outboxes")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Outbox Operations");

        group.MapGet("/artifacts", ReadArtifactPageAsync);
        group.MapGet("/artifacts/{reference}", ReadArtifactDetailAsync);
        group.MapGet("/artifacts/{reference}/audit", ReadArtifactAuditAsync);
        MapArtifactMutation(group, "/artifacts/replay", OutboxOperationAction.Replay);
        MapArtifactMutation(group, "/artifacts/abandon", OutboxOperationAction.Abandon);

        group.MapGet("/environmental", ReadEnvironmentalPageAsync);
        group.MapGet("/environmental/{reference}", ReadEnvironmentalDetailAsync);
        group.MapGet("/environmental/{reference}/audit", ReadEnvironmentalAuditAsync);
        MapEnvironmentalMutation(group, "/environmental/replay", OutboxOperationAction.Replay);
        MapEnvironmentalMutation(group, "/environmental/abandon", OutboxOperationAction.Abandon);

        group.MapGet("/execution-evidence", ReadExecutionEvidencePageAsync);
        group.MapGet("/execution-evidence/{reference}", ReadExecutionEvidenceDetailAsync);
        group.MapGet("/execution-evidence/{reference}/audit", ReadExecutionEvidenceAuditAsync);
        MapExecutionEvidenceMutation(group, "/execution-evidence/replay", OutboxOperationAction.Replay);
        MapExecutionEvidenceMutation(group, "/execution-evidence/abandon", OutboxOperationAction.Abandon);

        return endpoints;
    }

    private static void MapArtifactMutation(
        RouteGroupBuilder group,
        string pattern,
        OutboxOperationAction action)
    {
        group.MapPost(pattern, (HttpContext context, [FromBody] OutboxResolutionRequest request,
                [FromServices] IArtifactOutbox outbox, OutboxOperationsTokenService tokens, CancellationToken cancellationToken) =>
                ResolveArtifactCoreAsync(context, action, request, outbox, tokens, cancellationToken))
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
    }

    private static void MapEnvironmentalMutation(
        RouteGroupBuilder group,
        string pattern,
        OutboxOperationAction action)
    {
        group.MapPost(pattern, (HttpContext context, [FromBody] OutboxResolutionRequest request,
                [FromServices] IEnvironmentalObservationOutbox outbox, OutboxOperationsTokenService tokens,
                CancellationToken cancellationToken) =>
                ResolveEnvironmentalCoreAsync(context, action, request, outbox, tokens, cancellationToken))
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
    }

    private static void MapExecutionEvidenceMutation(
        RouteGroupBuilder group,
        string pattern,
        OutboxOperationAction action)
    {
        group.MapPost(pattern, (HttpContext context, [FromBody] OutboxResolutionRequest request,
                [FromServices] IExecutionEvidenceOutbox outbox, OutboxOperationsTokenService tokens,
                CancellationToken cancellationToken) =>
                ResolveExecutionEvidenceCoreAsync(context, action, request, outbox, tokens, cancellationToken))
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
    }

    private static async Task<IResult> ReadExecutionEvidencePageAsync(
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        IExecutionEvidenceOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100)
        {
            return InvalidQuery();
        }
        if (!await outbox.ExistsAsync(options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false))
        {
            // A lane that has never run has no durable store; reading it here must not create one.
            return Results.Ok(new OutboxPage<ExecutionEvidenceOutboxItem>([], null));
        }
        ExecutionEvidenceOutboxOperationsCursor? position = null;
        if (cursor is not null && !tokens.TryReadExecutionEvidenceCursor(cursor, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsPageAsync(
            options.Value.RawIngressRoot, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<ExecutionEvidenceOutboxItem>(
            page.Items.Select(item => ToExecutionEvidenceItem(item, tokens)).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectExecutionEvidenceCursor(page.NextCursor)));
    }

    private static async Task<IResult> ReadExecutionEvidenceDetailAsync(
        string reference,
        IExecutionEvidenceOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!tokens.TryReadExecutionEvidenceReference(reference, out var recordId) ||
            !await outbox.ExistsAsync(options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false))
        {
            return Results.NotFound();
        }
        var detail = await outbox.ReadOperationsDetailAsync(
            options.Value.RawIngressRoot, recordId, cancellationToken).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(ToExecutionEvidenceItem(detail, tokens));
    }

    private static async Task<IResult> ReadExecutionEvidenceAuditAsync(
        string reference,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        IExecutionEvidenceOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100 ||
            !tokens.TryReadExecutionEvidenceReference(reference, out var recordId) ||
            !await outbox.ExistsAsync(options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false))
        {
            return size is < 1 or > 100 ? InvalidQuery() : Results.NotFound();
        }
        var target = recordId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OutboxOperationsAuditCursor? position = null;
        if (cursor is not null && !tokens.TryReadAuditCursor("execution-evidence", cursor, target, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsAuditAsync(
            options.Value.RawIngressRoot, recordId, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<OutboxAuditItem>(
            page.Items.Select(ToAuditItem).ToArray(),
            page.NextCursor is null
                ? null
                : tokens.ProtectAuditCursor("execution-evidence", target, page.NextCursor)));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operational mutations must not disclose persistence failures.")]
    private static async Task<IResult> ResolveExecutionEvidenceCoreAsync(
        HttpContext context,
        OutboxOperationAction action,
        OutboxResolutionRequest request,
        IExecutionEvidenceOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!ValidRequest(context, action, request, out var operationKey))
        {
            return InvalidMutation();
        }
        if (!tokens.TryReadExecutionEvidenceAction(action, request.ActionToken, out var recordId))
        {
            return Results.NotFound();
        }
        var options = context.RequestServices.GetRequiredService<IOptions<CameraAgentHostOptions>>();
        if (!await outbox.ExistsAsync(options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false))
        {
            return Results.NotFound();
        }
        try
        {
            await outbox.ResolveOperationsAsync(
                options.Value.RawIngressRoot, recordId, action, operationKey, "owner", request.ReasonCode,
                cancellationToken).ConfigureAwait(false);
            context.RequestServices.GetRequiredService<ExecutionEvidenceExportWakeup>().Signal();
            return Results.NoContent();
        }
        catch (OutboxOperationCollisionException)
        {
            return MutationConflict();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return MutationConflict();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure();
        }
    }

    private static ExecutionEvidenceOutboxItem ToExecutionEvidenceItem(
        ExecutionEvidenceOutboxOperationsRecord item,
        OutboxOperationsTokenService tokens)
        => new(
            tokens.ProtectExecutionEvidenceReference(item.RecordId),
            item.BodyKind,
            item.OriginSequence,
            item.Status,
            item.AttemptCount,
            item.PayloadBytes,
            item.CreatedUtc,
            item.UpdatedUtc,
            item.NextAttemptUtc,
            item.ReasonCode is null ? null : OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
            new OutboxAllowedActions(
                item.CanReplay
                    ? tokens.ProtectExecutionEvidenceAction(OutboxOperationAction.Replay, item.RecordId)
                    : null,
                item.CanAbandon
                    ? tokens.ProtectExecutionEvidenceAction(OutboxOperationAction.Abandon, item.RecordId)
                    : null));

    private static async Task<IResult> ReadArtifactPageAsync(
        [FromQuery] string storage,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CameraAgentStorageResolver storageResolver,
        IArtifactOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100 || string.IsNullOrWhiteSpace(storage))
        {
            return InvalidQuery();
        }
        var location = await storageResolver.ResolveAliasAsync(storage, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return Results.NotFound();
        }
        ArtifactOutboxOperationsCursor? position = null;
        if (cursor is not null && !tokens.TryReadArtifactCursor(cursor, location.Alias, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsPageAsync(location.Root, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<ArtifactOutboxItem>(
            page.Items.Select(item => ToArtifactItem(location.Alias, item, tokens)).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectArtifactCursor(location.Alias, page.NextCursor)));
    }

    private static async Task<IResult> ReadArtifactDetailAsync(
        string reference,
        CameraAgentStorageResolver storageResolver,
        IArtifactOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!tokens.TryReadArtifactReference(reference, out var alias, out var recordKey))
        {
            return Results.NotFound();
        }
        var location = await storageResolver.ResolveAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return Results.NotFound();
        }
        var detail = await outbox.ReadOperationsDetailAsync(location.Root, recordKey, cancellationToken).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(ToArtifactItem(alias, detail, tokens));
    }

    private static async Task<IResult> ReadArtifactAuditAsync(
        string reference,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CameraAgentStorageResolver storageResolver,
        IArtifactOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100 ||
            !tokens.TryReadArtifactReference(reference, out var alias, out var recordKey))
        {
            return size is < 1 or > 100 ? InvalidQuery() : Results.NotFound();
        }
        var location = await storageResolver.ResolveAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return Results.NotFound();
        }
        OutboxOperationsAuditCursor? position = null;
        if (cursor is not null && !tokens.TryReadAuditCursor("artifact", cursor, recordKey, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsAuditAsync(
            location.Root, recordKey, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<OutboxAuditItem>(
            page.Items.Select(ToAuditItem).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectAuditCursor("artifact", recordKey, page.NextCursor)));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operational mutations must not disclose persistence or evidence failures.")]
    private static async Task<IResult> ResolveArtifactCoreAsync(
        HttpContext context,
        OutboxOperationAction action,
        OutboxResolutionRequest request,
        IArtifactOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!ValidRequest(context, action, request, out var operationKey))
        {
            return InvalidMutation();
        }
        if (!tokens.TryReadArtifactAction(action, request.ActionToken, out var alias, out var recordKey))
        {
            return Results.NotFound();
        }
        try
        {
            var resolver = context.RequestServices.GetRequiredService<CameraAgentStorageResolver>();
            var location = await resolver.ResolveAliasAsync(alias, cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                return Results.NotFound();
            }
            await outbox.ResolveOperationsAsync(
                location.Root, recordKey, action, operationKey, "owner", request.ReasonCode, cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (OutboxOperationCollisionException)
        {
            return MutationConflict();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return MutationConflict();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure();
        }
    }

    private static async Task<IResult> ReadEnvironmentalPageAsync(
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        IEnvironmentalObservationOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100)
        {
            return InvalidQuery();
        }
        EnvironmentalOutboxOperationsCursor? position = null;
        if (cursor is not null && !tokens.TryReadEnvironmentalCursor(cursor, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsPageAsync(
            options.Value.RawIngressRoot, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<EnvironmentalOutboxItem>(
            page.Items.Select(item => ToEnvironmentalItem(item, tokens)).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectEnvironmentalCursor(page.NextCursor)));
    }

    private static async Task<IResult> ReadEnvironmentalDetailAsync(
        string reference,
        IEnvironmentalObservationOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!tokens.TryReadEnvironmentalReference(reference, out var recordId))
        {
            return Results.NotFound();
        }
        var detail = await outbox.ReadOperationsDetailAsync(
            options.Value.RawIngressRoot, recordId, cancellationToken).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(ToEnvironmentalItem(detail, tokens));
    }

    private static async Task<IResult> ReadEnvironmentalAuditAsync(
        string reference,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        IEnvironmentalObservationOutbox outbox,
        IOptions<CameraAgentHostOptions> options,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 100 || !tokens.TryReadEnvironmentalReference(reference, out var recordId))
        {
            return size is < 1 or > 100 ? InvalidQuery() : Results.NotFound();
        }
        var target = recordId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OutboxOperationsAuditCursor? position = null;
        if (cursor is not null && !tokens.TryReadAuditCursor("environmental", cursor, target, out position))
        {
            return Results.NotFound();
        }
        var page = await outbox.ReadOperationsAuditAsync(
            options.Value.RawIngressRoot, recordId, size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new OutboxPage<OutboxAuditItem>(
            page.Items.Select(ToAuditItem).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectAuditCursor("environmental", target, page.NextCursor)));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operational mutations must not disclose persistence failures.")]
    private static async Task<IResult> ResolveEnvironmentalCoreAsync(
        HttpContext context,
        OutboxOperationAction action,
        OutboxResolutionRequest request,
        IEnvironmentalObservationOutbox outbox,
        OutboxOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!ValidRequest(context, action, request, out var operationKey))
        {
            return InvalidMutation();
        }
        if (!tokens.TryReadEnvironmentalAction(action, request.ActionToken, out var recordId))
        {
            return Results.NotFound();
        }
        var options = context.RequestServices.GetRequiredService<IOptions<CameraAgentHostOptions>>();
        try
        {
            await outbox.ResolveOperationsAsync(
                options.Value.RawIngressRoot, recordId, action, operationKey, "owner", request.ReasonCode, cancellationToken)
                .ConfigureAwait(false);
            context.RequestServices.GetRequiredService<EnvironmentalObservationDeliveryWakeup>().Signal();
            return Results.NoContent();
        }
        catch (OutboxOperationCollisionException)
        {
            return MutationConflict();
        }
        catch (InvalidOperationException)
        {
            return MutationConflict();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure();
        }
    }

    private static ArtifactOutboxItem ToArtifactItem(
        string alias,
        ArtifactOutboxOperationsRecord item,
        OutboxOperationsTokenService tokens)
        => new(
            tokens.ProtectArtifactReference(alias, item.RecordKey),
            alias,
            item.Status.ToString(),
            item.AttemptCount,
            item.PayloadBytes,
            item.MediaType,
            item.Role?.ToString(),
            item.CreatedUtc,
            item.UpdatedUtc,
            item.NextAttemptUtc,
            item.ReasonCode is null ? null : OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
            new OutboxAllowedActions(
                item.CanReplay ? tokens.ProtectArtifactAction(OutboxOperationAction.Replay, alias, item.RecordKey) : null,
                item.CanAbandon ? tokens.ProtectArtifactAction(OutboxOperationAction.Abandon, alias, item.RecordKey) : null));

    private static EnvironmentalOutboxItem ToEnvironmentalItem(
        EnvironmentalOutboxOperationsRecord item,
        OutboxOperationsTokenService tokens)
        => new(
            tokens.ProtectEnvironmentalReference(item.RecordId),
            item.Status,
            item.AttemptCount,
            item.PayloadBytes,
            item.CreatedUtc,
            item.UpdatedUtc,
            item.ReasonCode is null ? null : OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
            new OutboxAllowedActions(
                item.CanReplay ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Replay, item.RecordId) : null,
                item.CanAbandon ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Abandon, item.RecordId) : null));

    private static OutboxAuditItem ToAuditItem(OutboxOperationsAuditRecord item)
        => new(item.Action, item.ActorKind, item.ReasonCode, item.OccurredUtc);

    private static bool ValidRequest(
        HttpContext context,
        OutboxOperationAction action,
        OutboxResolutionRequest request,
        out string operationKey)
    {
        operationKey = context.Request.Headers["Idempotency-Key"].ToString();
        return !string.IsNullOrWhiteSpace(request.ActionToken) &&
            OutboxOperationsReasonCodes.IsAllowed(action, request.ReasonCode) &&
            !string.IsNullOrWhiteSpace(operationKey) && operationKey.Length <= 128 &&
            operationKey.All(static character => !char.IsControl(character));
    }

    private static IResult InvalidQuery() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "The outbox query is invalid.");

    private static IResult InvalidMutation() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "The outbox operation is invalid.");

    private static IResult MutationConflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "The outbox operation conflicts with durable state.");

    private static IResult MutationFailure() => Results.Problem(
        statusCode: StatusCodes.Status500InternalServerError,
        title: "The outbox operation could not be completed.");

    private sealed record OutboxResolutionRequest(string ActionToken, string ReasonCode);
    private sealed record OutboxPage<T>(IReadOnlyList<T> Items, string? NextCursor);
    private sealed record OutboxAllowedActions(string? ReplayToken, string? AbandonToken);
    private sealed record ArtifactOutboxItem(
        string Reference,
        string StorageAlias,
        string Status,
        int AttemptCount,
        long? PayloadBytes,
        string? MediaType,
        string? Role,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        DateTimeOffset NextAttemptUtc,
        string? ReasonCode,
        OutboxAllowedActions AllowedActions);
    private sealed record ExecutionEvidenceOutboxItem(
        string Reference,
        string BodyKind,
        long OriginSequence,
        string Status,
        int AttemptCount,
        long PayloadBytes,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        DateTimeOffset NextAttemptUtc,
        string? ReasonCode,
        OutboxAllowedActions AllowedActions);
    private sealed record EnvironmentalOutboxItem(
        string Reference,
        string Status,
        int AttemptCount,
        int PayloadBytes,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        string? ReasonCode,
        OutboxAllowedActions AllowedActions);
    private sealed record OutboxAuditItem(
        string Action,
        string ActorKind,
        string ReasonCode,
        DateTimeOffset OccurredUtc);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
