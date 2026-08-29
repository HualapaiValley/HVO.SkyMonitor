using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentEnvironmentalOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentEnvironmentalOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/environmental")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Environmental Operations");
        group.MapGet("/sources", GetSourcesAsync).WithName("GetCameraAgentEnvironmentalSources");
        group.MapGet("/attempts", GetAttemptsAsync).WithName("GetCameraAgentEnvironmentalAttempts");
        group.MapGet("/history", GetHistoryAsync).WithName("GetCameraAgentEnvironmentalHistory");
        group.MapGet("/history/{reference}", GetHistoryDetailAsync)
            .WithName("GetCameraAgentEnvironmentalHistoryDetail");
        group.MapGet("/captures/{captureId:guid}/associations", GetAssociationsAsync)
            .WithName("GetCameraAgentEnvironmentalAssociations");
        group.MapPost("/sources/{sourceId}/acquisitions", AcquireAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("AcquireCameraAgentEnvironmentalSource");
        return endpoints;
    }

    private static async Task<IResult> GetSourcesAsync(
        IEnvironmentalAcquisitionStateStore stateStore,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var configured = options.Value.EnvironmentalAcquisition;
        var states = await stateStore.ReadSourceStatesAsync(
            options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false);
        var byId = states.ToDictionary(static state => state.SourceId, StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        return Results.Ok(configured.Sources.OrderBy(static source => source.Id, StringComparer.Ordinal).Select(source =>
        {
            _ = byId.TryGetValue(source.Id, out var state);
            return new SourceResponse(
                source.Id,
                source.Type,
                source.Kind,
                source.Required,
                source.Triggers,
                state?.LastDisposition,
                state?.LastReason,
                state?.LastObservationId,
                state?.LastObservedUtc,
                state?.LastObservedUtc is { } observed ? Math.Max(0, (now - observed).TotalSeconds) : null,
                state?.LastStaleAfterUtc is { } staleAfter ? staleAfter > now ? "Fresh" : "Stale" : "NeverObserved",
                state?.NextPollUtc,
                state?.ConsecutiveFailures ?? 0);
        }).ToArray());
    }

    private static async Task<IResult> GetAttemptsAsync(
        IEnvironmentalAcquisitionStateStore stateStore,
        IOptions<CameraAgentHostOptions> options,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 100)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The attempt limit is invalid.");
        }
        var attempts = await stateStore.ReadAttemptsAsync(
            options.Value.RawIngressRoot, take, cancellationToken).ConfigureAwait(false);
        return Results.Ok(attempts.Select(static attempt => new AttemptResponse(
            attempt.SourceId,
            attempt.Kind,
            attempt.Required,
            attempt.Trigger,
            attempt.Disposition,
            attempt.Reason,
            attempt.ObservationId,
            attempt.CaptureSequence,
            attempt.CaptureId,
            attempt.StartedUtc,
            attempt.CompletedUtc)).ToArray());
    }

    private static async Task<IResult> GetHistoryAsync(
        ILocalEnvironmentalObservationStore store,
        OutboxOperationsTokenService tokens,
        IOptions<CameraAgentHostOptions> options,
        [AsParameters] HistoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query.PageSize is < 1 or > 100 || query.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The history query is invalid.");
        }
        LocalEnvironmentalObservationCursor? cursor = null;
        if (query.Cursor is not null && !tokens.TryReadEnvironmentalHistoryCursor(query.Cursor, query.Kind, out cursor))
        {
            return Results.NotFound();
        }
        var page = await store.ReadLocalPageAsync(
            options.Value.RawIngressRoot, query.Kind, query.PageSize, cursor, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new HistoryPageResponse(
            page.Items.Select(item => Project(item, tokens)).ToArray(),
            page.NextCursor is null ? null : tokens.ProtectEnvironmentalHistoryCursor(page.NextCursor, query.Kind)));
    }

    private static async Task<IResult> GetHistoryDetailAsync(
        string reference,
        ILocalEnvironmentalObservationStore store,
        OutboxOperationsTokenService tokens,
        IOptions<CameraAgentHostOptions> options,
        CancellationToken cancellationToken)
    {
        if (!tokens.TryReadEnvironmentalHistoryReference(reference, out var recordId))
        {
            return Results.NotFound();
        }
        var item = await store.ReadLocalDetailAsync(
            options.Value.RawIngressRoot, recordId, cancellationToken).ConfigureAwait(false);
        return item is null ? Results.NotFound() : Results.Ok(Project(item, tokens));
    }

    private static async Task<IResult> GetAssociationsAsync(
        Guid captureId,
        ILocalEnvironmentalAssociationStore store,
        OutboxOperationsTokenService tokens,
        IOptions<CameraAgentHostOptions> options,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty)
        {
            return Results.NotFound();
        }
        var associations = await store.ReadAssociationsAsync(
            options.Value.RawIngressRoot, captureId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(associations.Select(association => new AssociationResponse(
            association.CaptureId,
            association.CaptureSequence,
            association.Kind,
            association.RigId,
            association.ExposureFromUtc,
            association.ExposureThroughUtc,
            association.Status,
            association.SelectedRecordId is { } selected
                ? tokens.ProtectEnvironmentalHistoryReference(selected)
                : null,
            association.ConflictingRecordIds.Take(100).Select(tokens.ProtectEnvironmentalHistoryReference).ToArray(),
            association.ConflictingRecordIds.Count,
            association.CreatedUtc)).ToArray());
    }

    private static ObservationResponse Project(
        LocalEnvironmentalObservationRecord item,
        OutboxOperationsTokenService tokens)
        => new(
            tokens.ProtectEnvironmentalHistoryReference(item.RecordId),
            item.Fact.ObservationId,
            item.Fact.Source.SourceId,
            item.Fact.Source.Kind,
            item.Fact.Value.Kind,
            item.Fact.Value.Unit,
            item.Fact.Value.NumericValue,
            item.Fact.Value.BooleanValue,
            item.Fact.Value.Quality,
            item.Fact.RigId,
            item.Fact.ObservedAtUtc,
            item.Fact.ValidThroughUtc,
            item.Fact.StaleAfterUtc,
            item.RecordedUtc);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Operations endpoints return fixed errors and never disclose persistence details.")]
    private static async Task<IResult> AcquireAsync(
        string sourceId,
        HttpContext context,
        [FromBody] AcquisitionRequest request,
        EnvironmentalOnDemandAcquisitionService service,
        CancellationToken cancellationToken)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The acquisition command is not authorized.");
        }
        try
        {
            return Results.Ok(await service.AcquireAsync(
                sourceId, key, actor, request.Reason, cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (EnvironmentalOnDemandCommandConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The acquisition command conflicts with durable state.");
        }
        catch (Exception exception) when (exception is EnvironmentalOnDemandCommandBusyException or
            EnvironmentalOnDemandCommandCapacityException)
        {
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "The acquisition command is already running.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The acquisition command is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The acquisition command could not be completed.");
        }
    }

    internal sealed record HistoryQuery(
        EnvironmentalObservationKind? Kind = null,
        int PageSize = 50,
        string? Cursor = null);

    private sealed record SourceResponse(
        string SourceId,
        string Type,
        EnvironmentalObservationKind Kind,
        bool Required,
        IReadOnlyList<EnvironmentalAcquisitionTrigger> Triggers,
        EnvironmentalAcquisitionDisposition? LastDisposition,
        string? LastReason,
        Guid? LastObservationId,
        DateTimeOffset? LastObservedUtc,
        double? LastObservationAgeSeconds,
        string Freshness,
        DateTimeOffset? NextPollUtc,
        int ConsecutiveFailures);

    private sealed record ObservationResponse(
        string Reference,
        Guid ObservationId,
        string SourceId,
        EnvironmentalObservationSourceKind SourceKind,
        EnvironmentalObservationKind Kind,
        EnvironmentalObservationUnit Unit,
        double? NumericValue,
        bool? BooleanValue,
        EnvironmentalObservationQuality Quality,
        string? RigId,
        DateTimeOffset ObservedAtUtc,
        DateTimeOffset ValidThroughUtc,
        DateTimeOffset StaleAfterUtc,
        DateTimeOffset RecordedUtc);

    private sealed record HistoryPageResponse(
        IReadOnlyList<ObservationResponse> Items,
        string? NextCursor);

    private sealed record AttemptResponse(
        string SourceId,
        EnvironmentalObservationKind Kind,
        bool Required,
        EnvironmentalAcquisitionTrigger Trigger,
        EnvironmentalAcquisitionDisposition Disposition,
        string Reason,
        Guid? ObservationId,
        long? CaptureSequence,
        Guid? CaptureId,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc);

    private sealed record AssociationResponse(
        Guid CaptureId,
        long CaptureSequence,
        EnvironmentalObservationKind Kind,
        string? RigId,
        DateTimeOffset ExposureFromUtc,
        DateTimeOffset ExposureThroughUtc,
        LocalEnvironmentalAssociationStatus Status,
        string? SelectedObservationReference,
        IReadOnlyList<string> ConflictingObservationReferences,
        int ConflictCount,
        DateTimeOffset CreatedUtc);

    private sealed record AcquisitionRequest(string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }
}
