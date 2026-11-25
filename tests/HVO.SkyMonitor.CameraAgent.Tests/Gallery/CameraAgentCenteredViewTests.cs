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
}
