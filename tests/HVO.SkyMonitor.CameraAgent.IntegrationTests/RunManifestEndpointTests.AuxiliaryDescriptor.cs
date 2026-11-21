using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class RunManifestEndpointTests
{
    [TestMethod]
    public async Task CorruptAuxiliaryDescriptorRefusesOnlyManifestAndRecoversOverAuthenticatedHttp()
    {
        using var fixture = new CameraAgentIntegrationFixture();
        await fixture.InitializeAsync(static services =>
        {
            // Keep real acquisition running. This test completes only the two standard lane claims it owns.
            services.Remove(services.Single(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(AfterApplicationStartedService<CaptureDistributionService>)));
        }).ConfigureAwait(false);
        using var scope = fixture.CreateCameraAgentScope();
        var (executionId, consumerArtifactId, referenceArtifactId) =
            await PrepareAuxiliaryEvidenceAsync(scope.ServiceProvider).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            AuxiliaryRunManifestEvidenceFixture.CreateAsync(fixture.StorageRoot, consumerArtifactId, Guid.Empty)).ConfigureAwait(false);
        StringAssert.Contains(missing.Message, $"Reference artifact {Guid.Empty:D} must be retained", StringComparison.Ordinal);
        var auxiliary = await AuxiliaryRunManifestEvidenceFixture.CreateAsync(
            fixture.StorageRoot, consumerArtifactId, referenceArtifactId).ConfigureAwait(false);
        await using var auxiliaryLifetime = auxiliary.ConfigureAwait(false);
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        var path = $"/api/v1/operations/processing-graphs/executions/{executionId:D}";
        using var before = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
        var expected = await before.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        using (var manifest = JsonDocument.Parse(expected))
            Assert.IsTrue(manifest.RootElement.GetProperty("nodes").EnumerateArray()
                .SelectMany(static node => node.GetProperty("inputs").EnumerateArray())
                .Any(input => input.GetProperty("artifactId").GetGuid() == auxiliary.ReferenceArtifactId &&
                    input.GetProperty("kind").GetString() == "AuxiliaryProcessingOutput"));
        var ordinary = await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        await auxiliary.CorruptAsync().ConfigureAwait(false);
        using var refused = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsByteArrayAsync().ConfigureAwait(false)))
        {
            Assert.AreEqual("The retained run evidence is corrupt and cannot be downloaded.", problem.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(problem.RootElement.TryGetProperty("schemaVersion", out _));
        }
        Assert.AreEqual(ordinary, await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false));
        await auxiliary.RestoreReferenceAsync().ConfigureAwait(false);
        using var recovered = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        CollectionAssert.AreEqual(expected, await recovered.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual(ordinary, await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false));
    }

    private static async Task<(Guid ExecutionId, Guid ConsumerArtifactId, Guid ReferenceArtifactId)>
        PrepareAuxiliaryEvidenceAsync(IServiceProvider services)
    {
        const int historyPageSize = 10;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var operations = services.GetRequiredService<IProcessingGraphOperations>();
        var stage = "waiting for capture configuration";
        try
        {
            var configuration = await services.GetRequiredService<ICameraAgentConfigurationAccessor>()
                .WaitForConfigurationAsync(token).ConfigureAwait(false);
            var store = services.GetRequiredService<ICaptureLaneStore>();
            var handler = services.GetRequiredService<StandardCaptureLaneHandler>();
            var lane = services.GetRequiredService<CaptureLanePolicy>().Definitions.Single(definition => definition.Name == handler.Lane);

            stage = "completing the reference capture";
            var reference = await CompleteNextCaptureAsync().ConfigureAwait(false);
            stage = "completing the consumer capture";
            var consumer = await CompleteNextCaptureAsync().ConfigureAwait(false);
            Assert.AreNotEqual(reference.CaptureId, consumer.CaptureId);

            stage = "waiting for ten newer pending executions";
            while (true)
            {
                var recent = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, historyPageSize, token).ConfigureAwait(false);
                if (recent.Count == historyPageSize && recent.All(run => run.Status == ProcessingGraphExecutionStatus.Pending
                    && run.AcceptedUtc > consumer.AcceptedUtc && run.AcceptedUtc > reference.AcceptedUtc))
                {
                    break;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            stage = "reading the retained preview evidence by execution ID";
            var consumerArtifactId = await ReadPublishedPreviewAsync(consumer.ExecutionId).ConfigureAwait(false);
            var referenceArtifactId = await ReadPublishedPreviewAsync(reference.ExecutionId).ConfigureAwait(false);
            return (consumer.ExecutionId, consumerArtifactId, referenceArtifactId);

            async Task<ProcessingGraphExecutionState> CompleteNextCaptureAsync()
            {
                CaptureLaneLease? lease;
                while ((lease = await store.ClaimAsync(lane, "auxiliary-manifest-test", configuration, token).ConfigureAwait(false)) is null)
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                var execution = lease.Context.Execution;
                Assert.IsNotNull(execution, "The claimed capture must retain its live execution binding.");
                var before = await operations.ReadRunManifestAsync(execution.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(before);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Running, before.Execution.Status);
                Assert.IsFalse(before.Nodes.SelectMany(static node => node.Outputs).Any(),
                    "The live execution must have no derived output before its standard lane claim runs.");

                var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await store.CompleteAsync(lease, token).ConfigureAwait(false);

                var completed = await operations.ReadExecutionAsync(execution.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(completed);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status, completed.FailureReason);
                return completed;
            }

            async Task<Guid> ReadPublishedPreviewAsync(Guid executionId)
            {
                var detail = await operations.ReadRunManifestAsync(executionId, token).ConfigureAwait(false);
                Assert.IsNotNull(detail, $"No retained run manifest for execution {executionId:D}.");
                var previews = detail.Nodes.Where(static node => node.NodeId == "CalibratedPreview")
                    .SelectMany(static node => node.Outputs)
                    .Where(static output => output.Published && output.AvailabilityState == "Available")
                    .ToArray();
                Assert.HasCount(1, previews, $"Execution {executionId:D} must retain a published calibrated preview.");
                return previews[0].ArtifactId;
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"The auxiliary manifest fixture timed out while {stage}.", exception);
        }
    }
}
