using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
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
        deployment.MapPost("/bootstrap", BootstrapAsync)
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
        FleetRuntimeState fleetRuntime)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false)) return Results.BadRequest();
        captureTelemetry.Reset();
        fleetRuntime.ResetTimings();
        return Results.Ok(new { Status = "reset" });
    }

    private static async Task<IResult> GetOrCreateIdentityAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IDeviceIdentityStore identityStore,
        IDeviceSecretStore secretStore,
        IOptions<CameraAgentHostOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false)) return Results.BadRequest();
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToIdentityResponse(identity, secrets, options.Value.AgentId));
    }

    private static async Task<IResult> BootstrapAsync(
        DeploymentBootstrapRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IDeviceIdentityStore identityStore,
        IDeviceSecretStore secretStore,
        IDeviceBootstrapWorkflow bootstrapWorkflow,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false)) return Results.BadRequest();
        if (string.IsNullOrWhiteSpace(request.Envelope) || request.Envelope.Length > 65536)
        {
            return Results.BadRequest();
        }
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var existing = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Results.Ok(ToBootstrapResponse(identity, existing, alreadyProvisioned: true));
        }
        var secrets = await bootstrapWorkflow.BootstrapAsync(request.Envelope, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToBootstrapResponse(identity, secrets, alreadyProvisioned: false));
    }

    private static async Task<IResult> GetContinuityAsync(
        IDeviceIdentityStore identityStore,
        IDeviceSecretStore secretStore,
        IOptions<CameraAgentHostOptions> options,
        DeploymentContinuityReader continuityReader,
        RawIngressState rawIngressState,
        ArtifactOutboxState artifactOutboxState,
        FleetHeartbeatState fleetHeartbeatState,
        ICameraAgentConfigurationLoader configurationLoader,
        long? fromCaptureSequence,
        long? toCaptureSequence,
        CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var raw = rawIngressState.Snapshot;
        var outbox = artifactOutboxState.Snapshot;
        var fleet = fleetHeartbeatState.Snapshot;
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
            secrets is not null,
            secrets?.DevicePublicId,
            secrets?.ObservatoryId,
            raw.PendingCount,
            raw.QuarantineCount,
            outbox.PendingCount,
            outbox.LeasedCount,
            outbox.RetryCount,
            outbox.QuarantineCount,
            fleet.Outbox?.PendingCount,
            fleet.Outbox?.LeasedCount,
            fleet.Outbox?.RetryCount,
            fleet.Outbox?.QuarantineCount,
            fleet.LastAcknowledgedUtc,
            configuration.Rig.ProfileVersion,
            CameraRigProfileIdentity.ComputeSha256(configuration.Rig),
            configurationSha256,
            durable));
    }

    private static DeploymentIdentityResponse ToIdentityResponse(
        DeviceIdentity identity,
        DeviceSecrets? secrets,
        string? configuredAgentId)
        => new(identity.DeviceId, identity.VerificationCode, identity.CreatedUtc, configuredAgentId,
            secrets is not null, secrets?.DevicePublicId, secrets?.ObservatoryId);

    private static DeploymentBootstrapResponse ToBootstrapResponse(
        DeviceIdentity identity,
        DeviceSecrets secrets,
        bool alreadyProvisioned)
        => new(identity.DeviceId, secrets.DevicePublicId, secrets.ObservatoryId, secrets.FriendlyName,
            secrets.IssuedAtUtc, secrets.ExpiresAtUtc, alreadyProvisioned);

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
    private sealed record DeploymentBootstrapRequest(string Envelope);
    private sealed record DeploymentIdentityResponse(
        string DeviceId, string VerificationCode, DateTimeOffset CreatedUtc, string? ConfiguredAgentId,
        bool IsProvisioned, Guid? DevicePublicId, Guid? ObservatoryId);
    private sealed record DeploymentBootstrapResponse(
        string DeviceId, Guid DevicePublicId, Guid ObservatoryId, string FriendlyName,
        DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, bool AlreadyProvisioned);
    private sealed record DeploymentContinuityResponse(
        string DeviceId, string? ConfiguredAgentId, bool IsProvisioned, Guid? DevicePublicId, Guid? ObservatoryId,
        long RawPendingCount, long RawQuarantineCount, long ArtifactPendingCount, long ArtifactLeasedCount,
        long ArtifactRetryCount, long ArtifactQuarantineCount, long? FleetPendingCount, long? FleetLeasedCount,
        long? FleetRetryCount, long? FleetQuarantineCount, DateTimeOffset? LastFleetAcknowledgedUtc,
        string ExpectedRigProfileVersion, string ExpectedRigProfileHash, string ActiveConfigurationSha256,
        DurableDeploymentContinuity Durable);

}
