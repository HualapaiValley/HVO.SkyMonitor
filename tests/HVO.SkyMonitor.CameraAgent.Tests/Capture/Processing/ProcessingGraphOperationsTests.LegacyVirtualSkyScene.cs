using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly string[] LegacySceneConstellations = ["CAS"];
    private static readonly string[] LegacySceneBodies = ["Moon"];

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task LegacyVirtualSkySceneRetainsCanonicalAndIndependentEvidenceAcrossUploadAndReplay(bool namedGraph, bool retentionOwner)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-legacy-virtualsky-scene");
        var requestedUtc = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        var celestialUtc = requestedUtc.AddMinutes(-15);
        var location = DeploymentLocationSnapshot.Create("legacy-site", 1, "test", null,
            DateTimeOffset.UnixEpoch, null, 35, -115, 1100, "UTC");
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(value => value.Active).Returns(location);
        locationStore.Setup(value => value.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>()))
            .Returns(location);
        var zenith = EquatorialPrecession.PrecessToJ2000(new EquatorialPoint(
            AstronomyTime.LocalMeanSiderealDegrees(celestialUtc, location.LongitudeDegrees) / 15, location.LatitudeDegrees), celestialUtc);
        var catalog = new InMemoryCelestialCatalog([
            new("hip-901", "Cassiopeia A", zenith.RightAscensionHours, zenith.DeclinationDegrees - 2, 2, null, "901"),
            new("hip-902", "Cassiopeia B", zenith.RightAscensionHours, zenith.DeclinationDegrees + 2, 3, null, "902")
        ]);
        var metadata = new CatalogMetadata("legacy-scene-test", "v1", new Uri("https://example.invalid"),
            new string('A', 64), "fixture", "v1");
        var installedCatalog = new Mock<ICelestialCatalog>();
        var installedMetadata = installedCatalog.As<ICelestialCatalogMetadataSource>();
        installedMetadata.SetupGet(value => value.Metadata).Returns(metadata);
        installedMetadata.SetupGet(value => value.PreprocessingVersion).Returns("legacy-catalog-preprocessing-v1");
        installedCatalog.Setup(value => value.QueryCandidatesAsync(It.IsAny<CatalogCandidateQuery>(), It.IsAny<CancellationToken>()))
            .Returns((CatalogCandidateQuery query, CancellationToken token) => catalog.QueryCandidatesAsync(query, token));
        var ephemeris = new Mock<IPlanetEphemeris>(MockBehavior.Strict);
        ephemeris.SetupGet(value => value.ModelVersion).Returns("fixture-moon-v1");
        ephemeris.Setup(value => value.GetPosition(SolarSystemBody.Moon, It.IsAny<DateTimeOffset>()))
            .Returns(new SolarSystemPosition(new(zenith.RightAscensionHours, zenith.DeclinationDegrees + 4), -12));
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("CAS", "901", "902")],
            new("legacy-topology", "v1", new Uri("https://example.invalid/topology"), new string('C', 64), "fixture", "v1"));
        var overrides = new Dictionary<string, string?>
        {
        };
        var baseline = CreateProjectedSceneConfiguration();
        if (retentionOwner)
        {
            var storage = new CaptureProcessingStepConfig("Storage", "archive",
                Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                {
                    StorageRoot = Path.Combine(root, "archive"),
                    UpdateLatestFrame = false
                }),
                DependsOn: ["projected-scene"]);
            baseline = baseline with { Pipeline = baseline.Pipeline with { Steps = [.. baseline.Pipeline.Steps, storage] } };
        }
        var configuration = baseline with
        {
            Observatory = new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, "UTC"),
            DeploymentLocation = location,
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { WidthPixels = 64, HeightPixels = 48, ResponseMode = SensorResponseMode.Monochrome },
                Optics = baseline.Rig.Optics with { PrincipalPointX = 32, PrincipalPointY = 24, ImageCircleRadiusPixels = 23 }
            },
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                fixedSceneUtc = celestialUtc,
                constellationIds = LegacySceneConstellations,
                solarSystemBodies = LegacySceneBodies,
                includeConstellationEndpointStars = true,
                magnitudeZeroElectronsPerSecond = 100_000,
                backgroundElectronsPerSecond = 0,
                catalogChecksumSha256 = new string('B', 64)
            }))
        };
        var legacy = configuration.Pipeline with
        {
            Steps = [configuration.Pipeline.Steps[0] with
            {
                Options = JsonSerializer.SerializeToElement(new
                    { independentPrediction = true, maximumMagnitude = 4.5, astronomyAlgorithmVersion = "legacy-astrometry-v1" })
            }, .. configuration.Pipeline.Steps.Skip(1)]
        };
        if (!namedGraph) configuration = configuration with { Pipeline = legacy };

        try
        {
            string sceneRevision;
            Guid captureId;
            Guid rawId;
            string stageKey;
            var outputs = new Dictionary<string, (string Identity, string Path, string Sha256)>(StringComparer.Ordinal);
            var predictionId = AstrometryPredictionCaptureProcessingStep.Alias;
            using (var provider = CreateProvider(root, overrides, configureServices: services =>
            {
                services.AddSingleton(installedCatalog.Object);
                services.AddSingleton(locationStore.Object);
            }))
            {
                var token = CancellationToken.None;
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(token).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
                if (namedGraph)
                {
                    var revision = await operations.CreateRevisionAsync(configuration, "legacy-virtual", "1", legacy,
                        "legacy-create", "test", null, token).ConfigureAwait(false);
                    await operations.ValidateRevisionAsync(revision.RevisionId, "legacy-validate", "test", null, token).ConfigureAwait(false);
                    registry = await operations.ActivateRevisionAsync(revision.RevisionId, registry.StateVersion,
                        "legacy-activate", "test", null, token).ConfigureAwait(false);
                }
                var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(requestedUtc, autoAdvance: true), catalog,
                    provider.GetRequiredService<IProjectedSceneStore>(), topology, ephemeris.Object,
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>(),
                    graphSelection: namedGraph ? new VirtualSkyGraphSelectionProvider(operations, enabled: true)
                        : provider.GetRequiredService<IVirtualSkyGraphSelection>());
                await using var moduleLifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(configuration, token).ConfigureAwait(false);
                var request = new CaptureRequest(requestedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still);
                var capture = await module.CaptureAsync(request, token).ConfigureAwait(false);
                var provenance = capture.Frame!.Metadata.Scene!;
                stageKey = provenance.ProjectedSceneStageKey!;
                var receipt = await ingress.AcceptAsync(configuration, new CaptureLoopSubmission(request, capture, requestedUtc,
                    request.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                sceneRevision = receipt.Manifest.CaptureGraphRevisionId!;
                if (retentionOwner)
                {
                    Assert.AreEqual(provenance.CaptureGraphRevisionId, sceneRevision);
                    Assert.AreNotEqual(registry.ActiveRevisionId, sceneRevision);
                }
                else Assert.AreEqual(registry.ActiveRevisionId, sceneRevision);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                rawId = receipt.Manifest.Descriptor.Artifact.ArtifactId;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var successor = await store.ReadRevisionAsync(sceneRevision, token).ConfigureAwait(false);
                Assert.HasCount(retentionOwner ? 3 : 1, successor.Pipeline.Steps);
                if (retentionOwner) Assert.IsFalse(successor.Pipeline.Steps.Single(value => value.Id == predictionId).Required);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                Assert.IsNull(await provider.GetRequiredService<IProjectedSceneStagingReader>()
                    .ReadAsync(stageKey, token).ConfigureAwait(false));

                var guard = new ProjectedSceneRetentionGuard(root, store);
                var expectedOutputs = new List<(string Node, string Recipe, ProjectedSceneKind Kind)>
                {
                    ("projected-scene", BuiltInProcessingRecipes.ProjectedScene, ProjectedSceneKind.VirtualRenderAuthoritative)
                };
                if (retentionOwner) expectedOutputs.Add((predictionId, BuiltInProcessingRecipes.AstrometryPrediction, ProjectedSceneKind.Predicted));
                foreach (var (node, expectedRecipe, expectedKind) in expectedOutputs)
                {
                    var state = await store.ReadNodeAsync(captureId, node, token).ConfigureAwait(false);
                    Assert.IsNotNull(state);
                    Assert.HasCount(1, state.Outputs, $"{node}: {state.Status} {state.Reason}");
                    var output = state.Outputs.Single();
                    var payloadPath = Path.Combine(root, output.PayloadRelativePath);
                    var product = ProjectedSceneJson.Parse(await File.ReadAllBytesAsync(payloadPath, token).ConfigureAwait(false));
                    Assert.IsTrue(product.IsValid, product.ErrorPath);
                    var scene = product.Scene!;
                    Assert.AreEqual(expectedRecipe, output.Artifact.Recipe.Name);
                    Assert.AreEqual(expectedKind, scene.Kind);
                    Assert.AreEqual(new ProjectedSceneSource(captureId, rawId, CaptureContractJson.ComputeDescriptorSha256(receipt.Manifest.Descriptor)), scene.Source);
                    if (node == predictionId)
                    {
                        Assert.AreEqual("legacy-astrometry-v1", scene.AstronomyAlgorithmVersion);
                        Assert.AreEqual(4.5, scene.Selection.MaximumMagnitude);
                        Assert.AreEqual(requestedUtc.AddTicks(capture.Frame.Metadata.Exposure.Ticks / 2), scene.EffectiveUtc);
                        Assert.AreEqual(metadata.Checksum, scene.Catalog.ChecksumSha256);
                        Assert.AreEqual("legacy-catalog-preprocessing-v1", scene.Catalog.PreprocessingVersion);
                        Assert.IsNotEmpty(scene.Objects);
                        Assert.IsTrue(scene.Objects.All(static value => value.Kind == CelestialObjectKind.Star));
                        Assert.IsEmpty(scene.Selection.SolarSystemBodies);
                        Assert.IsEmpty(scene.Selection.ConstellationIds);
                    }
                    else
                    {
                        Assert.AreEqual(provenance.SceneUtc, scene.EffectiveUtc);
                        Assert.AreEqual("fixture-moon-v1", scene.EphemerisModelVersion);
                        Assert.AreEqual(topology.Metadata.SourceSha256, scene.ConstellationTopology!.SourceSha256);
                        Assert.AreEqual("CAS", scene.Selection.ConstellationIds.Single());
                        Assert.AreEqual(SolarSystemBody.Moon, scene.Selection.SolarSystemBodies.Single());
                        Assert.AreEqual(provenance.CatalogChecksumSha256, scene.Catalog.ChecksumSha256);
                        Assert.IsTrue(scene.Objects.Any(static value => value.Id == "solar-system:Moon"));
                        Assert.IsTrue(scene.Segments.Any(static value => value.ConstellationId == "CAS"));
                    }
                    Assert.AreEqual(node != predictionId, await guard.HasRetainedConsumerAsync(new DurableProcessingEvidence(
                        output.OutputIdentitySha256, output.Artifact.ArtifactId, captureId, FrameArtifactRole.Metadata,
                        output.PayloadRelativePath, output.SidecarRelativePath, output.EvidenceJson, 0, "Available", null), token)
                        .ConfigureAwait(false));
                    outputs.Add(node, (output.OutputIdentitySha256, payloadPath, Sha256OfFile(payloadPath)));
                }


            }

            using (var restarted = CreateProvider(root, overrides))
            {
                var token = CancellationToken.None;
                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var selected = await operations.SelectLiveRevisionAsync(configuration, retainedCapture: false, token).ConfigureAwait(false);
                Assert.AreEqual(sceneRevision, selected.State.RevisionId);
                var replay = await operations.SubmitReplayAsync(new(captureId, sceneRevision, rawId, Reason: "legacy scene replay"),
                    "legacy-virtual-replay", "test", token).ConfigureAwait(false);
                foreach (var (node, evidence) in outputs)
                    Assert.AreEqual((1, 0, evidence.Identity), ReadOutputPins(root, replay.Execution.ExecutionId, node).Single());
                var completed = await RunReplayToCompletionAsync(restarted, replay.Execution.ExecutionId).ConfigureAwait(false);
                foreach (var (node, evidence) in outputs)
                {
                    var replayNode = completed.Nodes.Single(value => value.NodeId == node);
                    Assert.AreEqual(evidence.Identity, replayNode.Outputs.Single().OutputIdentitySha256);
                    Assert.AreEqual(evidence.Sha256, Sha256OfFile(evidence.Path));
                    Assert.IsEmpty(ReadOutputPins(root, replay.Execution.ExecutionId, node));
                }
                Assert.IsNull(await restarted.GetRequiredService<IProjectedSceneStagingReader>()
                    .ReadAsync(stageKey, token).ConfigureAwait(false));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
