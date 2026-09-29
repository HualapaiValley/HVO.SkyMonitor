using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Buffers;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentNamedRigEndpoints
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);
    internal static IEndpointRouteBuilder MapCameraAgentNamedRigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/rig-profiles").WithTags("CameraAgent Rig Profiles");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
                return await next(context).ConfigureAwait(false);
            if (context.HttpContext.Features.Get<IAntiforgeryValidationFeature>()?.IsValid != true)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "The rig profile request is invalid.");
            return await next(context).ConfigureAwait(false);
        });
        group.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapGet("/inventory", GetInventoryAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapGet("/equipment/{revisionId}", GetEquipmentAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapPost("/equipment", SaveEquipmentAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .AddEndpointFilter(async (context, next) =>
            {
                var request = context.HttpContext.Request;
                if (request.ContentLength > 1048576)
                    return Results.Problem(statusCode: 400, title: "Equipment request is too large.");
                using var buffer = new MemoryStream();
                var chunk = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    int count;
                    while ((count = await request.Body.ReadAsync(chunk, context.HttpContext.RequestAborted).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > 1048576)
                            return Results.Problem(statusCode: 400, title: "Equipment request is too large.");
                        await buffer.WriteAsync(chunk.AsMemory(0, count), context.HttpContext.RequestAborted).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(chunk); }
                buffer.Position = 0;
                try
                {
                    context.HttpContext.Items[typeof(EquipmentRequest)] = await JsonSerializer.DeserializeAsync<EquipmentRequest>(
                    buffer, RequestJson, context.HttpContext.RequestAborted).ConfigureAwait(false);
                }
                catch (JsonException) { return Results.Problem(statusCode: 400, title: "Invalid equipment request."); }
                return await next(context).ConfigureAwait(false);
            });
        group.MapPost("/profiles", SaveProfileAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
        group.MapPost("/compose", ComposeAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
        group.MapGet("/preview/{revisionId}", PreviewAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapPost("/stage", StageAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
        group.MapPost("/cancel", CancelAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetAsync(SqliteNamedRigProfileStore store, CancellationToken token)
    {
        try
        {
            var catalog = await store.GetAsync(token).ConfigureAwait(false);
            // Module options may contain provider credentials; the catalog API exposes identity only.
            return Results.Ok(new
            {
                Selection = new
                {
                    catalog.Selection.ActiveRevisionId,
                    catalog.Selection.PendingRevisionId,
                    catalog.Selection.Version,
                    catalog.Selection.PendingScheduleRevisionId
                },
                store.PendingRuntimeFailure,
                store.ActiveRuntimeFailure,
                Revisions = catalog.Revisions.Select(revision => new
                {
                    revision.RevisionId,
                    revision.ProfileId,
                    revision.RevisionNumber,
                    revision.CameraRevisionId,
                    revision.OpticsRevisionId,
                    revision.MountRevisionId,
                    ModuleType = revision.Module.Type,
                    SensorName = revision.Rig.Sensor.Name,
                    revision.SourceScheduleRevisionId
                })
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: 500, title: "Named rig profiles are unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> StageAsync(HttpContext context, [FromBody] NamedRigStageRequest request,
        SqliteNamedRigProfileStore store, CaptureScheduleRuntimeCoordinator runtime, CancellationToken token)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: 403, title: "The rig profile command is not authorized.");
        }
        try
        {
            if (runtime.Snapshot is null)
                return Results.Problem(statusCode: 503, title: "The active rig is not ready for staging.");
            return Results.Ok(await store.StageAsync(request.RevisionId, request.ExpectedVersion,
                context.Request.Headers["Idempotency-Key"].ToString(), actor,
                request.AcknowledgeUnvalidated, token).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "The rig profile command is invalid or lacks unvalidated acknowledgement.");
        }
        catch (KeyNotFoundException)
        {
            return Results.Problem(statusCode: 404, title: "The rig profile revision was not found.");
        }
        catch (CaptureProfileCompatibilityException)
        {
            return Results.Problem(statusCode: 422, title: "The rig profile is not verified compatible with this CameraAgent.");
        }
        catch (CaptureScheduleStoreConflictException)
        {
            return Results.Problem(statusCode: 409, title: "The rig profile command conflicts with durable state.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: 500, title: "The rig profile command could not be completed.");
        }
    }

    private sealed record NamedRigStageRequest(string RevisionId, long ExpectedVersion, bool AcknowledgeUnvalidated);
    private sealed record NamedRigCancelRequest(string RevisionId, long ExpectedVersion);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> CancelAsync(HttpContext context, [FromBody] NamedRigCancelRequest request,
        SqliteNamedRigProfileStore store, CancellationToken token)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
            return Results.Problem(statusCode: 403, title: "The rig profile command is not authorized.");
        try
        {
            return Results.Ok(await store.CancelPendingAsync(request.RevisionId, request.ExpectedVersion,
                context.Request.Headers["Idempotency-Key"].ToString(), actor, token).ConfigureAwait(false));
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "The rig profile command is invalid."); }
        catch (CaptureScheduleStoreConflictException) { return Results.Problem(statusCode: 409, title: "The rig profile command conflicts with durable state."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "The rig profile command could not be completed."); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetInventoryAsync(SqliteNamedRigProfileStore store, CancellationToken token)
    {
        try { return Results.Ok(await store.GetInventoryAsync(token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Named rig inventory is unavailable."); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetEquipmentAsync(string revisionId, SqliteNamedRigProfileStore store,
        CancellationToken token)
    {
        try { return Results.Ok(await store.GetEquipmentAsync(revisionId, token).ConfigureAwait(false)); }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Equipment revision was not found."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Equipment revision is unavailable."); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> SaveEquipmentAsync(HttpContext context,
        SqliteNamedRigProfileStore store, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(CameraAgentCredentialAccess.GetOwnerId(context.User)))
            return Results.Problem(statusCode: 403, title: "The equipment command is not authorized.");
        if (context.Items[typeof(EquipmentRequest)] is not EquipmentRequest request)
            return Results.Problem(statusCode: 400, title: "Invalid equipment request.");
        try
        {
            return Results.Ok(await store.SaveEquipmentAsync(request.DefinitionId, request.Kind,
                request.DisplayName, request.Definition, token, request.BasisRevisionId,
                request.ExpectedRevisionId).ConfigureAwait(false));
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid equipment definition."); }
        catch (CaptureScheduleStoreConflictException) { return Results.Problem(statusCode: 409, title: "Equipment cannot be changed."); }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        { return Results.Problem(statusCode: 409, title: "Equipment name conflicts with existing inventory."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Equipment could not be saved."); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> SaveProfileAsync(HttpContext context, [FromBody] ProfileRequest request,
        SqliteNamedRigProfileStore store, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(CameraAgentCredentialAccess.GetOwnerId(context.User)))
            return Results.Problem(statusCode: 403, title: "The rig profile command is not authorized.");
        try { return Results.Ok(await store.SaveProfileNameAsync(request.ProfileId, request.DisplayName, token).ConfigureAwait(false)); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid rig profile name."); }
        catch (CaptureScheduleStoreConflictException) { return Results.Problem(statusCode: 409, title: "Rig profile cannot be changed."); }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        { return Results.Problem(statusCode: 409, title: "Rig profile name conflicts with existing inventory."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Rig profile could not be saved."); }
    }

    private sealed record EquipmentRequest(string? DefinitionId, string Kind, string DisplayName, JsonElement Definition,
        string? BasisRevisionId = null, string? ExpectedRevisionId = null);
    private sealed record ProfileRequest(string? ProfileId, string DisplayName);
    private sealed record ComposeRequest(string ProfileId, string CameraRevisionId, string OpticsRevisionId, string MountRevisionId);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ComposeAsync(HttpContext context, [FromBody] ComposeRequest request,
        SqliteNamedRigProfileStore store, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(CameraAgentCredentialAccess.GetOwnerId(context.User)))
            return Results.Problem(statusCode: 403, title: "Rig composition is not authorized.");
        try
        {
            var revision = await store.ComposeAsync(request.ProfileId, request.CameraRevisionId,
                request.OpticsRevisionId, request.MountRevisionId, token).ConfigureAwait(false);
            return Results.Ok(new
            {
                revision.RevisionId,
                revision.ProfileId,
                revision.RevisionNumber,
                revision.CameraRevisionId,
                revision.OpticsRevisionId,
                revision.MountRevisionId
            });
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Rig composition is invalid."); }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Equipment revision was not found."); }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        { return Results.Problem(statusCode: 409, title: "Rig composition conflicts with inventory."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Rig composition could not be saved."); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> PreviewAsync(string revisionId, SqliteNamedRigProfileStore store, CancellationToken token)
    {
        try { return Results.Ok(await store.PreviewAsync(revisionId, token).ConfigureAwait(false)); }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Rig revision was not found."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 500, title: "Rig preview is unavailable."); }
    }

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static readonly RequiredAntiforgeryMetadata Instance = new();
        public bool RequiresValidation => true;
    }
}
