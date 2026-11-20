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
    [TestMethod]
    [DataRow("annotation")]
    [DataRow("annotation-occupied-prediction")]
    [DataRow("storage")]
    [DataRow("storage-fit-failure")]
    [DataRow("isolated")]
    [DataRow("annotation-preset")]
    [DataRow("annotation-occupied-prediction-storage-preset")]
    public async Task LegacyVirtualSkyFitUsesPredictionAndPreservesExistingConsumers(string consumer)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-legacy-shared-fit");
        var token = CancellationToken.None;
        const string measurementId = "astrometry-prediction-associations";
        const string privateMeasurementVariant = "astrometry-prediction-associations-v1";
        const string predictionVariant = AstrometryPredictionCaptureProcessingStep.AliasVariant;
        var combined = consumer == "annotation-occupied-prediction-storage-preset";
        var occupiedPrediction = consumer == "annotation-occupied-prediction" || combined;
        var measurementVariant = occupiedPrediction ? predictionVariant : privateMeasurementVariant;
        var previewId = occupiedPrediction ? "astrometryprediction" : "preview";
        var predictionId = occupiedPrediction ? "AstrometryPrediction-1" : AstrometryPredictionCaptureProcessingStep.Alias;
        var configurePreset = consumer == "annotation-preset" || combined;
        var sharedAssociations = consumer == "annotation" || configurePreset || occupiedPrediction;
        var failedFit = consumer == "storage-fit-failure";
        var sharedFit = consumer == "storage" || failedFit || combined;
        var fitMeasurementId = sharedAssociations ? measurementId + "-1" : measurementId;
        var archiveRoot = Path.Combine(root, "archive");
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
                StorageRoot = archiveRoot,
                QueueForUpload = false,
                UpdateLatestFrame = false
            }));
        var configuration = baseline with
        {
            Observatory = new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, "UTC"),
            DeploymentLocation = location,
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { WidthPixels = 512, HeightPixels = 512, ResponseMode = SensorResponseMode.Monochrome },
                Optics = baseline.Rig.Optics with
                {
                    PrincipalPointX = 256,
                    PrincipalPointY = 256,
                    ImageCircleRadiusPixels = 232,
                    FocalLengthXPixels = 144,
                    FocalLengthYPixels = 144
                },
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
                sceneStep with
                {
                    Options = occupiedPrediction
                        ? JsonSerializer.SerializeToElement(new { independentPrediction = true, maximumMagnitude = 4.5, outputVariant = predictionVariant + "-1" })
                        : JsonSerializer.SerializeToElement(new { independentPrediction = true, maximumMagnitude = 4.5 })
                },
                new("MeasuredStellarAssociations", measurementId, DependsOn: ["projected-scene"],
                    Options: JsonSerializer.SerializeToElement(new { outputVariant = measurementVariant }), Publication: durable),
                new("MeasuredAstrometry", "fit", DependsOn: ["projected-scene", measurementId], Required: false,
                    Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions
                    {
                        CatalogSelection = new(fitCatalog.IdentitySha256, failedFit ? new string('F', 64) : fitCatalog.SelectionIdentitySha256, 4.5),
                        Recipe = new(new(MaximumCatalogMagnitude: 4.5))
                    }), Publication: durable),
                new("Preview", previewId, Options: JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" }),
                    DependsOn: ["$raw"], Publication: durable),
                new("Annotation", "annotation",
                    Options: JsonSerializer.SerializeToElement(new { requireProjectedSceneDependency = true, constellationIds = LegacySceneConstellations }),
                    DependsOn: sharedAssociations ? [previewId, "projected-scene", measurementId] : [previewId, "projected-scene"],
                    Publication: sharedFit ? null : durable),
                storage with { DependsOn = sharedFit ? ["projected-scene", "annotation", "fit"] : ["projected-scene", "annotation"] }
            ]
        };
        void AddCaptureServices(IServiceCollection services)
        {
            services.AddSingleton(installedCatalog.Object);
            services.AddSingleton(fitCatalogSource.Object);
            services.AddSingleton(locationStore.Object);
        }

        VirtualSkyCameraModule CreateModule(ServiceProvider host, DateTimeOffset startUtc)
            => new(new ExposureTestTimeProvider(startUtc, autoAdvance: true), catalog,
                host.GetRequiredService<IProjectedSceneStore>(), topology, ephemeris.Object,
                stagingStore: host.GetRequiredService<IProjectedSceneStagingStore>(),
                graphSelection: new VirtualSkyGraphSelectionProvider(host.GetRequiredService<ProcessingGraphOperationsCoordinator>(), enabled: true));

        try
        {
            string activeId;
            string liveId;
            Guid captureId;
            Guid rawId;
            byte[] successorPipeline;
            string? previousId = null;
            var replayOutputs = new Dictionary<string, DurableProcessingOutput>(StringComparer.Ordinal);
            using (var provider = CreateProvider(root, configureServices: AddCaptureServices))
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

                if (sharedAssociations && !occupiedPrediction)
                {
                    // The earlier migration added the independent prediction while keeping the fit on the canonical scene.
                    var earlier = legacy with
                    {
                        Steps = [sceneStep, .. legacy.Steps.Skip(1),
                        new("AstrometryPrediction", "AstrometryPrediction", DependsOn: ["$raw"], Required: false, Publication: durable,
                            Options: sceneStep.Options)]
                    };
                    var previous = await operations.CreateRevisionAsync("virtualsky-scene-compatibility", activeId, earlier,
                        "previous-create", "test", null, token).ConfigureAwait(false);
                    await operations.ValidateRevisionAsync(previous.RevisionId, "previous-validate", "test", null, token).ConfigureAwait(false);
                    previousId = previous.RevisionId;
                }

                var module = CreateModule(provider, requestedUtc);
                await using var moduleLifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(configuration, token).ConfigureAwait(false);
                var receipt = await CaptureAndAdmitAsync(provider, module, requestedUtc).ConfigureAwait(false);
                liveId = receipt.Manifest.CaptureGraphRevisionId!;
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                rawId = receipt.Manifest.Descriptor.Artifact.ArtifactId;
                Assert.AreNotEqual(activeId, liveId);
                Assert.AreNotEqual(previousId, liveId);
                Assert.AreEqual(1L, receipt.Manifest.Descriptor.Capture.CaptureSequence);
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var successor = await store.ReadRevisionAsync(liveId, token).ConfigureAwait(false);
                successorPipeline = successor.PipelineJson;
                var fit = successor.Pipeline.Steps.Single(step => step.Id == "fit");
                Assert.AreEqual(sharedAssociations ? 8 : 7, successor.Pipeline.Steps.Count);
                CollectionAssert.AreEqual(new[] { predictionId, fitMeasurementId }, fit.DependsOn!.ToArray());
                var predictionStep = successor.Pipeline.Steps.Single(step => step.Id == predictionId);
                Assert.AreEqual(occupiedPrediction, predictionStep.Options!.Value.TryGetProperty("outputVariant", out _));
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
                if (sharedFit)
                {
                    var runtime = provider.GetRequiredService<ICaptureProcessingPipelineFactory>()
                        .CreateRetainedGraph(configuration with { Pipeline = successor.Pipeline });
                    try
                    {
                        Assert.IsTrue(successor.IsCompiledBy(runtime));
                        var archiveNode = runtime.Nodes.Single(node => node.Id == "archive");
                        Assert.IsTrue(archiveNode.Required);
                        Assert.IsNotNull(archiveNode.OptionalDependencies);
                        Assert.HasCount(1, archiveNode.OptionalDependencies);
                        Assert.Contains("fit", archiveNode.OptionalDependencies);
                    }
                    finally { runtime.DisposeSteps(); }
                }

                var canonical = ProjectedSceneJson.Parse(await ReadPayloadAsync("projected-scene").ConfigureAwait(false)).Scene!;
                Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, canonical.Kind);
                Assert.IsTrue(canonical.Objects.Any(value => value.Id == "solar-system:Moon"));
                Assert.IsTrue(canonical.Segments.Any(value => value.ConstellationId == "CAS"));
                var prediction = ProjectedSceneJson.Parse(await ReadPayloadAsync(predictionId).ConfigureAwait(false)).Scene!;
                Assert.AreEqual(ProjectedSceneKind.Predicted, prediction.Kind);
                Assert.AreEqual(occupiedPrediction ? predictionVariant + "-2" : predictionVariant,
                    (await ReadOutputAsync(predictionId).ConfigureAwait(false)).Artifact.Variant);
                Assert.AreEqual(requestedUtc.AddSeconds(1), prediction.EffectiveUtc);
                Assert.AreEqual(metadata.Checksum, prediction.Catalog.ChecksumSha256);
                Assert.AreEqual(preprocessingVersion, prediction.Catalog.PreprocessingVersion);
                Assert.AreEqual(canonical.Source, prediction.Source);
                Assert.AreNotEqual(canonical.SceneIdentitySha256, prediction.SceneIdentitySha256);
                var measuredOutput = await ReadOutputAsync(fitMeasurementId).ConfigureAwait(false);
                Assert.AreEqual(sharedAssociations && !occupiedPrediction ? privateMeasurementVariant + "-1" : privateMeasurementVariant,
                    measuredOutput.Artifact.Variant);
                var measured = MeasuredStellarAssociationJson.Parse(await ReadPayloadAsync(fitMeasurementId).ConfigureAwait(false)).Associations!;
                Assert.AreEqual(prediction.SceneIdentitySha256, measured.Scene.SceneIdentitySha256);
                if (!failedFit)
                {
                    var solution = MeasuredAstrometryJson.Parse(await ReadPayloadAsync("fit", BuiltInProcessingRecipes.MeasuredAstrometry).ConfigureAwait(false));
                    Assert.AreEqual(prediction.SceneIdentitySha256, solution.SceneIdentitySha256);
                    Assert.AreEqual(measuredOutput.ArtifactId, solution.MeasuredArtifactId);
                    Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.ReasonCode);
                }

                var annotation = await ReadOutputAsync("annotation").ConfigureAwait(false);
                var canonicalOutput = await ReadOutputAsync("projected-scene").ConfigureAwait(false);
                if (occupiedPrediction) Assert.AreEqual(predictionVariant + "-1", canonicalOutput.Artifact.Variant);
                Assert.Contains(canonicalOutput.ArtifactId, annotation.Artifact.SourceArtifactIds!);
                Assert.DoesNotContain(measuredOutput.ArtifactId, annotation.Artifact.SourceArtifactIds!);
                if (sharedAssociations)
                {
                    var canonicalMeasurementOutput = await ReadOutputAsync(measurementId).ConfigureAwait(false);
                    Assert.AreEqual(measurementVariant, canonicalMeasurementOutput.Artifact.Variant);
                    var canonicalMeasurement = MeasuredStellarAssociationJson.Parse(await ReadPayloadAsync(measurementId).ConfigureAwait(false)).Associations!;
                    Assert.AreEqual(canonical.SceneIdentitySha256, canonicalMeasurement.Scene.SceneIdentitySha256);
                    Assert.Contains(canonicalMeasurementOutput.ArtifactId, annotation.Artifact.SourceArtifactIds!);
                    if (occupiedPrediction) replayOutputs.Add(measurementId, canonicalMeasurementOutput);
                }
                var archive = await store.ReadNodeAsync(captureId, "archive", token).ConfigureAwait(false);
                Assert.IsNotNull(archive);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, archive.Status, archive.Reason);
                if (occupiedPrediction)
                {
                    replayOutputs.Add("projected-scene", canonicalOutput);
                    replayOutputs.Add(predictionId, await ReadOutputAsync(predictionId).ConfigureAwait(false));
                    replayOutputs.Add(fitMeasurementId, measuredOutput);
                    replayOutputs.Add("fit", await ReadOutputAsync("fit", BuiltInProcessingRecipes.MeasuredAstrometry).ConfigureAwait(false));
                    replayOutputs.Add("annotation", annotation);

                    var invalidProposal = successor.Pipeline with
                    {
                        Steps = successor.Pipeline.Steps.Select(step => step == predictionStep
                            ? step with { Options = JsonSerializer.SerializeToElement(new { outputVariant = measurementVariant }) }
                            : step).ToArray()
                    };
                    var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
                    var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                        factory.PreviewPlan(configuration with { Pipeline = invalidProposal }));
                    StringAssert.Contains(failure.Message, $"duplicate output Metadata/{measurementVariant}", StringComparison.Ordinal);
                }

                if (sharedFit || consumer == "isolated")
                {
                    await CheckCadenceEvidenceAsync(provider, receipt,
                        failedFit ? DurableProcessingNodeStatus.TerminalFailure : DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
                    if (!combined)
                        await CaptureCadenceFrameAsync(provider, module, liveId, 2).ConfigureAwait(false);
                }

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
            string? presetReceipt = null;
            using (var restarted = CreateProvider(root, configureServices: sharedFit || occupiedPrediction || configurePreset ? AddCaptureServices : null))
            {
                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var selected = await reopened.SelectLiveRevisionAsync(configuration, retainedCapture: false, token).ConfigureAwait(false);
                Assert.AreEqual(liveId, selected.State.RevisionId);
                CollectionAssert.AreEqual(successorPipeline, selected.PipelineJson);
                var reopenedRegistry = await reopened.GetRegistryAsync(token).ConfigureAwait(false);
                Assert.AreEqual(activeId, reopenedRegistry.ActiveRevisionId);
                if (consumer == "storage")
                {
                    var resumedModule = CreateModule(restarted, requestedUtc.AddSeconds(4));
                    await using var resumedLifetime = resumedModule.ConfigureAwait(false);
                    await resumedModule.InitializeAsync(configuration, token).ConfigureAwait(false);
                    for (var sequence = 3; sequence <= 61; sequence++)
                        await CaptureCadenceFrameAsync(restarted, resumedModule, liveId, sequence).ConfigureAwait(false);
                    var archiveFiles = Directory.GetFiles(archiveRoot, "*.bin", SearchOption.AllDirectories);
                    Assert.HasCount(61, archiveFiles);
                    Assert.IsTrue(archiveFiles.All(path => Path.GetFileName(Path.GetDirectoryName(path)) == nameof(FrameArtifactRole.AnnotatedPreview)));
                    Assert.IsFalse(Directory.Exists(Path.Combine(archiveRoot, "derived")));
                }
                if (occupiedPrediction)
                {
                    var replay = await ExecuteReplayAsync(restarted, new(captureId, liveId, rawId, Reason: "prediction variant collision"),
                        "replay-prediction-variant").ConfigureAwait(false);
                    Assert.AreEqual(liveId, replay.Execution.GraphRevisionId);
                    foreach (var (nodeId, liveOutput) in replayOutputs)
                    {
                        var replayed = replay.Nodes.Single(node => node.NodeId == nodeId).Outputs
                            .Single(output => output.Variant == liveOutput.Artifact.Variant);
                        Assert.AreEqual(liveOutput.ArtifactId, replayed.ArtifactId, nodeId);
                        Assert.AreEqual(liveOutput.OutputIdentitySha256, replayed.OutputIdentitySha256, nodeId);
                        Assert.AreEqual(liveOutput.Artifact.ChecksumSha256, replayed.PayloadSha256, nodeId);
                        Assert.IsFalse(replayed.Published, nodeId);
                    }

                    var nextUtc = requestedUtc.AddSeconds(2);
                    var module = CreateModule(restarted, nextUtc);
                    await using var moduleLifetime = module.ConfigureAwait(false);
                    await module.InitializeAsync(configuration, token).ConfigureAwait(false);
                    await CaptureCadenceFrameAsync(restarted, module, liveId, 2).ConfigureAwait(false);
                }
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
                if (configurePreset)
                {
                    Console.WriteLine($"Accepted fit on effective revision {liveId}; registry basis {activeId} after restart.");
                    var (receipt, capture) = await ConfigureSharedLegacyAstrometryAsync(root, configuration, restarted, selected, activeId, measurementId,
                        predictionId, combined ? 2 : 1,
                        fitCatalog, captureUtc => CreateModule(restarted, captureUtc),
                        requestedUtc.AddSeconds(4), token).ConfigureAwait(false);
                    presetReceipt = receipt;
                    if (combined)
                    {
                        await CheckCadenceEvidenceAsync(restarted, capture, DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
                        Assert.HasCount(3, Directory.GetFiles(archiveRoot, "*.bin", SearchOption.AllDirectories));
                    }
                }
            }
            if (presetReceipt is not null)
                await VerifySharedLegacyPresetReceiptAfterRestartAsync(root, configuration,
                    activeId, liveId, presetReceipt, token).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        async Task CaptureCadenceFrameAsync(ServiceProvider host, VirtualSkyCameraModule camera, string expectedRevisionId, int sequence)
        {
            var utc = requestedUtc.AddSeconds((sequence - 1) * 2);
            // Distinct exposure samples give each capture its own durable outputs.
            var receipt = await CaptureAndAdmitAsync(host, camera, utc, sequence).ConfigureAwait(false);
            Assert.AreEqual((long)sequence, receipt.Manifest.Descriptor.Capture.CaptureSequence);
            Assert.AreEqual(expectedRevisionId, receipt.Manifest.CaptureGraphRevisionId);
            await CompleteLiveAsync(host, configuration).ConfigureAwait(false);
            await CheckCadenceEvidenceAsync(host, receipt, sequence == 61 ? DurableProcessingNodeStatus.Completed :
                DurableProcessingNodeStatus.Skipped).ConfigureAwait(false);
        }

        async Task CheckCadenceEvidenceAsync(ServiceProvider host, RawCaptureReceipt receipt, DurableProcessingNodeStatus expectedFit)
        {
            var capture = receipt.Manifest.Descriptor.Capture;
            var rawSha = Sha256OfFile(receipt.StoredFrame.AbsolutePath);
            Assert.AreEqual(receipt.Manifest.Descriptor.Artifact.ChecksumSha256, rawSha);
            var store = host.GetRequiredService<SqliteCaptureProcessingStore>();
            var fit = await ReadCadenceNodeAsync("fit", expectedFit).ConfigureAwait(false);
            if (expectedFit == DurableProcessingNodeStatus.Skipped)
                Assert.AreEqual("astrometry.capture-cadence", fit.Reason);
            if (expectedFit == DurableProcessingNodeStatus.TerminalFailure)
                Assert.AreEqual("processing.optional-degraded", fit.Reason);
            var scene = (await ReadCadenceNodeAsync("projected-scene", DurableProcessingNodeStatus.Completed).ConfigureAwait(false)).Outputs.Single();
            var annotated = (await ReadCadenceNodeAsync("annotation", DurableProcessingNodeStatus.Completed).ConfigureAwait(false)).Outputs.Single();
            var prediction = (await ReadCadenceNodeAsync(predictionId, DurableProcessingNodeStatus.Completed)
                .ConfigureAwait(false)).Outputs.Single();
            var measured = (await ReadCadenceNodeAsync(fitMeasurementId, DurableProcessingNodeStatus.Completed).ConfigureAwait(false)).Outputs.Single();
            var archive = await ReadCadenceNodeAsync("archive", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
            Assert.IsEmpty(archive.Outputs, "Storage selects producer outputs and does not produce scientific artifacts.");
            Assert.Contains(scene.ArtifactId, annotated.Artifact.SourceArtifactIds!);
            Assert.DoesNotContain(prediction.ArtifactId, annotated.Artifact.SourceArtifactIds!);
            Assert.DoesNotContain(measured.ArtifactId, annotated.Artifact.SourceArtifactIds!);
            if (sharedAssociations)
            {
                var canonicalMeasurement = (await ReadCadenceNodeAsync(measurementId, DurableProcessingNodeStatus.Completed)
                    .ConfigureAwait(false)).Outputs.Single();
                Assert.Contains(canonicalMeasurement.ArtifactId, annotated.Artifact.SourceArtifactIds!);
            }
            var canonical = ProjectedSceneJson.Parse(await File.ReadAllBytesAsync(Path.Combine(root, scene.PayloadRelativePath), token)
                .ConfigureAwait(false)).Scene!;
            Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, canonical.Kind);
            Assert.IsTrue(canonical.Objects.Any(value => value.Id == "solar-system:Moon"));
            Assert.IsTrue(canonical.Segments.Any(value => value.ConstellationId == "CAS"));
            if (expectedFit == DurableProcessingNodeStatus.Completed)
            {
                Assert.HasCount(2, fit.Outputs);
                var solutionOutput = fit.Outputs.Single(value => value.Artifact.Recipe.Name == BuiltInProcessingRecipes.MeasuredAstrometry);
                var solution = MeasuredAstrometryJson.Parse(await File.ReadAllBytesAsync(Path.Combine(root, solutionOutput.PayloadRelativePath), token)
                    .ConfigureAwait(false));
                Assert.AreEqual(prediction.ContentIdentitySha256, solution.SceneIdentitySha256);
                Assert.AreEqual(measured.ArtifactId, solution.MeasuredArtifactId);
                Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.ReasonCode);
            }
            else Assert.IsEmpty(fit.Outputs);

            if (sharedFit)
            {
                var selected = fit.Outputs.Select(output => (Guid?)output.ArtifactId)
                    .Append(scene.ArtifactId).Append(annotated.ArtifactId).ToArray();
                Assert.IsNotNull(archive.Inputs);
                CollectionAssert.AreEquivalent(selected, archive.Inputs.Select(input => input.ArtifactId).ToArray());
                var image = Directory.GetFiles(archiveRoot, $"*-{annotated.ArtifactId:N}.bin", SearchOption.AllDirectories).Single();
                Assert.AreEqual(annotated.Artifact.ChecksumSha256, Sha256OfFile(image));
                Assert.IsTrue(File.Exists(Path.ChangeExtension(image, ".json")));
            }
            var standard = (await host.GetRequiredService<ICaptureLaneStore>().ReadBacklogsAsync(token).ConfigureAwait(false))
                .Single(lane => lane.Lane == "standard");
            Assert.AreEqual(0L, standard.QuarantineCount);
            Assert.AreEqual(0L, standard.PendingCount);
            if (capture.CaptureSequence <= 3 || capture.CaptureSequence >= 60)
                Console.WriteLine($"{consumer} sequence={capture.CaptureSequence} raw={rawSha} fit={fit.Status}/{fit.Reason} fitOutputs={fit.Outputs.Count} archive={archive.Status} annotation={annotated.Artifact.ChecksumSha256} terminal=0 quarantine={standard.QuarantineCount}");

            async Task<DurableProcessingNode> ReadCadenceNodeAsync(string nodeId, DurableProcessingNodeStatus status)
            {
                var node = await store.ReadNodeAsync(capture.CaptureId, nodeId, token).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.AreEqual(status, node.Status, $"{nodeId}: {node.Reason}");
                return node;
            }
        }

        async Task<RawCaptureReceipt> CaptureAndAdmitAsync(
            ServiceProvider provider, VirtualSkyCameraModule module, DateTimeOffset start, int sequence = 1)
        {
            var request = new CaptureRequest(start, TimeSpan.FromSeconds(2), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(sequence - 1), 1, null, null));
            var capture = await module.CaptureAsync(request, token).ConfigureAwait(false);
            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
                new CaptureLoopSubmission(request, capture, start, request.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(capture.Frame!.Metadata.Scene!.CaptureGraphRevisionId, receipt.Manifest.CaptureGraphRevisionId);
            return receipt;
        }
    }
}
