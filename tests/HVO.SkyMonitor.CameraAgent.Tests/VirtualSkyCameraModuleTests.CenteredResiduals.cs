using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.SkyMap;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public Task CenteredViewReopensAcceptedReferenceResidualsAfterCropAndResize()
    {
        const int size = CompactAstrometryField.Size;
        const double focalLength = CompactAstrometryField.FocalLength;
        var start = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(1);
        var observer = new ObserverLocation(0, 0, 0);
        var geometry = new ProjectionContext(ProjectionModel.EquidistantFisheye, size / 2d, size / 2d, focalLength, focalLength,
            size, size, ProjectionAperture.Circular, CompactAstrometryField.ImageCircleRadius, 72, 243, 17);
        var (raw, fitCatalog) = CreateAcceptedResidualField(geometry, observer, start.AddMilliseconds(500));
        var catalog = new CameraAgentSkyMapProjectionTests.FixtureCatalog(fitCatalog.Stars,
            fitCatalog.Metadata, fitCatalog.Provenance!.PreprocessingVersion);
        // Use the Unit lane's runaway budgets for this compact field.
        return VerifyCenteredReferenceResidualsAsync(raw, geometry, observer, start, end, "calibration-v1", fitCatalog,
            catalog, MeasuredAstrometryFixture.Options, new(256, 256, 9, 13, 234, 230, 117, 115), TestContext);
    }

    internal static async Task VerifyCenteredReferenceResidualsAsync(MeasuredAstrometryOptions recipe, TestContext testContext)
    {
        var fixture = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        var context = fixture.Context;
        var catalog = new CameraAgentSkyMapProjectionTests.FixtureCatalog(context.CatalogStars,
            context.CatalogMetadata, context.CatalogPackage.PreprocessingVersion);
        await VerifyCenteredReferenceResidualsAsync(fixture.Raw, context.Projection, context.Frame.Observer,
            context.Frame.ExposureStartUtc, context.Frame.ExposureEndUtc, context.CalibrationVersion, fixture.Catalog,
            catalog, recipe, new(512, 512, 9, 13, 490, 486, 245, 243), testContext).ConfigureAwait(false);
    }

    private static async Task VerifyCenteredReferenceResidualsAsync(ProcessingArtifact raw, ProjectionContext geometry,
        ObserverLocation observer, DateTimeOffset start, DateTimeOffset end, string calibrationVersion,
        AstrometricCatalogData fitCatalog, CameraAgentSkyMapProjectionTests.FixtureCatalog catalog,
        MeasuredAstrometryOptions recipe, CenteredViewPreviewTransform expectedPreview, TestContext testContext)
    {
        var location = DeploymentLocationSnapshot.Create("accepted-residual-fixture", 1, "virtual", null,
            DateTimeOffset.UnixEpoch, null, observer.LatitudeDegrees, observer.LongitudeDegrees, observer.ElevationMeters, "UTC");
        var solverCatalog = new Mock<IAstrometricCatalogSource>(MockBehavior.Strict);
        solverCatalog.Setup(source => source.ReadAsync(7, AstrometricCatalogData.MaximumEntries, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fitCatalog);
        var clock = new CenteredClock { UtcNow = start };
        var token = testContext.CancellationToken;
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-accepted-residuals");

        try
        {
            CenteredView view;
            CenteredViewArtifact fitPin;
            byte[] mappedBytes;
            CameraAgentLayeredPresentation presentation;
            Guid captureId;
            using (var provider = MeasurementProvider(root, location, catalog, timeProvider: clock,
                configureServices: services => services.AddSingleton(solverCatalog.Object)))
            {
                var profile = AcceptedResidualProfile(root, start, geometry, location, fitCatalog, calibrationVersion, recipe);
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var sceneResources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, sceneResources, null, token).ConfigureAwait(false);
                var attempt = reservation.Pending.Key;
                var exposureTime = end - start;
                var setpoint = new CaptureSetpoint(exposureTime, 1, null, null);
                var request = new CaptureRequest(start, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint);
                var layout = SensorReadoutResolver.Resolve(active.Rig.Sensor, active.Rig.Readout!).Layout;
                // Admission and the frozen graph produce the retained association, context and solution from these pixels.
                var frame = new CameraFrame(start, layout.Width, layout.Height, layout.PixelFormat, raw.Payload,
                    new(exposureTime, 1, 5, SourceId: "accepted-residual-fixture"), layout.StrideBytes)
                { Layout = layout };
                var readout = end.AddMilliseconds(100);
                await ingress.DispatchCenteredAsync(attempt, setpoint, null, "accepted-residual", token, start, request.TargetInterval)
                    .ConfigureAwait(false);
                clock.UtcNow = readout.AddMilliseconds(1);
                var capture = new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
                {
                    AcquisitionTiming = new(start, end, readout)
                };
                var receipt = await ingress.AcceptCenteredAsync(active,
                    new(request, capture, start, request.TargetInterval, readout - start), reservation, token).ConfigureAwait(false);
                Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
                var descriptor = receipt.Manifest.Descriptor;
                captureId = descriptor.Capture.CaptureId;
                Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(raw.Payload), descriptor.Artifact.ChecksumSha256);
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                var exposure = new ExposureKey(attempt.Camera, attempt.RunId, captureId);
                var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(exposure, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product, linear?.Reason);
                var executionId = linear.Product.Slot.ExecutionId;
                var stream = new CenteredViewStream(attempt.Camera, attempt.RunId, reservation.Revision.State.RevisionId);
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var sources = await store.ReadCenteredViewSourcesAsync(exposure, executionId, 0, 24, token).ConfigureAwait(false);
                var output = sources.SingleOrDefault(source => source.Slot.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
                if (output is null)
                {
                    var execution = await store.ReadExecutionDetailAsync(executionId, token).ConfigureAwait(false);
                    Assert.IsNotNull(execution);
                    Assert.Fail(string.Join(", ", execution.Nodes.Select(node => $"{node.NodeId}={node.Status}/{node.Reason}")));
                }
                Assert.IsNotNull(output);
                Assert.AreEqual(ExposureProductAvailability.Available, output.Availability, output.Reason);
                Assert.IsNotNull(output.Product);
                fitPin = new(output.Product.ArtifactId, output.Product.OutputIdentitySha256, output.Product.PayloadSha256);
                var fit = await provider.GetRequiredService<ICameraAgentRetainedAstrometryService>().GetExactAsync(captureId, fitPin, token)
                    .ConfigureAwait(false);
                Assert.AreEqual("Retained", fit.Availability, fit.Reason);
                Assert.IsNotNull(fit.Solution);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, fit.Solution.Assessment.Status, fit.Solution.Assessment.Reason);
                Assert.AreEqual(fitPin.ArtifactId, fit.ArtifactId);
                Assert.IsNotNull(fit.SourceDescriptor);
                var descriptorHash = CaptureContractJson.ComputeDescriptorSha256(descriptor);
                Assert.AreEqual(descriptorHash, CaptureContractJson.ComputeDescriptorSha256(fit.SourceDescriptor));
                Assert.AreEqual(descriptorHash, fit.Solution.Assessment.Frame.SourceDescriptorSha256);
                Assert.AreEqual(descriptor.Artifact.ArtifactId, fit.Solution.Assessment.Frame.SourceArtifactId);
                Assert.IsNotNull(fit.Diagnostics);

                var initial = await provider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(
                    stream, executionId, descriptor.Capture.CaptureSequence, linear,
                    new(MaximumDimension: expectedPreview.PreviewWidth,
                        Crop: new(expectedPreview.CropX, expectedPreview.CropY, expectedPreview.CropWidth, expectedPreview.CropHeight),
                        AllowRawFallback: false), token).ConfigureAwait(false);
                Assert.IsNotNull(initial);
                Assert.AreEqual(expectedPreview, initial.Base.Definition.Preview.Transform);
                Assert.AreEqual(CenteredViewReadiness.Waiting, ReferenceResidual(initial).Readiness);
                view = await DrainCenteredReferenceAsync(provider, exposure, token).ConfigureAwait(false);
                Assert.AreEqual(initial.Base.BaseId, view.Base.BaseId);
                Assert.AreEqual(descriptorHash, CaptureContractJson.ComputeDescriptorSha256(view.Base.Definition.Reference.Input.Raw));
                Assert.AreNotEqual(initial.Revision.RevisionId, view.Revision.RevisionId);
                var layer = ReferenceResidual(view);
                Assert.AreEqual(CenteredViewReadiness.Ready, layer.Readiness, layer.Reason);
                Assert.AreEqual(new CenteredViewLayerKey(AstrometricDiagnosticPresentation.ResidualsLayer,
                    CenteredViewLayerScope.Reference, captureId), layer.Key);
                Assert.AreEqual(output.Revision, layer.EvidenceOrder);
                Assert.AreEqual(fitPin, layer.Evidence);
                Assert.IsNotNull(layer.Drawable);
                var mapped = await provider.GetRequiredService<CaptureProcessingPersistence>()
                    .ReadCenteredViewParentAsync(captureId, layer.Drawable.SourceProduct.ArtifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(mapped.SourceArtifactIds);
                Assert.AreEqual(fitPin.ArtifactId, Assert.ContainsSingle(mapped.SourceArtifactIds));
                mappedBytes = mapped.Payload.ToArray();
                var payload = PresentationLayerPayloadJson.Parse(mappedBytes).Payload;
                Assert.IsNotNull(payload);
                Assert.AreEqual(fit.SolutionIdentitySha256, payload.SourceIdentitySha256);
                Assert.AreEqual(layer.Drawable.SourceProduct.ProductIdentitySha256, payload.ContentIdentitySha256);
                Assert.AreEqual((expectedPreview.PreviewWidth, expectedPreview.PreviewHeight), (payload.WidthPixels, payload.HeightPixels));

                presentation = await ReadResidualPresentationAsync(provider, view, token).ConfigureAwait(false);
                AssertResidualGeometry(fit.Diagnostics, payload, presentation);
            }

            using var reopened = MeasurementProvider(root, location, catalog, timeProvider: clock);
            var archived = Assert.ContainsSingle(await reopened.GetRequiredService<ICenteredViewLookup>()
                .ReadArchiveAsync(captureId, token).ConfigureAwait(false));
            Assert.AreEqual(view.Base.BaseId, archived.Base.BaseId);
            Assert.AreEqual(view.Revision.RevisionId, archived.LatestRevisionId);
            var retained = await reopened.GetRequiredService<ICenteredViewReadIndex>()
                .ReadRevisionAsync(archived.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(retained);
            var reopenedLayer = ReferenceResidual(retained);
            Assert.AreEqual(CenteredViewReadiness.Ready, reopenedLayer.Readiness);
            Assert.AreEqual(fitPin, reopenedLayer.Evidence);
            Assert.IsNotNull(reopenedLayer.Drawable);
            Assert.AreEqual(ReferenceResidual(view).Drawable!.SourceProduct, reopenedLayer.Drawable.SourceProduct);
            var reopenedPayload = await reopened.GetRequiredService<CaptureProcessingPersistence>()
                .ReadCenteredViewParentAsync(captureId, reopenedLayer.Drawable.SourceProduct.ArtifactId, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(mappedBytes, reopenedPayload.Payload.ToArray());
            var rendered = await ReadResidualPresentationAsync(reopened, retained, token).ConfigureAwait(false);
            Assert.AreEqual(presentation.PresentationIdentitySha256, rendered.PresentationIdentitySha256);
            Assert.AreEqual(presentation.SvgChecksumSha256, rendered.SvgChecksumSha256);
            CollectionAssert.AreEqual(presentation.Svg.ToArray(), rendered.Svg.ToArray());
            testContext.WriteLine($"fit={fitPin.ArtifactId} base={archived.Base.BaseId} revision={archived.LatestRevisionId} svg={rendered.SvgChecksumSha256}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (ProcessingArtifact Raw, AstrometricCatalogData Catalog) CreateAcceptedResidualField(
        ProjectionContext geometry, ObserverLocation observer, DateTimeOffset midpoint)
    {
        var stars = CompactAstrometryField.CreateStars(geometry, observer, midpoint);
        var pixels = MeasuredStellarAssociationFixtures.Field(geometry.WidthPixels, geometry.HeightPixels);
        var shifted = ProjectorFactory.Create(geometry with { RollDegrees = 17.04 });
        for (var index = 0; index < stars.Length; index++)
        {
            var star = stars[index];
            var ofDate = EquatorialPrecession.PrecessJ2000(new(star.RightAscensionHours, star.DeclinationDegrees), midpoint);
            var direction = CoordinateTransforms.EquatorialToHorizontal(ofDate, midpoint,
                observer.LatitudeDegrees, observer.LongitudeDegrees);
            var point = shifted.Project(direction);
            Assert.IsNotNull(point);
            // One local offset survives the solver's correction for the global roll.
            var center = index == 0 ? new PixelPoint(point.Value.X + .25, point.Value.Y - .15) : point.Value;
            MeasuredStellarAssociationFixtures.AddGaussian(pixels, center, 1.15,
                5000 * Math.Pow(10, -.4 * (star.Magnitude - 2)), geometry.WidthPixels);
        }
        MeasuredStellarAssociationFixtures.AddNoise(pixels, 1205, 4);
        var raw = MeasuredStellarAssociationFixtures.RawArtifact(pixels, width: geometry.WidthPixels, height: geometry.HeightPixels);
        var metadata = new CatalogMetadata("centered-residual-field", "1", new Uri("https://example.test/reference"),
            MeasuredAstrometryFixture.Hash("centered-residual-field-v1"), "test-generated", "1");
        var provenance = new AstrometricCatalogProvenance(metadata.Name, metadata.Version, "fixture", "unspecified");
        return (raw, new(metadata, stars, true, 7, provenance: provenance));
    }

    private static CameraModuleConfig AcceptedResidualProfile(string root, DateTimeOffset start, ProjectionContext geometry,
        DeploymentLocationSnapshot location, AstrometricCatalogData catalog, string calibrationVersion, MeasuredAstrometryOptions recipe)
    {
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        // Pin the fixture field without embedding a catalog snapshot in the bounded graph options.
        var catalogPin = new MeasuredAstrometryCatalogSelection(catalog.IdentitySha256, catalog.SelectionIdentitySha256, 7);
        return profile with
        {
            DeploymentLocation = location,
            Rig = profile.Rig with
            {
                Sensor = profile.Rig.Sensor with { WidthPixels = geometry.WidthPixels, HeightPixels = geometry.HeightPixels },
                Readout = new(new(0, 0, geometry.WidthPixels, geometry.HeightPixels), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample, 0, ushort.MaxValue),
                Optics = profile.Rig.Optics with
                {
                    PrincipalPointX = geometry.PrincipalPointX,
                    PrincipalPointY = geometry.PrincipalPointY,
                    ImageCircleRadiusPixels = geometry.ImageCircleRadiusPixels,
                    FocalLengthXPixels = geometry.FocalLengthXPixels,
                    FocalLengthYPixels = geometry.FocalLengthYPixels,
                    CalibrationVersion = calibrationVersion
                },
                Orientation = new(geometry.BoresightAltitudeDegrees, geometry.BoresightAzimuthDegrees, geometry.RollDegrees)
            },
            Pipeline = profile.Pipeline with
            {
                Steps = [
                    .. profile.Pipeline.Steps,
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable),
                    new("MeasuredAstrometry", "fit", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions
                        {
                            CatalogSelection = catalogPin, Recipe = recipe
                        }))
                ]
            }
        };
    }

    private static CenteredViewLayer ReferenceResidual(CenteredView view) => Assert.ContainsSingle(
        view.Revision.Layers.Where(layer => layer.Key.Kind == AstrometricDiagnosticPresentation.ResidualsLayer));

    private static async Task<CameraAgentLayeredPresentation> ReadResidualPresentationAsync(
        ServiceProvider provider, CenteredView view, CancellationToken token)
    {
        var rendered = await provider.GetRequiredService<ICenteredViewPresentationReader>()
            .ReadAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status, rendered.Reason);
        Assert.IsNotNull(rendered.Presentation);
        Assert.AreEqual(view.Base.Definition.Reference.Capture.CaptureId, rendered.Presentation.CaptureId);
        Assert.AreEqual(view.Base.Definition.Preview.Product.ArtifactId, rendered.Presentation.BaseArtifactId);
        return rendered.Presentation;
    }

    private static void AssertResidualGeometry(AstrometricResidualDiagnostics diagnostics,
        PresentationLayerPayloadV1 payload, CameraAgentLayeredPresentation presentation)
    {
        Assert.IsTrue(diagnostics.Residuals.Any(residual => residual.ResidualPixels > .01), "Use a field with a resolvable vector.");
        Assert.HasCount(diagnostics.Residuals.Count, payload.Segments);
        var layer = Assert.ContainsSingle(presentation.Layers.Where(layer => layer.Kind == AstrometricDiagnosticPresentation.ResidualsLayer));
        var svg = XDocument.Parse(Encoding.UTF8.GetString(presentation.Svg.Span));
        var group = svg.Descendants().Single(element => (string?)element.Attribute("id") == layer.DomGroupId);
        var lines = group.Descendants().Where(element => element.Name.LocalName == "line").ToArray();
        Assert.HasCount(diagnostics.Residuals.Count, lines);
        for (var index = 0; index < lines.Length; index++)
        {
            var residual = diagnostics.Residuals[index];
            var segment = payload.Segments[index];
            Assert.AreEqual(1, segment.Thickness);
            Assert.AreEqual("1", (string?)lines[index].Attribute("stroke-width"));
            AssertEndpoint(residual.Predicted, segment.From, lines[index], "1");
            AssertEndpoint(residual.Measured, segment.To, lines[index], "2");
        }

        static void AssertEndpoint(PixelPoint source, PixelPoint retained, XElement line, string endpoint)
        {
            // Independent of the registered transform and mapping code. Both crops are halved.
            var x = (source.X - 9) / 2;
            var y = (source.Y - 13) / 2;
            Assert.AreEqual(x, retained.X, .001);
            Assert.AreEqual(y, retained.Y, .001);
            Assert.AreEqual(x, double.Parse(line.Attribute("x" + endpoint)!.Value, CultureInfo.InvariantCulture), .001);
            Assert.AreEqual(y, double.Parse(line.Attribute("y" + endpoint)!.Value, CultureInfo.InvariantCulture), .001);
        }
    }
}

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class VirtualSkyCameraPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task CenteredViewReopensAcceptedReferenceResidualsWithinAstrometryBudget() =>
        VirtualSkyCameraModuleTests.VerifyCenteredReferenceResidualsAsync(MeasuredAstrometryFixture.PerformanceOptions, TestContext);
}
