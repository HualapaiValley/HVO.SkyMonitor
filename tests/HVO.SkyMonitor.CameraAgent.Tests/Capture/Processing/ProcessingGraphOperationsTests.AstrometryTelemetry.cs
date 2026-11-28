using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("telemetry-first")]
    [DataRow("archive-first-migrated")]
    [DataRow("bad-pin")]
    [DataRow("prediction-io")]
    [DataRow("prediction-io-archive-first")]
    [DataRow("archive-failure")]
    public async Task RequiredTelemetryReportsUnavailableAstrometryAndKeepsSiloProducts(string scenario)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-astrometry-telemetry");
        var token = CancellationToken.None;
        var migrateLegacy = scenario == "archive-first-migrated";
        var invalidPin = scenario == "bad-pin";
        var failPrediction = scenario is "prediction-io" or "prediction-io-archive-first";
        var failArchive = scenario == "archive-failure";
        var archiveFirst = migrateLegacy || failArchive || scenario == "prediction-io-archive-first";
        try
        {
            var start = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
            var sceneUtc = start.AddSeconds(1);
            var location = DeploymentLocationSnapshot.Create("telemetry-site", 1, "test", null, DateTimeOffset.UnixEpoch,
                null, 35, -115, 1100, "UTC");
            var locations = new Mock<IDeploymentLocationStore>();
            locations.SetupGet(store => store.Active).Returns(location);
            locations.Setup(store => store.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>())).Returns(location);
#pragma warning disable CA5394 // Seeded synthetic sky, with no security use.
            var random = new Random(934817);
            var reference = CreateReferenceCatalog(stars: Enumerable.Range(0, 560).Select(index =>
                new CelestialCatalogObject($"SYN{index:0000}", $"Artificial {index}", random.NextDouble() * 24,
                    Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1.8 + 2.6 * random.NextDouble())).ToArray());
#pragma warning restore CA5394
            var referenceData = await reference.ReadAsync(4.5, 2500, token).ConfigureAwait(false);
            var stars = new InMemoryCelestialCatalog(referenceData.Stars);
            var catalog = new Mock<ICelestialCatalog>(MockBehavior.Strict);
            var metadata = catalog.As<ICelestialCatalogMetadataSource>();
            metadata.SetupGet(value => value.Metadata).Returns(referenceData.Metadata);
            metadata.SetupGet(value => value.PreprocessingVersion).Returns(referenceData.Provenance!.PreprocessingVersion);
            var predictionQueriesFailed = 0;
            var failCatalogQueries = false;
            catalog.Setup(value => value.QueryCandidatesAsync(It.IsAny<CatalogCandidateQuery>(), It.IsAny<CancellationToken>()))
                .Returns((CatalogCandidateQuery query, CancellationToken cancellation) =>
                {
                    if (failCatalogQueries)
                    {
                        predictionQueriesFailed++;
                        throw new IOException("Independent prediction catalog could not be read.");
                    }
                    return stars.QueryCandidatesAsync(query, cancellation);
                });

            var basic = CreateProjectedSceneConfiguration();
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var scene = basic.Pipeline.Steps.Single() with { Publication = durable };
            var config = basic with
            {
                Observatory = new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, "UTC"),
                DeploymentLocation = location,
                Rig = basic.Rig with
                {
                    Sensor = basic.Rig.Sensor with { WidthPixels = 512, HeightPixels = 512, ResponseMode = SensorResponseMode.Monochrome },
                    Optics = basic.Rig.Optics with
                    {
                        PrincipalPointX = 256,
                        PrincipalPointY = 256,
                        ImageCircleRadiusPixels = 232,
                        FocalLengthXPixels = 144,
                        FocalLengthYPixels = 144
                    },
                    Readout = new(new SensorCrop(0, 0, 512, 512), 1, 1, FrameBinningAlgorithm.IdentityV1,
                        CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1,
                        FrameLevelCodeSpace.StoredContainer, 0, ushort.MaxValue)
                },
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    fixedSceneUtc = sceneUtc,
                    magnitudeZeroElectronsPerSecond = 6000,
                    backgroundElectronsPerSecond = 100
                })),
                Pipeline = basic.Pipeline with
                {
                    Steps = [
                        scene,
                        new("Preview", "preview", DependsOn: ["$raw"],
                            Options: JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" })),
                        new("Annotation", "annotation", DependsOn: ["preview", "projected-scene"],
                            Options: JsonSerializer.SerializeToElement(new { requireProjectedSceneDependency = true })),
                        new("JpegEncoding", "jpeg", DependsOn: ["annotation"]),
                        new("Storage", "archive", DependsOn: ["jpeg", "annotation", "projected-scene"],
                            Options: JsonSerializer.SerializeToElement(new SiloCaptureProcessingStepOptions
                            { UpdateLatestFrame = false }))
                    ]
                }
            };
            var (generated, pin) = await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, 4.5, reference, token).ConfigureAwait(false);
            var fit = generated.Steps.Single(step => step.Id == "Astrometry");
            var measured = generated.Steps.Single(step => step.Id == "Measured");
            CaptureProcessingStepConfig telemetryStep = new("Telemetry", "telemetry", Order: 90,
                DependsOn: archiveFirst ? ["archive", "Astrometry"] : ["Astrometry"]);
            var archiveStep = generated.Steps.Single(step => step.Id == "archive");
            var steps = generated.Steps.Select(step => archiveFirst && step == archiveStep
                ? archiveStep with { DependsOn = ["jpeg", "annotation", "projected-scene", "Astrometry"] }
                : step).ToArray();
            var pipeline = generated with
            {
                Steps = migrateLegacy
                    ? [
                        .. steps.Where(step => step != fit && step != measured && step.Id != "AstrometryPrediction").Select(step => step == scene
                            ? scene with { Options = JsonSerializer.SerializeToElement(new { independentPrediction = true, maximumMagnitude = 4.5 }) }
                            : step),
                        measured with { DependsOn = ["projected-scene"], Options = null },
                        fit with
                        {
                            DependsOn = ["projected-scene", "Measured"],
                            Options = JsonSerializer.SerializeToElement(new
                            {
                                catalogSelection = pin, recipe = new { solver = new { maximumCatalogMagnitude = 4.5 } }
                            })
                        },
                        telemetryStep
                    ]
                    : [.. steps, telemetryStep]
            };

            // Model a changed installed catalog for the bad-pin sample. The graph retains its original pin.
            using var provider = CreateProvider(root, configureServices: services =>
            {
                services.AddSingleton(catalog.Object);
                services.AddSingleton(locations.Object);
                services.AddSingleton(invalidPin ? CreateReferenceCatalog("replacement", stars: referenceData.Stars) : reference);
                if (failArchive)
                    services.AddSingleton<ICaptureProcessingFaultInjector>(new StorageSelectionFailure());
            });
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var draft = await operations.CreateRevisionAsync("telemetry", "1", pipeline, "create", "owner", null, token).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "owner", null, token).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "owner", null, token).ConfigureAwait(false);

            var lanes = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(lane => lane.Name == "standard");
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(value => value.Lane == "standard");
            var telemetry = provider.GetRequiredService<ICaptureTelemetryProvider>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            await lanes.InitializeLanesAsync(token).ConfigureAwait(false);
            ProcessingGraphRevisionSnapshot? frozenRevision = null;
            var frameCount = failArchive || invalidPin || failPrediction ? 1 : 61;
            for (var sequence = 1; sequence <= frameCount; sequence++)
            {
                var utc = start.AddSeconds((sequence - 1) * 2);
                var camera = new VirtualSkyCameraModule(new ExposureTestTimeProvider(utc, autoAdvance: true), catalog.Object,
                    provider.GetRequiredService<IProjectedSceneStore>(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>(),
                    graphSelection: new VirtualSkyGraphSelectionProvider(operations, enabled: true));
                await using var cameraLifetime = camera.ConfigureAwait(false);
                await camera.InitializeAsync(config, token).ConfigureAwait(false);
                var request = new CaptureRequest(utc, TimeSpan.FromSeconds(2), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(sequence - 1), 1, null, null));
                var capture = await camera.CaptureAsync(request, token).ConfigureAwait(false);
                var raw = await ingress.AcceptAsync(config, new(request, capture, utc, request.TargetInterval, TimeSpan.Zero), token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(raw);
                Assert.AreEqual(RawIngressOutcome.Committed, raw.Outcome);
                Assert.AreEqual((long)sequence, raw.Manifest.Descriptor.Capture.CaptureSequence);
                Assert.AreEqual(migrateLegacy, draft.RevisionId != raw.Manifest.CaptureGraphRevisionId);
                var revision = await operations.ReadRevisionSnapshotAsync(raw.Manifest.CaptureGraphRevisionId!, token).ConfigureAwait(false);
                if (frozenRevision is null)
                {
                    frozenRevision = revision;
                    Assert.IsTrue(revision.Pipeline.Steps.Single(step => step.Id == "telemetry").Required);
                    Assert.IsTrue(revision.Pipeline.Steps.Single(step => step.Id == "archive").Required);
                    Assert.IsFalse(revision.Pipeline.Steps.Single(step => step.Id == "Astrometry").Required);
                    var nodeOrder = revision.Nodes.Select(node => node.NodeId).ToList();
                    Assert.IsTrue(nodeOrder.IndexOf("Astrometry") < nodeOrder.IndexOf("telemetry"));
                    Assert.AreEqual(archiveFirst, nodeOrder.IndexOf("archive") < nodeOrder.IndexOf("telemetry"));
                    var runtime = provider.GetRequiredService<ICaptureProcessingPipelineFactory>()
                        .CreateRetainedGraph(config with { Pipeline = revision.Pipeline });
                    try
                    {
                        Assert.IsTrue(revision.IsCompiledBy(runtime));
                        var archiveNode = runtime.Nodes.Single(node => node.Id == "archive");
                        Assert.Contains("jpeg", archiveNode.Dependencies);
                        Assert.Contains("annotation", archiveNode.Dependencies);
                        Assert.Contains("projected-scene", archiveNode.Dependencies);
                        Assert.IsFalse(archiveNode.OptionalDependencies?.Contains("jpeg") == true);
                        Assert.IsFalse(archiveNode.OptionalDependencies?.Contains("annotation") == true);
                        Assert.IsFalse(archiveNode.OptionalDependencies?.Contains("projected-scene") == true);
                    }
                    finally { runtime.DisposeSteps(); }
                }
                Assert.AreEqual(frozenRevision.State.RevisionId, revision.State.RevisionId);
                var executionId = await operations.ReadLiveExecutionIdAsync(raw.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false);
                Assert.IsNotNull(executionId);
                var frozenBefore = await ReadFrozenExecutionJsonAsync(root, executionId.Value).ConfigureAwait(false);
                var captureId = raw.Manifest.Descriptor.Capture.CaptureId;
                var lease = await lanes.ClaimAsync(standard, "telemetry-cadence", config, token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                // The real camera has staged its canonical scene and raw admission has committed.
                failCatalogQueries = failPrediction;
                var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
                if (result.Outcome == CaptureLaneHandlerOutcome.Completed)
                    await lanes.CompleteAsync(lease, token).ConfigureAwait(false);
                else
                    await lanes.FailAsync(lease, result, token).ConfigureAwait(false);
                var solved = await store.ReadNodeAsync(captureId, "Astrometry", token).ConfigureAwait(false);
                var observed = await store.ReadNodeAsync(captureId, "telemetry", token).ConfigureAwait(false);
                var archive = await store.ReadNodeAsync(captureId, "archive", token).ConfigureAwait(false);
                await SiloInfrastructureFixture.SettleRawAdmissionsAsync(provider, token).ConfigureAwait(false);
                var rawSha = await ReadArtifactShaAsync(provider, raw.Manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
                Assert.AreEqual(SiloCenteredViews.LowerHex(raw.Manifest.Descriptor.Artifact.ChecksumSha256), rawSha);
                var archived = archive?.Status == DurableProcessingNodeStatus.Completed;
                var stepTelemetry = telemetry.Latest?.ProcessingSteps.SingleOrDefault(step => step.Name == "Astrometry");
                Assert.IsNotNull(solved);
                var standardBacklog = (await lanes.ReadBacklogsAsync(token).ConfigureAwait(false)).Single(lane => lane.Lane == "standard");
                if (sequence is 1 or 2 or 61)
                    Console.WriteLine($"{scenario} revision={raw.Manifest.CaptureGraphRevisionId} sequence={sequence} raw={rawSha} fit={solved.Status}/{solved.Reason} telemetry={observed?.Status}/{observed?.Reason} reported={stepTelemetry?.ErrorMessage} archive={archive?.Status} lane={result.Outcome}/{result.Reason} quarantined={standardBacklog.QuarantineCount}");
                CollectionAssert.AreEqual(frozenBefore, await ReadFrozenExecutionJsonAsync(root, executionId.Value).ConfigureAwait(false));
                if (failArchive)
                {
                    Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
                    Assert.AreEqual("processing.step-exception", result.Reason);
                    Assert.AreEqual(DurableProcessingNodeStatus.RetryableFailure, archive?.Status);
                    Assert.IsNull(observed);
                    Assert.IsNull(stepTelemetry);
                    continue;
                }

                var sampled = sequence is 1 or 61;
                var expectedFit = failPrediction ? DurableProcessingNodeStatus.Skipped :
                    invalidPin ? DurableProcessingNodeStatus.TerminalFailure :
                    sampled ? DurableProcessingNodeStatus.Completed : DurableProcessingNodeStatus.Skipped;
                var expectedReason = failPrediction ? "processing.dependency-unavailable" :
                    invalidPin ? "processing.optional-degraded" : sampled ? null : "astrometry.capture-cadence";
                Assert.AreEqual(expectedFit, solved.Status);
                Assert.AreEqual(expectedReason, solved.Reason);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome,
                    "Required Telemetry must observe the optional fit while independent Storage completes.");
                Assert.AreEqual(0L, standardBacklog.QuarantineCount);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, observed?.Status);
                Assert.IsTrue(archived);
                var annotated = (await store.ReadNodeAsync(captureId, "annotation", token).ConfigureAwait(false))!.Outputs.Single();
                var canonical = (await store.ReadNodeAsync(captureId, "projected-scene", token).ConfigureAwait(false))!.Outputs.Single();
                var inputs = archive!.Inputs;
                Assert.IsNotNull(inputs);
                Assert.IsTrue(inputs.Any(input => input.ArtifactId == annotated.ArtifactId));
                Assert.IsTrue(inputs.Any(input => input.ArtifactId == canonical.ArtifactId));
                var archiveSha = await ReadArtifactShaAsync(provider, annotated.ArtifactId).ConfigureAwait(false);
                Assert.AreEqual(annotated.Artifact.ChecksumSha256, archiveSha);
                if (expectedFit == DurableProcessingNodeStatus.Completed)
                {
                    var solution = solved.Outputs.Single(output => output.ProductSchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
                    var assessment = MeasuredAstrometryJson.Parse(await SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, solution.ArtifactId, token).ConfigureAwait(false)).Assessment;
                    Assert.IsTrue(assessment.HasMeasuredMapping, assessment.ReasonCode);
                }
                else Assert.IsEmpty(solved.Outputs);
                if (sequence is 1 or 2 or 61)
                    Console.WriteLine($"{scenario} sequence={sequence} archiveSha256={archiveSha} sceneArtifact={canonical.ArtifactId:N} frozenPlanSha256={Convert.ToHexString(SHA256.HashData(frozenBefore))}");

                if (failPrediction)
                {
                    Assert.AreEqual(1, predictionQueriesFailed);
                    var prediction = await store.ReadNodeAsync(captureId, "AstrometryPrediction", token).ConfigureAwait(false);
                    Assert.IsNotNull(prediction);
                    Assert.AreEqual(DurableProcessingNodeStatus.TerminalFailure, prediction.Status);
                    Assert.AreEqual("processing.optional-degraded", prediction.Reason);
                    var journal = await operations.ReadExecutionDetailAsync(executionId.Value, token).ConfigureAwait(false);
                    Assert.IsNotNull(journal);
                    Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, journal.Execution.Status);
                    var fitNode = journal.Nodes.Single(node => node.NodeId == solved.NodeId);
                    var attempt = fitNode.Attempts.Single();
                    Assert.AreEqual("Skipped", fitNode.Status, ignoreCase: true);
                    Assert.AreEqual(expectedReason, fitNode.Reason);
                    Assert.AreEqual(ProcessingOutcomeStatus.Skipped, attempt.Outcome);
                    Assert.AreEqual(expectedReason, attempt.Reason);
                    Console.WriteLine($"{scenario} execution={executionId} stored={fitNode.NodeId}/{fitNode.Status}/{fitNode.Reason} published={stepTelemetry?.Name ?? "<missing>"}/{stepTelemetry?.ErrorMessage}");
                }

                Assert.IsNotNull(stepTelemetry, "Telemetry must report the persisted fit outcome.");
                Assert.AreEqual(solved.NodeId, stepTelemetry.Name);
                Assert.AreEqual(expectedFit == DurableProcessingNodeStatus.Completed, stepTelemetry.Succeeded);
                Assert.AreEqual(expectedReason, stepTelemetry.ErrorMessage);
                if (failPrediction)
                {
                    Assert.AreEqual(TimeSpan.Zero, stepTelemetry.Duration);
                    var stepsReported = telemetry.Latest!.ProcessingSteps;
                    CollectionAssert.AreEquivalent(
                        archiveFirst ? new[] { "archive", solved.NodeId } : [solved.NodeId],
                        stepsReported.Select(step => step.Name).ToArray());
                    const string inputSchema = "capture-processing-telemetry-input-v1";
                    var canonicalInput = observed!.Inputs!.Single(input => input.Name == "dependency-telemetry");
                    Assert.AreEqual(inputSchema, canonicalInput.SchemaVersion);
                    Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(new
                    {
                        schemaVersion = inputSchema,
                        steps = stepsReported.Select(static step => new
                        {
                            step.Name,
                            durationTicks = step.Duration.Ticks,
                            step.Succeeded,
                            step.ErrorMessage
                        })
                    })), canonicalInput.IdentitySha256);
                    Console.WriteLine($"{scenario} dependencyTelemetrySha256={canonicalInput.IdentitySha256}");
                }
            }
            Assert.IsNotNull(frozenRevision);
            var persisted = await operations.ReadRevisionSnapshotAsync(frozenRevision.State.RevisionId, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(frozenRevision.PipelineJson, persisted.PipelineJson);
            CollectionAssert.AreEqual(frozenRevision.DefinitionJson, persisted.DefinitionJson);
            CollectionAssert.AreEqual(frozenRevision.FrozenPlanJson, persisted.FrozenPlanJson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StorageSelectionFailure : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.BeforeNodeExecution && nodeId == "archive")
                throw new IOException("Simulated interruption while selecting retained storage inputs.");
        }
    }
}
