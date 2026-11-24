using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    private static readonly string ScienceHash = new('A', 64);
    private static readonly string[] DeepSkyLayerKinds = ["scene-deep-sky", "measured-deep-sky"];
    private static readonly JsonSerializerOptions MeasuredAssessmentJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    public async Task MeasuredDeepSkyLayerUsesExactPreviewTransformAndRemainsIndependentOfExpectedLayer()
    {
        var token = CancellationToken.None;
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(new byte[96 * 96 * 2], 96, 96).ConfigureAwait(false);
        var neighbor = await fixture.AddRawAsync(new byte[96 * 96 * 2], 96, 96).ConfigureAwait(false);
        var descriptor = raw.Manifest.Descriptor;
        var scope = new CenteredViewStream(new(descriptor.Capture.AgentId, "camera-a"), Guid.NewGuid(), "centered");
        var reference = DeepSkyExposure(scope, descriptor);
        var later = DeepSkyExposure(scope, neighbor.Manifest.Descriptor);
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var publisher = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var rawArtifact = new ProcessingArtifact(raw.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
            descriptor.Layout, raw.Payload, descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor));
        var (previewProduct, preview) = CenteredViewPreviewEncoder.Encode([rawArtifact], descriptor.Layout,
            CenteredViewImageSource.RawFallback, null, null, new(MaximumDimension: 40, Crop: new(16, 24, 80, 48)), token);
        await persistence.PersistCenteredViewProductAsync(descriptor, previewProduct, token).ConfigureAwait(false);

        var pin = DeepSkyDummyPin();
        var definition = DeepSkyStackDefinition(scope, reference, later, preview, pin);
        var time = reference.ExposureEndedUtc;
        var assessment = new MeasuredDeepSkyStackV1(MeasuredDeepSkyStackV1.CurrentSchemaVersion,
            new("evidence", pin.ArtifactId, pin.IdentitySha256, pin.PayloadSha256), ScienceHash, ScienceHash, ScienceHash,
            reference.Capture.CaptureId, time, ScienceHash, [reference.Capture.CaptureId, later.Capture.CaptureId], [],
            96, 96, CenteredRegisteredStackEvidence.LinearUnits, new("openngc", "1", "1", "1", ScienceHash),
            ScienceHash, ScienceHash, .25, DeepSkyStackStatus.Qualified, DeepSkyStackReason.QualifiedAssociations,
            new(20, 15, 1, 0, 0, false, [new(17, new(42, 44), 3, 950, 1000, 130, 7.3, 31, null)]),
            1, 1, [new(17, DeepSkyIdentificationStatus.Qualified, "NGC0224", "M31", new(42.2, 43.9), .22, 1)]);
        var assessmentRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            MeasuredDeepSkyStackRecipe.RecipeName, "1.0.0", MeasuredDeepSkyStackRecipe.AlgorithmVersion, JsonSerializer.SerializeToElement(new { })));
        assessment = assessment with { AssessmentRecipeSha256 = assessmentRecipe.IdentitySha256 };
        var assessmentPin = await StoreAssessmentAsync(assessment, "test-measured").ConfigureAwait(false);
        var expectedPayload = PresentationLayerPayloadJson.Create(ProcessingIdentity.ComputePayloadSha256(rawArtifact.Payload), 40, 24,
            markers: [new(new(8, 8), 4, new(225, 180, 10))]);
        var (expectedEvidence, expectedDrawable) = await StoreDrawingAsync(expectedPayload, rawArtifact, "scene-deep-sky", 45, true).ConfigureAwait(false);
        var expected = new CenteredViewLayer(new("scene-deep-sky", CenteredViewLayerScope.Reference, reference.Capture.CaptureId),
            1, expectedEvidence, time, CenteredViewReadiness.Ready, null, expectedDrawable);
        var initial = await publisher.RegisterBaseAsync(definition, [expected], token).ConfigureAwait(false);
        var baseId = initial.Base.BaseId;
        var staged = CenteredMeasuredDeepSkyWork.FromRegisteredStack(initial);
        Assert.IsNotNull(staged);
        Assert.AreEqual(initial.Revision.Order, staged.EvidenceOrder);
        Assert.AreEqual(initial.Revision.RevisionId, staged.RegisteredRevisionId);
        var firstPage = await publisher.ReadMeasuredStackWorkAsync(0, long.MaxValue, 4, token).ConfigureAwait(false);
        Assert.AreEqual(staged, Assert.ContainsSingle(firstPage.Work));
        Assert.AreEqual(staged.EvidenceOrder, firstPage.ScannedThrough);

        var (qualified, payload) = CenteredMeasuredDeepSky.CreateLayer(initial.Base, assessment, assessmentPin, staged.EvidenceOrder);
        Assert.IsNotNull(payload);
        Assert.AreEqual(new PixelPoint(13, 10), Assert.ContainsSingle(payload.Markers).Center);
        Assert.AreEqual("M31", Assert.ContainsSingle(payload.TextBlocks).Lines.Single());
        var measuredSource = await persistence.ReadCenteredViewProductAsync(descriptor.Capture.CaptureId, assessmentPin, token)
            .ConfigureAwait(false);
        Assert.AreEqual(assessmentPin.PayloadSha256, measuredSource.ContentIdentitySha256);
        var (drawablePin, drawable) = await StoreDrawingAsync(payload, measuredSource, qualified.Key.Kind, 65, false)
            .ConfigureAwait(false);
        var published = await publisher.PublishLayerAsync(baseId, qualified with { Drawable = drawable }, token).ConfigureAwait(false);
        Assert.IsNotNull(published);
        var measuredRevision = published.Revision.RevisionId;

        var negative = assessment with { Status = DeepSkyStackStatus.AssessedEmpty, Reason = DeepSkyStackReason.NoEligibleSources, Identifications = [] };
        var negativePin = await StoreAssessmentAsync(negative, "test-empty").ConfigureAwait(false);
        var (empty, emptyPayload) = CenteredMeasuredDeepSky.CreateLayer(initial.Base, negative, negativePin, staged.EvidenceOrder + 2);
        Assert.IsNull(emptyPayload);
        Assert.AreEqual(CenteredViewReadiness.AssessedEmpty, empty.Readiness);
        var latest = await publisher.PublishLayerAsync(baseId, empty, token).ConfigureAwait(false);
        Assert.IsNotNull(latest);

        var reopened = new SqliteCenteredViews(new SqliteCaptureProcessingStore(options), TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var reader = new CenteredViewPresentationReader(reopened, fixture.Service);
        using var measuredReader = new CenteredMeasuredDeepSky(reopened, reopened,
            new UnusedRegistrationSource(), persistence, new InMemoryCelestialCatalog([]), null!);
        var afterPublication = await reopened.ReadMeasuredStackWorkAsync(0, long.MaxValue, 4, token).ConfigureAwait(false);
        Assert.IsEmpty(afterPublication.Work);
        Assert.AreEqual(latest.Revision.Order, afterPublication.ScannedThrough);
        // An enriched revision cannot masquerade as the registered scheduler input.
        Assert.IsNull(await measuredReader.ProcessAsync(new(baseId, measuredRevision, published.Revision.Order), token)
            .ConfigureAwait(false));
        Assert.IsNull(await measuredReader.ReadAsync(baseId, initial.Revision.RevisionId, token).ConfigureAwait(false));
        var reopenedQualified = await measuredReader.ReadAsync(baseId, measuredRevision, token).ConfigureAwait(false);
        Assert.IsNotNull(reopenedQualified);
        Assert.AreEqual("NGC0224", Assert.ContainsSingle(reopenedQualified.Identifications).CanonicalId);
        var retainedDrawing = await persistence.ReadCenteredViewProductAsync(descriptor.Capture.CaptureId, drawablePin, token)
            .ConfigureAwait(false);
        Assert.AreEqual(assessmentPin.ArtifactId, Assert.ContainsSingle(retainedDrawing.SourceArtifactIds!));
        Assert.AreEqual(assessmentPin.PayloadSha256, PresentationLayerPayloadJson.Parse(retainedDrawing.Payload).Payload?.SourceIdentitySha256);
        var reopenedNegative = await measuredReader.ReadAsync(baseId, latest.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.IsNotNull(reopenedNegative);
        Assert.AreEqual(DeepSkyStackStatus.AssessedEmpty, reopenedNegative.Status);
        var drawing = await reader.ReadAsync(baseId, measuredRevision, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, drawing.Status);
        Assert.IsNotNull(drawing.Presentation);
        CollectionAssert.AreEqual(DeepSkyLayerKinds, drawing.Presentation.Layers.Select(layer => layer.Kind).ToArray());
        Assert.IsTrue(drawing.Presentation.Layers[0].EnabledByDefault);
        Assert.IsFalse(drawing.Presentation.Layers[1].EnabledByDefault);
        var selected = drawing.Presentation.Layers[1].IdentitySha256;
        var saved = await reopened.SaveAsync("reader", Guid.NewGuid(), baseId, measuredRevision, [selected], token).ConfigureAwait(false);
        Assert.IsNotNull(saved);
        CollectionAssert.AreEqual(new[] { selected }, saved.EnabledLayerIds.ToArray());
        var svg = Encoding.UTF8.GetString(drawing.Presentation.Svg.Span);
        StringAssert.Contains(svg, "data-layer-kind=\"scene-deep-sky\"", StringComparison.Ordinal);
        StringAssert.Contains(svg, "data-layer-kind=\"measured-deep-sky\"", StringComparison.Ordinal);
        foreach (var revision in new[] { initial.Revision.RevisionId, latest.Revision.RevisionId })
        {
            var withoutMeasured = await reader.ReadAsync(baseId, revision, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, withoutMeasured.Status);
            Assert.AreEqual("scene-deep-sky", Assert.ContainsSingle(withoutMeasured.Presentation!.Layers).Kind);
        }

        // Reusing an existing artifact ID with the other layer's immutable content identity must fail at readback.
        var mismatchedDrawable = LayeredPresentationJson.CreateLayer(qualified.Key.Kind,
            drawable.SourceProduct with { ArtifactId = expectedEvidence.ArtifactId }, null,
            PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
            "deep-sky-test-v1", 65, PresentationBlendMode.Normal, 1_000_000, false, JsonSerializer.SerializeToElement(new { }));
        var mismatched = await publisher.PublishLayerAsync(baseId,
            qualified with { EvidenceOrder = staged.EvidenceOrder + 3, Drawable = mismatchedDrawable }, token).ConfigureAwait(false);
        Assert.IsNotNull(mismatched);
        var refused = await reader.ReadAsync(baseId, mismatched.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Malformed, refused.Status);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found,
            (await reader.ReadAsync(baseId, measuredRevision, token).ConfigureAwait(false)).Status);

        var opened = await reader.OpenPreviewAsync(baseId, measuredRevision, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
        Assert.IsNotNull(opened.Content);
        await using var lease = opened.Content.ConfigureAwait(false);
        var bytes = new byte[(int)opened.Content.ByteLength];
        await opened.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Assert.AreEqual(preview.PayloadSha256, ProcessingIdentity.ComputePayloadSha256(bytes));
        var image = JpegImageCodec.DecodeJpeg(bytes, token);
        Assert.AreEqual(40, image.Width);
        Assert.AreEqual(24, image.Height);

        async ValueTask<CenteredViewArtifact> StoreAssessmentAsync(MeasuredDeepSkyStackV1 value, string variant)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
                JsonSerializer.SerializeToElement(value, MeasuredAssessmentJson)));
            var checksum = ProcessingIdentity.ComputePayloadSha256(bytes);
            Guid[] parents = [pin.ArtifactId];
            var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, variant, assessmentRecipe.IdentitySha256, parents);
            var product = new ProcessingProduct(FrameArtifactRole.Metadata, variant, identity, MeasuredDeepSkyStackV1.MediaType,
                null, bytes, checksum, assessmentRecipe, [new(MeasuredDeepSkyStackRecipe.RecipeName, MeasuredDeepSkyStackRecipe.AlgorithmVersion)],
                parents, rawArtifact.Integration, rawArtifact.Compatibility)
            { Kind = ProcessingProductKind.Metadata, SchemaVersion = MeasuredDeepSkyStackV1.CurrentSchemaVersion, ContentIdentitySha256 = checksum };
            await persistence.PersistCenteredViewProductAsync(descriptor, product, token).ConfigureAwait(false);
            return new(ProcessingIdentity.CreateArtifactId(identity), identity, checksum);
        }

        async ValueTask<(CenteredViewArtifact, PresentationLayerV1)> StoreDrawingAsync(
            PresentationLayerPayloadV1 layerPayload, ProcessingArtifact source, string kind, int order, bool enabled)
        {
            var product = PresentationProcessingProducts.CreateLayerProduct(layerPayload, kind, [source], "deep-sky-test-v1");
            await persistence.PersistCenteredViewProductAsync(descriptor, product, token).ConfigureAwait(false);
            var artifactPin = new CenteredViewArtifact(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                product.OutputIdentitySha256, product.ChecksumSha256);
            var layer = LayeredPresentationJson.CreateLayer(kind,
                new(artifactPin.ArtifactId, layerPayload.ContentIdentitySha256, product.MediaType, preview.Product.Compatibility),
                null, PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
                "deep-sky-test-v1", order, PresentationBlendMode.Normal, 1_000_000, enabled, JsonSerializer.SerializeToElement(new { }));
            return (artifactPin, layer);
        }
    }

    [TestMethod]
    public async Task MeasuredDeepSkySchedulerInitializesRawJournalBeforeFirstDiscovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-measured-scheduler-startup");
        try
        {
            var processingSchema = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var provider = ProcessingGraphOperationsTests.CreateProvider(root, configureServices: services =>
                services.AddSingleton<ICaptureProcessingFaultInjector>(new SignalProcessingSchemaCommit(processingSchema))
                    .AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([])));
            var worker = provider.GetRequiredService<CenteredMeasuredDeepSkyScheduler>();
            Assert.IsFalse(File.Exists(Path.Combine(root, "journal", "raw-ingress.db")));

            await worker.StartAsync(token).ConfigureAwait(false);
            try
            {
                var execution = worker.ExecuteTask!;
                // No other worker initializes this root. Observe the schema transaction initiated by the hosted poll.
                var first = await Task.WhenAny(processingSchema.Task, execution).WaitAsync(token).ConfigureAwait(false);
                await first.ConfigureAwait(false);
                Assert.AreSame(processingSchema.Task, first);
                var views = provider.GetRequiredService<SqliteCenteredViews>();
                Assert.AreEqual(0L, await views.ReadMeasuredStackRecoveryOrderAsync(128, token).ConfigureAwait(false));
                Assert.IsFalse(execution.IsCompleted);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MeasuredDeepSkySchedulerPagesAcrossGraphChangesAndDefersFailures()
    {
        var token = CancellationToken.None;
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(new byte[96 * 96 * 2], 96, 96).ConfigureAwait(false);
        var next = await fixture.AddRawAsync(new byte[96 * 96 * 2], 96, 96).ConfigureAwait(false);
        var last = await fixture.AddRawAsync(new byte[96 * 96 * 2], 96, 96).ConfigureAwait(false);
        var descriptor = raw.Manifest.Descriptor;
        var scope = new CenteredViewStream(new(descriptor.Capture.AgentId, "camera-a"), Guid.NewGuid(), "earlier-graph");
        var reference = DeepSkyExposure(scope, descriptor);
        var neighbor = DeepSkyExposure(scope, next.Manifest.Descriptor);
        var artifact = new ProcessingArtifact(raw.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
            descriptor.Layout, raw.Payload, descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor));
        var (_, preview) = CenteredViewPreviewEncoder.Encode([artifact], descriptor.Layout,
            CenteredViewImageSource.RawFallback, null, null, new(), token);
        var definition = DeepSkyStackDefinition(scope, reference, neighbor, preview, DeepSkyDummyPin());
        var clock = new MeasuredSchedulerClock(reference.ExposureEndedUtc);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, clock, NullLogger<SqliteCenteredViews>.Instance);
        var obsolete = await views.RegisterBaseAsync(definition with { Window = null }, [], token).ConfigureAwait(false);
        var first = await views.RegisterBaseAsync(
            DeepSkyStackDefinition(scope with { GenerationId = "first-graph" }, neighbor, reference, preview, DeepSkyDummyPin()),
            [], token).ConfigureAwait(false);
        var empty = await views.ReadMeasuredStackWorkAsync(0, long.MaxValue, 1, token).ConfigureAwait(false);
        Assert.IsEmpty(empty.Work);
        Assert.AreEqual(obsolete.Revision.Order, empty.ScannedThrough);
        var nextPage = await views.ReadMeasuredStackWorkAsync(empty.ScannedThrough, long.MaxValue, 1, token).ConfigureAwait(false);
        Assert.AreEqual(first.Base.BaseId, Assert.ContainsSingle(nextPage.Work).BaseId);
        Assert.AreEqual(first.Revision.Order, nextPage.ScannedThrough);
        Assert.AreEqual(obsolete.Revision.Order, await views.ReadMeasuredStackRecoveryOrderAsync(1, token).ConfigureAwait(false));

        var service = new Mock<ICenteredMeasuredDeepSky>(MockBehavior.Strict);
        var ingress = new Mock<IRawCaptureIngress>(MockBehavior.Strict);

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        service.SetupSequence(assessment => assessment.ProcessAsync(
            It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == first.Base.BaseId),
            It.IsAny<CancellationToken>()))
            .Throws(new IOException("Storage unavailable."))
            .Returns(PublishVerifiedDispositionAsync);
        using var worker = CreateScheduler();
        await worker.RunOnceAsync(token).ConfigureAwait(false);
        var later = await views.RegisterBaseAsync(DeepSkyStackDefinition(scope with { GenerationId = "later-graph" },
            DeepSkyExposure(scope, last.Manifest.Descriptor), reference, preview, DeepSkyDummyPin()),
            [], token).ConfigureAwait(false);
        service.Setup(assessment => assessment.ProcessAsync(
            It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == later.Base.BaseId),
            It.IsAny<CancellationToken>())).Returns(async (CenteredMeasuredDeepSkyWork _, CancellationToken ct) =>
        {
            await cancelled.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return null;
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunOnceAsync(cancelled.Token).AsTask()).ConfigureAwait(false);
        Assert.HasCount(1, await views.ReadRevisionsAsync(first.Base.BaseId, 0, 4, token).ConfigureAwait(false));
        Assert.HasCount(1, await views.ReadRevisionsAsync(later.Base.BaseId, 0, 4, token).ConfigureAwait(false));
        service.Verify(assessment => assessment.ProcessAsync(It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == first.Base.BaseId),
            It.IsAny<CancellationToken>()), Times.Once);

        service.Setup(assessment => assessment.ProcessAsync(
            It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == later.Base.BaseId),
            It.IsAny<CancellationToken>())).Throws(new InvalidDataException("Admitted profile could not be verified."));
        clock.UtcNow = clock.UtcNow.AddSeconds(15);
        await worker.RunOnceAsync(token).ConfigureAwait(false);
        var revisions = await views.ReadRevisionsAsync(first.Base.BaseId, 0, 4, token).ConfigureAwait(false);
        Assert.HasCount(2, revisions);
        var terminal = Assert.ContainsSingle(revisions[^1].Layers);
        Assert.AreEqual(CenteredViewReadiness.Incompatible, terminal.Readiness);
        Assert.IsNull(terminal.Drawable);
        var untouched = Assert.ContainsSingle(await views.ReadRevisionsAsync(later.Base.BaseId, 0, 4, token).ConfigureAwait(false));
        Assert.AreEqual(later.Revision.RevisionId, untouched.RevisionId);
        Assert.IsEmpty(untouched.Layers);
        using var restarted = CreateScheduler();
        await restarted.RunOnceAsync(token).ConfigureAwait(false);
        service.Verify(assessment => assessment.ProcessAsync(It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == first.Base.BaseId),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        service.Verify(assessment => assessment.ProcessAsync(It.Is<CenteredMeasuredDeepSkyWork>(work => work.BaseId == later.Base.BaseId),
            It.IsAny<CancellationToken>()), Times.Exactly(3));

        async ValueTask<CenteredViewRevision?> PublishVerifiedDispositionAsync() =>
            (await views.PublishLayerAsync(first.Base.BaseId,
                new(new("measured-deep-sky", CenteredViewLayerScope.Stack, null), first.Revision.Order,
                    null, null, CenteredViewReadiness.Incompatible, "retained-stack-evidence-incompatible", null), token)
                .ConfigureAwait(false))?.Revision;

        CenteredMeasuredDeepSkyScheduler CreateScheduler() =>
            new(ingress.Object, views, service.Object, clock, NullLogger<CenteredMeasuredDeepSkyScheduler>.Instance);
    }

    private static CenteredViewBaseDefinition DeepSkyStackDefinition(CenteredViewStream stream, CenteredViewExposure reference,
        CenteredViewExposure neighbor, CenteredViewPreview preview, CenteredViewArtifact pin) =>
        new(stream, reference, reference.Capture.CaptureSequence, CenteredViewKind.Stack,
            pin, preview with { Source = CenteredViewImageSource.RegisteredLinear },
            [new(reference, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { })),
             new(neighbor, "VerifiedCentroidSimilarity", JsonSerializer.SerializeToElement(new { }))], pin)
        {
            Window = new(ScienceHash, ScienceHash, ScienceHash)
            {
                Slots = Enumerable.Range(-2, 5).Select(offset => new CenteredViewWindowSlot(offset,
                    reference.ExposureStartedUtc.AddSeconds(offset), offset == 0 ? reference.Capture.CaptureId :
                        offset == 1 ? neighbor.Capture.CaptureId : null, null, null, "closed", "observed", null)).ToArray()
            }
        };

    private sealed class MeasuredSchedulerClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class SignalProcessingSchemaCommit(TaskCompletionSource reached) : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.BeforeSchemaCommit)
                reached.TrySetResult();
        }
    }

    private static CenteredViewExposure DeepSkyExposure(CenteredViewStream stream, ReconstructionDescriptor descriptor) =>
        new(new(new(stream.Camera, stream.RunId, descriptor.Capture.CaptureId), descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), ScienceHash, "test-optics", null)), Guid.NewGuid(), DeepSkyDummyPin());

    private static CenteredViewArtifact DeepSkyDummyPin() => new(Guid.NewGuid(), ScienceHash, ScienceHash);

    private sealed class UnusedRegistrationSource : ICenteredWindowRegistrationSource
    {
        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An exact-revision read must not request a live exposure.");

        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An exact-revision read must not reopen live registration.");

        public ValueTask<ProcessingArtifact?> ReadArtifactAsync(ExposureKey key, CenteredStackArtifactPin pin,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An exact-revision read must not reopen reference mapping.");
    }
}
