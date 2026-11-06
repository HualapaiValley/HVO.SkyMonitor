using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MeasuredAssociationRunner_ClaimsDownloadsExecutesAndCompletesBothSceneSchemas(bool resolvedSun)
    {
        var data = await CreateMeasuredAssociationCaseAsync(resolvedSun: resolvedSun).ConfigureAwait(false);
        using var factory = CreateMeasuredAssociationRunnerFactory();
        using var http = factory.CreateClient();
        using var runner = CreateMeasuredAssociationRunnerClient(http);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            var registration = await runner.RegisterAsync(CreateMeasuredAssociationRunnerRegistration(runner), CancellationToken.None)
                .ConfigureAwait(false);
            registration.EligibleRecipes.Should().Equal(BuiltInProcessingRecipes.MeasuredStellarAssociations);
            var claim = await runner.ClaimAsync(new(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
                .ConfigureAwait(false);
            claim.Should().NotBeNull();
            claim!.RecipeName.Should().Be(BuiltInProcessingRecipes.MeasuredStellarAssociations);
            claim.Inputs.Should().ContainSingle().Which.CaptureId.Should().Be(data.Raw.Descriptor.Capture.CaptureId);
            var auxiliary = claim.AuxiliaryInputs.Should().ContainSingle().Which;
            auxiliary.SchemaVersion.Should().Be(resolvedSun
                ? ProjectedSceneV1.ResolvedFootprintSchemaVersion : ProjectedSceneV1.CurrentSchemaVersion);
            // Claim preparation downloads the frozen scene through the object reader and sends its verified bytes
            // as canonical JSON. Runner execution downloads the raw input over its job-scoped HTTP endpoint.
            Encoding.UTF8.GetBytes(auxiliary.CanonicalJson!).Should().Equal(data.SceneBytes);
            auxiliary.ChecksumSha256.Should().Be(ProcessingIdentity.ComputePayloadSha256(data.SceneBytes));
            auxiliary.IdentitySha256.Should().Be(ProjectedSceneJson.Parse(data.SceneBytes).Scene!.SceneIdentitySha256);
            auxiliary.ChecksumSha256.Should().NotBe(auxiliary.IdentitySha256);

            var execution = await RunnerJobExecution.ExecuteAsync(runner, new ProcessingRecipeExecutor(), claim,
                ProcessingRunnerProtocol.MaximumTransferBytes, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
            execution.Failure.Should().BeNull();
            execution.InputBytes.Should().Be(data.Pixels.Length);
            execution.Outcome!.Status.Should().Be(ProcessingOutcomeStatus.Produced, execution.Outcome.ReasonCode);
            var (request, payloads) = ProcessingRunnerProjection.ProjectOutcome(claim.LeaseToken, execution.Outcome,
                execution.InputBytes, execution.Duration);
            var completion = await runner.CompleteAsync(claim.JobId, request, payloads, CancellationToken.None).ConfigureAwait(false);
            completion.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            var artifactId = completion.ArtifactIds.Should().ContainSingle().Which;
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.ResultArtifact)
                .SingleAsync(item => item.Id == claim.JobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            job.ResultArtifact!.ArtifactId.Should().Be(artifactId);
            job.ResultArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(
                TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
            var bytes = await ReadDerivativeAsync(owner, job.ResultArtifact.DevicePublicId, artifactId).ConfigureAwait(false);
            bytes.Should().Equal(execution.Outcome.Products.Single().Payload.ToArray());
            MeasuredStellarAssociationJson.Parse(bytes).Associations!.Associations.Select(static item => item.CatalogId)
                .Should().BeEquivalentTo("alpha", "beta");
        }
        finally
        {
            await RetireMeasuredAssociationRunnerAsync(db, runner).ConfigureAwait(false);
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task MeasuredAssociationRunner_RejectsPayloadChecksumAndContentIdentityIndependently(
        bool resolvedSun, bool tamperPayload)
    {
        var data = await CreateMeasuredAssociationCaseAsync(resolvedSun: resolvedSun).ConfigureAwait(false);
        using var factory = CreateMeasuredAssociationRunnerFactory();
        using var http = factory.CreateClient();
        using var runner = CreateMeasuredAssociationRunnerClient(http);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            await runner.RegisterAsync(CreateMeasuredAssociationRunnerRegistration(runner), CancellationToken.None).ConfigureAwait(false);
            var claim = await runner.ClaimAsync(new(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
                .ConfigureAwait(false);
            claim.Should().NotBeNull();
            var auxiliary = claim!.AuxiliaryInputs.Should().ContainSingle().Which;
            var tampered = tamperPayload
                ? auxiliary with { CanonicalJson = auxiliary.CanonicalJson!.Replace("calibration-v1", "calibration-v2", StringComparison.Ordinal) }
                : auxiliary with { IdentitySha256 = new string('A', 64) };
            if (tamperPayload)
                tampered.CanonicalJson.Should().NotBe(auxiliary.CanonicalJson);
            else
                ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(tampered.CanonicalJson!))
                    .Should().Be(tampered.ChecksumSha256, "the identity control keeps the payload checksum valid");
            var execution = await RunnerJobExecution.ExecuteAsync(runner, new ProcessingRecipeExecutor(),
                claim with { AuxiliaryInputs = [tampered] }, ProcessingRunnerProtocol.MaximumTransferBytes,
                TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
            execution.Outcome.Should().BeNull();
            execution.Failure.Should().NotBeNull();
            execution.Failure!.Retryable.Should().BeFalse();
            execution.Failure.ReasonCode.Should().Be(tamperPayload
                ? ProcessingRunnerReasonCodes.PayloadChecksumMismatch : ProcessingReasonCodes.InvalidProjectedScene);
            await runner.FailAsync(claim.JobId, execution.Failure, CancellationToken.None).ConfigureAwait(false);
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Outputs)
                .SingleAsync(item => item.Id == claim.JobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
            job.LastError.Should().Be(execution.Failure.ReasonCode);
            job.Outputs.Should().OnlyContain(item => item.ResultCentralArtifactId == null);
        }
        finally
        {
            await RetireMeasuredAssociationRunnerAsync(db, runner).ConfigureAwait(false);
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateMeasuredAssociationRunnerFactory()
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.MeasuredStellarAssociations}", "Runner");
        });

    private static ProcessingRunnerClient CreateMeasuredAssociationRunnerClient(HttpClient http)
        => new(http, new ProcessingRunnerClientOptions
        {
            RunnerId = $"measured-runner-{Guid.NewGuid():N}",
            ClientId = TestClients.SystemProcessingRunner.ClientId,
            ClientSecret = TestClients.SystemProcessingRunner.ClientSecret
        });

    private static ProcessingRunnerRegistrationRequest CreateMeasuredAssociationRunnerRegistration(ProcessingRunnerClient runner)
        => new(runner.RunnerId, "Measured association runner",
            ProcessingRunnerCapabilities.CreateForCurrentProcess(1, ProcessingRunnerProtocol.MaximumTransferBytes,
                null, null, null, null), Environment.ProcessId, ProcessingRunnerProcessInfo.GetProcessStartedUtc());

    private static Task<int> RetireMeasuredAssociationRunnerAsync(ApplicationDbContext db, ProcessingRunnerClient runner)
        => db.CentralProcessingRunners.Where(item => item.RunnerId == runner.RunnerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(item => item.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(item => item.UpdatedAtUtc, DateTimeOffset.UtcNow));
}
