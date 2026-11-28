using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] MonoStackChannels = ["mono", "support"];
    private static readonly string[] ColorStackChannels = ["red", "green", "blue", "support"];

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task MeasuredDeepSky_DurableRelativeOnlyWindowPublishesInsufficientAfterReopen(CameraPixelFormat format)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-measured-deep-sky-runtime");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, format);
        var steps = profile.Pipeline.Steps.Select(step => step.Type == "ProjectedScene"
            ? step with { Options = JsonSerializer.SerializeToElement(new { assessMeasuredDeepSky = true }) }
            : step).ToArray();
        profile = profile with { Pipeline = profile.Pipeline with { Steps = steps } };
        var catalog = new CatalogSnapshotIdentity("openngc-unavailable-test", "1", "1", "1", new string('E', 64));
        var installed = new CuratedVirtualCatalog(catalog);
        try
        {
            Guid[] ids = new Guid[5];
            using (var provider = CenteredRuntimeProvider(root, clock, services => services.AddSingleton<ICelestialCatalog>(installed)))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeWideCatalog(start, active), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                for (var index = 0; index < ids.Length; index++)
                {
                    var capture = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                        start.AddSeconds(index * 2), 1, token, resources: resources).ConfigureAwait(false);
                    var admission = await ingress.AcceptCenteredAsync(active, capture.Submission, capture.Reservation, token).ConfigureAwait(false);
                    ids[index] = admission.Manifest.Descriptor.Capture.CaptureId;
                    await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                    await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                }
            }

            string baseId;
            string revisionId;
            string initialRevisionId;
            using (var worker = CenteredRuntimeProvider(root, clock))
            {
                await worker.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                await worker.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
                await worker.GetRequiredService<CenteredViewWindowPublicationWorker>().PublishReadyAsync(token).ConfigureAwait(false);
                await SiloInfrastructureFixture.SettleViewChangesAsync(worker, token).ConfigureAwait(false);
                var views = worker.GetRequiredService<SqliteCenteredViews>();
                var window = await worker.GetRequiredService<ICenteredWindowReader>().ReadAsync(ids[2], token).ConfigureAwait(false);
                Assert.IsNotNull(window);
                var slots = string.Join(", ", window.Slots.Select(slot => $"{slot.Offset}: {slot.State} / {slot.Reason}"));
                var registration = window.EvidenceArtifactId is { } evidenceId
                    ? await DescribeCenteredRegistrationAsync(worker, evidenceId, token).ConfigureAwait(false) : "no receipt";
                Assert.AreEqual(CenteredStackDisposition.Registered, window.StackDisposition,
                    $"{window.FailureReason}; frozen slots [{slots}]; registration [{registration}]");
                var archive = await worker.GetRequiredService<ICenteredViewLookup>().ReadArchiveAsync(ids[2], token).ConfigureAwait(false);
                var stack = Assert.ContainsSingle(archive.Where(view => view.Base.Definition.Kind == CenteredViewKind.Stack),
                    "The committed registration must produce a step-50 stack base.");
                var (scannedThrough, workItems) = await views.ReadMeasuredStackWorkAsync(0, long.MaxValue, 128, token).ConfigureAwait(false);
                var work = workItems.Single(item => item.BaseId == stack.Base.BaseId);
                Assert.IsGreaterThanOrEqualTo(work.EvidenceOrder, scannedThrough);
                var processor = worker.GetRequiredService<ICenteredMeasuredDeepSky>();
                var persistence = worker.GetRequiredService<CaptureProcessingPersistence>();
                var originalRevisions = await views.ReadRevisionsAsync(work.BaseId, 0, 8, token).ConfigureAwait(false);
                Assert.IsFalse(originalRevisions.Any(revision => revision.Layers.Any(layer => layer.Key.Kind == "measured-deep-sky")));
                var failedPublisher = new Mock<ICenteredViewPublisher>(MockBehavior.Strict);
                CenteredViewArtifact? retainedAfterFailure = null;
                failedPublisher.Setup(publisher => publisher.PublishLayerAsync(It.IsAny<string>(), It.IsAny<CenteredViewLayer>(),
                    It.IsAny<CancellationToken>())).Returns((string id, CenteredViewLayer layer, CancellationToken ct) =>
                {
                    if (id != work.BaseId)
                    {
                        return views.PublishLayerAsync(id, layer, ct);
                    }
                    retainedAfterFailure = layer.Evidence;
                    throw new IOException("Interrupt after retaining the assessment and before publishing the layer.");
                });
                using (var interrupted = new CenteredMeasuredDeepSky(views, failedPublisher.Object,
                    worker.GetRequiredService<ICenteredWindowRegistrationSource>(), persistence, new InMemoryCelestialCatalog([]),
                    worker.GetRequiredService<RawCaptureIngress>()))
                using (var scheduler = MakeScheduler(interrupted))
                {
                    await scheduler.RunOnceAsync(token).ConfigureAwait(false);
                    Assert.IsNotNull(retainedAfterFailure);
                    Assert.HasCount(originalRevisions.Count, await views.ReadRevisionsAsync(work.BaseId, 0, 8, token).ConfigureAwait(false));
                    await scheduler.RunOnceAsync(token).ConfigureAwait(false);
                    failedPublisher.Verify(publisher => publisher.PublishLayerAsync(work.BaseId, It.IsAny<CenteredViewLayer>(),
                        It.IsAny<CancellationToken>()), Times.Once);
                }
                using var resumed = MakeScheduler(processor);
                await resumed.RunOnceAsync(token).ConfigureAwait(false);
                await SiloInfrastructureFixture.SettleViewChangesAsync(worker, token).ConfigureAwait(false);
                var published = await views.ReadRevisionsAsync(work.BaseId, 0, 8, token).ConfigureAwait(false);
                Assert.HasCount(originalRevisions.Count + 1, published);
                var revision = published[^1];
                Assert.AreNotEqual(work.RegisteredRevisionId, revision.RevisionId);
                var repeated = await processor.ProcessAsync(work, token).ConfigureAwait(false);
                Assert.AreEqual(revision.RevisionId, repeated?.RevisionId);
                var measuredLayer = Assert.ContainsSingle(revision.Layers.Where(layer => layer.Key.Kind == "measured-deep-sky"));
                Assert.IsNull(measuredLayer.Drawable);
                Assert.AreEqual(retainedAfterFailure, measuredLayer.Evidence);
                baseId = work.BaseId;
                initialRevisionId = work.RegisteredRevisionId;
                revisionId = revision.RevisionId;

                CenteredMeasuredDeepSkyScheduler MakeScheduler(ICenteredMeasuredDeepSky service) =>
                    new(worker.GetRequiredService<IRawCaptureIngress>(), views, service, clock,
                        NullLogger<CenteredMeasuredDeepSkyScheduler>.Instance);
            }

            using var reopened = CenteredRuntimeProvider(root, clock);
            var measurement = await reopened.GetRequiredService<ICenteredMeasuredDeepSky>().ReadAsync(baseId, revisionId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(measurement);
            Assert.AreEqual(ids[2], measurement.ReferenceCaptureId);
            Assert.IsGreaterThanOrEqualTo(3, measurement.Contributors.Count);
            Assert.Contains(ids[2], measurement.Contributors);
            CollectionAssert.AreEqual(ids.Where(measurement.Contributors.Contains).ToArray(), measurement.Contributors.ToArray());
            Assert.IsNull(measurement.MappingSolutionSha256);
            Assert.AreEqual(catalog, measurement.Catalog);
            Assert.AreEqual(DeepSkyStackStatus.Insufficient, measurement.Status);
            Assert.AreEqual(DeepSkyStackReason.ReferenceMappingUnavailable, measurement.Reason);
            Assert.IsEmpty(measurement.Identifications);

            var viewIndex = reopened.GetRequiredService<ICenteredViewReadIndex>();
            var exact = await viewIndex.ReadRevisionAsync(baseId, revisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(exact);
            var definition = exact.Base.Definition;
            Assert.IsNotNull(definition.ClosedWindow);
            Assert.AreNotEqual(definition.Science.ArtifactId, definition.ClosedWindow.ArtifactId);
            Assert.AreEqual(definition.Science.ArtifactId, measurement.Stack.ArtifactId);
            Assert.AreEqual(definition.Science.PayloadSha256, measurement.Stack.PayloadSha256);
            Assert.AreEqual(definition.Window?.RegistrationPlanIdentitySha256, measurement.PlanSha256);

            var reader = reopened.GetRequiredService<CaptureProcessingPersistence>();
            var closure = await reader.ReadCenteredViewProductAsync(ids[2], definition.ClosedWindow, token).ConfigureAwait(false);
            Assert.AreEqual(definition.Window?.ClosureIdentitySha256, closure.ContentIdentitySha256);
            CollectionAssert.AreEqual(new[] { definition.Reference.Input.Raw.Artifact.ArtifactId, definition.Science.ArtifactId },
                closure.SourceArtifactIds!.ToArray());
            var frozen = await reader.ReadCenteredViewProductAsync(ids[2], definition.Science, token).ConfigureAwait(false);
            var planes = new List<ProcessingArtifact>();
            foreach (var plane in measurement.Planes)
            {
                var input = await reader.ReadCenteredViewProductAsync(ids[2],
                    new(plane.ArtifactId, plane.OutputIdentitySha256, plane.PayloadSha256), token).ConfigureAwait(false);
                Assert.AreEqual(plane.PayloadSha256, ProcessingIdentity.ComputePayloadSha256(input.Payload));
                planes.Add(input);
            }
            Assert.AreEqual(CenteredRegisteredStackEvidence.SupportMediaType, planes[^1].MediaType);
            if (format == CameraPixelFormat.BayerRggb16)
            {
                CollectionAssert.AreEqual(ColorStackChannels, measurement.Planes.Select(plane => plane.Channel).ToArray());
                Assert.HasCount(3, measurement.Planes.Take(3).Select(plane => plane.PayloadSha256).Distinct().ToArray());
            }
            else
            {
                CollectionAssert.AreEqual(MonoStackChannels, measurement.Planes.Select(plane => plane.Channel).ToArray());
                CollectionAssert.AreEqual(ids, measurement.Contributors.ToArray());
            }
            var request = MeasuredDeepSkyStackRecipe.CreateRequest(frozen, planes, catalog);
            var replay = await new ProcessingRecipeExecutor([new MeasuredDeepSkyStackRecipe(null)])
                .ExecuteAsync(request, token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, replay.Status, replay.ReasonCode);
            var repeatedProduct = Assert.ContainsSingle(replay.Products);
            var publishedAssessment = Assert.ContainsSingle(exact.Revision.Layers.Where(layer => layer.Key.Kind == "measured-deep-sky"));
            Assert.IsNotNull(publishedAssessment.Evidence);
            Assert.AreEqual(publishedAssessment.Evidence.IdentitySha256, repeatedProduct.OutputIdentitySha256);
            Assert.AreEqual(publishedAssessment.Evidence.PayloadSha256, repeatedProduct.ChecksumSha256);
            Assert.IsNull(await reopened.GetRequiredService<ICenteredMeasuredDeepSky>()
                .ReadAsync(baseId, initialRevisionId, token).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> DescribeCenteredRegistrationAsync(IServiceProvider services, Guid evidenceId, CancellationToken token)
    {
        var artifact = await ReadCenteredRegistrationAsync(services, evidenceId, token).ConfigureAwait(false);
        if (artifact is null)
        {
            return "receipt artifact unavailable";
        }
        var evidence = CenteredRegisteredStackEvidence.Read(artifact);
        return string.Join(", ", evidence.Sources.Select(source =>
            $"{source.Offset}: {source.Reason} / {source.PairCheck?.Status} / {source.MeasurementReason}"));
    }

    private static async Task<ProcessingProduct?> ReadCenteredRegistrationAsync(
        IServiceProvider services, Guid evidenceId, CancellationToken token)
    {
        var output = await services.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadOutputByArtifactIdAsync(evidenceId, token).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }
        return await services.GetRequiredService<CaptureProcessingPersistence>().ReadCenteredViewRetainedProductAsync(
            output.Capture.CaptureId, new(evidenceId, output.OutputIdentitySha256, output.Artifact.ChecksumSha256), token)
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MeasuredDeepSky_TwentySecondVirtualSkyRenderingHasNoReportablePixelCandidate()
    {
        const int size = 224;
        var start = new DateTimeOffset(2026, 11, 19, 7, 13, 0, TimeSpan.Zero);
        var exposure = TimeSpan.FromSeconds(20);
        var cadence = TimeSpan.FromSeconds(22);
        var midpoint = start + 2 * cadence + exposure / 2;
        var config = CreateConfig(CameraPixelFormat.Mono16, size, size) with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 1926,
                fixedSequenceStartUtc = start,
                illuminationMode = "ControlledNight"
            }))
        };
        var stars = Enumerable.Range(0, 24).Select(index =>
        {
            // An irregular field distinguishes translation from the equally plausible
            // rotated matches that a symmetric ring gives the independent fitter.
            var altitude = 15 + 22 * (index / 8) + (index * index * 17 % 13) - 6;
            var azimuth = 45 * (index % 8) + 14 * (index / 8) + (index * index * 13 % 27) - 13;
            var atTime = CoordinateTransforms.HorizontalToEquatorial(new(altitude, azimuth), midpoint, 35.347, -113.878);
            var sky = EquatorialPrecession.PrecessToJ2000(atTime, midpoint);
            var id = "field-star-" + index.ToString(CultureInfo.InvariantCulture);
            return new CelestialCatalogObject(id, id, sky.RightAscensionHours, sky.DeclinationDegrees, 2.2 + (index % 5) * .27);
        });
        var camera = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(),
            new InMemoryCelestialCatalog(stars), new ProjectedSceneStore());
        await using var cameraLease = camera.ConfigureAwait(false);
        await camera.InitializeAsync(config, TestContext.CancellationToken).ConfigureAwait(false);
        var images = new List<double[]>();
        var measurements = new List<StellarMeasurementResult>();
        var valid = Enumerable.Range(0, size * size).Select(index =>
        {
            var x = index % size + .5 - size / 2d;
            var y = index / size + .5 - size / 2d;
            return x * x + y * y < .98 * .98 * size * size / 4;
        }).ToArray();
        for (var slot = 0; slot < 5; slot++)
        {
            var result = await camera.CaptureAsync(new(start + slot * cadence, cadence, CaptureMode.Still,
                new(exposure, 1, null, null)), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(result.Frame);
            Assert.AreEqual(exposure, result.Frame.Metadata.Exposure);
            Assert.AreEqual(size * size * 2, result.Frame.PixelData.Length);
            var fingerprint = ProcessingIdentity.ComputePayloadSha256(result.Frame.PixelData);
            TestContext.WriteLine($"{slot} {result.Frame.TimestampUtc:O} {fingerprint}");
            var pixels = Enumerable.Range(0, size * size).Select(index =>
                (double)BinaryPrimitives.ReadUInt16LittleEndian(result.Frame.PixelData.Span[(index * 2)..])).ToArray();
            images.Add(pixels);
            measurements.Add(StellarSourceMeasurer.Measure(pixels, valid,
                pixels.Select(value => value == ushort.MaxValue).ToArray(), size, size,
                cancellationToken: TestContext.CancellationToken));
        }

        var registered = new LinearRegisteredMean(size, size, 1);
        for (var slot = 0; slot < 5; slot++)
        {
            var fit = slot == 2 ? null : DirectCentroidRegistration.Fit(measurements[slot].Detections,
                measurements[2].Detections, size, size, TestContext.CancellationToken);
            if (fit is not null)
            {
                Assert.AreEqual(DirectRegistrationStatus.Verified, fit.Check.Status,
                    $"Slot {slot}, detections {measurements[slot].Detections.Count}/{measurements[2].Detections.Count}");
                Assert.IsNotNull(fit.Map);
            }
            registered.Add(slot, new(size, size, valid, images[slot]), fit?.Map, TestContext.CancellationToken);
        }
        var stack = registered.Finish(TestContext.CancellationToken);
        var linear = stack.Codes.ToArray().Select(code => code == ushort.MaxValue ? -1f : code).ToArray();
        var extraction = ExtendedStackSourceExtractor.Extract(linear, stack.Support.Span, size, size, TestContext.CancellationToken);
        TestContext.WriteLine(JsonSerializer.Serialize(extraction));
        Assert.IsGreaterThan(0, extraction.SupportedPositions);
        Assert.IsFalse(extraction.LimitExceeded);
        Assert.IsFalse(extraction.Detections.Any(detection => detection.Rejection is null));
    }
}
