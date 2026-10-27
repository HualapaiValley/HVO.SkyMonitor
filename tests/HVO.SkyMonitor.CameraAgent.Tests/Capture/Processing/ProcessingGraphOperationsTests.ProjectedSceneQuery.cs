using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ActivatedProjectedSceneQueryMustMatchTheSceneStagedForItsCapture()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-projected-query-1619");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration() with
            {
                Module = new CameraModuleDescriptor("PhysicalCamera"),
                DeploymentLocation = DeploymentLocationSnapshot.Create(
                    "test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC")
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var edited = config.Pipeline with
            {
                Steps = [config.Pipeline.Steps[0] with
                {
                    Options = JsonSerializer.SerializeToElement(new
                    {
                        maximumMagnitude = 2.0, maximumResults = 1, includeConstellationEndpointStars = true
                    })
                }]
            };
            var draft = await operations.CreateRevisionAsync("query", "2", edited, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);

            var submission = CreateSubmission(0, CameraPixelFormat.Mono16, DateTimeOffset.UnixEpoch.AddDays(1));
            var frame = submission.Result.Frame!;
            submission = submission with
            {
                Result = submission.Result with
                {
                    Frame = frame with
                    {
                        Layout = ReconstructableCaptureContractTests.CreateManifest(
                    CameraPixelFormat.Mono16, 2, 2, 4, frame.PixelData.ToArray()).Descriptor.Layout
                    }
                }
            };
            var stager = new CaptureProjectedSceneStager(provider.GetRequiredService<IProjectedSceneStagingStore>(), new QueryFixtureCatalog());
            var host = new CaptureHostContext(config, ingress, new QueryTestDistributor(),
                projectedSceneStaging: provider.GetRequiredService<IProjectedSceneStagingStore>(),
                projectedSceneLifecycle: provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
                projectedSceneStager: stager);
            await host.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            var captureId = RawCaptureDescriptorFactory.CreateStableIds(config, submission).CaptureId;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"hvo.skymonitor.projected-scene-stage-key.v1\0{captureId:N}")));
            var scene = await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(scene);
            var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            var execution = await operations.ReadExecutionDetailAsync(executionId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, execution!.Execution.GraphRevisionId);
            Console.WriteLine($"Active graph={draft.RevisionId}; requested query=2/1/true; staged query={scene.Selection.MaximumMagnitude}/{scene.Selection.MaximumResults}/{scene.Selection.IncludeConstellationEndpointStars}");
            Assert.AreEqual(2.0, scene.Selection.MaximumMagnitude, "The activated query must govern scene staging.");
            Assert.AreEqual(1, scene.Selection.MaximumResults);
            Assert.IsTrue(scene.Selection.IncludeConstellationEndpointStars);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class QueryTestDistributor : ICaptureDistributor
    {
        public void NotifyCommittedCapture() { }
        public ValueTask ProcessEphemeralAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class QueryFixtureCatalog : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = new(
            "fixture", "1", new Uri("https://example.test/catalog"), new string('A', 64), "test", "v1");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => [];

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>([]);
    }
}
