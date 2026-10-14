using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RetainedCuratedSceneExecutesAnActualCentralAnnotationJobWithFrozenLineage(bool graph, bool remote)
    {
        var data = await CreateSceneIngestCaseAsync(graph, graph ? FrameArtifactRole.Calibrated : FrameArtifactRole.Preview,
            curated: true).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Parse(data.SceneBytes).Scene!;
        scene.SchemaVersion.Should().Be(ProjectedSceneV1.DeepSkySchemaVersion);
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.Annotation}", remote ? "Runner" : "InProcess");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var response = await PostAsync(client, data.Raw, data.Pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var response = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var response = await PostAsync(client, data.Derivative, data.Pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var captureId = data.Raw.Descriptor.Capture.CaptureId;
        var job = await db.CentralDerivativeJobs.Include(item => item.InputRequirements).Include(item => item.Inputs)
            .SingleAsync(item => item.SourceArtifact!.ArtifactId == data.Derivative.Descriptor.Artifact.ArtifactId &&
                item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
        var frozen = CentralProjectedSceneResolver.ReadReference(job)!;
        frozen.PayloadSchemaVersion.Should().Be(ProjectedSceneV1.DeepSkySchemaVersion);
        frozen.CaptureId.Should().Be(captureId);
        frozen.Source.ArtifactIdentitySha256.Should().Be(data.Raw.IdempotencyKey);
        frozen.ContentIdentitySha256.Should().Be(scene.SceneIdentitySha256);
        var sceneInput = job.Inputs.Single(item => item.Requirement!.BindingName == CentralProjectedSceneResolver.BindingName);
        sceneInput.CompatibilitySha256.Should().Be(CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.Deserialize<JsonElement>(sceneInput.CompatibilityJson)));
        // The assembly shares a real database; claim only this fixture's annotation and leave no unrelated waiter.
        await db.CentralDerivativeJobs.Where(item => item.Id != job.Id &&
                (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);

        Guid outputId;
        if (remote)
        {
            var runnerId = $"curated-runner-{Guid.NewGuid():N}";
            using var http = factory.CreateClient();
            using var runner = new ProcessingRunnerClient(http, new ProcessingRunnerClientOptions
            {
                RunnerId = runnerId,
                ClientId = TestClients.SystemProcessingRunner.ClientId,
                ClientSecret = TestClients.SystemProcessingRunner.ClientSecret
            });
            var warmup = ProcessingRunnerWarmupStages.NotRun() with
            {
                RuntimeJit = new("runtime-jit", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(1), "test"),
                NativeLibraries = new("native-libraries", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(1), "test")
            };
            await runner.RegisterAsync(new(runnerId, runnerId, ProcessingRunnerCapabilities.CreateForCurrentProcess(
                1, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, ["site:integration"], warmup),
                Environment.ProcessId, ProcessingRunnerProcessInfo.GetProcessStartedUtc()), CancellationToken.None).ConfigureAwait(false);
            try
            {
                var claim = await runner.ClaimAsync(new(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(claim);
                claim.JobId.Should().Be(job.Id);
                claim.ExpectedRecipeIdentitySha256.Should().Be(job.ExpectedRecipeIdentitySha256);
                var retained = claim.Inputs.Single(item => item.ArtifactId == data.SceneManifest.Descriptor.Artifact.ArtifactId);
                retained.SchemaVersion.Should().Be(scene.SchemaVersion);
                retained.CaptureId.Should().Be(captureId);
                retained.ContentIdentitySha256.Should().Be(scene.SceneIdentitySha256);
                retained.PayloadSha256.Should().Be(frozen.ChecksumSha256);
                claim.Inputs.Single(item => item.ArtifactId == data.Derivative.Descriptor.Artifact.ArtifactId).CaptureId.Should().Be(captureId);
                claim.AuxiliaryInputs!.Single(item => item.Name == CentralProjectedSceneResolver.BindingName)
                    .ArtifactId.Should().Be(retained.ArtifactId);
                var execution = await RunnerJobExecution.ExecuteAsync(runner, new ProcessingRecipeExecutor(), claim,
                    ProcessingRunnerProtocol.MaximumTransferBytes, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
                execution.Failure.Should().BeNull();
                execution.Outcome!.Status.Should().Be(ProcessingOutcomeStatus.Produced);
                execution.Outcome.Products.Single().Algorithms.Should().Contain(item => item.Name == "curated-dso-overlay");
                var (completion, payloads) = ProcessingRunnerProjection.ProjectOutcome(claim.LeaseToken,
                    execution.Outcome, execution.InputBytes, execution.Duration);
                var accepted = await runner.CompleteAsync(job.Id, completion, payloads, CancellationToken.None).ConfigureAwait(false);
                accepted.Status.Should().Be(ProcessingOutcomeStatus.Produced);
                outputId = accepted.ArtifactIds.Single();
            }
            finally
            {
                await db.CentralProcessingRunners.Where(item => item.RunnerId == runnerId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralProcessingRunnerStatus.Retired))
                    .ConfigureAwait(false);
            }
        }
        else
        {
            var lease = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("curated-central-annotation", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            lease.JobId.Should().Be(job.Id);
            lease.ExpectedRecipeIdentitySha256.Should().Be(job.ExpectedRecipeIdentitySha256);
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
            outputId = result.ArtifactId!.Value;
        }

        var output = await db.CentralArtifacts.AsNoTracking().Include(item => item.Sources).Include(item => item.Recipe)
            .SingleAsync(item => item.ArtifactId == outputId).ConfigureAwait(false);
        var evidence = await db.CentralArtifactProcessingEvidence.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == output.Id).ConfigureAwait(false);
        evidence.RecipeIdentitySha256.Should().Be(job.ExpectedRecipeIdentitySha256);
        JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(evidence.AlgorithmsJson)!
            .Should().Contain(item => item.Name == "curated-dso-overlay");
        output.Sources.OrderBy(static item => item.Ordinal).Select(static item => item.SourceArtifactId).Should().Equal(
            data.Derivative.Descriptor.Artifact.ArtifactId, data.SceneManifest.Descriptor.Artifact.ArtifactId);
        var expectedIdentity = ProcessingIdentity.CreateOutputIdentity(output.Role, output.Variant!,
            job.ExpectedRecipeIdentitySha256, output.Sources.OrderBy(static item => item.Ordinal).Select(static item => item.SourceArtifactId).ToArray());
        output.ArtifactId.Should().Be(ProcessingIdentity.CreateArtifactId(expectedIdentity));
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var pixels = await ReadDerivativeAsync(owner, output.DevicePublicId, outputId).ConfigureAwait(false);
        pixels.Should().NotEqual(data.Pixels, "the actual central product must draw the retained curated layer");
        var baseline = await scope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Services.Processing.LogicHostRecipeExecutionAdapter>()
            .ExecuteAsync(data.Derivative.Descriptor, data.Pixels, BuiltInProcessingRecipes.Annotation,
                JsonSerializer.Deserialize<JsonElement>(job.RecipeOptionsJson),
                JsonSerializer.Deserialize<ProcessingInputSelector>(job.InputSelectorJson, SceneSelectorOptions)!, job.TargetVariant,
                CentralProjectedSceneResolver.CreateAnnotation(scene, frozen.SceneId)).ConfigureAwait(false);
        pixels.Should().NotEqual(baseline.Products.Single().Payload.ToArray(), "legacy stellar marks alone do not supply the curated caption/glyph");
        (await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id).ConfigureAwait(false))
            .Status.Should().Be(CentralDerivativeJobStatus.Completed);
        CentralProjectedSceneResolver.ReadReference(await db.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements).Include(item => item.Inputs).SingleAsync(item => item.Id == job.Id).ConfigureAwait(false))
            .Should().Be(frozen, "execution and remote completion retain the authenticated frozen selector");
    }
}
