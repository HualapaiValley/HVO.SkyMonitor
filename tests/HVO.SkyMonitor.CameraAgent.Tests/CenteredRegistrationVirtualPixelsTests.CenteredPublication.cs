using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class CenteredRegistrationVirtualPixelsTests
{
    private static readonly JsonSerializerOptions PublicationJson = new(JsonSerializerDefaults.Web);

    private async Task VerifyRetainedPublicationAsync(IReadOnlyList<Row> acquired, CenteredStackPlanV1 original,
        CameraRigConfig rig, ProcessingRecipeExecutor executor, bool mixedPresentation, CancellationToken token)
    {
        Assert.HasCount(3, acquired);
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-plate-presentation");
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        try
        {
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(token).ConfigureAwait(false);
            using var files = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var telemetry = new CaptureProcessingTelemetry();
            CenteredView initial;
            ExposureProductLookup plateTrail;
            ExposureProductLookup rightTrail;
            PixelPoint plateFrom;
            PixelPoint plateTo;
            PixelPoint rightFrom;
            PixelPoint rightTo;
            var reference = acquired[1];
            using (var store = new SqliteCaptureProcessingStore(options))
            {
                await store.InitializeAsync(token).ConfigureAwait(false);
                var persistence = new CaptureProcessingPersistence(options, store, files, telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance);
                var plate = await RetainPlateAsync(acquired[0], store, persistence, token).ConfigureAwait(false);
                var center = await RetainPlateAsync(reference, store, persistence, token).ConfigureAwait(false);
                var other = mixedPresentation
                    ? acquired[2] with { Pin = acquired[2].Pin with { Plate = null }, Plate = [] }
                    : await RetainPlateAsync(acquired[2], store, persistence, token).ConfigureAwait(false);
                Row[] rows = [plate, center, other];
                var slots = original.Slots.Select(slot => slot.Offset switch
                {
                    -1 => slot with { Exposure = plate.Pin },
                    0 => slot with { Exposure = center.Pin },
                    1 => slot with
                    {
                        Exposure = other.Pin,
                        CaptureId = other.Pin.Key.CaptureId,
                        ExpectedStartUtc = other.Pin.ExposureStartedUtc
                    },
                    _ => slot
                }).ToArray();
                var plan = original with { Slots = slots };
                var artifacts = rows.SelectMany(row => new[] { row.Raw, row.Linear, row.Mask, row.Measurement }.Concat(row.Plate)).ToArray();
                var request = CenteredRegisteredStackRecipe.CreateRequest(plan, LinearVariant, "mixed", artifacts);
                var outcome = await executor.ExecuteAsync(request, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
                var science = outcome.Products.Single(product => product.SchemaVersion == CenteredRegisteredStackEvidence.CurrentSchemaVersion);
                var evidence = CenteredRegisteredStackEvidence.Read(science);
                Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, DescribeRegistration(evidence));
                Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, evidence.Sources[1].Method, DescribeRegistration(evidence));
                Assert.AreEqual(mixedPresentation ? CenteredRegistrationMethod.VerifiedCentroidSimilarity :
                    CenteredRegistrationMethod.VerifiedPlatePair, evidence.Sources[3].Method, DescribeRegistration(evidence));
                Assert.IsNotEmpty(evidence.Sources[1].PairCheck!.Verification);
                TestContext.WriteLine(DescribeRegistration(evidence));
                var descriptor = center.Settlement.Exposure.Raw;
                await persistence.PersistCenteredViewProductAsync(descriptor, science, token).ConfigureAwait(false);
                var plane = outcome.Products.Single(product => product.Variant == "mixed-mono");
                var layout = plane.Layout!;
                var crop = new CenteredViewPreviewCrop(13, 9, layout.Width - 26, layout.Height - 18);
                var preview = CenteredViewPreviewEncoder.Encode([FromProduct(center.Raw, plane)], center.Linear.Layout!,
                    CenteredViewImageSource.RegisteredLinear, null, null, new(MaximumDimension: 480, Crop: crop), token);
                await persistence.PersistCenteredViewProductAsync(descriptor, preview.Product, token).ConfigureAwait(false);
                var stream = new CenteredViewStream(plan.Camera, plan.RunId, plan.GenerationIdentity);
                var contributors = evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed)
                    .Select(source => new CenteredViewContributor(
                        BindViewExposure(rows.Single(row => row.Pin.Key.CaptureId == source.CaptureId)),
                        source.Method!.Value.ToString(), JsonSerializer.SerializeToElement(source, PublicationJson))).ToArray();
                var sciencePin = ProductPin(science);
                var views = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
                // Exercise the layer boundary with a prepublished base built from the exact recipe output.
                initial = await views.RegisterBaseAsync(new(stream, BindViewExposure(center), descriptor.Capture.CaptureSequence,
                    CenteredViewKind.Stack, sciencePin, preview.Preview, contributors, sciencePin), [], token).ConfigureAwait(false);
                plateFrom = new(layout.Width * .45, layout.Height * .43);
                plateTo = new(layout.Width * .54, layout.Height * .48);
                rightFrom = new(layout.Width * .46, layout.Height * .55);
                rightTo = new(layout.Width * .53, layout.Height * .61);
                plateTrail = await RetainTrailAsync(plate, plateFrom, plateTo, persistence, token).ConfigureAwait(false);
                rightTrail = await RetainTrailAsync(other, rightFrom, rightTo, persistence, token).ConfigureAwait(false);
            }

            using var reopenedStore = new SqliteCaptureProcessingStore(options);
            var reopenedPersistence = new CaptureProcessingPersistence(options, reopenedStore, files, telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            var reopenedViews = new SqliteCenteredViews(reopenedStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            var input = new PlatePublicationInputs(acquired, reopenedPersistence);
            var layers = new CenteredViewLayerProducer(reopenedViews, reopenedPersistence, registration: input);
            var current = await layers.PublishAsync(initial.Base.BaseId, CenteredViewLayerScope.Contributor, plateTrail,
                new("plate-trail", 400, true), acquired[0].Pin.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
            Assert.IsNotNull(current);
            current = await layers.PublishAsync(initial.Base.BaseId, CenteredViewLayerScope.Contributor, rightTrail,
                new("right-trail", 401, true), acquired[2].Pin.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
            Assert.IsNotNull(current);
            Assert.HasCount(2, current.Revision.Manifest.Layers);
            using var artifactsReader = new CameraAgentArtifactService(options, reopenedStore, new CameraAgentPreviewEncoder());
            var reader = new CenteredViewPresentationReader(reopenedViews, artifactsReader);
            var rendered = await reader.ReadAsync(current.Base.BaseId, current.Revision.RevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(rendered.Presentation, rendered.Status.ToString());
            var svg = XDocument.Parse(Encoding.UTF8.GetString(rendered.Presentation.Svg.Span));
            var lines = svg.Descendants().Where(element => element.Name.LocalName == "line").ToArray();
            Assert.HasCount(2, lines);
            var transform = initial.Base.Definition.Preview.Transform;
            AssertEndpoint(lines[0], "1", plateFrom, acquired[0]);
            AssertEndpoint(lines[0], "2", plateTo, acquired[0]);
            AssertEndpoint(lines[1], "1", rightFrom, acquired[2]);
            AssertEndpoint(lines[1], "2", rightTo, acquired[2]);
            var originalView = await reopenedViews.ReadRevisionAsync(initial.Base.BaseId, initial.Revision.RevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(originalView);
            Assert.IsEmpty(originalView.Revision.Manifest.Layers);

            void AssertEndpoint(XElement line, string suffix, PixelPoint source, Row row)
            {
                var ray = VirtualAstrometryReference.Unproject(rig, source);
                Assert.IsNotNull(ray);
                var sky = VirtualAstrometryReference.FromEnu(ray.Value, row.Midpoint, VirtualAstrometryFixture.Observer);
                var observed = VirtualAstrometryReference.ToEnu(sky, reference.Midpoint, VirtualAstrometryFixture.Observer);
                var expectedReference = VirtualAstrometryReference.Project(rig, observed);
                Assert.IsNotNull(expectedReference);
                var expected = expectedReference.Value;
                var x = double.Parse(line.Attribute("x" + suffix)!.Value, CultureInfo.InvariantCulture);
                var y = double.Parse(line.Attribute("y" + suffix)!.Value, CultureInfo.InvariantCulture);
                var expectedX = (expected.X - transform.CropX) * transform.PreviewWidth / transform.CropWidth;
                var expectedY = (expected.Y - transform.CropY) * transform.PreviewHeight / transform.CropHeight;
                TestContext.WriteLine(FormattableString.Invariant($"source={row.Pin.Key.CaptureId:D}, endpoint={suffix}, SVG=({x:F3},{y:F3}), oracle=({expectedX:F3},{expectedY:F3})"));
                Assert.AreEqual(expectedX, x, .05);
                Assert.AreEqual(expectedY, y, .05);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Row> RetainPlateAsync(Row row, SqliteCaptureProcessingStore store,
        CaptureProcessingPersistence persistence, CancellationToken token)
    {
        var restored = new List<ProcessingArtifact>(4);
        foreach (var product in row.PlateProducts)
        {
            await persistence.PersistCenteredViewProductAsync(row.Settlement.Exposure.Raw, product, token).ConfigureAwait(false);
            var output = await store.ReadOutputByArtifactIdAsync(ProductPin(product).ArtifactId, token).ConfigureAwait(false);
            Assert.IsNotNull(output);
            var header = CenteredWindowArtifactHeader.FromOutput(output);
            var pin = CenteredStackArtifactPin.FromHeader(CenteredStackArtifactHeader.FromArtifact(header), product.ChecksumSha256);
            var artifact = await persistence.ReadCenteredRetainedProductAsync(row.Pin.Key, pin, token).ConfigureAwait(false);
            Assert.IsNotNull(artifact);
            restored.Add(artifact);
        }
        var pins = restored.Select(CenteredStackArtifactPin.From).ToArray();
        Assert.HasCount(4, pins);
        return row with { Plate = restored.ToArray(), Pin = row.Pin with { Plate = new(pins[0], pins[1], pins[2], pins[3]) } };
    }

    private static async Task<ExposureProductLookup> RetainTrailAsync(Row row, PixelPoint from, PixelPoint to,
        CaptureProcessingPersistence persistence, CancellationToken token)
    {
        var layout = row.Raw.Layout!;
        var payload = PresentationLayerPayloadJson.Create(row.Raw.DescriptorIdentitySha256!, layout.Width, layout.Height,
            segments: [new(from, to, 2, new(255, 210, 100))]);
        var product = PresentationProcessingProducts.CreateLayerProduct(payload, "fixture-trail", [row.Raw], "retained-mixed-proof");
        await persistence.PersistCenteredViewProductAsync(row.Settlement.Exposure.Raw, product, token).ConfigureAwait(false);
        var pin = ProductPin(product);
        var slot = new ExposureProductSlot(row.Settlement.Product!.Slot.ExecutionId, "fixture-trail", payload.SchemaVersion, product.Variant);
        return new(row.Pin.Key, slot, ExposureProductAvailability.Available, row.Settlement.Revision,
            new(row.Pin.Key, slot, pin.ArtifactId, pin.IdentitySha256, pin.PayloadSha256, payload.ContentIdentitySha256), null);
    }

    private static CenteredViewExposure BindViewExposure(Row row)
    {
        var linear = row.Settlement.Product!;
        return new(row.Settlement.Exposure, linear.Slot.ExecutionId,
            new(linear.ArtifactId, linear.OutputIdentitySha256, linear.PayloadSha256));
    }

    private static CenteredViewArtifact ProductPin(ProcessingProduct product) =>
        new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.OutputIdentitySha256, product.ChecksumSha256);

    private static string DescribeRegistration(CenteredRegisteredStackEvidence evidence) =>
        string.Join(", ", evidence.Sources.Select(source =>
            $"{source.Offset}={source.Method}/{source.Reason} ({source.PairCheck?.Status})"));

    private sealed class PlatePublicationInputs(IReadOnlyList<Row> acquired,
        CaptureProcessingPersistence persistence) : ICenteredWindowRegistrationSource
    {
        public ValueTask<ProcessingArtifact?> ReadArtifactAsync(ExposureKey exposure, CenteredStackArtifactPin pin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var row = acquired.Single(row => row.Pin.Key == exposure);
            return pin == row.Pin.Raw
                ? ValueTask.FromResult<ProcessingArtifact?>(row.Raw)
                : persistence.ReadCenteredRetainedProductAsync(exposure, pin, token);
        }

        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
