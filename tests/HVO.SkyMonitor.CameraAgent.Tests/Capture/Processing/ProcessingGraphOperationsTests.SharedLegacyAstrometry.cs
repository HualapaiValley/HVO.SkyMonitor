using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
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
    [TestMethod]
    [DataRow("annotation")]
    [DataRow("storage")]
    [DataRow("isolated")]
    public async Task LegacyVirtualSkyFitUsesPredictionAndPreservesExistingConsumers(string consumer)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-legacy-shared-fit");
        var token = CancellationToken.None;
        const string measurementId = "astrometry-prediction-associations";
        const string measurementVariant = "astrometry-prediction-associations-v1";
        var sharedAssociations = consumer == "annotation";
        var sharedFit = consumer == "storage";
        var requestedUtc = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        var celestialUtc = requestedUtc.AddSeconds(1);
        var location = DeploymentLocationSnapshot.Create("legacy-shared-site", 1, "test", null,
            DateTimeOffset.UnixEpoch, null, 35, -115, 1100, "UTC");
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(value => value.Active).Returns(location);
        locationStore.Setup(value => value.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>()))
            .Returns(location);
        var zenith = EquatorialPrecession.PrecessToJ2000(new EquatorialPoint(
            AstronomyTime.LocalMeanSiderealDegrees(celestialUtc, location.LongitudeDegrees) / 15, location.LatitudeDegrees), celestialUtc);
#pragma warning disable CA5394 // Seeded synthetic sky, with no security use.
        var random = new Random(934817);
        var stars = Enumerable.Range(0, 560).Select(i => new CelestialCatalogObject($"SYN{i:0000}", $"Artificial {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1.8 + 2.6 * random.NextDouble()))
            .Concat([
                new("hip-901", "Cassiopeia A", zenith.RightAscensionHours, zenith.DeclinationDegrees - 2, 2, null, "901"),
                new("hip-902", "Cassiopeia B", zenith.RightAscensionHours, zenith.DeclinationDegrees + 2, 3, null, "902")
            ]).ToArray();
#pragma warning restore CA5394
        var catalog = new InMemoryCelestialCatalog(stars);
        var metadata = new CatalogMetadata("legacy-shared-test", "v1", new Uri("https://example.invalid"),
            new string('A', 64), "fixture", "v1");
        const string preprocessingVersion = "legacy-catalog-preprocessing-v1";
        var installedCatalog = new Mock<ICelestialCatalog>();
        var installedMetadata = installedCatalog.As<ICelestialCatalogMetadataSource>();
        installedMetadata.SetupGet(value => value.Metadata).Returns(metadata);
        installedMetadata.SetupGet(value => value.PreprocessingVersion).Returns(preprocessingVersion);
        installedCatalog.Setup(value => value.QueryCandidatesAsync(It.IsAny<CatalogCandidateQuery>(), It.IsAny<CancellationToken>()))
            .Returns((CatalogCandidateQuery query, CancellationToken cancel) => catalog.QueryCandidatesAsync(query, cancel));
        var fitCatalog = new AstrometricCatalogData(metadata, stars, true, 4.5,
            provenance: new(metadata.Name, metadata.Version, "fixture", preprocessingVersion));
        var fitCatalogSource = new Mock<IAstrometricCatalogSource>(MockBehavior.Strict);
        fitCatalogSource.Setup(value => value.ReadAsync(4.5, 2500, It.IsAny<CancellationToken>())).ReturnsAsync(fitCatalog);
        var ephemeris = new Mock<IPlanetEphemeris>(MockBehavior.Strict);
        ephemeris.SetupGet(value => value.ModelVersion).Returns("fixture-moon-v1");
        ephemeris.Setup(value => value.GetPosition(SolarSystemBody.Moon, It.IsAny<DateTimeOffset>()))
            .Returns(new SolarSystemPosition(new(zenith.RightAscensionHours, zenith.DeclinationDegrees + 4), -12));
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("CAS", "901", "902")],
            new("legacy-topology", "v1", new Uri("https://example.invalid/topology"), new string('C', 64), "fixture", "v1"));
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var baseline = CreateProjectedSceneConfiguration();
        var sceneStep = baseline.Pipeline.Steps.Single() with
        {
            Options = JsonSerializer.SerializeToElement(new { maximumMagnitude = 4.5 }),
            Publication = durable
        };
        var storage = new CaptureProcessingStepConfig("Storage", "archive", DependsOn: ["projected-scene"],
            Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
            {
                StorageRoot = Path.Combine(root, "archive"), QueueForUpload = false, UpdateLatestFrame = false
            }));
        var configuration = baseline with
        {
            Observatory = new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, "UTC"),
            DeploymentLocation = location,
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { WidthPixels = 512, HeightPixels = 512, ResponseMode = SensorResponseMode.Monochrome },
                Optics = baseline.Rig.Optics with { PrincipalPointX = 256, PrincipalPointY = 256, ImageCircleRadiusPixels = 232,
                    FocalLengthXPixels = 144, FocalLengthYPixels = 144 },
                Readout = new(new(0, 0, 512, 512), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1,
                    FrameLevelCodeSpace.StoredContainer, 0, ushort.MaxValue)
            },
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                fixedSceneUtc = celestialUtc,
                constellationIds = LegacySceneConstellations,
                solarSystemBodies = LegacySceneBodies,
                includeConstellationEndpointStars = true,
                magnitudeZeroElectronsPerSecond = 6000,
                backgroundElectronsPerSecond = 100,
                catalogChecksumSha256 = new string('B', 64)
            })),
            Pipeline = baseline.Pipeline with { Steps = [sceneStep, storage] }
        };
        var legacy = configuration.Pipeline with
        {
            Steps =
            [
                sceneStep with { Options = JsonSerializer.SerializeToElement(new { independentPrediction = true, maximumMagnitude = 4.5 }) },
                new("MeasuredStellarAssociations", measurementId, DependsOn: ["projected-scene"],
                    Options: JsonSerializer.SerializeToElement(new { outputVariant = measurementVariant }), Publication: durable),
                new("MeasuredAstrometry", "fit", DependsOn: ["projected-scene", measurementId], Required: false,
                    Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions
                    {
                        CatalogSelection = new(fitCatalog.IdentitySha256, fitCatalog.SelectionIdentitySha256, 4.5),
                        Recipe = new(new(MaximumCatalogMagnitude: 4.5))
                    }), Publication: durable),
                new("Preview", "preview", Options: JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" }),
                    DependsOn: ["$raw"], Publication: durable),
                new("Annotation", "annotation",
                    Options: JsonSerializer.SerializeToElement(new { requireProjectedSceneDependency = true, constellationIds = LegacySceneConstellations }),
                    DependsOn: sharedAssociations ? ["preview", "projected-scene", measurementId] : ["preview", "projected-scene"], Publication: durable),
                storage with { DependsOn = sharedFit ? ["projected-scene", "annotation", "fit"] : ["projected-scene", "annotation"] }
            ]
        };
        try
        {
            string activeId;
            string liveId;
            byte[] successorPipeline;
            string? previousId = null;
            using (var provider = CreateProvider(root, configureServices: services =>
            {
                services.AddSingleton(installedCatalog.Object);
                services.AddSingleton(fitCatalogSource.Object);
                services.AddSingleton(locationStore.Object);
            }))
            {
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(token).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
                var revision = await operations.CreateRevisionAsync("legacy-shared", "1", legacy, "legacy-create", "test", null, token)
                    .ConfigureAwait(false);
                await operations.ValidateRevisionAsync(revision.RevisionId, "legacy-validate", "test", null, token).ConfigureAwait(false);
                registry = await operations.ActivateRevisionAsync(revision.RevisionId, registry.StateVersion,
                    "legacy-activate", "test", null, token).ConfigureAwait(false);
                activeId = registry.ActiveRevisionId;
                Assert.AreEqual(revision.RevisionId, activeId);

                if (sharedAssociations)
                {
                    // The earlier migration added the independent prediction while keeping the fit on the canonical scene.
                    var earlier = legacy with { Steps = [sceneStep, .. legacy.Steps.Skip(1),
                        new("AstrometryPrediction", "AstrometryPrediction", DependsOn: ["$raw"], Required: false, Publication: durable,
                            Options: sceneStep.Options)] };
                    var previous = await operations.CreateRevisionAsync("virtualsky-scene-compatibility", activeId, earlier,
                        "previous-create", "test", null, token).ConfigureAwait(false);
                    await operations.ValidateRevisionAsync(previous.RevisionId, "previous-validate", "test", null, token).ConfigureAwait(false);
                    previousId = previous.RevisionId;
                }

                var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(requestedUtc, autoAdvance: true), catalog,
                    provider.GetRequiredService<IProjectedSceneStore>(), topology, ephemeris.Object,
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>(),
                    graphSelection: new VirtualSkyGraphSelectionProvider(operations, enabled: true));
                await using var moduleLifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(configuration, token).ConfigureAwait(false);
                var request = new CaptureRequest(requestedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null));
                var capture = await module.CaptureAsync(request, token).ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(configuration, new CaptureLoopSubmission(request, capture, requestedUtc,
                    request.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                liveId = receipt.Manifest.CaptureGraphRevisionId!;
                Assert.AreEqual(capture.Frame!.Metadata.Scene!.CaptureGraphRevisionId, liveId);
                Assert.AreNotEqual(activeId, liveId);
                Assert.AreNotEqual(previousId, liveId);
                Assert.AreEqual(1L, receipt.Manifest.Descriptor.Capture.CaptureSequence);
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var successor = await store.ReadRevisionAsync(liveId, token).ConfigureAwait(false);
                successorPipeline = successor.PipelineJson;
                var fit = successor.Pipeline.Steps.Single(step => step.Id == "fit");
                var predictionId = AstrometryPredictionCaptureProcessingStep.Alias;
                var fitMeasurementId = sharedAssociations ? measurementId + "-1" : measurementId;
                Assert.AreEqual(sharedAssociations ? 8 : 7, successor.Pipeline.Steps.Count);
                CollectionAssert.AreEqual(new[] { predictionId, fitMeasurementId }, fit.DependsOn!.ToArray());
                foreach (var nodeId in new[] { "annotation", "archive" })
                {
                    var original = legacy.Steps.Single(step => step.Id == nodeId);
                    var migrated = successor.Pipeline.Steps.Single(step => step.Id == nodeId);
                    Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(original), CaptureContractJson.ComputeCanonicalJsonSha256(migrated));
                }
                var rawPath = receipt.StoredFrame.AbsolutePath;
                var rawSha = Sha256OfFile(rawPath);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                Assert.AreEqual(rawSha, Sha256OfFile(rawPath));
                Assert.AreEqual(receipt.Manifest.Descriptor.Artifact.ChecksumSha256, rawSha);

                var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                var canonical = ProjectedSceneJson.Parse(await ReadPayloadAsync("projected-scene").ConfigureAwait(false)).Scene!;
                Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, canonical.Kind);
                Assert.IsTrue(canonical.Objects.Any(value => value.Id == "solar-system:Moon"));
                Assert.IsTrue(canonical.Segments.Any(value => value.ConstellationId == "CAS"));
                var prediction = ProjectedSceneJson.Parse(await ReadPayloadAsync(predictionId).ConfigureAwait(false)).Scene!;
                Assert.AreEqual(ProjectedSceneKind.Predicted, prediction.Kind);
                Assert.AreEqual(requestedUtc.AddSeconds(1), prediction.EffectiveUtc);
                Assert.AreEqual(metadata.Checksum, prediction.Catalog.ChecksumSha256);
                Assert.AreEqual(preprocessingVersion, prediction.Catalog.PreprocessingVersion);
                Assert.AreEqual(canonical.Source, prediction.Source);
                Assert.AreNotEqual(canonical.SceneIdentitySha256, prediction.SceneIdentitySha256);
                var measuredOutput = await ReadOutputAsync(fitMeasurementId).ConfigureAwait(false);
                Assert.AreEqual(sharedAssociations ? measurementVariant + "-1" : measurementVariant, measuredOutput.Artifact.Variant);
                var measured = MeasuredStellarAssociationJson.Parse(await ReadPayloadAsync(fitMeasurementId).ConfigureAwait(false)).Associations!;
                Assert.AreEqual(prediction.SceneIdentitySha256, measured.Scene.SceneIdentitySha256);
                var solution = MeasuredAstrometryJson.Parse(await ReadPayloadAsync("fit", BuiltInProcessingRecipes.MeasuredAstrometry).ConfigureAwait(false));
                Assert.AreEqual(prediction.SceneIdentitySha256, solution.SceneIdentitySha256);
                Assert.AreEqual(measuredOutput.ArtifactId, solution.MeasuredArtifactId);
                Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.ReasonCode);

                var annotation = await ReadOutputAsync("annotation").ConfigureAwait(false);
                var canonicalOutput = await ReadOutputAsync("projected-scene").ConfigureAwait(false);
                Assert.Contains(canonicalOutput.ArtifactId, annotation.Artifact.SourceArtifactIds!);
                Assert.DoesNotContain(measuredOutput.ArtifactId, annotation.Artifact.SourceArtifactIds!);
                if (sharedAssociations)
                {
                    var canonicalMeasurement = MeasuredStellarAssociationJson.Parse(await ReadPayloadAsync(measurementId).ConfigureAwait(false)).Associations!;
                    Assert.AreEqual(canonical.SceneIdentitySha256, canonicalMeasurement.Scene.SceneIdentitySha256);
                    Assert.Contains((await ReadOutputAsync(measurementId).ConfigureAwait(false)).ArtifactId, annotation.Artifact.SourceArtifactIds!);
                }
                var archive = await store.ReadNodeAsync(captureId, "archive", token).ConfigureAwait(false);
                Assert.IsNotNull(archive);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, archive.Status, archive.Reason);

                async Task<DurableProcessingOutput> ReadOutputAsync(string nodeId, string? recipe = null)
                {
                    var node = await store.ReadNodeAsync(captureId, nodeId, token).ConfigureAwait(false);
                    Assert.IsNotNull(node);
                    Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, $"Node {nodeId}: {node.Reason}");
                    return recipe is null ? node.Outputs.Single() : node.Outputs.Single(output => output.Artifact.Recipe.Name == recipe);
                }

                async Task<byte[]> ReadPayloadAsync(string nodeId, string? recipe = null)
                    => await File.ReadAllBytesAsync(Path.Combine(root, (await ReadOutputAsync(nodeId, recipe).ConfigureAwait(false)).PayloadRelativePath), token)
                        .ConfigureAwait(false);
            }
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var selected = await reopened.SelectLiveRevisionAsync(configuration, retainedCapture: false, token).ConfigureAwait(false);
            Assert.AreEqual(liveId, selected.State.RevisionId);
            CollectionAssert.AreEqual(successorPipeline, selected.PipelineJson);
            var reopenedRegistry = await reopened.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(activeId, reopenedRegistry.ActiveRevisionId);
            if (previousId is not null)
            {
                Assert.AreEqual(ProcessingGraphOperationsCoordinator.VirtualSkySharedFitCompatibilityGraphName, selected.State.Name);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await reopened.RetireRevisionAsync(liveId, reopenedRegistry.StateVersion,
                        "retire-effective-shared-fit", "test", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(reopenedRegistry.StateVersion,
                    (await reopened.GetRegistryAsync(token).ConfigureAwait(false)).StateVersion);
                var old = await reopened.SelectLiveRevisionAsync(configuration, retainedCapture: true, token, previousId).ConfigureAwait(false);
                Assert.AreEqual(previousId, old.State.RevisionId);
                CollectionAssert.AreEqual(new[] { "projected-scene", measurementId },
                    old.Pipeline.Steps.Single(step => step.Id == "fit").DependsOn!.ToArray());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
