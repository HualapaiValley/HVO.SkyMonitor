using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly DeploymentLocationSnapshot SceneCompatibilitySite = DeploymentLocationSnapshot.Create(
        "scene-revision-site", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SceneCompatibilitySuccessorIsProtectedWhileSourceIsSelected(bool named)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-revision");
        var token = CancellationToken.None;
        try
        {
            var configuration = CreateSceneCompatibilityConfiguration(root);
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            string successor;
            string source;
            using (var provider = CreateSceneCompatibilityProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = provider.GetRequiredService<IProcessingGraphOperations>();
                var registry = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                    .EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
                if (named)
                {
                    var revision = await operations.CreateRevisionAsync(
                        "legacy-sky", "1", configuration.Pipeline, "create-legacy", "test", null, token).ConfigureAwait(false);
                    await operations.ValidateRevisionAsync(revision.RevisionId, "validate-legacy", "test", null, token)
                        .ConfigureAwait(false);
                    registry = await operations.ActivateRevisionAsync(
                        revision.RevisionId, registry.StateVersion, "activate-legacy", "test", null, token).ConfigureAwait(false);
                }
                source = registry.ActiveRevisionId;
                successor = (await CaptureWithSceneCompatibilityAsync(provider, configuration, now).ConfigureAwait(false))
                    .Manifest.CaptureGraphRevisionId!;
                Assert.AreNotEqual(source, successor);
            }

            using var restarted = CreateSceneCompatibilityProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var restartedOperations = restarted.GetRequiredService<IProcessingGraphOperations>();
            var state = await restartedOperations.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(source, state.ActiveRevisionId);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await restartedOperations.RetireRevisionAsync(
                    successor, state.StateVersion, "retire-effective", "test", null, token).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(state.StateVersion,
                (await restartedOperations.GetRegistryAsync(token).ConfigureAwait(false)).StateVersion);

            var receipt = await CaptureWithSceneCompatibilityAsync(restarted, configuration, now.AddSeconds(2))
                .ConfigureAwait(false);
            Assert.AreEqual(successor, receipt.Manifest.CaptureGraphRevisionId);
            await AssertSceneCompatibilityReplayAsync(restarted, receipt).ConfigureAwait(false);

            var nonLegacyPipeline = configuration.Pipeline with
            {
                Steps = [configuration.Pipeline.Steps[0] with { Options = null }, configuration.Pipeline.Steps[1]]
            };
            var replacement = await restartedOperations.CreateRevisionAsync(
                "new-sky", "1", nonLegacyPipeline, "create-new", "test", null, token).ConfigureAwait(false);
            await restartedOperations.ValidateRevisionAsync(replacement.RevisionId, "validate-new", "test", null, token)
                .ConfigureAwait(false);
            var active = await restartedOperations.ActivateRevisionAsync(replacement.RevisionId, state.StateVersion,
                "activate-new", "test", null, token).ConfigureAwait(false);
            var retired = await restartedOperations.RetireRevisionAsync(
                successor, active.StateVersion, "retire-inactive", "test", null, token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Retired,
                retired.Revisions.Single(value => value.RevisionId == successor).Lifecycle);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("retired")]
    [DataRow("draft")]
    [DataRow("wrong-id")]
    [DataRow("wrong-plan")]
    public async Task SceneCompatibilitySuccessorRecoversFromIneligibleStoredRecord(string storedRecord)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-revision-recovery");
        var token = CancellationToken.None;
        try
        {
            var configuration = CreateSceneCompatibilityConfiguration(root);
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            string ineligible;
            string source;
            RawCaptureReceipt? original = null;
            using (var provider = CreateSceneCompatibilityProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var coordinator = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await coordinator.EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
                source = registry.ActiveRevisionId;
                if (storedRecord == "wrong-id")
                {
                    var row = await coordinator.CreateRevisionAsync("virtualsky-scene-compatibility", source,
                        configuration.Pipeline, "occupy-scene-name", "test", null, token).ConfigureAwait(false);
                    ineligible = row.RevisionId;
                    await coordinator.ValidateRevisionAsync(ineligible, "validate-other-graph", "test", null, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    original = await CaptureWithSceneCompatibilityAsync(provider, configuration, now).ConfigureAwait(false);
                    ineligible = original.Manifest.CaptureGraphRevisionId!;
                    UpdateStoredCompatibilityRevision(root, ineligible, storedRecord, now);
                }
            }

            RawCaptureReceipt replacement;
            using (var restarted = CreateSceneCompatibilityProvider(root))
            {
                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                replacement = await CaptureWithSceneCompatibilityAsync(restarted, configuration, now.AddSeconds(2))
                    .ConfigureAwait(false);
                var successor = replacement.Manifest.CaptureGraphRevisionId;
                Assert.AreNotEqual(ineligible, successor, $"Fresh capture reused the {storedRecord} row.");
                Assert.AreNotEqual(source, successor);

                var operations = restarted.GetRequiredService<IProcessingGraphOperations>();
                var state = await operations.GetRegistryAsync(token).ConfigureAwait(false);
                Assert.AreEqual(source, state.ActiveRevisionId);
                var candidate = state.Revisions.Single(value => value.RevisionId == successor);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, candidate.Lifecycle);
                Assert.IsNotNull(candidate.ValidatedUtc);
                var prior = state.Revisions.Single(value => value.RevisionId == ineligible);
                var expectedLifecycle = storedRecord switch
                {
                    "retired" => ProcessingGraphRevisionLifecycle.Retired,
                    "draft" => ProcessingGraphRevisionLifecycle.Draft,
                    _ => ProcessingGraphRevisionLifecycle.Validated
                };
                Assert.AreEqual(expectedLifecycle, prior.Lifecycle);
                if (storedRecord == "wrong-id")
                {
                    var retired = await operations.RetireRevisionAsync(
                        ineligible, state.StateVersion, "retire-unrelated", "test", null, token).ConfigureAwait(false);
                    Assert.AreEqual(source, retired.ActiveRevisionId);
                    Assert.AreEqual(state.StateVersion + 1, retired.StateVersion);
                    Assert.AreEqual(ProcessingGraphRevisionLifecycle.Retired,
                        retired.Revisions.Single(value => value.RevisionId == ineligible).Lifecycle);
                    var retry = await operations.RetireRevisionAsync(
                        ineligible, state.StateVersion, "retire-unrelated", "test", null, token).ConfigureAwait(false);
                    Assert.AreEqual(retired.StateVersion, retry.StateVersion);
                    state = retired;
                }
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.RetireRevisionAsync(candidate.RevisionId, state.StateVersion,
                        "retire-recovered", "test", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                if (original is not null && storedRecord is "retired" or "draft")
                {
                    var descriptor = original.Manifest.Descriptor;
                    await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                        await operations.SubmitReplayAsync(new(descriptor.Capture.CaptureId, ineligible,
                            descriptor.Artifact.ArtifactId), "replay-ineligible", "test", token).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    var historical = await restarted.GetRequiredService<SqliteCaptureProcessingStore>().ReadExecutionsAsync(
                        ProcessingGraphExecutionClass.Live, 10, token).ConfigureAwait(false);
                    Assert.AreEqual(ineligible, historical.Single(value => value.CaptureId == descriptor.Capture.CaptureId)
                        .GraphRevisionId);
                }
            }

            using var reopened = CreateSceneCompatibilityProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedOperations = reopened.GetRequiredService<IProcessingGraphOperations>();
            var reopenedState = await reopenedOperations.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(source, reopenedState.ActiveRevisionId);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await reopenedOperations.RetireRevisionAsync(replacement.Manifest.CaptureGraphRevisionId!,
                    reopenedState.StateVersion, "retire-reopened", "test", null, token).ConfigureAwait(false)).ConfigureAwait(false);
            var fresh = await CaptureWithSceneCompatibilityAsync(reopened, configuration, now.AddSeconds(4)).ConfigureAwait(false);
            Assert.AreEqual(replacement.Manifest.CaptureGraphRevisionId, fresh.Manifest.CaptureGraphRevisionId);
            await AssertSceneCompatibilityReplayAsync(reopened, fresh).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SceneCompatibilitySuccessorRetirementFollowsTheFirstEligibleGeneration(bool collision, bool restartBeforeRetire)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-generation");
        var token = CancellationToken.None;
        const string actor = "test";
        const string retireUnused = "retire-unused-generation";
        try
        {
            var configuration = CreateSceneCompatibilityConfiguration(root);
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            ProcessingGraphRegistryState state;
            ProcessingGraphRevisionState unused;
            RawCaptureReceipt first;
            string effectiveId;

            using (var provider = CreateSceneCompatibilityProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = provider.GetRequiredService<IProcessingGraphOperations>();
                var coordinator = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await coordinator.EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
                var source = await CreateValidatedAsync(operations, "test-legacy", "1", configuration.Pipeline, "source").ConfigureAwait(false);
                state = await operations.ActivateRevisionAsync(
                    source.RevisionId, registry.StateVersion, "activate-source", actor, null, token).ConfigureAwait(false);

                const string compatibilityGraph = "virtualsky-scene-compatibility";
                if (collision)
                {
                    _ = await CreateValidatedAsync(operations, compatibilityGraph, source.RevisionId, configuration.Pipeline,
                        "collision").ConfigureAwait(false);
                }

                first = await CaptureWithSceneCompatibilityAsync(provider, configuration, now).ConfigureAwait(false);
                effectiveId = first.Manifest.CaptureGraphRevisionId!;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var effective = await store.ReadRevisionAsync(effectiveId, token).ConfigureAwait(false);
                Assert.AreEqual(compatibilityGraph, effective.State.Name);
                Assert.AreEqual(collision ? source.RevisionId + "-1" : source.RevisionId, effective.State.Revision);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, effective.State.Lifecycle);

                unused = await CreateValidatedAsync(operations, compatibilityGraph,
                    source.RevisionId + (collision ? "-2" : "-1"), effective.Pipeline, "copy").ConfigureAwait(false);
                Assert.AreNotEqual(effectiveId, unused.RevisionId);
                Assert.AreEqual(effective.State.DefinitionIdentitySha256, unused.DefinitionIdentitySha256);
                Assert.AreEqual(effective.State.SharedPlanIdentitySha256, unused.SharedPlanIdentitySha256);
                Assert.AreEqual(effective.State.LocalPlanIdentitySha256, unused.LocalPlanIdentitySha256);
                CollectionAssert.AreEqual(effective.PipelineJson,
                    (await store.ReadRevisionAsync(unused.RevisionId, token).ConfigureAwait(false)).PipelineJson);

                var fresh = await CaptureWithSceneCompatibilityAsync(provider, configuration, now.AddSeconds(2)).ConfigureAwait(false);
                Assert.AreEqual(effectiveId, fresh.Manifest.CaptureGraphRevisionId);
                if (!restartBeforeRetire)
                    await RetireUnusedAsync(operations).ConfigureAwait(false);
            }

            using var reopened = CreateSceneCompatibilityProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedOperations = reopened.GetRequiredService<IProcessingGraphOperations>();
            if (restartBeforeRetire)
                await RetireUnusedAsync(reopenedOperations).ConfigureAwait(false);
            var retry = await reopenedOperations.RetireRevisionAsync(
                unused.RevisionId, state.StateVersion, retireUnused, actor, null, token).ConfigureAwait(false);
            Assert.AreEqual(state.StateVersion + 1, retry.StateVersion);
            Assert.AreEqual(state.ActiveRevisionId, retry.ActiveRevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Retired,
                retry.Revisions.Single(value => value.RevisionId == unused.RevisionId).Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await reopenedOperations.RetireRevisionAsync(effectiveId, retry.StateVersion, "retire-effective-after-reopen",
                    actor, null, token).ConfigureAwait(false)).ConfigureAwait(false);
            var afterRestart = await CaptureWithSceneCompatibilityAsync(reopened, configuration, now.AddSeconds(4)).ConfigureAwait(false);
            Assert.AreEqual(effectiveId, afterRestart.Manifest.CaptureGraphRevisionId);
            await AssertSceneCompatibilityReplayAsync(reopened, first).ConfigureAwait(false);
            await AssertSceneCompatibilityReplayAsync(reopened, afterRestart, "replay-fresh-scene").ConfigureAwait(false);

            async Task RetireUnusedAsync(IProcessingGraphOperations operations)
            {
                var current = await operations.GetRegistryAsync(token).ConfigureAwait(false);
                Assert.AreEqual(state.StateVersion, current.StateVersion);
                Assert.AreEqual(state.ActiveRevisionId, current.ActiveRevisionId);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.RetireRevisionAsync(effectiveId, current.StateVersion, "retire-effective-generation",
                        actor, null, token).ConfigureAwait(false)).ConfigureAwait(false);
                var retired = await operations.RetireRevisionAsync(
                    unused.RevisionId, current.StateVersion, retireUnused, actor, null, token).ConfigureAwait(false);
                Assert.AreEqual(current.StateVersion + 1, retired.StateVersion);
                Assert.AreEqual(current.ActiveRevisionId, retired.ActiveRevisionId);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Retired,
                    retired.Revisions.Single(value => value.RevisionId == unused.RevisionId).Lifecycle);
            }

            async Task<ProcessingGraphRevisionState> CreateValidatedAsync(
                IProcessingGraphOperations operations, string name, string revision, CapturePipelineConfig pipeline, string key)
            {
                var created = await operations.CreateRevisionAsync(name, revision, pipeline, "create-" + key, actor, null, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, created.Lifecycle);
                var validated = await operations.ValidateRevisionAsync(created.RevisionId, "validate-" + key, actor, null, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, validated.Lifecycle);
                return validated;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SceneCompatibilitySuccessorPrefersAnEarlierGenerationOnceItIsValidated()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-generation-validation");
        var token = CancellationToken.None;
        const string actor = "test";
        try
        {
            var configuration = CreateSceneCompatibilityConfiguration(root);
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            using var provider = CreateSceneCompatibilityProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<IProcessingGraphOperations>();
            var coordinator = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var template = await coordinator.SelectLiveRevisionAsync(configuration, retainedCapture: false, token).ConfigureAwait(false);
            var registry = await operations.GetRegistryAsync(token).ConfigureAwait(false);
            var source = await operations.CreateRevisionAsync(
                "test-legacy", "1", configuration.Pipeline, "create-source", actor, null, token).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(source.RevisionId, "validate-source", actor, null, token).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(source.RevisionId, registry.StateVersion, "activate-source", actor, null, token)
                .ConfigureAwait(false);

            var zero = await operations.CreateRevisionAsync(template.State.Name, source.RevisionId, template.Pipeline,
                "create-zero", actor, null, token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, zero.Lifecycle);
            var fallback = await CaptureWithSceneCompatibilityAsync(provider, configuration, now).ConfigureAwait(false);
            var fallbackRevision = (await operations.GetRegistryAsync(token).ConfigureAwait(false)).Revisions.Single(
                value => value.RevisionId == fallback.Manifest.CaptureGraphRevisionId);
            Assert.AreEqual(source.RevisionId + "-1", fallbackRevision.Revision);

            await operations.ValidateRevisionAsync(zero.RevisionId, "validate-zero", actor, null, token).ConfigureAwait(false);
            var fresh = await CaptureWithSceneCompatibilityAsync(provider, configuration, now.AddSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(zero.RevisionId, fresh.Manifest.CaptureGraphRevisionId);
            await AssertSceneCompatibilityReplayAsync(provider, fallback).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider CreateSceneCompatibilityProvider(string root)
    {
        var catalog = new Mock<ICelestialCatalog>();
        var catalogMetadata = catalog.As<ICelestialCatalogMetadataSource>();
        catalogMetadata.SetupGet(value => value.Metadata).Returns(
            new CatalogMetadata("scene-revision-test", "v1", new Uri("https://example.invalid"),
                new string('A', 64), "fixture", "v1"));
        catalogMetadata.SetupGet(value => value.PreprocessingVersion).Returns("scene-revision-preprocessing-v1");
        var stars = new InMemoryCelestialCatalog([new("star", "Star", 0, 0, 1)]);
        catalog.Setup(value => value.QueryCandidatesAsync(It.IsAny<CatalogCandidateQuery>(), It.IsAny<CancellationToken>()))
            .Returns((CatalogCandidateQuery query, CancellationToken token) => stars.QueryCandidatesAsync(query, token));

        var sites = new Mock<IDeploymentLocationStore>();
        sites.SetupGet(value => value.Active).Returns(SceneCompatibilitySite);
        sites.Setup(value => value.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>()))
            .Returns(SceneCompatibilitySite);
        return CreateProvider(root, configureServices: services =>
        {
            services.AddSingleton(catalog.Object);
            services.AddSingleton(sites.Object);
        });
    }

    private static CameraModuleConfig CreateSceneCompatibilityConfiguration(string root)
    {
        var configuration = CreateProjectedSceneConfiguration();
        return configuration with
        {
            DeploymentLocation = SceneCompatibilitySite,
            Rig = configuration.Rig with
            {
                Sensor = configuration.Rig.Sensor with
                {
                    WidthPixels = 64,
                    HeightPixels = 48,
                    ResponseMode = SensorResponseMode.Monochrome
                },
                Optics = configuration.Rig.Optics with { PrincipalPointX = 32, PrincipalPointY = 24, ImageCircleRadiusPixels = 23 }
            },
            Pipeline = configuration.Pipeline with
            {
                Steps =
                [
                    configuration.Pipeline.Steps[0] with
                    {
                        Options = JsonSerializer.SerializeToElement(new { independentPrediction = true })
                    },
                    new CaptureProcessingStepConfig("Storage", "archive",
                        Options: JsonSerializer.SerializeToElement(new SiloCaptureProcessingStepOptions
                        { UpdateLatestFrame = false }), DependsOn: ["projected-scene"])
                ]
            }
        };
    }

    private static async Task<RawCaptureReceipt> CaptureWithSceneCompatibilityAsync(
        ServiceProvider provider, CameraModuleConfig configuration, DateTimeOffset utc)
    {
        var token = CancellationToken.None;
        var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(utc, autoAdvance: true),
            provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<IProjectedSceneStore>(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>(),
            graphSelection: provider.GetRequiredService<IVirtualSkyGraphSelection>());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(configuration, token).ConfigureAwait(false);
        var request = new CaptureRequest(utc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        var result = await module.CaptureAsync(request, token).ConfigureAwait(false);
        var capturedRevision = result.Frame!.Metadata.Scene!.CaptureGraphRevisionId;
        Assert.IsNotNull(capturedRevision);
        var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
            new CaptureLoopSubmission(request, result, utc, request.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
        Assert.AreEqual(capturedRevision, receipt.Manifest.CaptureGraphRevisionId);
        await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
        return receipt;
    }

    private static async Task AssertSceneCompatibilityReplayAsync(
        ServiceProvider provider, RawCaptureReceipt receipt, string idempotencyKey = "replay-compatible-scene")
    {
        var descriptor = receipt.Manifest.Descriptor;
        var completed = await ExecuteReplayAsync(provider,
            new(descriptor.Capture.CaptureId, receipt.Manifest.CaptureGraphRevisionId!, descriptor.Artifact.ArtifactId),
            idempotencyKey).ConfigureAwait(false);
        Assert.AreEqual(receipt.Manifest.CaptureGraphRevisionId, completed.Execution.GraphRevisionId);
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        foreach (var nodeId in new[] { "projected-scene", AstrometryPredictionCaptureProcessingStep.Alias })
        {
            var live = await store.ReadNodeAsync(descriptor.Capture.CaptureId, nodeId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(live);
            Assert.AreEqual(live.Outputs.Single().OutputIdentitySha256,
                completed.Nodes.Single(value => value.NodeId == nodeId).Outputs.Single().OutputIdentitySha256);
        }
    }

    private static void UpdateStoredCompatibilityRevision(string root, string revisionId, string storedRecord, DateTimeOffset utc)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        switch (storedRecord)
        {
            case "retired":
                command.CommandText = "UPDATE processing_graph_revisions SET lifecycle = 'Retired', retired_unix_ms = $utc WHERE revision_id = $revision;";
                command.Parameters.AddWithValue("$utc", utc.ToUnixTimeMilliseconds());
                break;
            case "draft":
                command.CommandText = "UPDATE processing_graph_revisions SET lifecycle = 'Draft', validated_unix_ms = NULL WHERE revision_id = $revision;";
                break;
            case "wrong-plan":
                command.CommandText = "UPDATE processing_graph_revisions SET local_plan_identity_sha256 = $identity WHERE revision_id = $revision;";
                command.Parameters.AddWithValue("$identity", new string('0', 64));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(storedRecord));
        }
        command.Parameters.AddWithValue("$revision", revisionId);
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }
}
