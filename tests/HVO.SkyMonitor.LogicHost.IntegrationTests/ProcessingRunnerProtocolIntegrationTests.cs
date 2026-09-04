using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// End-to-end <c>processing-runner-v1</c> coverage against the real host: registration, eligibility, claim, job-scoped
/// input reads, completion through the shared publication path, stale/duplicate rejection, cancellation, lease
/// expiry, staleness, and in-process equivalence.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessingRunnerProtocolIntegrationTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];

    [TestMethod]
    public async Task Runner_ClaimsFetchesExecutesAndCompletesARunnerPlacedJob()
    {
        using var factory = CreateFactory();
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);
        var sourceId = await SeedPreviewJobAsync("runner-e2e").ConfigureAwait(false);
        using var http = factory.CreateClient();
        using var client = CreateRunnerClient(http, "runner-e2e-01");

        var registration = await client.RegisterAsync(
            CreateRegistration("runner-e2e-01"), CancellationToken.None).ConfigureAwait(false);
        registration.EligibleRecipes.Should().Equal(BuiltInProcessingRecipes.EncodedPreview);
        registration.Status.Should().Be(ProcessingRunnerRegistrationStatus.Active);

        var claim = await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false);
        claim.Should().NotBeNull();
        claim!.RecipeName.Should().Be(BuiltInProcessingRecipes.EncodedPreview);
        claim.JobClass.Should().Be(ProcessingRunnerJobClass.CentralRecipe);
        claim.Inputs.Should().ContainSingle();
        claim.Inputs[0].PayloadLength.Should().Be(SourcePayload.Length);
        claim.Inputs[0].Layout.Should().NotBeNull();

        var execution = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim, ProcessingRunnerProtocol.MaximumTransferBytes,
            TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        execution.Failure.Should().BeNull();
        execution.Outcome!.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        var (request, payloads) = ProcessingRunnerProjection.ProjectOutcome(
            claim.LeaseToken, execution.Outcome, execution.InputBytes, execution.Duration);
        var completion = await client.CompleteAsync(claim.JobId, request, payloads, CancellationToken.None)
            .ConfigureAwait(false);
        completion.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        completion.ArtifactIds.Should().ContainSingle();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.ResultArtifact)
            .SingleAsync(item => item.Id == claim.JobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
        var attempt = await db.CentralDerivativeJobAttempts.AsNoTracking()
            .SingleAsync(item => item.CentralDerivativeJobId == claim.JobId && item.AttemptNumber == claim.AttemptCount)
            .ConfigureAwait(false);
        attempt.WorkerId.Should().Be("runner-e2e-01");
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        attempt.OutputBytes.Should().BeGreaterThan(0);
        job.ResultArtifact!.ChecksumSha256.Should().Be(execution.Outcome.Products[0].ChecksumSha256);
        job.ResultArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        job.SourceCentralArtifactId.Should().Be(sourceId);
        var runner = await db.CentralProcessingRunners.AsNoTracking()
            .SingleAsync(item => item.RunnerId == "runner-e2e-01").ConfigureAwait(false);
        runner.Status.Should().Be(CentralProcessingRunnerStatus.Active);
        runner.ClientSubject.Should().NotBeNullOrWhiteSpace();

        var duplicate = await client.Invoking(item => item.CompleteAsync(claim.JobId, request, payloads, CancellationToken.None))
            .Should().ThrowAsync<ProcessingRunnerClientException>().ConfigureAwait(false);
        duplicate.Which.IsLeaseStale.Should().BeTrue();
        var heartbeat = await client.HeartbeatAsync(
            new ProcessingRunnerHeartbeatRequest(ProcessingRunnerWarmState.Warm, 1, [claim.JobId]), CancellationToken.None)
            .ConfigureAwait(false);
        heartbeat.StaleJobIds.Should().Equal(claim.JobId);
        heartbeat.CancelRequestedJobIds.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Runner_OutputIsEquivalentToInProcessExecutionForTheSameFrozenPlan()
    {
        using var factory = CreateFactory();
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);
        await SeedPreviewJobAsync("runner-equivalence-a").ConfigureAwait(false);
        await SeedPreviewJobAsync("runner-equivalence-b").ConfigureAwait(false);

        CentralArtifact inProcessResult;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ICentralDerivativeRunnerLeaseService>();
            var lease = await leases.ClaimNextAsync(
                "in-process-worker", TimeSpan.FromMinutes(1),
                CentralDerivativeClaimScope.Only([BuiltInProcessingRecipes.EncodedPreview]), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease!, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            inProcessResult = await db.CentralArtifacts.AsNoTracking().Include(item => item.Recipe)
                .SingleAsync(item => item.ArtifactId == result.ArtifactId).ConfigureAwait(false);
        }

        using var http = factory.CreateClient();
        using var client = CreateRunnerClient(http, "runner-equivalence-01");
        await client.RegisterAsync(CreateRegistration("runner-equivalence-01"), CancellationToken.None).ConfigureAwait(false);
        var claim = await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false);
        claim.Should().NotBeNull();
        var execution = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim!, ProcessingRunnerProtocol.MaximumTransferBytes,
            TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        var (request, payloads) = ProcessingRunnerProjection.ProjectOutcome(
            claim!.LeaseToken, execution.Outcome!, execution.InputBytes, execution.Duration);
        var completion = await client.CompleteAsync(claim.JobId, request, payloads, CancellationToken.None).ConfigureAwait(false);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runnerResult = await verifyDb.CentralArtifacts.AsNoTracking().Include(item => item.Recipe)
            .SingleAsync(item => item.ArtifactId == completion.ArtifactIds[0]).ConfigureAwait(false);
        runnerResult.ChecksumSha256.Should().Be(inProcessResult.ChecksumSha256);
        runnerResult.ByteLength.Should().Be(inProcessResult.ByteLength);
        runnerResult.MediaType.Should().Be(inProcessResult.MediaType);
        runnerResult.Role.Should().Be(inProcessResult.Role);
        runnerResult.Variant.Should().Be(inProcessResult.Variant);
        runnerResult.Recipe!.OptionsSha256.Should().Be(inProcessResult.Recipe!.OptionsSha256);
        runnerResult.Recipe.ImplementationVersion.Should().Be(inProcessResult.Recipe.ImplementationVersion);
    }

    [TestMethod]
    public async Task Runner_CannotClaimLiveOrReplayClassesOrInProcessPlacedRecipes()
    {
        using var factory = CreateFactory();
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);
        await SeedPreviewJobAsync("runner-eligibility").ConfigureAwait(false);
        using var http = factory.CreateClient();
        using var client = CreateRunnerClient(http, "runner-eligibility-01");
        await client.RegisterAsync(CreateRegistration("runner-eligibility-01"), CancellationToken.None).ConfigureAwait(false);

        foreach (var jobClass in new[] { ProcessingRunnerJobClass.CameraAgentLive, ProcessingRunnerJobClass.CameraAgentArchivedReplay })
        {
            var rejected = await client.Invoking(item => item.ClaimAsync(
                    new ProcessingRunnerClaimRequest(jobClass, 0), CancellationToken.None))
                .Should().ThrowAsync<ProcessingRunnerClientException>().ConfigureAwait(false);
            rejected.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            rejected.Which.ReasonCode.Should().Be(ProcessingRunnerReasonCodes.JobClassNotClaimable);
        }

        // The seeded source also scheduled in-process recipes (quality, rolling mean); only the preview is claimable.
        var claim = await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false);
        claim!.RecipeName.Should().Be(BuiltInProcessingRecipes.EncodedPreview);
        (await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false)).Should().BeNull();

        // The in-process claim never returns a runner-placed recipe.
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        while (await jobs.ClaimNextAsync("in-process-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                   .ConfigureAwait(false) is { } lease)
        {
            lease.RecipeName.Should().NotBe(BuiltInProcessingRecipes.EncodedPreview);
        }
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == claim.JobId).ConfigureAwait(false))
            .LeaseOwner.Should().Be("runner-eligibility-01");
    }

    [TestMethod]
    public async Task Runner_InputReadsAreScopedToTheRunnersOwnLease()
    {
        using var factory = CreateFactory();
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);
        await SeedPreviewJobAsync("runner-input-scope").ConfigureAwait(false);
        using var http = factory.CreateClient();
        using var client = CreateRunnerClient(http, "runner-scope-01");
        await client.RegisterAsync(CreateRegistration("runner-scope-01"), CancellationToken.None).ConfigureAwait(false);
        var claim = (await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false))!;
        var input = claim.Inputs[0];

        (await client.DownloadInputAsync(claim, input, CancellationToken.None).ConfigureAwait(false))
            .Should().Equal(SourcePayload);

        var runnerToken = await HttpHelpers.GetClientCredentialsTokenAsync(
            http, "/connect/token", TestClients.SystemProcessingRunner.ClientId,
            TestClients.SystemProcessingRunner.ClientSecret, string.Join(' ', TestClients.SystemProcessingRunner.Scopes))
            .ConfigureAwait(false);
        using var token = new HttpRequestMessage(HttpMethod.Get, new Uri(input.ContentPath, UriKind.Relative));
        token.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerToken.AccessToken);
        token.Headers.Add(ArtifactRetrievalController.JobIdHeader, claim.JobId.ToString("D"));
        token.Headers.Add(ArtifactRetrievalController.LeaseTokenHeader, Guid.NewGuid().ToString("D"));
        token.Headers.Add(ArtifactRetrievalController.RunnerIdHeader, "runner-scope-01");
        using var wrongLease = await http.SendAsync(token).ConfigureAwait(false);
        wrongLease.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, new Uri(input.ContentPath, UriKind.Relative));
        foreign.Headers.Authorization = token.Headers.Authorization;
        foreign.Headers.Add(ArtifactRetrievalController.JobIdHeader, claim.JobId.ToString("D"));
        foreign.Headers.Add(ArtifactRetrievalController.LeaseTokenHeader, claim.LeaseToken.ToString("D"));
        foreign.Headers.Add(ArtifactRetrievalController.RunnerIdHeader, "runner-not-registered");
        using var foreignResponse = await http.SendAsync(foreign).ConfigureAwait(false);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A non-runner system credential cannot use the runner surface at all.
        using var internalClient = factory.CreateClient();
        var internalToken = await HttpHelpers.GetClientCredentialsTokenAsync(
            internalClient, "/connect/token", TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret, string.Join(' ', TestClients.SystemInternal.Scopes)).ConfigureAwait(false);
        internalClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", internalToken.AccessToken);
        using var forbidden = await internalClient.PostAsync(
            new Uri($"/{ProcessingRunnerProtocol.RoutePrefix}/runner-scope-01/claims", UriKind.Relative),
            new StringContent("{\"jobClass\":\"CentralRecipe\",\"slotOrdinal\":0}", System.Text.Encoding.UTF8, "application/json"))
            .ConfigureAwait(false);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Runner_CancellationExpiryAndStalenessRecoverSafely()
    {
        using var factory = CreateFactory();
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);
        await SeedPreviewJobAsync("runner-recovery").ConfigureAwait(false);
        using var http = factory.CreateClient();
        using var client = CreateRunnerClient(http, "runner-recovery-01");
        await client.RegisterAsync(CreateRegistration("runner-recovery-01"), CancellationToken.None).ConfigureAwait(false);
        var first = (await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false))!;

        // Cancellation reaches the runner through heartbeat and renewal.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralDerivativeJobs.Where(job => job.Id == first.JobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.CancellationRequestedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(job => job.CancellationRequestedBy, "test"))
                .ConfigureAwait(false);
        }
        var heartbeat = await client.HeartbeatAsync(
            new ProcessingRunnerHeartbeatRequest(ProcessingRunnerWarmState.Warm, 0, [first.JobId]), CancellationToken.None)
            .ConfigureAwait(false);
        heartbeat.CancelRequestedJobIds.Should().Equal(first.JobId);
        var canceled = await client.Invoking(item => item.RenewAsync(first.JobId, first.LeaseToken, CancellationToken.None))
            .Should().ThrowAsync<ProcessingRunnerClientException>().ConfigureAwait(false);
        canceled.Which.IsLeaseCanceled.Should().BeTrue();

        // Lease expiry lets the job be reclaimed; the expired lease can no longer complete or fail it.
        await SeedPreviewJobAsync("runner-recovery-expiry").ConfigureAwait(false);
        var second = (await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false))!;
        second.JobId.Should().NotBe(first.JobId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Age both the job lease and its attempt record the way real expiry would.
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.CentralDerivativeJobs.Where(job => job.Id == second.JobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, expired))
                .ConfigureAwait(false);
            await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == second.JobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired))
                .ConfigureAwait(false);
        }
        var reclaimed = await client.ClaimAsync(
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
            .ConfigureAwait(false);
        if (reclaimed is null)
        {
            await using var diagnosticScope = factory.Services.CreateAsyncScope();
            var diagnosticDb = diagnosticScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await diagnosticDb.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Id == second.JobId)
                .Select(job => new { job.Status, job.LeaseOwner, job.LeaseExpiresAtUtc, job.AttemptCount, job.MaxAttempts, job.StateReasonCode, job.LastError, job.AvailableAtUtc })
                .SingleAsync().ConfigureAwait(false);
            Assert.Fail($"Expired lease was not reclaimed: {row}");
        }
        reclaimed!.JobId.Should().Be(second.JobId);
        reclaimed.AttemptCount.Should().Be(second.AttemptCount + 1);
        reclaimed.LeaseToken.Should().NotBe(second.LeaseToken);
        var stale = await client.Invoking(item => item.FailAsync(
                second.JobId, new ProcessingRunnerFailureRequest(second.LeaseToken, "runner.test", true), CancellationToken.None))
            .Should().ThrowAsync<ProcessingRunnerClientException>().ConfigureAwait(false);
        stale.Which.IsLeaseStale.Should().BeTrue();
        await client.FailAsync(
            reclaimed.JobId, new ProcessingRunnerFailureRequest(reclaimed.LeaseToken, "object.missing", true, null, reclaimed.Inputs[0].ArtifactId),
            CancellationToken.None).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.SourceArtifact)
                .SingleAsync(item => item.Id == reclaimed.JobId).ConfigureAwait(false);
            job.Status.Should().NotBe(CentralDerivativeJobStatus.Leased);
            job.SourceArtifact!.StateReasonCode.Should().Be("object.missing");
            job.SourceArtifact.ObjectState.Should().NotBe(CentralArtifactObjectState.Available);

            // Heartbeat loss marks the runner stale; the next heartbeat recovers it and health reports the transition.
            var silentSince = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.CentralProcessingRunners.Where(runner => runner.RunnerId == "runner-recovery-01")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(runner => runner.RegisteredAtUtc, silentSince)
                    .SetProperty(runner => runner.LastHeartbeatAtUtc, silentSince))
                .ConfigureAwait(false);
            var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>();
            var counts = await registry.RefreshStatusesAsync(CancellationToken.None).ConfigureAwait(false);
            counts[CentralProcessingRunnerStatus.Stale].Should().BeGreaterThanOrEqualTo(1);
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == "runner-recovery-01")
                .ConfigureAwait(false)).Status.Should().Be(CentralProcessingRunnerStatus.Stale);
            await registry.Invoking(item => item.ResolveOwnedAsync("someone-else", "runner-recovery-01", CancellationToken.None))
                .Should().ThrowAsync<CentralProcessingRunnerRejectedException>()
                .Where(exception => exception.ReasonCode == ProcessingRunnerReasonCodes.RegistrationNotOwned)
                .ConfigureAwait(false);
        }
        var recovered = await client.HeartbeatAsync(
            new ProcessingRunnerHeartbeatRequest(ProcessingRunnerWarmState.Warm, 1, []), CancellationToken.None)
            .ConfigureAwait(false);
        recovered.Status.Should().Be(ProcessingRunnerRegistrationStatus.Active);
        await client.RetireAsync(CancellationToken.None).ConfigureAwait(false);
        var retired = await client.Invoking(item => item.ClaimAsync(
                new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None))
            .Should().ThrowAsync<ProcessingRunnerClientException>().ConfigureAwait(false);
        retired.Which.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [TestMethod]
    public async Task Health_DegradesWhenRecipesArePlacedOnRunnersButNoneIsActive()
    {
        using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralProcessingRunners.Where(runner => runner.Status == CentralProcessingRunnerStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
        var check = new CentralProcessingRunnerHealthCheck(
            db,
            scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>(),
            scope.ServiceProvider.GetRequiredService<IOptions<CentralProcessingRunnerOptions>>(),
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["runnerPlacedRecipes"].Should().Be(1);
        result.Data["activeRunners"].Should().Be(0);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory()
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
            builder.UseSetting("ProcessingRunners:HeartbeatInterval", "00:00:01");
            builder.UseSetting("ProcessingRunners:StaleAfter", "00:00:30");
        });

    private static ProcessingRunnerClient CreateRunnerClient(HttpClient http, string runnerId)
        => new(http, new ProcessingRunnerClientOptions
        {
            RunnerId = runnerId,
            ClientId = TestClients.SystemProcessingRunner.ClientId,
            ClientSecret = TestClients.SystemProcessingRunner.ClientSecret
        });

    private static ProcessingRunnerRegistrationRequest CreateRegistration(string runnerId)
        => new(
            runnerId,
            $"Integration {runnerId}",
            ProcessingRunnerCapabilities.CreateForCurrentProcess(
                1, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, ["site:integration"],
                ProcessingRunnerWarmupStages.NotRun() with
                {
                    RuntimeJit = new("runtime-jit", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(1), "test"),
                    NativeLibraries = new("native-libraries", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(1), "test")
                }),
            Environment.ProcessId,
            ProcessingRunnerProcessInfo.GetProcessStartedUtc());

    private static async Task<Guid> SeedPreviewJobAsync(string scenario)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
            $"{scenario}-{suffix}",
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            SourcePayload,
            $"{scenario}-profile").ConfigureAwait(false);
    }

    private static async Task DisableClaimableJobsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        await db.CentralProcessingRunners.Where(runner => runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
    }
}
