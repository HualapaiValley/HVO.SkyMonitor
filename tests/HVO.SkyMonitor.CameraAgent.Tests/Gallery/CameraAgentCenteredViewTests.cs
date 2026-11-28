using System.Buffers.Binary;
using System.Diagnostics;
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
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
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
        using var telemetry = new CaptureProcessingTelemetry();
        var failure = new CenteredLayerCommitInterruption();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
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
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, silo: fixture.Storage);
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
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, silo: fixture.Storage);
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
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, silo: fixture.Storage);
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
        var allocationCalls = 0;
        long allocatedBytes = 0;
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
            maximumConcurrentLayerReads: totalLayers,
            bufferAllocator: length =>
            {
                Interlocked.Increment(ref allocationCalls);
                Interlocked.Add(ref allocatedBytes, length);
                return new byte[length];
            });

        try
        {
            var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token).AsTask();
            await budgetProbe.AllOpenedAndBudgetSaturatedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(totalLayers, budgetProbe.TotalOpens, "All 6 layers should have been opened concurrently.");
            Assert.AreEqual(4, budgetProbe.PeakConcurrentReadAllocations, "At most 4 x 4 MiB = 16 MiB may be allocated concurrently.");
            Assert.AreEqual(4L * fourMiB, budgetProbe.PeakAllocatedBytes, "Pre-allocation budget must strictly cap aggregate buffers at 16 MiB.");
            Assert.AreEqual(4, Volatile.Read(ref allocationCalls), "Buffer allocation must strictly follow budget reservation.");
            Assert.AreEqual(4L * fourMiB, Interlocked.Read(ref allocatedBytes), "Rejected layers beyond 16 MiB must never allocate a buffer.");
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

    [TestMethod]
    public async Task CenteredPresentationReaderRetainsAcceptedLayerBudgetAndRejectsExcessBeforeAllocatingAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(fixture, layerCount: 3, "gen-budget-retain", token)
            .ConfigureAwait(false);

        long firstTwoBytes = 0;
        for (var i = 0; i < 2; i++)
        {
            var opened = await fixture.Service.OpenContentAsync(enriched.Revision.Manifest.Layers[i].SourceProduct.ArtifactId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(opened.Content);
            await using (opened.Content)
            {
                firstTwoBytes += opened.Content.ByteLength;
            }
        }

        var allocationCalls = 0;
        var layerArtifactIds = enriched.Revision.Manifest.Layers.Select(static l => l.SourceProduct.ArtifactId).ToHashSet();
        var readTracker = new StreamReadCountingArtifactService(fixture.Service, layerArtifactIds);
        var reader = new CenteredViewPresentationReader(
            views,
            readTracker,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumConcurrentLayerReads: 1,
            maximumSourcePayloadBytes: checked((int)firstTwoBytes),
            bufferAllocator: length =>
            {
                Interlocked.Increment(ref allocationCalls);
                return new byte[length];
            });

        var rendered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
        Assert.IsNotNull(rendered.Presentation);
        Assert.HasCount(2, rendered.Presentation.Layers, "Accepted layers must retain their budget reservation so layer 3 is rejected.");
        Assert.AreEqual(2, Volatile.Read(ref allocationCalls), "The 3rd layer exceeding retained budget must fail before allocating its buffer.");
        Assert.AreEqual(2, readTracker.StreamsReadCount, "The 3rd layer exceeding retained budget must fail before reading from its stream.");
        Assert.AreEqual(0, readTracker.OpenStreamCount, "All opened layer streams must be disposed.");
    }

    [TestMethod]
    [DataRow("async-open")]
    [DataRow("sync-read")]
    [DataRow("async-read")]
    public async Task CenteredPresentationReaderBoundsTimedOutLayerOperationsToCapAsync(string stallMode)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        const int totalLayers = 10;
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(fixture, totalLayers, $"gen-m7-{stallMode}", token)
            .ConfigureAwait(false);

        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();
        var safetyTimeout = TimeSpan.FromSeconds(30);
        using var probe = new StalledConcurrencyProbeArtifactService(fixture.Service, layerArtifactIds, stallMode, permitCount, safetyTimeout);
        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var layerTimeout = TimeSpan.FromMilliseconds(80);
        var overallTimeout = TimeSpan.FromMilliseconds(350);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            time,
            optionalLayerReadTimeout: layerTimeout,
            maximumConcurrentLayerReads: permitCount,
            overallOptionalLayersReadTimeout: overallTimeout);

        using var callerCts = new CancellationTokenSource();
        try
        {
            var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
            await probe.WaitForExpectedOperationsEnteredAsync().WaitAsync(safetyTimeout).ConfigureAwait(false);
            Assert.AreEqual(permitCount, probe.PeakActiveOperations, $"All {permitCount} {stallMode} operations must overlap.");

            // All permits are held by stalled operations. Expire their individual deadlines and
            // prove the next wave is queued at the gate before expiring the overall deadline.
            time.Advance(layerTimeout);
            await WaitForConditionAsync(
                () => reader.PendingLayerReadWaiterCount == permitCount,
                safetyTimeout).ConfigureAwait(false);
            Assert.AreEqual(0, reader.AvailableLayerReadPermits, "Timed-out operations must retain their permits.");
            Assert.AreEqual(permitCount, probe.TotalStartedOperations, "Queued layers must not start while the gate is full.");

            time.Advance(overallTimeout - layerTimeout);
            var rendered = await readTask.WaitAsync(safetyTimeout).ConfigureAwait(false);

            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
            Assert.IsNotNull(rendered.Presentation);
            Assert.IsEmpty(rendered.Presentation.Layers);
            Assert.AreEqual(permitCount, probe.PeakActiveOperations, $"Timed-out {stallMode} operations must stay within the concurrency cap.");
            Assert.AreEqual(permitCount, probe.TotalStartedOperations, "Expiring the overall deadline must not start any queued layer.");
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount, "The overall deadline must remove all queued layers.");
        }
        finally
        {
            await callerCts.CancelAsync().ConfigureAwait(false);
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                safetyTimeout).ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount, "All detached streams must be disposed after stalled operations finish.");
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderOffloadsStalledSynchronousReadAndRendersHealthySiblingAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(fixture, layerCount: 2, "gen-sync-read-offload", token)
            .ConfigureAwait(false);

        var layer1ArtifactId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
        var layer2Kind = enriched.Revision.Manifest.Layers[1].LayerKind;
        using var probe = new StalledConcurrencyProbeArtifactService(fixture.Service, [layer1ArtifactId], "sync-read");
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            TimeProvider.System,
            optionalLayerReadTimeout: TimeSpan.FromMilliseconds(150));

        try
        {
            var rendered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(1, probe.TotalStartedOperations, "Stalled synchronous read should have started.");
            Assert.IsFalse(probe.IsReleased, "Stalled synchronous read gate must still be blocked when healthy sibling returns.");
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
            Assert.IsNotNull(rendered.Presentation);
            Assert.HasCount(1, rendered.Presentation.Layers);
            Assert.AreEqual(layer2Kind, rendered.Presentation.Layers[0].Kind);
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderReturnsCompletedHealthySiblingsWhenOverallDeadlineExpiresAcrossWavesAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(fixture, layerCount: 10, "gen-m5-waves", CancellationToken.None)
            .ConfigureAwait(false);

        var healthyArtifactId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
        var healthyLayerKind = enriched.Revision.Manifest.Layers[0].LayerKind;
        var slowArtifactIds = enriched.Revision.Manifest.Layers.Skip(1)
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var waveProbe = new CooperativeMultiWaveSlowArtifactService(fixture.Service, healthyArtifactId, slowArtifactIds);
        var reader = new CenteredViewPresentationReader(
            views,
            waveProbe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();

        // Wave 1: layer 0 completes immediately while 4 slow siblings occupy all 4 permits.
        await waveProbe.WaitForHealthyCompletedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await waveProbe.WaitForSlowStartedAsync(4).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(readTask.IsCompleted);

        // Advance past wave 1's 2s per-layer deadline -> wave 1 slow layers cancel and wave 2 (layers 5..8) starts.
        time.Advance(CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout);
        await waveProbe.WaitForSlowStartedAsync(8).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(readTask.IsCompleted);

        // Advance to the 3.5s overall deadline (strictly under Current Sky's 5s request deadline):
        // reader stops waiting on incomplete waves and returns healthy sibling layer 0 cleanly!
        time.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout - CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout);
        var rendered = await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
        Assert.IsNotNull(rendered.Presentation);
        Assert.HasCount(1, rendered.Presentation.Layers, "Already-completed healthy sibling must be returned when overall deadline expires.");
        Assert.AreEqual(healthyLayerKind, rendered.Presentation.Layers[0].Kind);
        Assert.AreEqual(8, waveProbe.SlowStartedCount, "Wave 3 must not start after the overall deadline expires.");

        // Explicit caller cancellation (e.g. navigation/dispose) still cancels cooperatively.
        var cancelProbe = new CooperativeMultiWaveSlowArtifactService(fixture.Service, healthyArtifactId, slowArtifactIds);
        var cancelReader = new CenteredViewPresentationReader(
            views,
            cancelProbe,
            new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow),
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);
        using var callerCts = new CancellationTokenSource();
        var canceledRead = cancelReader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
        await cancelProbe.WaitForSlowStartedAsync(4).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await callerCts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceledRead.WaitAsync(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CenteredPresentationReaderSharesConcurrencyGateAcrossOverlappingReadCallsAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            "gen-m7-shared-gate",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var probe = new OverlappingGateProbeArtifactService(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            stalledOpenCount: CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            // First ReadAsync saturates all 4 shared gate permits with blocked opens and times out.
            var firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForStalledOpensEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            time.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

            var firstRendered = await firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, firstRendered.Status);
            Assert.IsNotNull(firstRendered.Presentation);
            Assert.IsEmpty(firstRendered.Presentation.Layers);
            Assert.AreEqual(CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads, probe.PeakActiveLayerOpens);
            Assert.AreEqual(CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads, probe.TotalLayerOpens);

            // Second ReadAsync on the same reader must respect the shared 4-op cap while first call's
            // 4 opens remain blocked in flight, never starting a 5th concurrent open.
            var secondReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForSecondPreviewOpenedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            Assert.IsFalse(secondReadTask.IsCompleted,
                "Second ReadAsync must wait on the shared gate while 4 opens from the first call remain blocked.");
            Assert.AreEqual(
                CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
                probe.PeakActiveLayerOpens,
                "Overlapping ReadAsync calls on the same reader must never exceed the shared 4-op cap.");
            Assert.AreEqual(
                CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
                probe.TotalLayerOpens,
                "No layer open from the second ReadAsync may start until a shared gate permit is released.");

            // Release the 4 blocked opens from the first call -> permits free up -> second call succeeds cleanly!
            probe.ReleaseStalledOpens();
            var secondRendered = await secondReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, secondRendered.Status);
            Assert.IsNotNull(secondRendered.Presentation);
            Assert.HasCount(CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads, secondRendered.Presentation.Layers);
            Assert.AreEqual(
                CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
                probe.PeakActiveLayerOpens,
                "Peak concurrent opens across both calls must never exceed 4.");
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderAnchorsOverallBudgetAtReadEntryAcrossSlowPreviewOpenAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: 5,
            "gen-m5-preview-anchor",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var healthyArtifactId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
        var healthyLayerKind = enriched.Revision.Manifest.Layers[0].LayerKind;
        var slowArtifactIds = enriched.Revision.Manifest.Layers.Skip(1)
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        // Case 1: Slow preview open consumes 2.0s of the 3.5s overall budget -> remaining 1.5s expires
        // strictly within 3.5s of ReadAsync entry (before the 2.0s per-layer deadline) and returns
        // the verified base + completed healthy layer cleanly.
        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var previewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowPreviewProbe = new SlowPreviewAndLayersArtifactService(
            fixture.Service,
            previewArtifactId,
            healthyArtifactId,
            slowArtifactIds,
            previewEntered,
            previewGate);
        var reader = new CenteredViewPresentationReader(
            views,
            slowPreviewProbe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
        await previewEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var previewDelay = TimeSpan.FromSeconds(2);
        time.Advance(previewDelay);
        previewGate.TrySetResult();

        await slowPreviewProbe.WaitForHealthyCompletedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await slowPreviewProbe.WaitForSlowStartedAsync(4).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(readTask.IsCompleted);

        // Advance only the remaining 1.5s (total 3.5s from ReadAsync entry, still < 2.0s since preview finished).
        time.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout - previewDelay);
        var rendered = await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
        Assert.IsNotNull(rendered.Presentation);
        Assert.HasCount(1, rendered.Presentation.Layers);
        Assert.AreEqual(healthyLayerKind, rendered.Presentation.Layers[0].Kind);

        // Case 2: Preview open consumes the full 3.5s overall budget -> optional layers are skipped and
        // the verified base returns cleanly without starting any optional layer opens.
        var exhaustedTime = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var exhaustedPreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhaustedPreviewEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhaustedProbe = new SlowPreviewAndLayersArtifactService(
            fixture.Service,
            previewArtifactId,
            healthyArtifactId,
            slowArtifactIds,
            exhaustedPreviewEntered,
            exhaustedPreviewGate);
        var exhaustedReader = new CenteredViewPresentationReader(
            views,
            exhaustedProbe,
            exhaustedTime,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        var exhaustedReadTask = exhaustedReader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
        await exhaustedPreviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        exhaustedTime.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout + TimeSpan.FromMilliseconds(100));
        exhaustedPreviewGate.TrySetResult();

        var baseOnly = await exhaustedReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, baseOnly.Status);
        Assert.IsNotNull(baseOnly.Presentation);
        Assert.IsEmpty(baseOnly.Presentation.Layers);
        Assert.AreEqual(0, exhaustedProbe.TotalLayerOpens, "Expired overall budget after slow preview must skip optional layer opens.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredPresentationReaderDetachesStalledOptionalLayerDisposeWhileHoldingGatePermitAsync(bool synchronousDisposeStall)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            $"gen-dispose-stall-{(synchronousDisposeStall ? "sync" : "async")}",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        using var probe = new StalledDisposeArtifactService(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            stalledDisposeCount: CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            synchronousDisposeStall);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            var firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForAllStalledDisposesEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(firstReadTask.IsCompleted);

            // Advancing past the layer/overall deadline detaches the stalled disposals so ReadAsync returns
            // promptly with the already-verified base and completed layers.
            time.Advance(CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout);
            var firstRendered = await firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, firstRendered.Status);
            Assert.IsNotNull(firstRendered.Presentation);
            Assert.HasCount(CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads, firstRendered.Presentation.Layers);

            // While the 4 detached DisposeAsync calls remain stalled, all 4 gate permits must stay held.
            var secondReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForSecondPreviewOpenedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            Assert.IsFalse(secondReadTask.IsCompleted,
                "Gate permits must remain held while detached optional-layer DisposeAsync calls are still stalled.");
            Assert.AreEqual(0, probe.SecondCallLayerOpens,
                "No second-call layer open may acquire a permit until detached disposals complete.");

            // Unblocking the stalled disposals cleanly releases the permits so the second ReadAsync succeeds!
            probe.ReleaseDisposes();
            var secondRendered = await secondReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, secondRendered.Status);
            Assert.IsNotNull(secondRendered.Presentation);
            Assert.HasCount(CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads, secondRendered.Presentation.Layers);
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount, "All wrapped optional-layer streams must finish disposal cleanly.");
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderIsolatesThrowingOptionalLayerDisposeAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: 3,
            "gen-dispose-throw",
            CancellationToken.None).ConfigureAwait(false);

        var layer0 = enriched.Revision.Manifest.Layers[0];
        var layer1 = enriched.Revision.Manifest.Layers[1];
        var layer2 = enriched.Revision.Manifest.Layers[2];
        var probe = new ThrowingDisposeArtifactService(
            fixture.Service,
            new Dictionary<Guid, (bool FailRead, Exception DisposeException)>
            {
                [layer0.SourceProduct.ArtifactId] = (false, new IOException("Simulated optional layer close IO failure.")),
                [layer1.SourceProduct.ArtifactId] = (false, new InvalidOperationException("Simulated optional layer close state error.")),
                [layer2.SourceProduct.ArtifactId] = (true, new IOException("Simulated broken layer close IO failure."))
            });
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            TimeProvider.System,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout);

        var rendered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status,
            "Throwing optional-layer DisposeAsync must not turn an otherwise valid result into Unavailable.");
        Assert.IsNotNull(rendered.Presentation);
        Assert.HasCount(2, rendered.Presentation.Layers);
        CollectionAssert.AreEqual(
            new[] { layer0.LayerKind, layer1.LayerKind },
            rendered.Presentation.Layers.Select(static layer => layer.Kind).ToArray());
        Assert.AreEqual(3, probe.DisposeAttempts);

        // Verify permits were cleanly released after throwing DisposeAsync by running a second read.
        var second = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, second.Status);
        Assert.IsNotNull(second.Presentation);
        Assert.HasCount(2, second.Presentation.Layers);
    }

    [TestMethod]
    public async Task CenteredPresentationReaderReleasesGatePermitsWhenTimedOutReadsFinishMidCallAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int stalledCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        const int totalLayers = stalledCount * 2;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: totalLayers,
            "gen-l2-read-timeout-release",
            CancellationToken.None).ConfigureAwait(false);

        var stalledArtifactIds = enriched.Revision.Manifest.Layers.Take(stalledCount)
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();
        var trailingLayerKinds = enriched.Revision.Manifest.Layers.Skip(stalledCount)
            .Select(static layer => layer.LayerKind)
            .ToArray();
        var trailingArtifactIds = enriched.Revision.Manifest.Layers.Skip(stalledCount)
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        using var probe = new ReadTimeoutReleaseProbeArtifactService(
            fixture.Service,
            stalledArtifactIds,
            trailingArtifactIds,
            stalledCount);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            var readTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForAllStalledReadsEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Trigger the 2.0s per-layer read timeout while 1.5s still remains on the 3.5s overall budget.
            // Confirm deterministically that all 4 stalled reads observed cancellation AND handed off to
            // DisposeAfterReadAsync (causing all 4 workers to queue trailing layers on the saturated gate).
            time.Advance(CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout);
            await probe.WaitForAllStalledCancellationsObservedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitForConditionAsync(
                () => reader.PendingLayerReadWaiterCount == trailingLayerKinds.Length,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsFalse(readTask.IsCompleted);
            Assert.AreEqual(0, reader.AvailableLayerReadPermits,
                "All 4 gate permits must remain held by detached DisposeAfterReadAsync handoffs.");
            Assert.AreEqual(0, probe.TrailingLayerOpens,
                "Trailing healthy layers must wait while the 4 timed-out reads still hold their gate permits.");

            // Unblock the 4 timed-out reads mid-call so DisposeAfterReadAsync completes and releases permits:
            // if gate.Release() were missing from DisposeAfterReadAsync, trailing layers would never acquire permits.
            probe.ReleaseStalledReads();
            var rendered = await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status);
            Assert.IsNotNull(rendered.Presentation);
            CollectionAssert.AreEqual(
                trailingLayerKinds,
                rendered.Presentation.Layers.Select(static layer => layer.Kind).ToArray(),
                "Releasing permits from timed-out reads mid-call must allow subsequent healthy layers to render cleanly.");
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == stalledCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderCancelsPromptlyWhenDetachedReadDisposeBlocksSynchronouslyAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: permitCount,
            "gen-m5-read-sync-dispose",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        using var probe = new CanceledSyncDisposeProbeArtifactService(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            permitCount,
            stallAtOpen: false);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            TimeProvider.System,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            permitCount,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            using var callerCts = new CancellationTokenSource();
            var firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
            await probe.WaitForAllOperationsEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Cancel the caller synchronously on a dedicated non-ThreadPool thread: each readTask cancels on the token
            // while DisposeAsync blocks synchronously on a ManualResetEventSlim. Both Cancel() and ReadAsync must return promptly.
            var (_, cancelElapsed) = await RunOnDedicatedThreadAsync(
                threadId =>
                {
                    probe.SetCancellingThreadId(threadId);
                    callerCts.Cancel();
                    return true;
                }).ConfigureAwait(false);
            Assert.IsLessThan(TimeSpan.FromSeconds(2), cancelElapsed,
                "Synchronous caller cancellation must return promptly without blocking on detached DisposeAsync.");
            await Assert.ThrowsAsync<OperationCanceledException>(() => firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);
            Assert.AreEqual(0, probe.CompletedDisposeCount,
                "Caller cancellation must return before synchronously blocking detached DisposeAsync finishes.");

            await probe.WaitForAllDisposesEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(0, probe.DisposesOnCancellingThread,
                "Detached read DisposeAsync must not execute on the caller's cancelling thread.");
            Assert.AreEqual(0, reader.AvailableLayerReadPermits,
                "Gate permits must stay held until detached read disposal completes.");

            var secondReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForSecondPreviewOpenedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitForConditionAsync(
                () => reader.PendingLayerReadWaiterCount == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsFalse(secondReadTask.IsCompleted,
                "Second ReadAsync must wait while detached read disposals still hold all gate permits.");
            Assert.AreEqual(0, probe.SecondCallLayerOpens,
                "No second-call layer open may start until detached read disposal releases its permit.");

            // Unblock synchronously blocking DisposeAsync -> permits release -> second call succeeds cleanly.
            probe.ReleaseDisposes();
            var secondRendered = await secondReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, secondRendered.Status);
            Assert.IsNotNull(secondRendered.Presentation);
            Assert.HasCount(permitCount, secondRendered.Presentation.Layers);
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderOffloadsLateOpenSyncBlockingDisposeAndHoldsGatePermitAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: permitCount,
            "gen-m5-open-sync-dispose",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        // Part 1: Direct test of DisposeLateOpenedContentAsync with a synchronously-completed openTask whose
        // DisposeAsync blocks synchronously (and also when DisposeAsync throws a non-fatal exception).
        var directGate = new CenteredViewPresentationReader.LayerReadGate(1);
        Assert.IsTrue(await directGate.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, directGate.AvailablePermits);

        var sampleLayerId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
        var openedDirect = await fixture.Service.OpenContentAsync(sampleLayerId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(openedDirect.Content);
        using var directDisposeGate = new ManualResetEventSlim(false);
        var directDisposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directStream = new BlockingTestDisposeStream(
            openedDirect.Content,
            directDisposeGate,
            directDisposeEntered,
            exceptionOnDispose: null);
        var directResult = new CameraAgentArtifactContentResult(
            openedDirect.Status,
            new CameraAgentArtifactContentStream(
                directStream,
                openedDirect.Content.ArtifactId,
                openedDirect.Content.CaptureId,
                openedDirect.Content.Role,
                openedDirect.Content.MediaType,
                openedDirect.Content.ByteLength,
                openedDirect.Content.ChecksumSha256,
                openedDirect.Content.FileName,
                openedDirect.Content.Descriptor,
                openedDirect.Content.EncodedWidth,
                openedDirect.Content.EncodedHeight));

        try
        {
            var callingThreadId = 0;
            var (directCleanupTask, directElapsed) = await RunOnDedicatedThreadAsync(
                threadId =>
                {
                    callingThreadId = threadId;
                    return CenteredViewPresentationReader.DisposeLateOpenedContentAsync(
                        Task.FromResult(directResult),
                        directGate);
                }).ConfigureAwait(false);

            Assert.IsLessThan(TimeSpan.FromSeconds(2), directElapsed,
                "DisposeLateOpenedContentAsync must return immediately without executing sync-blocking DisposeAsync inline.");
            Assert.IsFalse(directStream.DisposeCompleted,
                "Sync-blocking DisposeAsync must not have completed before the test releases the gate.");
            await directDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreNotEqual(callingThreadId, directStream.DisposeThreadId,
                "Offloaded late-open DisposeAsync must execute on a background thread, not inline on the caller's thread.");
            Assert.IsFalse(directCleanupTask.IsCompleted);
            Assert.AreEqual(0, directGate.AvailablePermits,
                "Gate permit must remain held while offloaded late-open DisposeAsync is blocked.");

            directDisposeGate.Set();
            await directCleanupTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(TaskStatus.RanToCompletion, directCleanupTask.Status);
            Assert.AreEqual(1, directGate.AvailablePermits,
                "Gate permit must be released exactly once after late-open DisposeAsync finishes.");

            // Verify non-fatal disposal exception is swallowed without faulting the detached task and still releases the permit.
            Assert.IsTrue(await directGate.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false));
            var openedThrow = await fixture.Service.OpenContentAsync(sampleLayerId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(openedThrow.Content);
            using var throwDisposeGate = new ManualResetEventSlim(false);
            var throwDisposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var throwStream = new BlockingTestDisposeStream(
                openedThrow.Content,
                throwDisposeGate,
                throwDisposeEntered,
                exceptionOnDispose: new IOException("Simulated late-open dispose IO failure."));
            var throwResult = new CameraAgentArtifactContentResult(
                openedThrow.Status,
                new CameraAgentArtifactContentStream(
                    throwStream,
                    openedThrow.Content.ArtifactId,
                    openedThrow.Content.CaptureId,
                    openedThrow.Content.Role,
                    openedThrow.Content.MediaType,
                    openedThrow.Content.ByteLength,
                    openedThrow.Content.ChecksumSha256,
                    openedThrow.Content.FileName,
                    openedThrow.Content.Descriptor,
                    openedThrow.Content.EncodedWidth,
                    openedThrow.Content.EncodedHeight));

            var throwCleanupTask = CenteredViewPresentationReader.DisposeLateOpenedContentAsync(
                Task.FromResult(throwResult),
                directGate);
            try
            {
                await throwDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(0, directGate.AvailablePermits);
                throwDisposeGate.Set();
                await throwCleanupTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(TaskStatus.RanToCompletion, throwCleanupTask.Status);
                Assert.AreEqual(1, directGate.AvailablePermits);
            }
            finally
            {
                throwDisposeGate.Set();
            }
        }
        finally
        {
            directDisposeGate.Set();
        }

        // Part 2: End-to-end ReadAsync late-open cancellation with synchronously blocking DisposeAsync.
        using var probe = new CanceledSyncDisposeProbeArtifactService(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            permitCount,
            stallAtOpen: true);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            TimeProvider.System,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            permitCount,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            using var callerCts = new CancellationTokenSource();
            var firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
            await probe.WaitForAllOperationsEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var (_, cancelElapsed) = await RunOnDedicatedThreadAsync(
                _ =>
                {
                    callerCts.Cancel();
                    return true;
                }).ConfigureAwait(false);
            Assert.IsLessThan(TimeSpan.FromSeconds(2), cancelElapsed,
                "Synchronous caller cancellation must return promptly on the late-open path.");
            await Assert.ThrowsAsync<OperationCanceledException>(() => firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);
            Assert.AreEqual(0, reader.AvailableLayerReadPermits,
                "Cancellation must leave every permit held while its layer open is outstanding.");

            // Release the 4 late opens; their wrapped streams enter synchronously blocking DisposeAsync while holding permits.
            probe.ReleaseLateOpens();
            await probe.WaitForAllDisposesEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(0, probe.CompletedDisposeCount,
                "All late-open disposals have entered and must stay blocked until released.");
            Assert.AreEqual(0, reader.AvailableLayerReadPermits,
                "Every permit must remain held while its late-open disposal is blocked.");

            var secondReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForSecondPreviewOpenedAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitForConditionAsync(
                () => reader.PendingLayerReadWaiterCount == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsFalse(secondReadTask.IsCompleted);
            Assert.AreEqual(0, probe.SecondCallLayerOpens);

            probe.ReleaseDisposes();
            var secondRendered = await secondReadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, secondRendered.Status);
            Assert.IsNotNull(secondRendered.Presentation);
            Assert.HasCount(permitCount, secondRendered.Presentation.Layers);

            // The second read can reuse a permit while other late-open disposals are still running.
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.AreEqual(permitCount, probe.CompletedDisposeCount,
                "Every late-open disposal must finish before all permits return to the gate.");
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CenteredPresentationReaderDoesNotRunBlockingDisposeOnCancellingThreadWhenInlineCompletionFiresDuringCancelAsync(
        bool stallAtOpen)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: permitCount,
            stallAtOpen ? "gen-m1-inline-open" : "gen-m1-inline-read",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToArray();

        using var probe = await InlineCancelCompletionProbeArtifactService.CreateAsync(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            stallAtOpen).ConfigureAwait(false);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            TimeProvider.System,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            permitCount,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            using var callerCts = new CancellationTokenSource();
            Task<CameraAgentLayeredPresentationResult>? firstReadTask = null;

            // Register BEFORE ReadAsync so LIFO cancellation runs ReadAsync's linked-token callbacks first
            // (detaching DisposeLateOpenedContentAsync / DisposeAfterReadAsync), and runs THIS callback
            // second on the caller's synchronous Cancel() thread, completing the stalled open/read inline.
            using var inlineCompletionRegistration = callerCts.Token.Register(() =>
            {
                SpinWait.SpinUntil(() => firstReadTask is { IsCompleted: true }, TimeSpan.FromSeconds(2));
                using var settle = new ManualResetEventSlim(false);
                settle.Wait(30);
                probe.CompletePendingOperationsInline();
            });

            firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
            await probe.WaitForAllOperationsEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var (_, cancelElapsed) = await RunOnDedicatedThreadAsync(
                threadId =>
                {
                    probe.SetCancellingThreadId(threadId);
                    callerCts.Cancel();
                    return true;
                }).ConfigureAwait(false);

            Assert.IsLessThan(TimeSpan.FromSeconds(2), cancelElapsed,
                "Synchronous callerCts.Cancel() must return promptly even when inline completion fires during cancellation.");
            await Assert.ThrowsAsync<OperationCanceledException>(() => firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);

            await probe.WaitForAllDisposesEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(0, probe.DisposesOnCancellingThread,
                "Zero DisposeAsync calls may run on the caller's synchronous cancelling thread.");
            Assert.AreEqual(0, probe.CompletedDisposeCount,
                "Blocking DisposeAsync must still be waiting on the background thread before release.");
            Assert.AreEqual(0, reader.AvailableLayerReadPermits,
                "Gate permits must stay held until offloaded DisposeAsync finishes.");

            probe.ReleaseDisposes();
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            Assert.AreEqual(0, probe.OpenStreamCount);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderSwallowsDetachedReadDisposeExceptionAndReleasesResourcesAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: permitCount,
            "gen-l2-read-dispose-throw",
            CancellationToken.None).ConfigureAwait(false);

        var sentinelMessage = $"Simulated detached read dispose IO failure ({Guid.NewGuid():N}).";
        var unobservedSentinelExceptions = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(ex =>
                    ex.Message.Contains(sentinelMessage, StringComparison.Ordinal)))
            {
                lock (unobservedSentinelExceptions)
                {
                    unobservedSentinelExceptions.Add(args.Exception);
                }
                args.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // Part 1: Direct unit test of DisposeAfterReadAsync with a throwing DisposeAsync on both
            // canceled and completed readTask states. Removing the inner catch at DisposeAfterReadAsync
            // faults cleanupTask and fails this test deterministically.
            var sampleLayerId = enriched.Revision.Manifest.Layers[0].SourceProduct.ArtifactId;
            foreach (var readTask in new Task[]
                     {
                         Task.FromCanceled(new CancellationToken(canceled: true)),
                         Task.CompletedTask
                     })
            {
                var gate = new CenteredViewPresentationReader.LayerReadGate(1);
                Assert.IsTrue(await gate.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false));
                var budget = new CenteredViewPresentationReader.PayloadBudget(1024);
                Assert.IsTrue(budget.TryReserve(256));

                var opened = await fixture.Service.OpenContentAsync(sampleLayerId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(opened.Content);
                using var disposeGate = new ManualResetEventSlim(true);
                var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var throwingStream = new BlockingTestDisposeStream(
                    opened.Content,
                    disposeGate,
                    disposeEntered,
                    exceptionOnDispose: new IOException(sentinelMessage));
                var wrappedContent = new CameraAgentArtifactContentStream(
                    throwingStream,
                    opened.Content.ArtifactId,
                    opened.Content.CaptureId,
                    opened.Content.Role,
                    opened.Content.MediaType,
                    opened.Content.ByteLength,
                    opened.Content.ChecksumSha256,
                    opened.Content.FileName,
                    opened.Content.Descriptor,
                    opened.Content.EncodedWidth,
                    opened.Content.EncodedHeight);

                var cleanupTask = CenteredViewPresentationReader.DisposeAfterReadAsync(
                    readTask,
                    wrappedContent,
                    budget,
                    reservedBytes: 256,
                    gate);

                await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(TaskStatus.RanToCompletion, cleanupTask.Status,
                    "DisposeAfterReadAsync must swallow non-fatal DisposeAsync exceptions instead of faulting.");
                Assert.AreEqual(1024, budget.RemainingBytes,
                    "Payload budget must be released even when detached read DisposeAsync throws.");
                Assert.AreEqual(1, gate.AvailablePermits,
                    "Gate permit must be released even when detached read DisposeAsync throws.");
            }

            // Part 2: End-to-end ReadAsync path with detached read DisposeAsync throwing a sentinel IOException.
            var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
            var layerArtifactIds = enriched.Revision.Manifest.Layers
                .Select(static layer => layer.SourceProduct.ArtifactId)
                .ToHashSet();

            using var probe = new CanceledSyncDisposeProbeArtifactService(
                fixture.Service,
                previewArtifactId,
                layerArtifactIds,
                permitCount,
                stallAtOpen: false,
                exceptionOnDisposeFactory: () => new IOException(sentinelMessage));
            var reader = new CenteredViewPresentationReader(
                views,
                probe,
                TimeProvider.System,
                CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
                permitCount,
                CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
                CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

            try
            {
                using var callerCts = new CancellationTokenSource();
                var firstReadTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, callerCts.Token).AsTask();
                await probe.WaitForAllOperationsEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                await callerCts.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(() => firstReadTask.WaitAsync(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(false);

                await probe.WaitForAllDisposesEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                probe.ReleaseDisposes();
                await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
                await WaitForConditionAsync(
                    () => reader.AvailableLayerReadPermits == permitCount,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                var recovered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, recovered.Status);
                Assert.IsNotNull(recovered.Presentation);
                Assert.HasCount(permitCount, recovered.Presentation.Layers);
            }
            finally
            {
                await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobservedSentinelExceptions)
            {
                Assert.IsEmpty(unobservedSentinelExceptions,
                    "Swallowed read-path DisposeAsync exceptions must produce zero unobserved task exceptions.");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [TestMethod]
    public async Task CenteredPresentationReaderUnlinksCanceledGateWaitersWhileSaturatedAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        const int permitCount = CenteredViewPresentationReader.DefaultMaximumConcurrentLayerReads;
        var (views, enriched) = await CreateMultiLayerCenteredViewAsync(
            fixture,
            layerCount: permitCount,
            "gen-m7-unlink-waiters",
            CancellationToken.None).ConfigureAwait(false);

        var previewArtifactId = enriched.Base.Definition.Preview.Product.ArtifactId;
        var layerArtifactIds = enriched.Revision.Manifest.Layers
            .Select(static layer => layer.SourceProduct.ArtifactId)
            .ToHashSet();

        var time = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
        var probe = new OverlappingGateProbeArtifactService(
            fixture.Service,
            previewArtifactId,
            layerArtifactIds,
            stalledOpenCount: permitCount);
        var reader = new CenteredViewPresentationReader(
            views,
            probe,
            time,
            CenteredViewPresentationReader.DefaultOptionalLayerReadTimeout,
            permitCount,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

        try
        {
            // Pin all 4 permits with stalled opens and let the pinning call time out cleanly.
            var pinTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
            await probe.WaitForStalledOpensEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            time.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);

            var pinnedResult = await pinTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, pinnedResult.Status);
            Assert.IsNotNull(pinnedResult.Presentation);
            Assert.IsEmpty(pinnedResult.Presentation.Layers);
            Assert.AreEqual(0, reader.AvailableLayerReadPermits);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);

            // Execute 40 caller-canceled waits + 40 timed-out waits (80 calls = 320 enqueued waiters) while all 4 permits stay pinned.
            const int canceledIterations = 40;
            for (var iteration = 0; iteration < canceledIterations; iteration++)
            {
                using var cts = new CancellationTokenSource();
                var waitTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, cts.Token).AsTask();
                await WaitForConditionAsync(
                    () => reader.PendingLayerReadWaiterCount == permitCount,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(false);
                Assert.AreEqual(0, reader.PendingLayerReadWaiterCount,
                    "Canceled waiters must be unlinked immediately while the gate remains saturated.");
            }

            const int timedOutIterations = 40;
            for (var iteration = 0; iteration < timedOutIterations; iteration++)
            {
                var waitTask = reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None).AsTask();
                await WaitForConditionAsync(
                    () => reader.PendingLayerReadWaiterCount == permitCount,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                time.Advance(CenteredViewPresentationReader.DefaultOverallOptionalLayersReadTimeout);
                var timedOutResult = await waitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, timedOutResult.Status);
                Assert.IsNotNull(timedOutResult.Presentation);
                Assert.IsEmpty(timedOutResult.Presentation.Layers);
                Assert.AreEqual(0, reader.PendingLayerReadWaiterCount,
                    "Timed-out waiters must be unlinked immediately while the gate remains saturated.");
            }

            Assert.AreEqual(permitCount, probe.TotalLayerOpens,
                "No additional layer opens may start while all 4 permits remain pinned.");
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);

            // Release the 4 pinned operations and confirm all 4 permits recover cleanly.
            probe.ReleaseStalledOpens();
            await WaitForConditionAsync(
                () => reader.AvailableLayerReadPermits == permitCount,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var recovered = await reader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, recovered.Status);
            Assert.IsNotNull(recovered.Presentation);
            Assert.HasCount(permitCount, recovered.Presentation.Layers);
            Assert.AreEqual(permitCount, reader.AvailableLayerReadPermits);
            Assert.AreEqual(0, reader.PendingLayerReadWaiterCount);
        }
        finally
        {
            await probe.ReleaseAndDrainAsync().ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Forwards any dedicated-thread exception to the awaiting test task.")]
    private static Task<(T Result, TimeSpan Elapsed)> RunOnDedicatedThreadAsync<T>(Func<int, T> action)
    {
        var tcs = new TaskCompletionSource<(T Result, TimeSpan Elapsed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var threadId = Environment.CurrentManagedThreadId;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = action(threadId);
                tcs.TrySetResult((result, Stopwatch.GetElapsedTime(started)));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "CenteredPresentationReader-DedicatedCaller"
        };
        thread.Start();
        return tcs.Task;
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout.TotalSeconds:F1}s.");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static async Task<(SqliteCenteredViews Views, CenteredView Enriched)> CreateMultiLayerCenteredViewAsync(
        ArtifactFixture fixture,
        int layerCount,
        string generation,
        CancellationToken token)
    {
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, silo: fixture.Storage);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 64;
        const int height = 48;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);
        var pixels = new byte[width * height * 2];
        var raw = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), generation);
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

        var enriched = single;
        for (var index = 0; index < layerCount; index++)
        {
            var payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
                segments: [new(new(2 + index, 4), new(20 + index, 20), 2, new(255, 255, 255))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, $"{generation}-layer-{index}-v1", [sourceArtifact], $"v{index}");
            await persistence.PersistCenteredViewProductAsync(input.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(Guid.NewGuid(), $"{generation}-slot-{index}", payload.SchemaVersion, product.Variant);
            var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            var productRef = new ExposureProductRef(input.Key, slot, artifactId,
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            enriched = (await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
                new(input.Key, slot, ExposureProductAvailability.Available, index + 1, productRef, null),
                new($"{generation}-layer-{index:D2}", index, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false))!;
        }
        Assert.HasCount(layerCount, enriched.Revision.Manifest.Layers);
        return (views, enriched);
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

    private sealed class StreamReadCountingArtifactService(
        ICameraAgentArtifactService inner,
        HashSet<Guid> trackedArtifactIds) : ICameraAgentArtifactService
    {
        private int _streamsRead;
        private int _openStreams;

        public int StreamsReadCount => Volatile.Read(ref _streamsRead);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (!trackedArtifactIds.Contains(artifactId) || opened.Content is not { } content)
            {
                return opened;
            }
            Interlocked.Increment(ref _openStreams);
            var wrapped = new CountingStream(content, this);
            return new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    wrapped,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        private sealed class CountingStream(CameraAgentArtifactContentStream innerContent, StreamReadCountingArtifactService owner) : Stream
        {
            private int _readRecorded;
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                RecordRead();
                return innerContent.Read(buffer, offset, count);
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                RecordRead();
                return innerContent.ReadAsync(buffer, cancellationToken);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    Interlocked.Decrement(ref owner._openStreams);
                }
                base.Dispose(disposing);
            }
            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    Interlocked.Decrement(ref owner._openStreams);
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }

            private void RecordRead()
            {
                if (Interlocked.Exchange(ref _readRecorded, 1) == 0)
                {
                    Interlocked.Increment(ref owner._streamsRead);
                }
            }
        }
    }

    private sealed class StalledConcurrencyProbeArtifactService(
        ICameraAgentArtifactService inner,
        HashSet<Guid> stalledArtifactIds,
        string stallMode,
        int expectedOperationCount = 1,
        TimeSpan? safetyTimeout = null) : ICameraAgentArtifactService, IDisposable
    {
        private readonly ManualResetEventSlim _syncGate = new(false);
        private readonly TimeSpan _syncWaitTimeout = safetyTimeout ?? TimeSpan.FromSeconds(10);
        private readonly TimeSpan _drainTimeout = safetyTimeout ?? TimeSpan.FromSeconds(5);
        private readonly TaskCompletionSource _asyncGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _expectedOperationsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        private int _totalStarted;
        private int _inFlight;
        private int _openStreams;
        private int _released;

        public int PeakActiveOperations => Volatile.Read(ref _peak);
        public int TotalStartedOperations => Volatile.Read(ref _totalStarted);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public Task WaitForExpectedOperationsEnteredAsync() => _expectedOperationsEntered.Task;

        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (!stalledArtifactIds.Contains(artifactId))
            {
                return inner.OpenContentAsync(artifactId, cancellationToken);
            }

            if (stallMode == "async-open")
            {
                return OpenStalledAsync();
            }

            return OpenWithStalledReadStreamAsync(artifactId, cancellationToken);
        }

        private async ValueTask<CameraAgentArtifactContentResult> OpenStalledAsync()
        {
            EnterOperation();
            try
            {
                await _asyncGate.Task.ConfigureAwait(false);
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
            }
            finally
            {
                ExitOperation();
            }
        }

        private async ValueTask<CameraAgentArtifactContentResult> OpenWithStalledReadStreamAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (opened.Content is not { } content)
            {
                return opened;
            }
            Interlocked.Increment(ref _openStreams);
            var stream = new StalledReadStream(content, this, stallMode == "sync-read");
            return new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    stream,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));
        }

        private void EnterOperation()
        {
            Interlocked.Increment(ref _totalStarted);
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
            if (current >= expectedOperationCount)
            {
                _expectedOperationsEntered.TrySetResult();
            }
        }

        private void ExitOperation()
        {
            Interlocked.Decrement(ref _active);
            if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _released) != 0 && Volatile.Read(ref _openStreams) == 0)
            {
                _drained.TrySetResult();
            }
        }

        private void OnStreamDisposed()
        {
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _released) != 0 && Volatile.Read(ref _inFlight) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            Interlocked.Exchange(ref _released, 1);
            _syncGate.Set();
            _asyncGate.TrySetResult();
            if (Volatile.Read(ref _inFlight) > 0 || Volatile.Read(ref _openStreams) > 0)
            {
                await _drained.Task.WaitAsync(_drainTimeout).ConfigureAwait(false);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose() => _syncGate.Dispose();

        private sealed class StalledReadStream(
            CameraAgentArtifactContentStream innerContent,
            StalledConcurrencyProbeArtifactService owner,
            bool synchronousStall) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (synchronousStall)
                {
                    owner.EnterOperation();
                    try
                    {
                        owner._syncGate.Wait(owner._syncWaitTimeout, CancellationToken.None);
                        return ValueTask.FromResult(0);
                    }
                    finally
                    {
                        owner.ExitOperation();
                    }
                }
                return ReadStalledAsync();
            }

            private async ValueTask<int> ReadStalledAsync()
            {
                owner.EnterOperation();
                try
                {
                    await owner._asyncGate.Task.ConfigureAwait(false);
                    return 0;
                }
                finally
                {
                    owner.ExitOperation();
                }
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    owner.OnStreamDisposed();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    owner.OnStreamDisposed();
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CooperativeMultiWaveSlowArtifactService(
        ICameraAgentArtifactService inner,
        Guid healthyArtifactId,
        HashSet<Guid> slowArtifactIds) : ICameraAgentArtifactService
    {
        private readonly Lock _gate = new();
        private readonly List<(int Target, TaskCompletionSource Signal)> _waiters = [];
        private readonly TaskCompletionSource _healthyCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _slowStarted;

        public int SlowStartedCount => Volatile.Read(ref _slowStarted);

        public Task WaitForHealthyCompletedAsync() => _healthyCompleted.Task;

        public Task WaitForSlowStartedAsync(int target)
        {
            lock (_gate)
            {
                if (_slowStarted >= target)
                {
                    return Task.CompletedTask;
                }
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((target, tcs));
                return tcs.Task;
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (slowArtifactIds.Contains(artifactId))
            {
                List<TaskCompletionSource>? toSignal = null;
                lock (_gate)
                {
                    var count = ++_slowStarted;
                    for (var i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (count >= _waiters[i].Target)
                        {
                            toSignal ??= [];
                            toSignal.Add(_waiters[i].Signal);
                            _waiters.RemoveAt(i);
                        }
                    }
                }
                if (toSignal is not null)
                {
                    foreach (var signal in toSignal)
                    {
                        signal.TrySetResult();
                    }
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
            }

            var result = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (artifactId == healthyArtifactId)
            {
                _healthyCompleted.TrySetResult();
            }
            return result;
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);
    }

    private sealed class OverlappingGateProbeArtifactService(
        ICameraAgentArtifactService inner,
        Guid previewArtifactId,
        HashSet<Guid> layerArtifactIds,
        int stalledOpenCount) : ICameraAgentArtifactService
    {
        private readonly TaskCompletionSource _stalledEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPreviewOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _previewOpens;
        private int _totalLayerOpens;
        private int _stalledEnteredCount;
        private int _activeLayerOpens;
        private int _peakActiveLayerOpens;
        private int _inFlightStalled;
        private int _released;

        public int PeakActiveLayerOpens => Volatile.Read(ref _peakActiveLayerOpens);
        public int TotalLayerOpens => Volatile.Read(ref _totalLayerOpens);

        public Task WaitForStalledOpensEnteredAsync() => _stalledEntered.Task;
        public Task WaitForSecondPreviewOpenedAsync() => _secondPreviewOpened.Task;
        public void ReleaseStalledOpens()
        {
            Interlocked.Exchange(ref _released, 1);
            _releaseStalled.TrySetResult();
            if (Volatile.Read(ref _inFlightStalled) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            ReleaseStalledOpens();
            if (Volatile.Read(ref _inFlightStalled) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == previewArtifactId)
            {
                var result = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _previewOpens) >= 2)
                {
                    _secondPreviewOpened.TrySetResult();
                }
                return result;
            }

            if (!layerArtifactIds.Contains(artifactId))
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var callIndex = Interlocked.Increment(ref _totalLayerOpens);
            var active = Interlocked.Increment(ref _activeLayerOpens);
            while (true)
            {
                var peak = Volatile.Read(ref _peakActiveLayerOpens);
                if (active <= peak || Interlocked.CompareExchange(ref _peakActiveLayerOpens, active, peak) == peak)
                {
                    break;
                }
            }

            if (callIndex <= stalledOpenCount)
            {
                Interlocked.Increment(ref _inFlightStalled);
                try
                {
                    if (Interlocked.Increment(ref _stalledEnteredCount) >= stalledOpenCount)
                    {
                        _stalledEntered.TrySetResult();
                    }
                    await _releaseStalled.Task.ConfigureAwait(false);
                    return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeLayerOpens);
                    if (Interlocked.Decrement(ref _inFlightStalled) == 0 && Volatile.Read(ref _released) != 0)
                    {
                        _drained.TrySetResult();
                    }
                }
            }

            try
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeLayerOpens);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);
    }

    private sealed class SlowPreviewAndLayersArtifactService(
        ICameraAgentArtifactService inner,
        Guid previewArtifactId,
        Guid healthyArtifactId,
        HashSet<Guid> slowArtifactIds,
        TaskCompletionSource previewEntered,
        TaskCompletionSource previewRelease) : ICameraAgentArtifactService
    {
        private readonly Lock _gate = new();
        private readonly List<(int Target, TaskCompletionSource Signal)> _waiters = [];
        private readonly TaskCompletionSource _healthyCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _slowStarted;
        private int _totalLayerOpens;

        public int TotalLayerOpens => Volatile.Read(ref _totalLayerOpens);
        public Task WaitForHealthyCompletedAsync() => _healthyCompleted.Task;

        public Task WaitForSlowStartedAsync(int target)
        {
            lock (_gate)
            {
                if (_slowStarted >= target)
                {
                    return Task.CompletedTask;
                }
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((target, tcs));
                return tcs.Task;
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == previewArtifactId)
            {
                previewEntered.TrySetResult();
                await previewRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _totalLayerOpens);
            if (slowArtifactIds.Contains(artifactId))
            {
                List<TaskCompletionSource>? toSignal = null;
                lock (_gate)
                {
                    var count = ++_slowStarted;
                    for (var i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (count >= _waiters[i].Target)
                        {
                            toSignal ??= [];
                            toSignal.Add(_waiters[i].Signal);
                            _waiters.RemoveAt(i);
                        }
                    }
                }
                if (toSignal is not null)
                {
                    foreach (var signal in toSignal)
                    {
                        signal.TrySetResult();
                    }
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable);
            }

            var result = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (artifactId == healthyArtifactId)
            {
                _healthyCompleted.TrySetResult();
            }
            return result;
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);
    }

    private sealed class StalledDisposeArtifactService(
        ICameraAgentArtifactService inner,
        Guid previewArtifactId,
        HashSet<Guid> layerArtifactIds,
        int stalledDisposeCount,
        bool synchronousDisposeStall) : ICameraAgentArtifactService, IDisposable
    {
        private readonly ManualResetEventSlim _syncDisposeGate = new(false);
        private readonly TaskCompletionSource _asyncDisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allDisposesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPreviewOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _previewOpens;
        private int _layerOpens;
        private int _secondCallLayerOpens;
        private int _disposesEntered;
        private int _openStreams;
        private int _released;

        public int SecondCallLayerOpens => Volatile.Read(ref _secondCallLayerOpens);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);

        public Task WaitForAllStalledDisposesEnteredAsync() => _allDisposesEntered.Task;
        public Task WaitForSecondPreviewOpenedAsync() => _secondPreviewOpened.Task;

        public void ReleaseDisposes()
        {
            Interlocked.Exchange(ref _released, 1);
            _syncDisposeGate.Set();
            _asyncDisposeGate.TrySetResult();
            if (Volatile.Read(ref _openStreams) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            ReleaseDisposes();
            if (Volatile.Read(ref _openStreams) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == previewArtifactId)
            {
                var preview = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _previewOpens) >= 2)
                {
                    _secondPreviewOpened.TrySetResult();
                }
                return preview;
            }

            if (!layerArtifactIds.Contains(artifactId))
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var callIndex = Interlocked.Increment(ref _layerOpens);
            if (callIndex > stalledDisposeCount)
            {
                Interlocked.Increment(ref _secondCallLayerOpens);
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var opened = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (opened.Content is not { } content)
            {
                return opened;
            }

            Interlocked.Increment(ref _openStreams);
            var stream = new StalledDisposeStream(content, this, synchronousDisposeStall);
            return new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    stream,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));
        }

        private void OnDisposeEntered()
        {
            if (Interlocked.Increment(ref _disposesEntered) >= stalledDisposeCount)
            {
                _allDisposesEntered.TrySetResult();
            }
        }

        private void OnDisposeCompleted()
        {
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _released) != 0)
            {
                _drained.TrySetResult();
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose() => _syncDisposeGate.Dispose();

        private sealed class StalledDisposeStream(
            CameraAgentArtifactContentStream innerContent,
            StalledDisposeArtifactService owner,
            bool synchronousStall) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => innerContent.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                innerContent.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    owner.OnDisposeCompleted();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.OnDisposeEntered();
                    if (synchronousStall)
                    {
                        owner._syncDisposeGate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                        await innerContent.DisposeAsync().ConfigureAwait(false);
                        owner.OnDisposeCompleted();
                    }
                    else
                    {
                        try
                        {
                            await owner._asyncDisposeGate.Task.ConfigureAwait(false);
                        }
                        finally
                        {
                            await innerContent.DisposeAsync().ConfigureAwait(false);
                            owner.OnDisposeCompleted();
                        }
                    }
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ThrowingDisposeArtifactService(
        ICameraAgentArtifactService inner,
        Dictionary<Guid, (bool FailRead, Exception DisposeException)> faultByArtifactId) : ICameraAgentArtifactService
    {
        private int _disposeAttempts;

        public int DisposeAttempts => Volatile.Read(ref _disposeAttempts);

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (!faultByArtifactId.TryGetValue(artifactId, out var fault) || opened.Content is not { } content)
            {
                return opened;
            }

            var wrapped = new ThrowingDisposeStream(content, fault.FailRead, fault.DisposeException, () => Interlocked.Increment(ref _disposeAttempts));
            return new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    wrapped,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        private sealed class ThrowingDisposeStream(
            CameraAgentArtifactContentStream innerContent,
            bool failRead,
            Exception disposeException,
            Action onDisposeAttempt) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                failRead ? throw new InvalidDataException("Simulated optional layer corrupt stream.") : innerContent.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                failRead
                    ? ValueTask.FromException<int>(new InvalidDataException("Simulated optional layer corrupt stream."))
                    : innerContent.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
                {
                    innerContent.Dispose();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                var shouldThrow = false;
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    onDisposeAttempt();
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    shouldThrow = true;
                }
                await base.DisposeAsync().ConfigureAwait(false);
                if (shouldThrow)
                {
                    throw disposeException;
                }
            }
        }
    }

    private sealed class ReadTimeoutReleaseProbeArtifactService(
        ICameraAgentArtifactService inner,
        HashSet<Guid> stalledArtifactIds,
        HashSet<Guid> trailingArtifactIds,
        int expectedStalledCount) : ICameraAgentArtifactService, IDisposable
    {
        private readonly TaskCompletionSource _allStalledEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allStalledCancellationsObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stalledEntered;
        private int _stalledCancellationsObserved;
        private int _trailingOpens;
        private int _openStreams;
        private int _released;

        public int TrailingLayerOpens => Volatile.Read(ref _trailingOpens);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);

        public Task WaitForAllStalledReadsEnteredAsync() => _allStalledEntered.Task;
        public Task WaitForAllStalledCancellationsObservedAsync() => _allStalledCancellationsObserved.Task;

        public void ReleaseStalledReads()
        {
            Interlocked.Exchange(ref _released, 1);
            _releaseStalled.TrySetResult();
            if (Volatile.Read(ref _openStreams) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            ReleaseStalledReads();
            if (Volatile.Read(ref _openStreams) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (trailingArtifactIds.Contains(artifactId))
            {
                Interlocked.Increment(ref _trailingOpens);
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            if (!stalledArtifactIds.Contains(artifactId))
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var opened = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (opened.Content is not { } content)
            {
                return opened;
            }

            Interlocked.Increment(ref _openStreams);
            var stream = new BlockedReadStream(content, this);
            return new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    stream,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));
        }

        private void OnReadEntered()
        {
            if (Interlocked.Increment(ref _stalledEntered) >= expectedStalledCount)
            {
                _allStalledEntered.TrySetResult();
            }
        }

        private void OnReadCancellationObserved()
        {
            if (Interlocked.Increment(ref _stalledCancellationsObserved) >= expectedStalledCount)
            {
                _allStalledCancellationsObserved.TrySetResult();
            }
        }

        private void OnStreamDisposed()
        {
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _released) != 0)
            {
                _drained.TrySetResult();
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose() => ReleaseStalledReads();

        private sealed class BlockedReadStream(
            CameraAgentArtifactContentStream innerContent,
            ReadTimeoutReleaseProbeArtifactService owner) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                using var registration = cancellationToken.Register(
                    static state => ((ReadTimeoutReleaseProbeArtifactService)state!).OnReadCancellationObserved(),
                    owner);
                owner.OnReadEntered();
                await owner._releaseStalled.Task.ConfigureAwait(false);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    owner.OnStreamDisposed();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    owner.OnStreamDisposed();
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CanceledSyncDisposeProbeArtifactService(
        ICameraAgentArtifactService inner,
        Guid previewArtifactId,
        HashSet<Guid> layerArtifactIds,
        int expectedOperationCount,
        bool stallAtOpen,
        Func<Exception>? exceptionOnDisposeFactory = null) : ICameraAgentArtifactService, IDisposable
    {
        private readonly ManualResetEventSlim _syncDisposeGate = new(false);
        private readonly TaskCompletionSource _allOperationsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLateOpens = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allDisposesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPreviewOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _previewOpens;
        private int _layerOpens;
        private int _secondCallLayerOpens;
        private int _operationsEntered;
        private int _disposesEntered;
        private int _disposesCompleted;
        private int _cancellingThreadId;
        private int _disposesOnCancellingThread;
        private int _openStreams;
        private int _disposeReleased;

        public int CompletedDisposeCount => Volatile.Read(ref _disposesCompleted);
        public int DisposesOnCancellingThread => Volatile.Read(ref _disposesOnCancellingThread);
        public int SecondCallLayerOpens => Volatile.Read(ref _secondCallLayerOpens);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);

        public void SetCancellingThreadId(int threadId) => Volatile.Write(ref _cancellingThreadId, threadId);

        public Task WaitForAllOperationsEnteredAsync() => _allOperationsEntered.Task;
        public Task WaitForAllDisposesEnteredAsync() => _allDisposesEntered.Task;
        public Task WaitForSecondPreviewOpenedAsync() => _secondPreviewOpened.Task;

        public void ReleaseLateOpens() => _releaseLateOpens.TrySetResult();

        public void ReleaseDisposes()
        {
            Interlocked.Exchange(ref _disposeReleased, 1);
            _syncDisposeGate.Set();
            if (Volatile.Read(ref _openStreams) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            ReleaseLateOpens();
            ReleaseDisposes();
            if (Volatile.Read(ref _openStreams) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == previewArtifactId)
            {
                var preview = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _previewOpens) >= 2)
                {
                    _secondPreviewOpened.TrySetResult();
                }
                return preview;
            }

            if (!layerArtifactIds.Contains(artifactId))
            {
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var callIndex = Interlocked.Increment(ref _layerOpens);
            if (callIndex > expectedOperationCount)
            {
                Interlocked.Increment(ref _secondCallLayerOpens);
                return await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }

            var opened = await inner.OpenContentAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
            if (opened.Content is not { } content)
            {
                return opened;
            }

            Interlocked.Increment(ref _openStreams);
            var stream = new CanceledSyncDisposeStream(content, this, exceptionOnDisposeFactory);
            var wrapped = new CameraAgentArtifactContentResult(
                opened.Status,
                new CameraAgentArtifactContentStream(
                    stream,
                    content.ArtifactId,
                    content.CaptureId,
                    content.Role,
                    content.MediaType,
                    content.ByteLength,
                    content.ChecksumSha256,
                    content.FileName,
                    content.Descriptor,
                    content.EncodedWidth,
                    content.EncodedHeight));

            if (stallAtOpen)
            {
                OnOperationEntered();
                await _releaseLateOpens.Task.ConfigureAwait(false);
            }

            return wrapped;
        }

        private void OnOperationEntered()
        {
            if (Interlocked.Increment(ref _operationsEntered) >= expectedOperationCount)
            {
                _allOperationsEntered.TrySetResult();
            }
        }

        private void OnDisposeEntered()
        {
            var expectedThreadId = Volatile.Read(ref _cancellingThreadId);
            if (expectedThreadId != 0 && Environment.CurrentManagedThreadId == expectedThreadId)
            {
                Interlocked.Increment(ref _disposesOnCancellingThread);
            }
            if (Interlocked.Increment(ref _disposesEntered) >= expectedOperationCount)
            {
                _allDisposesEntered.TrySetResult();
            }
        }

        private void OnDisposeCompleted()
        {
            Interlocked.Increment(ref _disposesCompleted);
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _disposeReleased) != 0)
            {
                _drained.TrySetResult();
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose()
        {
            ReleaseLateOpens();
            ReleaseDisposes();
            _syncDisposeGate.Dispose();
        }

        private sealed class CanceledSyncDisposeStream(
            CameraAgentArtifactContentStream innerContent,
            CanceledSyncDisposeProbeArtifactService owner,
            Func<Exception>? exceptionOnDisposeFactory) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var tcs = new TaskCompletionSource<int>();
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
                    tcs);
                owner.OnOperationEntered();
                return await tcs.Task.ConfigureAwait(false);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    owner.OnDisposeCompleted();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                Exception? toThrow = null;
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.OnDisposeEntered();
                    owner._syncDisposeGate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    owner.OnDisposeCompleted();
                    toThrow = exceptionOnDisposeFactory?.Invoke();
                }
                await base.DisposeAsync().ConfigureAwait(false);
                if (toThrow is not null)
                {
                    throw toThrow;
                }
            }
        }
    }

    private sealed class InlineCancelCompletionProbeArtifactService : ICameraAgentArtifactService, IDisposable
    {
        private readonly CameraAgentArtifactService _inner;
        private readonly Guid _previewArtifactId;
        private readonly bool _stallAtOpen;
        private readonly Dictionary<Guid, CameraAgentArtifactContentResult> _preOpenedLayers;
        private readonly Lock _pendingLock = new();
        private readonly List<(TaskCompletionSource<CameraAgentArtifactContentResult> Tcs, CameraAgentArtifactContentResult Result)> _pendingOpens = [];
        private readonly List<TaskCompletionSource<int>> _pendingReads = [];
        private readonly ManualResetEventSlim _syncDisposeGate = new(false);
        private readonly TaskCompletionSource _allOperationsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allDisposesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedOperationCount;
        private int _operationsEntered;
        private int _disposesEntered;
        private int _disposesCompleted;
        private int _cancellingThreadId;
        private int _disposesOnCancellingThread;
        private int _openStreams;
        private int _disposeReleased;

        private InlineCancelCompletionProbeArtifactService(
            CameraAgentArtifactService inner,
            Guid previewArtifactId,
            bool stallAtOpen,
            int expectedOperationCount)
        {
            _inner = inner;
            _previewArtifactId = previewArtifactId;
            _stallAtOpen = stallAtOpen;
            _preOpenedLayers = new Dictionary<Guid, CameraAgentArtifactContentResult>(expectedOperationCount);
            _expectedOperationCount = expectedOperationCount;
            _openStreams = expectedOperationCount;
        }

        public static async Task<InlineCancelCompletionProbeArtifactService> CreateAsync(
            CameraAgentArtifactService inner,
            Guid previewArtifactId,
            Guid[] layerArtifactIds,
            bool stallAtOpen)
        {
            var probe = new InlineCancelCompletionProbeArtifactService(
                inner,
                previewArtifactId,
                stallAtOpen,
                layerArtifactIds.Length);
            foreach (var artifactId in layerArtifactIds)
            {
                var opened = await inner.OpenContentAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(opened.Content);
                var stream = new InlineBlockingDisposeStream(opened.Content, probe);
                probe._preOpenedLayers[artifactId] = new CameraAgentArtifactContentResult(
                    opened.Status,
                    new CameraAgentArtifactContentStream(
                        stream,
                        opened.Content.ArtifactId,
                        opened.Content.CaptureId,
                        opened.Content.Role,
                        opened.Content.MediaType,
                        opened.Content.ByteLength,
                        opened.Content.ChecksumSha256,
                        opened.Content.FileName,
                        opened.Content.Descriptor,
                        opened.Content.EncodedWidth,
                        opened.Content.EncodedHeight));
            }
            return probe;
        }

        public int CompletedDisposeCount => Volatile.Read(ref _disposesCompleted);
        public int DisposesOnCancellingThread => Volatile.Read(ref _disposesOnCancellingThread);
        public int OpenStreamCount => Volatile.Read(ref _openStreams);

        public void SetCancellingThreadId(int threadId) => Volatile.Write(ref _cancellingThreadId, threadId);

        public Task WaitForAllOperationsEnteredAsync() => _allOperationsEntered.Task;
        public Task WaitForAllDisposesEnteredAsync() => _allDisposesEntered.Task;

        public void CompletePendingOperationsInline()
        {
            (TaskCompletionSource<CameraAgentArtifactContentResult> Tcs, CameraAgentArtifactContentResult Result)[] opens;
            TaskCompletionSource<int>[] reads;
            lock (_pendingLock)
            {
                opens = [.. _pendingOpens];
                _pendingOpens.Clear();
                reads = [.. _pendingReads];
                _pendingReads.Clear();
            }

            foreach (var (tcs, result) in opens)
            {
                tcs.TrySetResult(result);
            }
            foreach (var tcs in reads)
            {
                tcs.TrySetCanceled();
            }
        }

        public void ReleaseDisposes()
        {
            Interlocked.Exchange(ref _disposeReleased, 1);
            _syncDisposeGate.Set();
            if (Volatile.Read(ref _openStreams) == 0)
            {
                _drained.TrySetResult();
            }
        }

        public async Task ReleaseAndDrainAsync()
        {
            CompletePendingOperationsInline();
            ReleaseDisposes();
            if (Volatile.Read(ref _openStreams) > 0)
            {
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId == _previewArtifactId || !_preOpenedLayers.TryGetValue(artifactId, out var wrapped))
            {
                return _inner.OpenContentAsync(artifactId, cancellationToken);
            }

            if (_stallAtOpen)
            {
                // Intentionally omit RunContinuationsAsynchronously so CompletePendingOperationsInline
                // runs continuations inline on the caller's cancelling thread.
                var openTcs = new TaskCompletionSource<CameraAgentArtifactContentResult>();
                lock (_pendingLock)
                {
                    _pendingOpens.Add((openTcs, wrapped));
                }
                OnOperationEntered();
                return new ValueTask<CameraAgentArtifactContentResult>(openTcs.Task);
            }

            return ValueTask.FromResult(wrapped);
        }

        private ValueTask<int> RegisterPendingRead()
        {
            // Intentionally omit RunContinuationsAsynchronously so CompletePendingOperationsInline
            // runs continuations inline on the caller's cancelling thread.
            var readTcs = new TaskCompletionSource<int>();
            lock (_pendingLock)
            {
                _pendingReads.Add(readTcs);
            }
            OnOperationEntered();
            return new ValueTask<int>(readTcs.Task);
        }

        private void OnOperationEntered()
        {
            if (Interlocked.Increment(ref _operationsEntered) >= _expectedOperationCount)
            {
                _allOperationsEntered.TrySetResult();
            }
        }

        private void OnDisposeEntered()
        {
            var expectedThreadId = Volatile.Read(ref _cancellingThreadId);
            if (expectedThreadId != 0 && Environment.CurrentManagedThreadId == expectedThreadId)
            {
                Interlocked.Increment(ref _disposesOnCancellingThread);
            }
            if (Interlocked.Increment(ref _disposesEntered) >= _expectedOperationCount)
            {
                _allDisposesEntered.TrySetResult();
            }
        }

        private void OnDisposeCompleted()
        {
            Interlocked.Increment(ref _disposesCompleted);
            if (Interlocked.Decrement(ref _openStreams) == 0 && Volatile.Read(ref _disposeReleased) != 0)
            {
                _drained.TrySetResult();
            }
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken) =>
            _inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null) =>
            _inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);

        public void Dispose()
        {
            CompletePendingOperationsInline();
            ReleaseDisposes();
            foreach (var item in _preOpenedLayers.Values)
            {
                item.Content?.Dispose();
            }
            _syncDisposeGate.Dispose();
        }

        private sealed class InlineBlockingDisposeStream(
            CameraAgentArtifactContentStream innerContent,
            InlineCancelCompletionProbeArtifactService owner) : Stream
        {
            private int _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => innerContent.ByteLength;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                owner.RegisterPendingRead();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    if (disposing)
                    {
                        innerContent.Dispose();
                    }
                    owner.OnDisposeCompleted();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.OnDisposeEntered();
                    owner._syncDisposeGate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                    await innerContent.DisposeAsync().ConfigureAwait(false);
                    owner.OnDisposeCompleted();
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class BlockingTestDisposeStream(
        CameraAgentArtifactContentStream innerContent,
        ManualResetEventSlim syncDisposeGate,
        TaskCompletionSource disposeEntered,
        Exception? exceptionOnDispose) : Stream
    {
        private int _disposed;
        private int _disposeThreadId;
        private int _disposeCompleted;

        public int DisposeThreadId => Volatile.Read(ref _disposeThreadId);
        public bool DisposeCompleted => Volatile.Read(ref _disposeCompleted) != 0;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => innerContent.ByteLength;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => innerContent.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            innerContent.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
            {
                innerContent.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            var shouldThrow = false;
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref _disposeThreadId, Environment.CurrentManagedThreadId);
                disposeEntered.TrySetResult();
                syncDisposeGate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                await innerContent.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _disposeCompleted, 1);
                shouldThrow = exceptionOnDispose is not null;
            }
            await base.DisposeAsync().ConfigureAwait(false);
            if (shouldThrow)
            {
                throw exceptionOnDispose!;
            }
        }
    }
}
