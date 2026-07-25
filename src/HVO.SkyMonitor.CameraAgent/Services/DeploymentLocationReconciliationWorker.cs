using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class DeploymentLocationReconciliationState
{
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DeploymentLocationResolutionStatus? Status { get; set; }
    public string Outcome { get; set; } = "not-started";
}

internal sealed partial class DeploymentLocationReconciliationWorker(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IDeploymentLocationStore deploymentLocationStore,
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> options,
    DeploymentLocationReconciliationState state,
    DeploymentLocationTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<DeploymentLocationReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(RetryInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity(
            "deployment-location.reconcile");
        state.LastAttemptUtc = timeProvider.GetUtcNow();
        var sourceKind = options.Value.DeploymentLocation.SourceKind;
        void Record(string outcome, string versionRelation = "unknown")
        {
            state.Outcome = outcome;
            telemetry.Record("reconcile", outcome, Stopwatch.GetElapsedTime(started));
            activity?.SetTag("deployment.outcome", outcome);
            activity?.SetTag("deployment.reason", outcome);
            activity?.SetTag("deployment.source_kind", sourceKind.ToString());
            activity?.SetTag("deployment.version_relation", versionRelation);
            Log.Completed(
                logger,
                outcome,
                sourceKind.ToString(),
                versionRelation);
        }
        try
        {
            var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
            var activeDeployment = deploymentLocationStore.Active;
            if (deploymentLocationStore.Staged is { } staged)
            {
                sourceKind = deploymentLocationStore.ResolveSourceKind(staged);
                state.Status = DeploymentLocationResolutionStatus.Acknowledged;
                var now = timeProvider.GetUtcNow();
                if (staged.EffectiveUntilUtc is { } until && until <= now)
                {
                    Record("staged-expired", "successor");
                    return;
                }
                state.LastSuccessUtc = now;
                Record(staged.EffectiveFromUtc > now ? "restart-scheduled" : "restart-required", "successor");
                return;
            }
            var deployment = deploymentLocationStore.Candidate ?? activeDeployment;
            if (secrets is null || deployment is null)
            {
                Record("not-provisioned");
                return;
            }
            var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            sourceKind = deploymentLocationStore.ResolveSourceKind(deployment);
            var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/device/deployment-location")
            {
                Content = JsonContent.Create(new DeviceDeploymentLocationProposal(
                    identity.DeviceId,
                    secrets.DeviceKey,
                    deployment,
                    sourceKind))
            };
            request.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var responseOutcome = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden ? "credentials-rejected" : "central-unavailable";
                Record(responseOutcome);
                return;
            }
            DeploymentLocationAcknowledgment? acknowledgment;
            try
            {
                acknowledgment = await response.Content.ReadFromJsonAsync<DeploymentLocationAcknowledgment>(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
            {
                Record("invalid-acknowledgment");
                return;
            }
            if (acknowledgment is null || !acknowledgment.Validate().IsValid
                || acknowledgment.SourceKind != sourceKind
                || acknowledgment.Observatory.ObservatoryId != secrets.ObservatoryId
                || !string.Equals(
                    acknowledgment.Deployment.CanonicalSha256,
                    deployment.CanonicalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                Record("invalid-acknowledgment");
                return;
            }
            var restartRequired = acknowledgment.Status == DeploymentLocationResolutionStatus.Acknowledged
                && activeDeployment is not null
                && acknowledgment.Deployment.CanonicalSha256 != activeDeployment.CanonicalSha256;
            if (restartRequired)
            {
                if (acknowledgment.Deployment.EffectiveUntilUtc is { } until
                    && until <= timeProvider.GetUtcNow())
                {
                    Record("acknowledgment-expired", "successor");
                    return;
                }
                if (acknowledgment.Deployment.CanonicalSha256 != deployment.CanonicalSha256)
                {
                    Record("invalid-acknowledgment");
                    return;
                }
                await deploymentLocationStore.StageAsync(acknowledgment.Deployment, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (secrets.DeploymentLocationAcknowledgment != acknowledgment)
            {
                await secretStore.SaveAsync(
                    secrets with { DeploymentLocationAcknowledgment = acknowledgment }, cancellationToken)
                    .ConfigureAwait(false);
            }
            state.Status = acknowledgment.Status;
            state.LastSuccessUtc = timeProvider.GetUtcNow();
            var reconciliationOutcome = restartRequired
                ? acknowledgment.Deployment.EffectiveFromUtc > state.LastSuccessUtc
                    ? "restart-scheduled"
                    : "restart-required"
                : acknowledgment.Status switch
                {
                    DeploymentLocationResolutionStatus.Acknowledged => "acknowledged",
                    DeploymentLocationResolutionStatus.Pending => "pending",
                    DeploymentLocationResolutionStatus.Rejected => "rejected",
                    _ => "invalid-acknowledgment"
                };
            Record(reconciliationOutcome, restartRequired ? "successor" : "exact");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException
            or InvalidOperationException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Record("central-unavailable");
            Log.Unavailable(logger, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7303, LogLevel.Information,
            "Deployment location reconciliation completed: Outcome={Outcome}, SourceKind={SourceKind}, VersionRelation={VersionRelation}")]
        internal static partial void Completed(
            ILogger logger,
            string outcome,
            string sourceKind,
            string versionRelation);

        [LoggerMessage(7304, LogLevel.Warning,
            "Deployment-location reconciliation is temporarily unavailable")]
        internal static partial void Unavailable(ILogger logger, Exception exception);
    }

    private sealed record DeviceDeploymentLocationProposal(
        string DeviceId,
        string DeviceKey,
        DeploymentLocationSnapshot DeploymentLocation,
        DeploymentLocationSourceKind SourceKind);
}
