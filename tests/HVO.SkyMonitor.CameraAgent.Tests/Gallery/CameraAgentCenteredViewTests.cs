using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    private static readonly JsonSerializerOptions CenteredTestJson = new(JsonSerializerDefaults.Web);
    private static readonly double[] Center103ExpectedLine = [21.5, 28, 41.5, 58];
    private static readonly double[] Center104ExpectedLine = [24.5, 29, 44.5, 59];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredViewServesRetainedColorPreviewAndExactLateTrailOnEachBase(bool malformedFirstMap)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var failure = new CenteredLayerCommitInterruption();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, failure);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);
        var reader = new CenteredViewPresentationReader(views, fixture.Service);

        const int width = 160;
        const int height = 128;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.BayerRggb16,
            FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, width * height * 2)
        {
            Readout = new(width + 2, height + 2, 1, 1, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1),
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };
        var bayer = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var red = (x & 1) == 1 && (y & 1) == 1 && x is > 22 and < 32 && y is > 17 and < 27;
                var blue = (x & 1) == 0 && (y & 1) == 0 && x is > 121 and < 131 && y is > 35 and < 45;
                var code = ClippedPatch(x, y) ? 4095 : red || blue ? 3800 : 90;
                BinaryPrimitives.WriteUInt16LittleEndian(bayer.AsSpan((y * width + x) * 2), (ushort)code);
            }
        }
        var raw103 = await fixture.AddRawAsync(bayer, width, height, layout).ConfigureAwait(false);
        var raw104 = await fixture.AddRawAsync(bayer, width, height, layout).ConfigureAwait(false);
        var raw105 = await fixture.AddRawAsync(bayer, width, height, layout).ConfigureAwait(false);
        var scope = new CenteredViewStream(new(raw103.Manifest.Descriptor.Capture.AgentId, "camera"), Guid.NewGuid(), "test-generation");
        var input103 = Input(raw103);
        var input104 = Input(raw104);
        var input105 = Input(raw105);
        var single = await producer.PublishSingleAsync(scope, Guid.NewGuid(), input103.Raw.Capture.CaptureSequence,
            new(input103, LinearExposureDisposition.Unsupported, 1, null, null, "normalization-unavailable"), new(), token)
            .ConfigureAwait(false);
        Assert.IsNotNull(single);
        Assert.AreEqual(CenteredViewImageSource.RawFallback, single.Base.Definition.Preview.Source);
        Assert.IsNull(single.Base.Definition.Reference.Linear);

        var exposure103 = single.Base.Definition.Reference with { Linear = LinearPin() };
        var exposure104 = new CenteredViewExposure(input104, Guid.NewGuid(), LinearPin());
        var exposure105 = new CenteredViewExposure(input105, Guid.NewGuid(), LinearPin());
        var rawPin = input105.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, bayer,
            rawPin.CreatedUtc, input105.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input105.Raw))
        {
            CaptureId = input105.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input105.Raw)
        };
        var stack103 = await RegisterStackAsync(exposure103, 1.5, -2, malformedMap: malformedFirstMap).ConfigureAwait(false);
        var stack104 = await RegisterStackAsync(exposure104, 4.5, -1).ConfigureAwait(false);
        await views.SelectLiveAsync(scope, stack103.Base.BaseId, token).ConfigureAwait(false);
        await views.SelectLiveAsync(scope, stack104.Base.BaseId, token).ConfigureAwait(false);

        var native = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(20, 30), new(40, 60), 2, new(255, 200, 100))]);
        var trail = PresentationProcessingProducts.CreateLayerProduct(native, "measured-trail-source", [sourceArtifact], "test-trail-v1");
        await persistence.PersistCenteredViewProductAsync(input105.Raw, trail, token).ConfigureAwait(false);
        var slot = new ExposureProductSlot(Guid.NewGuid(), "measured-trails", native.SchemaVersion, trail.Variant);
        var trailRef = new ExposureProductRef(input105.Key, slot, ProcessingIdentity.CreateArtifactId(trail.OutputIdentitySha256),
            trail.OutputIdentitySha256, trail.ChecksumSha256, native.ContentIdentitySha256);
        var lookup = new ExposureProductLookup(input105.Key, slot, ExposureProductAvailability.Available, 7, trailRef, null);
        var style = new CenteredViewLayerStyle("measured-trail", 500, true);
        if (malformedFirstMap)
            await PublishTrailAsync().ConfigureAwait(false);
        else
        {
            failure.Armed = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => PublishTrailAsync().AsTask()).ConfigureAwait(false);
            var interruptedArchive = Assert.ContainsSingle((await views.ReadArchiveAsync(scope, input103.Key.CaptureId, token)
                .ConfigureAwait(false)).Where(item => item.Base.Definition.Kind == CenteredViewKind.Stack));
            Assert.AreEqual(stack103.Revision.RevisionId, interruptedArchive.LatestRevisionId);
        }
        var advanced = await views.ReadCurrentAsync(scope, token).ConfigureAwait(false);
        Assert.IsNotNull(advanced);
        Assert.AreEqual(stack104.Base.BaseId, advanced.View.Base.BaseId);
        Assert.AreNotEqual(stack104.Revision.RevisionId, advanced.View.Revision.RevisionId);
        Assert.ContainsSingle(advanced.View.Revision.Layers.Where(layer => layer.Key.SourceCaptureId == input105.Key.CaptureId));
        await PublishTrailAsync().ConfigureAwait(false);

        var archive = (await views.ReadArchiveAsync(scope, input103.Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
        Assert.AreEqual(stack103.Revision.RevisionId, archive.InitialRevisionId);
        Assert.AreNotEqual(archive.InitialRevisionId, archive.LatestRevisionId);
        var current = await views.ReadCurrentAsync(scope, token).ConfigureAwait(false);
        Assert.IsNotNull(current);
        Assert.AreEqual(stack104.Base.BaseId, current.View.Base.BaseId);
        Assert.AreEqual(advanced.View.Revision.RevisionId, current.View.Revision.RevisionId);
        var enriched103 = await views.ReadRevisionAsync(archive.Base.BaseId, archive.LatestRevisionId, token).ConfigureAwait(false);
        Assert.IsNotNull(enriched103);
        var trailLayer = enriched103.Revision.Layers.Single();
        Assert.AreEqual(CenteredViewLayerScope.Contributor, trailLayer.Key.Scope);
        Assert.AreEqual(input105.Key.CaptureId, trailLayer.Key.SourceCaptureId);
        Assert.AreEqual(input105.Raw.Timing.ExposureEndedUtc, trailLayer.ObservedUtc);
        if (malformedFirstMap)
        {
            Assert.AreEqual(CenteredViewReadiness.Incompatible, trailLayer.Readiness);
            Assert.AreEqual("centered.registration-map-unavailable", trailLayer.Reason);
            Assert.IsNull(trailLayer.Drawable);
        }
        else
            Assert.AreEqual(input105.Raw.Timing.ExposureStartedUtc, trailLayer.Drawable!.Options.GetProperty("ExposureStartedUtc").GetDateTimeOffset());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorizationBuilder().AddPolicy(CameraAgentAuthorizationPolicyNames.OperationsReadV1,
            policy => policy.RequireClaim(ClaimTypes.NameIdentifier, "test-reader"));
        builder.Services.AddSingleton<ICenteredViewReadIndex>(views);
        builder.Services.AddSingleton<ICenteredViewPresentationReader>(reader);
        var app = builder.Build();
        await using var appLease = app.ConfigureAwait(false);
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "test-reader")], "test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapCameraAgentCenteredViewEndpoints();
        await app.StartAsync(token).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var bytes = await client.GetByteArrayAsync(new Uri(CenteredViewLinks.Revision(input103.Key.CaptureId,
            single.Base.BaseId, single.Revision.RevisionId) + "/preview?inline=true",
            UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(single.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(bytes));
        var baseScopedBytes = await client.GetByteArrayAsync(new Uri(CenteredViewLinks.Base(input103.Key.CaptureId,
            single.Base.BaseId) + "/preview?inline=true",
            UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(single.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(baseScopedBytes));
        using var wrongBaseRef = await client.GetAsync(new Uri(CenteredViewLinks.Base(input104.Key.CaptureId,
            single.Base.BaseId) + "/preview?inline=true", UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongBaseRef.StatusCode);
        var decoded = JpegImageCodec.DecodeJpeg(bytes, token);
        AssertChannel(decoded, 27, 22, 0);
        AssertChannel(decoded, 126, 40, 2);
        var stackBytes = await client.GetByteArrayAsync(new Uri(CenteredViewLinks.Preview(archive.Base.BaseId, archive.InitialRevisionId),
            UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(archive.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(stackBytes));
        var stackPixels = JpegImageCodec.DecodeJpeg(stackBytes, token);
        AssertChannel(stackPixels, 27, 22, 0);
        AssertChannel(stackPixels, 126, 40, 2);
        AssertClippedWhite(decoded, 67, 64);
        AssertClippedWhite(stackPixels, 67, 64);
        AssertClippedWhite(stackPixels, 77, 64);
        Assert.AreEqual("qualified-reference-display-v1",
            archive.Base.Definition.Preview.Recipe.Options.GetProperty("unsupportedPixels").GetProperty("algorithm").GetString());
        if (malformedFirstMap)
        {
            var incompatible = await ReadSvgAsync(archive.Base.BaseId, archive.LatestRevisionId).ConfigureAwait(false);
            Assert.IsEmpty(incompatible.Descendants().Where(element => element.Name.LocalName == "line"));
        }
        else
            await AssertLineAsync(archive.Base.BaseId, archive.LatestRevisionId, Center103ExpectedLine).ConfigureAwait(false);
        await AssertLineAsync(current.View.Base.BaseId, current.View.Revision.RevisionId, Center104ExpectedLine).ConfigureAwait(false);
        var original = await ReadSvgAsync(archive.Base.BaseId, archive.InitialRevisionId).ConfigureAwait(false);
        Assert.IsEmpty(original.Descendants().Where(element => element.Name.LocalName == "line"));
        var excluded = await RegisterStackAsync(exposure104, 0, 0, exclude105: true, parent: stack104.Base.BaseId).ConfigureAwait(false);
        Assert.IsNull(await layerProducer.PublishAsync(excluded.Base.BaseId, CenteredViewLayerScope.Contributor,
            lookup, style, input105.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false));
        var excludedSvg = await ReadSvgAsync(excluded.Base.BaseId, excluded.Revision.RevisionId).ConfigureAwait(false);
        Assert.IsEmpty(excludedSvg.Descendants().Where(element => element.Name.LocalName == "line"));
        using var mismatched = await client.GetAsync(new Uri(CenteredViewLinks.Presentation(archive.Base.BaseId, current.View.Revision.RevisionId),
            UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, mismatched.StatusCode);
        using var wrongReference = await client.GetAsync(new Uri(CenteredViewLinks.Revision(input104.Key.CaptureId,
            archive.Base.BaseId, archive.LatestRevisionId) + "/overlay.svg", UriKind.Relative), token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongReference.StatusCode);
        await app.StopAsync(token).ConfigureAwait(false);

        ValueTask PublishTrailAsync() => layerProducer.PublishToContributorsAsync(lookup, style,
            input105.Raw.Timing.ExposureEndedUtc, cancellationToken: token);

        ExposureInputV1 Input(StoredArtifact raw)
        {
            var descriptor = raw.Manifest.Descriptor;
            return new(new(scope.Camera, scope.RunId, descriptor.Capture.CaptureId), descriptor,
                new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        }

        async ValueTask<CenteredView> RegisterStackAsync(CenteredViewExposure reference, double x, double y,
            bool exclude105 = false, string? parent = null, bool malformedMap = false)
        {
            // These known maps discriminate the publication boundary. The ordinary
            // registration producer is exercised separately against frozen inputs.
            var neighbor = exclude105 ? exposure103 : exposure105;
            var sourceEvidence = new CenteredStackSourceEvidence(exclude105 ? -1 : 2, neighbor.Capture.CaptureId,
                CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedCentroidSimilarity,
                new ImageSimilarity(1, 0, x, y), 100);
            CenteredViewContributor refMember = new(reference, nameof(CenteredRegistrationMethod.ReferenceIdentity),
                JsonSerializer.SerializeToElement(new { }));
            var geometry = JsonSerializer.SerializeToNode(sourceEvidence, CenteredTestJson)!.AsObject();
            if (malformedMap)
                geometry["offset"] = "malformed";
            CenteredViewContributor sourceMember = new(neighbor, nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity),
                JsonSerializer.SerializeToElement(geometry));
            CenteredViewContributor[] members = exclude105 ? [sourceMember, refMember] : [refMember, sourceMember];

            var science = LinearPin();
            var planeLayout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
                FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                0, ushort.MaxValue, width * height * 2);
            var rgb = new ProcessingArtifact[3];
            for (var channel = 0; channel < rgb.Length; channel++)
            {
                var plane = new byte[width * height * 2];
                for (var row = 0; row < height; row++)
                {
                    for (var column = 0; column < width; column++)
                    {
                        var signal = channel == 0 && column is > 22 and < 32 && row is > 17 and < 27 ||
                            channel == 2 && column is > 121 and < 131 && row is > 35 and < 45;
                        var code = ClippedPatch(column, row) ? 0 : signal ? 3800 : 90;
                        BinaryPrimitives.WriteUInt16LittleEndian(plane.AsSpan((row * width + column) * 2), (ushort)code);
                    }
                }
                rgb[channel] = sourceArtifact with
                {
                    ArtifactId = Guid.NewGuid(),
                    Role = FrameArtifactRole.Combined,
                    Layout = planeLayout,
                    Payload = plane
                };
            }
            var support = new byte[width * height];
            for (var index = 0; index < support.Length; index++)
            {
                var column = index % width;
                support[index] = ClippedPatch(column, index / width) ? (byte)(column < 72 ? 0 : 4) : (byte)5;
            }
            var referencePixels = sourceArtifact with { Role = FrameArtifactRole.Calibrated, ArtifactId = reference.Linear!.ArtifactId };
            var encoded = CenteredViewPreviewEncoder.Encode(rgb, layout, CenteredViewImageSource.RegisteredLinear,
                null, null, new(), [], new(referencePixels, Guid.NewGuid(), support), token);
            await persistence.PersistCenteredViewProductAsync(reference.Input.Raw, encoded.Product, token).ConfigureAwait(false);
            return await views.RegisterBaseAsync(new(scope, reference, reference.Capture.CaptureSequence, CenteredViewKind.Stack,
                science, encoded.Preview, members, science, parent), [], token).ConfigureAwait(false);
        }

        static CenteredViewArtifact LinearPin() => new(Guid.NewGuid(), new string('D', 64), new string('E', 64));

        async Task<XDocument> ReadSvgAsync(string baseId, string revisionId)
        {
            var view = await views.ReadRevisionAsync(baseId, revisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(view);
            var link = CenteredViewLinks.Revision(view.Base.Definition.Reference.Capture.CaptureId, baseId, revisionId) + "/overlay.svg";
            var text = await client.GetStringAsync(new Uri(link, UriKind.Relative), token)
                .ConfigureAwait(false);
            return XDocument.Parse(text);
        }

        async Task AssertLineAsync(string baseId, string revisionId, double[] expected)
        {
            var svg = await ReadSvgAsync(baseId, revisionId).ConfigureAwait(false);
            var line = svg.Descendants().Single(element => element.Name.LocalName == "line");
            double Coordinate(string name) => double.Parse(line.Attribute(name)!.Value, CultureInfo.InvariantCulture);
            double[] actual = [Coordinate("x1"), Coordinate("y1"), Coordinate("x2"), Coordinate("y2")];
            CollectionAssert.AreEqual(expected, actual);
        }
    }

    private static bool ClippedPatch(int x, int y) => x is >= 61 and <= 83 && y is >= 57 and <= 71;

    private static void AssertClippedWhite(DecodedImage image, int x, int y)
    {
        var pixel = image.PixelData.Span.Slice(y * image.StrideBytes + x * 3, 3);
        foreach (var component in pixel)
            Assert.IsGreaterThan(235, component);
    }

    private sealed class CenteredLayerCommitInterruption : ICaptureProcessingFaultInjector
    {
        public bool Armed { get; set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!Armed || point != CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit ||
                nodeId != "centered-view-publication")
                return;

            Armed = false;
            throw new IOException("Interrupt the first contributor layer after file publication.");
        }
    }

    private static void AssertChannel(DecodedImage image, int x, int y, int expected)
    {
        var pixel = image.PixelData.Span.Slice(y * image.StrideBytes + x * 3, 3);
        for (var channel = 0; channel < 3; channel++)
        {
            if (channel != expected)
                Assert.IsGreaterThan(pixel[channel] + 90, pixel[expected]);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredPresentationReaderOpensHealthySiblingLayerWithoutWaitingForStalledLayerGateAsync(bool synchronousStall)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 64;
        const int height = 48;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);
        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), (ushort)(200 + (i % 500)));
        }

        var raw = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-m5");
        var input = new ExposureInputV1(
            new(stream.Camera, stream.RunId, raw.Manifest.Descriptor.Capture.CaptureId),
            raw.Manifest.Descriptor,
            new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var single = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input.Raw.Capture.CaptureSequence,
            new(input, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single);

        var rawPin = input.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
            rawPin.CreatedUtc, input.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input.Raw))
        {
            CaptureId = input.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input.Raw)
        };

        var layer1Payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(4, 4), new(20, 20), 2, new(255, 255, 255))]);
        var layer1Product = PresentationProcessingProducts.CreateLayerProduct(layer1Payload, "scene-annotation-layer-v1", [sourceArtifact], "layer-v1");
        await persistence.PersistCenteredViewProductAsync(input.Raw, layer1Product, token).ConfigureAwait(false);
        var layer1Slot = new ExposureProductSlot(Guid.NewGuid(), "stars", layer1Payload.SchemaVersion, layer1Product.Variant);
        var layer1Ref = new ExposureProductRef(input.Key, layer1Slot, ProcessingIdentity.CreateArtifactId(layer1Product.OutputIdentitySha256),
            layer1Product.OutputIdentitySha256, layer1Product.ChecksumSha256, layer1Payload.ContentIdentitySha256);
        await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
            new(input.Key, layer1Slot, ExposureProductAvailability.Available, 1, layer1Ref, null),
            new("star-annotations", 20, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);

        var layer2Payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(8, 8), new(28, 28), 2, new(255, 200, 100))]);
        var layer2Product = PresentationProcessingProducts.CreateLayerProduct(layer2Payload, "scene-constellation-layer-v1", [sourceArtifact], "layer-v1");
        await persistence.PersistCenteredViewProductAsync(input.Raw, layer2Product, token).ConfigureAwait(false);
        var layer2Slot = new ExposureProductSlot(Guid.NewGuid(), "constellations", layer2Payload.SchemaVersion, layer2Product.Variant);
        var layer2Ref = new ExposureProductRef(input.Key, layer2Slot, ProcessingIdentity.CreateArtifactId(layer2Product.OutputIdentitySha256),
            layer2Product.OutputIdentitySha256, layer2Product.ChecksumSha256, layer2Payload.ContentIdentitySha256);
        var enriched = await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
            new(input.Key, layer2Slot, ExposureProductAvailability.Available, 2, layer2Ref, null),
            new("scene-constellations", 10, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
        Assert.IsNotNull(enriched);
        Assert.HasCount(2, enriched.Revision.Manifest.Layers);

        var layer1ArtifactId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
        var layer2ArtifactId = enriched.Revision.Manifest.Layers[1].SourceProduct.ArtifactId;
        var layer2Kind = enriched.Revision.Manifest.Layers[1].LayerKind;

        var layer1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layer2OpenedWhileLayer1Blocked = false;
        using var gatedArtifacts = new GatedLayerArtifactService(
            fixture.Service,
            layer1ArtifactId,
            layer2ArtifactId,
            synchronousStall,
            layer1Entered,
            () => layer2OpenedWhileLayer1Blocked = true);

        var reader = new CenteredViewPresentationReader(
            views,
            gatedArtifacts,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(150));

        try
        {
            var rendered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsTrue(layer1Entered.Task.IsCompleted, "Stalled layer 1 read should have been started.");
            Assert.IsFalse(gatedArtifacts.IsReleased, "Layer 1 gate must remain unreleased while layer 2 completes.");
            Assert.IsTrue(layer2OpenedWhileLayer1Blocked, "Healthy sibling layer 2 must open without waiting for layer 1.");
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
            Assert.IsNotNull(rendered.Presentation);
            Assert.HasCount(1, rendered.Presentation.Layers);
            Assert.AreEqual(layer2Kind, rendered.Presentation.Layers[0].Kind);
            Assert.IsTrue(CameraAgentCenteredViewProjection.IsPresentationCompatible(enriched, rendered.Presentation));
        }
        finally
        {
            await gatedArtifacts.ReleaseAndDrainAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderBoundsConcurrentLayerReadsToConfiguredCapAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 64;
        const int height = 48;
        const int totalLayers = 10;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);
        var pixels = new byte[width * height * 2];
        var raw = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-m7");
        var input = new ExposureInputV1(
            new(stream.Camera, stream.RunId, raw.Manifest.Descriptor.Capture.CaptureId),
            raw.Manifest.Descriptor,
            new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var single = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input.Raw.Capture.CaptureSequence,
            new(input, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single);

        var rawPin = input.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
            rawPin.CreatedUtc, input.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input.Raw))
        {
            CaptureId = input.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input.Raw)
        };

        CenteredView enriched = single;
        for (var index = 0; index < totalLayers; index++)
        {
            var payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
                segments: [new(new(2 + index, 4), new(20 + index, 20), 2, new(255, 255, 255))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, $"bounded-layer-{index}-v1", [sourceArtifact], $"v{index}");
            await persistence.PersistCenteredViewProductAsync(input.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(Guid.NewGuid(), $"bounded-slot-{index}", payload.SchemaVersion, product.Variant);
            var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            var productRef = new ExposureProductRef(input.Key, slot, artifactId,
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            enriched = (await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
                new(input.Key, slot, ExposureProductAvailability.Available, index + 1, productRef, null),
                new($"bounded-layer-{index:D2}", index, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false))!;
        }
        Assert.HasCount(totalLayers, enriched.Revision.Manifest.Layers);
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var concurrencyProbe = new ConcurrencyTrackingArtifactService(
            fixture.Service,
            layerArtifactIds,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads);
        var reader = new CenteredViewPresentationReader(
            views,
            concurrencyProbe,
            TimeProvider.System,
            TimeSpan.FromSeconds(5));

        try
        {
            var rendered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
            Assert.IsNotNull(rendered.Presentation);
            Assert.HasCount(totalLayers, rendered.Presentation.Layers);
            Assert.AreEqual(
                CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
                concurrencyProbe.PeakConcurrentOpens,
                "Layer opens must saturate and never exceed the bounded concurrency cap.");
            CollectionAssert.AreEqual(
                enriched.Revision.Manifest.Layers.Select(static layer => layer.LayerKind).ToArray(),
                rendered.Presentation.Layers.Select(static layer => layer.Kind).ToArray());
        }
        finally
        {
            await concurrencyProbe.DrainAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderEnforcesAggregateSixteenMiBBudgetBeforeAllocatingBuffersAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 64;
        const int height = 48;
        const int totalLayers = 6;
        const int fourMiB = PresentationLayerPayloadJson.MaximumPayloadBytes;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);
        var pixels = new byte[width * height * 2];
        var raw = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-l3");
        var input = new ExposureInputV1(
            new(stream.Camera, stream.RunId, raw.Manifest.Descriptor.Capture.CaptureId),
            raw.Manifest.Descriptor,
            new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var single = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input.Raw.Capture.CaptureSequence,
            new(input, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single);

        var rawPin = input.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
            rawPin.CreatedUtc, input.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input.Raw))
        {
            CaptureId = input.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input.Raw)
        };

        CenteredView enriched = single;
        for (var index = 0; index < totalLayers; index++)
        {
            var payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
                segments: [new(new(2 + index, 4), new(20 + index, 20), 2, new(255, 255, 255))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, $"budget-layer-{index}-v1", [sourceArtifact], $"v{index}");
            await persistence.PersistCenteredViewProductAsync(input.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(Guid.NewGuid(), $"budget-slot-{index}", payload.SchemaVersion, product.Variant);
            var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            var productRef = new ExposureProductRef(input.Key, slot, artifactId,
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            enriched = (await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
                new(input.Key, slot, ExposureProductAvailability.Available, index + 1, productRef, null),
                new($"budget-layer-{index:D2}", index, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false))!;
        }
        Assert.HasCount(totalLayers, enriched.Revision.Manifest.Layers);
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        // Allow 6 concurrent workers against 6 x 4 MiB layers (24 MiB) with the real 16 MiB aggregate cap:
        // at most 4 streams may reserve/allocate/read simultaneously (4 x 4 MiB = 16 MiB) while the other 2
        // fail reservation BEFORE allocating a 4 MiB buffer or reading from their stream!
        var budgetProbe = new PreAllocationBudgetArtifactService(
            fixture.Service,
            layerArtifactIds,
            input.Key.CaptureId,
            fourMiB,
            expectedConcurrentBudgetHolders: 4);
        var reader = new CenteredViewPresentationReader(
            views,
            budgetProbe,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumConcurrentLayerReads: totalLayers);

        try
        {
            var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token).AsTask();
            await budgetProbe.AllOpenedAndBudgetSaturatedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(totalLayers, budgetProbe.TotalOpens, "All 6 layers should have been opened concurrently.");
            Assert.AreEqual(4, budgetProbe.PeakConcurrentReadAllocations, "At most 4 x 4 MiB = 16 MiB may be allocated concurrently.");
            Assert.AreEqual(4L * fourMiB, budgetProbe.PeakAllocatedBytes, "Pre-allocation budget must strictly cap aggregate buffers at 16 MiB.");
            Assert.AreEqual(2, budgetProbe.DisposedWithoutReadingCount, "The 2 layers beyond 16 MiB must reject BEFORE allocating or reading.");

            budgetProbe.ReleaseReaders();
            var rendered = await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
        }
        finally
        {
            budgetProbe.ReleaseReaders();
            await budgetProbe.DrainAsync().ConfigureAwait(false);
        }
    }

    private sealed class GatedLayerArtifactService(
        ICameraAgentArtifactService inner,
        Guid stalledArtifactId,
        Guid healthyArtifactId,
        bool synchronousStall,
        TaskCompletionSource stalledEntered,
        Action onHealthyOpened) : ICameraAgentArtifactService, IDisposable
    {
        private readonly ManualResetEventSlim _syncGate = new(false);
        private readonly TaskCompletionSource _asyncGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stalledCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stalledStarted;
        private int _released;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == stalledArtifactId)
            {
                Interlocked.Increment(ref _stalledStarted);
                if (synchronousStall)
                {
                    try
                    {
                        stalledEntered.TrySetResult();
                        _syncGate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                        return ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable));
                    }
                    finally
                    {
                        _stalledCompleted.TrySetResult();
                    }
                }
                return OpenStalledAsync();
            }
            if (artifactId == healthyArtifactId)
            {
                onHealthyOpened();
            }
            return inner.OpenContentAsync(artifactId, cancellationToken);
        }

        private async ValueTask<CameraAgentArtifactContentResult> OpenStalledAsync()
        {
            try
            {
                stalledEntered.TrySetResult();
                await _asyncGate.Task.ConfigureAwait(false);
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
            }
            finally
            {
                _stalledCompleted.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            Interlocked.Exchange(ref _released, 1);
            _syncGate.Set();
            _asyncGate.TrySetResult();
            if (Volatile.Read(ref _stalledStarted) > 0)
            {
                await _stalledCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose() => _syncGate.Dispose();
    }

    private sealed class ConcurrencyTrackingArtifactService(
        ICameraAgentArtifactService inner,
        HashSet<Guid> trackedArtifactIds,
        int expectedPeak) : ICameraAgentArtifactService
    {
        private readonly TaskCompletionSource _peakReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        private int _inFlight;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrentOpens => Volatile.Read(ref _peak);

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (!trackedArtifactIds.Contains(artifactId))
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _inFlight);
            var current = Interlocked.Increment(ref _active);
            while (true)
            {
                var observed = Volatile.Read(ref _peak);
                if (current <= observed || Interlocked.CompareExchange(ref _peak, current, observed) == observed)
                {
                    break;
                }
            }
            try
            {
                if (current >= expectedPeak)
                {
                    _peakReached.TrySetResult();
                }
                else
                {
                    await _peakReached.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                if (Interlocked.Decrement(ref _inFlight) == 0 && _peakReached.Task.IsCompleted)
                {
                    _drained.TrySetResult();
                }
            }
        }

        public async Task DrainAsync()
        {
            _peakReached.TrySetResult();
            if (Volatile.Read(ref _inFlight) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);
    }

    private sealed class PreAllocationBudgetArtifactService(
        ICameraAgentArtifactService inner,
        HashSet<Guid> layerArtifactIds,
        Guid captureId,
        int reportedByteLength,
        int expectedConcurrentBudgetHolders) : ICameraAgentArtifactService
    {
        private readonly TaskCompletionSource _budgetSaturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _excessRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allStreamsDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _totalOpens;
        private int _activeReaders;
        private int _peakReaders;
        private int _disposedWithoutReading;
        private int _openStreams;

        public int TotalOpens => Volatile.Read(ref _totalOpens);
        public int PeakConcurrentReadAllocations => Volatile.Read(ref _peakReaders);
        public long PeakAllocatedBytes => (long)PeakConcurrentReadAllocations * reportedByteLength;
        public int DisposedWithoutReadingCount => Volatile.Read(ref _disposedWithoutReading);

        public async Task AllOpenedAndBudgetSaturatedAsync()
        {
            await _budgetSaturated.Task.ConfigureAwait(false);
            await _excessRejected.Task.ConfigureAwait(false);
        }

        public void ReleaseReaders() => _releaseReads.TrySetResult();

        public async Task DrainAsync()
        {
            _releaseReads.TrySetResult();
            if (Volatile.Read(ref _openStreams) > 0)
            {
                await _allStreamsDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (!layerArtifactIds.Contains(artifactId))
            {
                return inner.OpenContentAsync(artifactId, cancellationToken);
            }

            Interlocked.Increment(ref _totalOpens);
            Interlocked.Increment(ref _openStreams);
            var stream = new BudgetProbeStream(this, reportedByteLength);
            var content = new CameraAgentArtifactContentStream(
                stream,
                artifactId,
                captureId,
                FrameArtifactRole.Metadata,
                PresentationLayerPayloadJson.MediaType,
                reportedByteLength,
                new string('A', 64),
                $"{artifactId:D}.json",
                descriptor: null);
            return ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Found, content));
        }

        private async ValueTask<int> OnStreamReadAsync(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _activeReaders);
            while (true)
            {
                var observed = Volatile.Read(ref _peakReaders);
                if (current <= observed || Interlocked.CompareExchange(ref _peakReaders, current, observed) == observed)
                {
                    break;
                }
            }
            if (current >= expectedConcurrentBudgetHolders)
            {
                _budgetSaturated.TrySetResult();
            }
            try
            {
                await _releaseReads.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                // Return 0 (EOF) so ReadExactlyAsync exits cleanly without needing 16 MiB of valid JSON.
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReaders);
            }
        }

        private void OnStreamDisposed(bool wasRead)
        {
            if (!wasRead)
            {
                var rejected = Interlocked.Increment(ref _disposedWithoutReading);
                if (rejected >= layerArtifactIds.Count - expectedConcurrentBudgetHolders)
                {
                    _excessRejected.TrySetResult();
                }
            }
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _totalOpens) >= layerArtifactIds.Count)
            {
                _allStreamsDisposed.TrySetResult();
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        private sealed class BudgetProbeStream(PreAllocationBudgetArtifactService owner, int length) : Stream
        {
            private int _wasRead;
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Interlocked.Exchange(ref _wasRead, 1);
                return owner.OnStreamReadAsync(cancellationToken);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.OnStreamDisposed(Volatile.Read(ref _wasRead) != 0);
                }
                base.Dispose(disposing);
            }
        }
    }
}
