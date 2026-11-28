using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentDeploymentEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var deployment = endpoints.MapGroup("/api/internal/deployment")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        deployment.MapPost("/session", static () => Results.NotFound()).AllowAnonymous();
        deployment.MapGet("/antiforgery", GetAntiforgeryToken);
        deployment.MapPost("/identity", GetOrCreateIdentityAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
        deployment.MapGet("/continuity", GetContinuityAsync);
        deployment.MapGet("/telemetry", GetTelemetry);
        deployment.MapPost("/measurement/reset", ResetMeasurementWindowAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
        return endpoints;
    }

    private static IResult GetTelemetry(
        long captureSequence,
        Guid artifactId,
        CapturePipelineTraceStore traceStore)
    {
        var trace = traceStore.Find(captureSequence, artifactId);
        return trace is null
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(trace);
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new DeploymentAntiforgeryResponse(
            tokens.FormFieldName,
            tokens.HeaderName ?? "RequestVerificationToken",
            tokens.RequestToken));
    }

    private static async Task<IResult> ResetMeasurementWindowAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        CaptureTelemetrySink captureTelemetry,
        CaptureRuntimeState captureRuntime)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false)) return Results.BadRequest();
        captureTelemetry.Reset();
        captureRuntime.ResetTimings();
        return Results.Ok(new { Status = "reset" });
    }

    private static async Task<IResult> GetOrCreateIdentityAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IDeviceIdentityStore identityStore,
        IOptions<CameraAgentHostOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false)) return Results.BadRequest();
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new DeploymentIdentityResponse(identity.DeviceId, identity.CreatedUtc, options.Value.AgentId));
    }

    private static async Task<IResult> GetContinuityAsync(
        IDeviceIdentityStore identityStore,
        IOptions<CameraAgentHostOptions> options,
        DeploymentContinuityReader continuityReader,
        RawIngressState rawIngressState,
        ICameraAgentConfigurationLoader configurationLoader,
        long? fromCaptureSequence,
        long? toCaptureSequence,
        CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var raw = rawIngressState.Snapshot;
        DurableDeploymentContinuity durable;
        try
        {
            durable = await continuityReader.ReadAsync(
                options.Value.RawIngressRoot, fromCaptureSequence, toCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest();
        }
        var configuration = await configurationLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var configurationPath = Path.GetFullPath(options.Value.ConfigFilePath);
        await using var configurationStream = File.OpenRead(configurationPath);
        using var configurationDocument = await JsonDocument.ParseAsync(configurationStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var configurationSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(configurationDocument.RootElement);
        return Results.Ok(new DeploymentContinuityResponse(
            identity.DeviceId,
            options.Value.AgentId,
            raw.PendingCount,
            raw.QuarantineCount,
            configuration.Rig.ProfileVersion,
            CameraRigProfileIdentity.ComputeSha256(configuration.Rig),
            configurationSha256,
            durable));
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private sealed record DeploymentAntiforgeryResponse(string FormFieldName, string HeaderName, string? RequestToken);
    private sealed record DeploymentIdentityResponse(
        string DeviceId, DateTimeOffset CreatedUtc, string? ConfiguredAgentId);
    private sealed record DeploymentContinuityResponse(
        string DeviceId, string? ConfiguredAgentId,
        long RawPendingCount, long RawQuarantineCount,
        string ExpectedRigProfileVersion, string ExpectedRigProfileHash, string ActiveConfigurationSha256,
        DurableDeploymentContinuity Durable);

}
