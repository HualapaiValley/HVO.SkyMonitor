using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentCalibrationOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentCalibrationOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var calibration = endpoints.MapGroup("/api/v1/operations/calibration")
            .WithTags("CameraAgent Calibration")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        calibration.MapGet("/status", GetStatusAsync).WithName("GetCameraAgentCalibrationStatus");
        calibration.MapGet("/bundles", GetBundlesAsync).WithName("GetCameraAgentCalibrationBundles");
        calibration.MapGet("/bundles/{bundleId}", GetBundleAsync).WithName("GetCameraAgentCalibrationBundle");
        calibration.MapGet("/acquisitions/{jobId}", GetAcquisitionAsync)
            .WithName("GetCameraAgentCalibrationAcquisition");
        calibration.MapPost("/acquisitions", AcquireAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("AcquireCameraAgentCalibration");
        calibration.MapPost("/acquisitions/{jobId}/cancel", CancelAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("CancelCameraAgentCalibrationAcquisition");
        calibration.MapPost("/bundles/{bundleId}/activate", ActivateAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ActivateCameraAgentCalibrationBundle");
        calibration.MapPost("/bundles/{bundleId}/rollback", RollbackAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("RollbackCameraAgentCalibrationBundle");
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetStatusAsync(
        SqliteCalibrationLibraryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(Project(await store.GetOperationsStatusAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ReadFailure();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetBundlesAsync(
        [FromQuery] int pageSize,
        [FromQuery] string? cursor,
        SqliteCalibrationLibraryStore store,
        CalibrationOperationsTokenService tokens,
        CancellationToken cancellationToken)
    {
        try
        {
            CalibrationLibraryBundleCursor? decoded = null;
            if (cursor is not null && !tokens.TryUnprotect(cursor, out decoded))
            {
                return Invalid("The calibration library cursor is invalid or expired.");
            }
            var page = await store.GetBundlePageAsync(
                pageSize == 0 ? 100 : pageSize, decoded, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                items = page.Items.Select(ProjectSummary),
                nextCursor = page.NextCursor is null ? null : tokens.Protect(page.NextCursor)
            });
        }
        catch (ArgumentException)
        {
            return Invalid("The calibration library page request is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ReadFailure();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetBundleAsync(
        string bundleId,
        SqliteCalibrationLibraryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await store.GetBundleAsync(bundleId, cancellationToken).ConfigureAwait(false);
            return bundle is null ? Results.NotFound() : Results.Ok(ProjectDetail(bundle));
        }
        catch (ArgumentException)
        {
            return Invalid("The calibration bundle identifier is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ReadFailure();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetAcquisitionAsync(
        string jobId,
        SqliteCalibrationLibraryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await store.GetAcquisitionJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            return job is null ? Results.NotFound() : Results.Ok(Project(job));
        }
        catch (ArgumentException)
        {
            return Invalid("The calibration acquisition identifier is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ReadFailure();
        }
    }

    private static Task<IResult> ActivateAsync(
        string bundleId,
        HttpContext context,
        [FromBody] CalibrationActivationRequest request,
        CalibrationLibraryOperationsCoordinator operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            request.ExpectedVersion,
            async (key, actor, version, token) => ProjectState(await operations.ActivateAsync(
                bundleId, key, version, actor, request.Reason, token).ConfigureAwait(false)),
            cancellationToken);

    private static Task<IResult> CancelAsync(
        string jobId,
        HttpContext context,
        [FromBody] CalibrationCancelRequest request,
        VirtualCalibrationAcquisitionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            request.ExpectedVersion,
            async (key, actor, version, token) => Project(await coordinator.CancelAsync(
                jobId, key, version, actor, request.Reason, token).ConfigureAwait(false)),
            cancellationToken);

    private static Task<IResult> RollbackAsync(
        string bundleId,
        HttpContext context,
        [FromBody] CalibrationActivationRequest request,
        CalibrationLibraryOperationsCoordinator operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            request.ExpectedVersion,
            async (key, actor, version, token) => ProjectState(await operations.RollbackAsync(
                bundleId, key, version, actor, request.Reason, token).ConfigureAwait(false)),
            cancellationToken);

    private static Task<IResult> AcquireAsync(
        HttpContext context,
        [FromBody] CalibrationAcquisitionRequest request,
        VirtualCalibrationAcquisitionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            request.ExpectedVersion,
            async (key, actor, version, token) => Project(await coordinator.AcquireAsync(
                new VirtualCalibrationAcquisitionRequestV1(
                    VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
                    key,
                    request.Gain,
                    request.Offset,
                    request.TemperatureC,
                    request.BiasExposure,
                    request.DarkExposure,
                    request.FlatExposure,
                    request.DefectExposure,
                    request.ApplicableLightExposure,
                    request.EffectiveFromUtc,
                    request.EffectiveUntilUtc,
                    request.SourceModel,
                    actor,
                    request.Reason,
                    version), token).ConfigureAwait(false)),
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ExecuteMutationAsync<T>(
        HttpContext context,
        long? expectedVersion,
        Func<string, string, long, CancellationToken, Task<T>> command,
        CancellationToken cancellationToken)
    {
        if (expectedVersion is not { } version)
        {
            return Invalid("The expected durable calibration state version is required.");
        }
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (!IsValidIdempotencyKey(key))
        {
            return Invalid("A valid Idempotency-Key header is required.");
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The calibration command is not authorized.");
        }
        try
        {
            return Results.Ok(await command(key, actor, version, cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (CalibrationLibraryStoreConflictException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The calibration command conflicts with durable state.");
        }
        catch (CalibrationLibraryAcquisitionException exception) when (
            exception.ReasonCode == CalibrationLibraryReasonCodes.PublicationConflict)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Calibration publication conflicts with committed evidence.");
        }
        catch (CalibrationLibraryAcquisitionException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Calibration acquisition could not be completed.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid("The calibration command is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Calibration acquisition was interrupted by another operator command.");
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The calibration command could not be completed.");
        }
    }

    internal static bool IsValidIdempotencyKey(string idempotencyKey)
        => !string.IsNullOrWhiteSpace(idempotencyKey) &&
            idempotencyKey.Length <= 128 &&
            idempotencyKey.All(static character => !char.IsControl(character));

    private static object Project(CalibrationLibraryOperationsStatus status) => new
    {
        version = status.State.Version,
        activeBundle = status.State.ActiveBundle is null ? null : ProjectSummary(status.State.ActiveBundle),
        pendingAcquisition = status.PendingAcquisition is null ? null : Project(status.PendingAcquisition),
        status.PublishedBundleCount,
        status.QuarantineCount,
        status.State.LastSelectionReason,
        status.State.LastSelectionUtc,
        status.State.LastReconciliationReason,
        status.State.LastReconciliationUtc,
        status.LastActivation
    };

    internal static object ProjectState(CalibrationLibraryStateSnapshot state) => new
    {
        state.Version,
        activeBundle = state.ActiveBundle is null ? null : ProjectSummary(state.ActiveBundle),
        state.LastSelectionReason,
        state.LastSelectionUtc,
        state.LastReconciliationReason,
        state.LastReconciliationUtc,
        state.UpdatedUtc
    };

    private static object ProjectSummary(CalibrationLibraryBundleSnapshot snapshot) => new
    {
        snapshot.Bundle.BundleId,
        snapshot.BundleIdentitySha256,
        snapshot.Bundle.Source,
        snapshot.CreatedUtc,
        snapshot.UpdatedUtc,
        snapshot.PublicationState,
        snapshot.FailureReason,
        snapshot.Bundle.ProfileIdentitySha256,
        snapshot.Bundle.AcquisitionModelIdentitySha256,
        snapshot.Bundle.Applicability,
        sourceCount = snapshot.Bundle.Artifacts.Count(static artifact => artifact.Role == "source"),
        masterCount = snapshot.Bundle.Artifacts.Count(static artifact => artifact.Role == "master")
    };

    private static object ProjectDetail(CalibrationLibraryBundleSnapshot snapshot) => new
    {
        summary = ProjectSummary(snapshot),
        artifacts = snapshot.Bundle.Artifacts.Select(artifact => new
        {
            artifact.Kind,
            artifact.Role,
            artifact.ArtifactId,
            artifact.PayloadSha256,
            artifact.Exposure,
            artifact.Gain,
            artifact.Offset,
            artifact.TemperatureC,
            artifact.SourceIndex,
            artifact.OrderedSourceArtifactIds,
            artifact.MasterBuildRecipe
        })
    };

    private static object Project(CalibrationAcquisitionJobSnapshot job) => new
    {
        job.Plan.JobId,
        job.State,
        job.Phase,
        job.AttemptCount,
        job.BundleId,
        job.FailureReason,
        job.CreatedUtc,
        job.UpdatedUtc,
        job.CompletedUtc,
        plan = new
        {
            job.Plan.ModuleType,
            job.Plan.AgentId,
            job.Plan.RigId,
            job.Plan.InputLayout,
            job.Plan.OutputLayout,
            job.Plan.Gain,
            job.Plan.Offset,
            job.Plan.TemperatureC,
            job.Plan.BiasExposure,
            job.Plan.DarkExposure,
            job.Plan.FlatExposure,
            job.Plan.DefectExposure,
            job.Plan.ApplicableLightExposure,
            job.Plan.EffectiveFromUtc,
            job.Plan.EffectiveUntilUtc,
            job.Plan.CreatedUtc,
            job.Plan.SourceModelIdentitySha256
        }
    };

    private static IResult Invalid(string title)
        => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private static IResult ReadFailure()
        => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Calibration library data is unavailable.");

    private sealed record CalibrationActivationRequest(long? ExpectedVersion = null, string? Reason = null);

    private sealed record CalibrationCancelRequest(long? ExpectedVersion = null, string? Reason = null);

    private sealed record CalibrationAcquisitionRequest(
        long? ExpectedVersion,
        double Gain,
        double Offset,
        double TemperatureC,
        TimeSpan BiasExposure,
        TimeSpan DarkExposure,
        TimeSpan FlatExposure,
        TimeSpan DefectExposure,
        TimeSpan ApplicableLightExposure,
        DateTimeOffset EffectiveFromUtc,
        DateTimeOffset? EffectiveUntilUtc,
        VirtualCalibrationSourceModelV1 SourceModel,
        string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }
}
