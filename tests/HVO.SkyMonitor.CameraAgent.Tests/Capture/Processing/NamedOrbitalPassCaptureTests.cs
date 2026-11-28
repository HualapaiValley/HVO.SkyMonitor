using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Processing")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NamedOrbitalPassCaptureTests
{
    private const string Agent = "named-pass-regression";
    private static readonly DateTimeOffset First = new(2026, 10, 20, 3, 59, 55, TimeSpan.Zero);
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutterDelay = TimeSpan.FromMilliseconds(1);
    private static readonly string[] CatalogNumbers = ["56838", "65283"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfiguredCameraPublishesPredictionAndMeasuresWithoutInjectedIdentity(bool bayer)
    {
        var export = Environment.GetEnvironmentVariable("HVO_NAMED_PASS_EVIDENCE");
        var root = export is null
            ? Path.Combine(Path.GetTempPath(), "hvo-named-pass-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Path.GetFullPath(export), bayer ? "bayer" : "mono");
        Assert.IsFalse(Directory.Exists(root), "Use an empty evidence location for every named-pass run.");
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ct = timeout.Token;
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-prediction-v1");
            var config = MakeConfiguration(fixture, bayer);
            var input = await PreparePackageAsync(root, fixture, config, ct).ConfigureAwait(false);
            config = ConfigureGraph(config, input, Path.Combine(root, "ingress"), omitGraphPin: true);
            await WriteAsync(Path.Combine(root, "camera.json"), config, ct).ConfigureAwait(false);
            var catalogRoot = Environment.GetEnvironmentVariable("HVO_NAMED_PASS_HYG42_ROOT");
            ICelestialCatalog catalog = catalogRoot is null
                ? new InMemoryCelestialCatalog([])
                : CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
                { ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1" }).Catalog;
            var preparationTime = TimeSpan.FromHours(3);
            var clock = new ExposureTestTimeProvider(First - preparationTime, autoAdvance: true);
            var ingressRoot = Path.Combine(root, "ingress");
            var acquisition = CreateProvider(ingressRoot, clock, catalog, input);
            await using (acquisition.ConfigureAwait(false))
            {
                await InstallSnapshotAsync(acquisition, input.Snapshot, ct).ConfigureAwait(false);
                clock.Advance(preparationTime);
                var telemetry = await CaptureSequenceAsync(acquisition, config, clock, ct).ConfigureAwait(false);
                await WriteAsync(Path.Combine(root, "capture-telemetry.json"), telemetry, ct).ConfigureAwait(false);
                var newer = OrbitalSnapshot.Create(input.Snapshot.Records, [input.Snapshot.Manifest.Sources.Single() with
                {
                    RetrievedUtc = clock.GetUtcNow(),
                    TermsReference = "A different installed snapshot after raw admission, before prediction."
                }], input.Snapshot.Manifest.Coverage);
                await InstallSnapshotAsync(acquisition, newer, ct).ConfigureAwait(false);
            }
            // Restart between raw admission and lane execution, and leave processing timers on a manually controlled clock.
            var provider = CreateProvider(ingressRoot, new ExposureTestTimeProvider(clock.GetUtcNow()), catalog, input);
            await using var providerLifetime = provider.ConfigureAwait(false);
            await InitializeCaptureServicesAsync(provider, config, ct).ConfigureAwait(false);
            var active = await provider.GetRequiredService<IOrbitalSnapshotReader>().ReadCurrentAsync(ct).ConfigureAwait(false);
            Assert.IsNotNull(active.Snapshot);
            Assert.AreNotEqual(input.Snapshot.Manifest.ManifestSha256, active.Snapshot.Manifest.ManifestSha256);
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
            var queue = provider.GetRequiredService<ICaptureLaneStore>();
            var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
            var worker = provider.GetRequiredService<TransientWorkerService>();
            var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var captures = new List<RawCaptureReceipt>();
            for (var index = 0; index < 6; index++)
            {
                var lease = await queue.ClaimAsync(lane, Agent, config, ct).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var receipt = lease.Context.RawCapture;
                var manifest = receipt.Manifest;
                var descriptor = manifest.Descriptor;
                Assert.IsLessThanOrEqualTo(ShutterDelay, (descriptor.Timing.ExposureStartedUtc - (First + index * Exposure)).Duration());
                if (index == 0)
                    Assert.AreEqual(ShutterDelay, descriptor.Timing.ExposureStartedUtc - descriptor.Timing.RequestedStartUtc);
                Assert.AreEqual(Exposure, descriptor.Timing.ExposureEndedUtc - descriptor.Timing.ExposureStartedUtc);
                Assert.IsTrue(manifest.Validate().IsValid);
                Assert.AreEqual(VirtualExposureProvenance.ShutterSchemaVersion, manifest.Scene!.VirtualExposure!.SchemaVersion);
                Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, manifest.Scene!.TransientScenario!.IntegrationStartUtc);
                Assert.AreEqual(input.PackageSha256, manifest.Scene.TransientScenario.Parameters.GetProperty("sourcePackageSha256").GetString());
                Assert.AreEqual(bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, descriptor.Layout.PixelFormat);
                Assert.AreEqual(bayer ? 0 : null, descriptor.Layout.Readout!.CfaOriginX);
                Assert.AreEqual(1280, descriptor.Layout.StrideBytes);
                Assert.AreEqual(64d, descriptor.Layout.BlackLevel);
                Assert.AreEqual(4095d, descriptor.Layout.WhiteLevel);
                var raw = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath, ct).ConfigureAwait(false);
                Assert.AreEqual(Hash(raw), descriptor.Artifact.ChecksumSha256);
                captures.Add(receipt);
                var result = await handler.HandleAsync(lease.Context, ct).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await queue.CompleteAsync(lease, ct).ConfigureAwait(false);
            }

            var transientLane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "transient");
            var transientHandler = provider.GetServices<ICaptureLaneHandler>().Single(static handler => handler.Lane == "transient");
            for (var index = 0; index < captures.Count; index++)
            {
                var lease = await queue.ClaimAsync(transientLane, Agent, config, ct).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var admitted = await transientHandler.HandleAsync(lease.Context, ct).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, admitted.Outcome, admitted.Reason);
                await queue.CompleteAsync(lease, ct).ConfigureAwait(false);
                Assert.IsTrue(await worker.ProcessFrameAsync(ct).ConfigureAwait(false), $"Worker did not process slot {index + 101}.");
            }

            var outcomeStore = provider.GetRequiredService<SqliteTransientRuntimeStore>();
            var trails = provider.GetRequiredService<ICameraAgentTransientOperatorProjection>();
            var presentation = provider.GetRequiredService<ICameraAgentLayeredPresentationService>();
            var outcomes = (await outcomeStore.ReadRecentCaptureOutcomesAsync(6, ct).ConfigureAwait(false))
                .ToDictionary(static item => item.CaptureId);
            var scored = new List<object>();
            TransientPointV1? previousBrightCenter = null;
            for (var index = 0; index < captures.Count; index++)
            {
                var receipt = captures[index];
                var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                var outcome = outcomes[captureId];
                var measurement = await trails.GetCaptureTrailsAsync(captureId, ct).ConfigureAwait(false);
                var view = await presentation.GetAsync(captureId, ct).ConfigureAwait(false);
                var predictionNode = await processing.ReadNodeAsync(captureId, "satellite", ct).ConfigureAwait(false);
                Assert.IsNotNull(predictionNode);
                var satelliteLayer = await processing.ReadNodeAsync(captureId, "satellite-layer", ct).ConfigureAwait(false);
                var overlay = await processing.ReadNodeAsync(captureId, "overlay", ct).ConfigureAwait(false);
                Assert.IsNotNull(satelliteLayer);
                Assert.IsNotNull(overlay);
                var evidence = predictionNode.Outputs.Single(static output => output.ProductSchemaVersion == CaptureSatelliteProducts.StatusSchema);
                var status = CaptureSatelliteProducts.ParseStatus(await File.ReadAllBytesAsync(
                    Path.Combine(ingressRoot, evidence.PayloadRelativePath), ct).ConfigureAwait(false));
                var linear = await processing.ReadNodeAsync(captureId, "linear", ct).ConfigureAwait(false);
                Assert.IsNotNull(linear);
                Assert.IsTrue(linear.Outputs.Any(static output => output.Artifact.Role == FrameArtifactRole.Calibrated));
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, view.Status, view.Reason);
                Assert.IsNotNull(view.Presentation);
                var preview = await provider.GetRequiredService<ICameraAgentArtifactService>()
                    .GetPreviewAsync(view.Presentation.BaseArtifactId, ct).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, preview.Status);
                using var pixels = SkiaSharp.SKBitmap.Decode(preview.Content.ToArray());
                Assert.IsNotNull(pixels);
                Assert.AreEqual(640, pixels.Width);
                Assert.AreEqual(640, pixels.Height);
                await File.WriteAllBytesAsync(Path.Combine(root, $"slot-{101 + index}-preview.jpg"), preview.Content, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(root, $"slot-{101 + index}-layers.svg"), view.Presentation.Svg, ct).ConfigureAwait(false);
                await WriteAsync(Path.Combine(root, $"slot-{101 + index}.json"), new
                {
                    ordinal = 101 + index,
                    receipt.Manifest,
                    receipt.CommittedManifestSha256,
                    outcome,
                    measurement = new
                    {
                        measurement.State,
                        measurement.Incomplete,
                        trails = measurement.Trails.Select(trail => new
                        {
                            trail.CandidateId,
                            trail.CandidateIdentitySha256,
                            trail.ExtractionIdentitySha256,
                            trail.SourceManifest.Descriptor.Artifact.ArtifactId,
                            trail.DetectorInput,
                            trail.Geometry
                        })
                    },
                    presentationStatus = view.Status,
                    presentationReason = view.Reason,
                    layers = view.Presentation.Layers,
                    view.Presentation.BaseArtifactId,
                    predictionProducts = predictionNode.Outputs,
                    status,
                    linearProducts = linear.Outputs,
                    satelliteLayerProducts = satelliteLayer.Outputs,
                    overlayProducts = overlay.Outputs
                }, ct).ConfigureAwait(false);
                TestContext.WriteLine($"slot {101 + index}: {status.Status}, {outcome.CausalSucceeded}/{outcome.Reason}/{outcome.CandidateCount}, {measurement.State}");
                if (index < 2)
                {
                    Assert.IsFalse(outcome.CausalSucceeded);
                    Assert.AreEqual("causal-context-pending", outcome.Reason);
                    continue;
                }

                Assert.IsTrue(view.Presentation.Layers.Single(static layer => layer.Kind == OrbitalTrackPresentation.LayerKind)
                    .Satellite!.Renderable);
                var binding = predictionNode.Outputs.Single(static output => output.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
                var bound = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(
                    Path.Combine(ingressRoot, binding.PayloadRelativePath), ct).ConfigureAwait(false));
                Assert.AreEqual(input.Snapshot.Manifest.ManifestSha256, CaptureSatelliteProducts.ReadSnapshotManifest(bound)!.ManifestSha256);
                var predictionFile = predictionNode.Outputs.Single(static output => output.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion);
                var prediction = CaptureOrbitalPredictionJson.Parse(await File.ReadAllBytesAsync(
                    Path.Combine(ingressRoot, predictionFile.PayloadRelativePath), ct).ConfigureAwait(false),
                    CaptureSatelliteProducts.Restore(bound, OrbitalSnapshotReadResult.Available(input.Snapshot)));
                CollectionAssert.AreEquivalent(CatalogNumbers, prediction.Tracks.Select(track => track.Lineage.ObjectIdentity.CatalogNumber).ToArray());
                foreach (var track in prediction.Tracks)
                {
                    var expected = input.Tracks[(index, track.Lineage.ObjectIdentity.CatalogNumber)];
                    Assert.AreEqual(status.Exposure, track.RequestedInterval);
                    Assert.IsLessThanOrEqualTo(ShutterDelay, (expected.RequestedInterval.StartUtc - track.RequestedInterval.StartUtc).Duration());
                    Assert.AreEqual(expected.Lineage.RecordSha256, track.Lineage.RecordSha256);
                }
                Assert.IsTrue(outcome.CausalSucceeded, outcome.Reason);
                if (index == 5)
                {
                    Assert.AreEqual("transient-extraction.no-candidate", outcome.Reason);
                    Assert.AreEqual(0, outcome.CandidateCount);
                    Assert.IsEmpty(measurement.Trails);
                    continue;
                }

                Assert.IsNotEmpty(measurement.Trails, $"Slot {index + 101} must contain a measured trail.");
                var bright = input.Tracks[(index, CatalogNumbers[0])];
                var dark = input.Tracks[(index, CatalogNumbers[1])];
                var rawBytes = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath, ct).ConfigureAwait(false);
                var brightCodes = SampleCorridor(rawBytes, bright, bayer);
                var darkCodes = SampleCorridor(rawBytes, dark, bayer);
                Assert.IsGreaterThan(50, brightCodes.RedOrMonoMedian - darkCodes.RedOrMonoMedian);
                Assert.IsLessThan(40, darkCodes.RedOrMonoMedian);
                if (bayer)
                {
                    Assert.IsGreaterThan(30, brightCodes.RedOrMonoMedian - brightCodes.BlueMedian!.Value);
                    var point = bright.Samples[bright.Samples.Count / 2].Pixel!.Value;
                    var previewColor = pixels.GetPixel((int)point.X, (int)point.Y);
                    Assert.IsGreaterThan(10, previewColor.Red - previewColor.Blue, "The decoded base retains asymmetric Bayer color.");
                }
                foreach (var trail in measurement.Trails)
                {
                    Assert.AreEqual(receipt.Manifest.Descriptor.Artifact.ArtifactId, trail.SourceManifest.Descriptor.Artifact.ArtifactId);
                    Assert.AreEqual(bayer ? .5 : 1, trail.DetectorInput.SourceToDetectorTransform.ScaleX);
                }
                var brightPath = TrackPixels(bright);
                var brightMatch = Assert.ContainsSingle(measurement.Trails.Where(trail => Matches(trail, brightPath)));
                Assert.IsFalse(Matches(PerpendicularControl(brightMatch, brightPath), brightPath),
                    "A perpendicular crossing must not score as the predicted trail.");
                var darkCount = measurement.Trails.Count(trail => Matches(trail, TrackPixels(dark)));
                Assert.AreEqual(0, darkCount, "No measured trail should match the dark prediction.");
                var centerline = brightMatch.Geometry.Polyline;
                var center = new TransientPointV1((centerline[0].X + centerline[^1].X) / 2, (centerline[0].Y + centerline[^1].Y) / 2);
                if (previousBrightCenter is { } previous)
                    Assert.IsGreaterThan(bayer ? 4 : 8, double.Hypot(center.X - previous.X, center.Y - previous.Y));
                previousBrightCenter = center;
                var unassigned = measurement.Trails.Where(trail => trail.CandidateId != brightMatch.CandidateId).ToArray();
                if (index == 4)
                {
                    var projector = ProjectorFactory.Create(RigProjectionContextFactory.Create(config.Rig));
                    var unrelatedPath = Enumerable.Range(0, 11).Select(step => projector.Project(new(54, 240 + step))!.Value).ToArray();
                    Assert.IsTrue(unassigned.Any(trail => Matches(trail, unrelatedPath)),
                        "The independent streak may produce multiple measured components.");
                }
                scored.Add(new { slot = index + 101, brightMatch.CandidateId, darkCount, center, unassigned = unassigned.Length, brightCodes, darkCodes });
            }
            await WriteAsync(Path.Combine(root, "private-score.json"), scored, ct).ConfigureAwait(false);
        }
        finally
        {
            if (export is null) Directory.Delete(root, recursive: true);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false, false, null, 10_000L, false)]
    [DataRow(true, false, false, null, 10_000L, false)]
    [DataRow(false, true, false, null, 10_000L, false)]
    [DataRow(false, false, true, null, 10_000L, false)]
    [DataRow(false, false, false, "missing-marker", 10_000L, false)]
    [DataRow(false, false, false, "missing-inputs", 10_000L, false)]
    [DataRow(false, false, false, "null-inputs", 10_000L, false)]
    [DataRow(false, false, false, "ordinary-transient", 10_000L, false)]
    [DataRow(false, false, false, "generic-package-hash", 10_000L, false)]
    [DataRow(false, false, true, null, 5_000L, false)]
    [DataRow(false, false, false, null, 5_000L, false)]
    [DataRow(false, false, true, "retained-coarse-plan", 5_000L, false)]
    [DataRow(false, false, false, "missing-shutter", 10_000L, false)]
    [DataRow(false, false, false, "contradictory-shutter", 10_000L, false)]
    [DataRow(false, false, true, null, 10_000L, true)]
    [DataRow(false, false, true, null, 5_000L, true)]
    [DataRow(false, false, false, null, 5_000L, true)]
    [DataRow(false, false, false, "generic-package-hash", 10_000L, true)]
    [DataRow(false, false, false, "missing-shutter", 10_000L, true)]
    [DataRow(false, false, false, "contradictory-shutter", 10_000L, true)]
    public Task DelayedShutterCommitsAndPredictsWithItsObservedInterval(bool clouds, bool wrongExpectedRecord,
        bool crossesSelectionBoundary, string? metadataCase, long shutterDelayTicks, bool bayer)
        => CaptureAndVerifyPredictionAsync(clouds, wrongExpectedRecord, crossesSelectionBoundary, metadataCase, shutterDelayTicks, bayer);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UnavailableNamedPassSnapshotRetainsStatusWithoutTracks(bool bayer)
        => CaptureAndVerifyPredictionAsync(clouds: false, wrongExpectedRecord: false, crossesSelectionBoundary: false,
            metadataCase: null, shutterDelayTicks: 5_000L, bayer: bayer,
            unavailablePinReason: CaptureSatelliteReason.PinnedSnapshotUnavailable);

    [TestMethod]
    [DataRow(false, false, DisplayName = "Mono, different leap-second file")]
    [DataRow(true, false, DisplayName = "Bayer, different leap-second file")]
    [DataRow(false, true, DisplayName = "Mono, different Earth-orientation file")]
    [DataRow(true, true, DisplayName = "Bayer, different Earth-orientation file")]
    public Task UnavailableNamedPassTimeInputsRetainStatusWithoutTracks(bool bayer, bool overrideEarthOrientation)
        => CaptureAndVerifyPredictionAsync(clouds: false, wrongExpectedRecord: false, crossesSelectionBoundary: false,
            metadataCase: null, shutterDelayTicks: 5_000L, bayer: bayer,
            unavailablePinReason: CaptureSatelliteReason.PinnedTimeInputsUnavailable, overrideEarthOrientation);

    private async Task CaptureAndVerifyPredictionAsync(bool clouds, bool wrongExpectedRecord, bool crossesSelectionBoundary,
        string? metadataCase, long shutterDelayTicks, bool bayer, CaptureSatelliteReason? unavailablePinReason = null,
        bool overrideEarthOrientation = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-named-shutter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ct = TestContext.CancellationToken;
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-prediction-v1");
            var config = MakeConfiguration(fixture, bayer);
            var requested = First + 3 * Exposure;
            var shutter = requested.AddTicks(shutterDelayTicks);
            var input = await PreparePackageAsync(root, fixture, config, ct,
                selectionBoundary: crossesSelectionBoundary ? shutter : null).ConfigureAwait(false);
            var ingressRoot = Path.Combine(root, "ingress");
            var genericPackageHash = metadataCase == "generic-package-hash";
            var ordinaryTransient = genericPackageHash || metadataCase == "ordinary-transient";
            config = ConfigureGraph(config, input, ingressRoot, wrongExpectedRecord,
                omitGraphPin: crossesSelectionBoundary || metadataCase is not null);
            if (ordinaryTransient)
            {
                var options = JsonNode.Parse(config.Module.Options!.Value.GetRawText())!.AsObject();
                Assert.IsTrue(options.Remove("namedOrbitalPass"));
                if (genericPackageHash)
                    options["transientScenario"]!["sourcePackageSha256"] = input.PackageSha256;
                config = config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options, Json) } };
            }
            if (clouds)
            {
                var options = JsonNode.Parse(config.Module.Options!.Value.GetRawText())!.AsObject();
                options["cloudScenario"] = JsonSerializer.SerializeToNode(new VirtualCloudScenarioDefinition
                {
                    EpochUtc = First,
                    Keyframes = [new() { Coverage = .3, MaximumOpacity = .2 }]
                }, Json);
                config = config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options, Json) } };
            }
            if (crossesSelectionBoundary)
            {
                var objectIdentity = new OrbitalObjectIdentity("norad", CatalogNumbers[0]);
                var policy = CaptureSatelliteTracksOptions.CreateSelectionPolicy(false);
                var nominal = OrbitalRecordSelector.Select(input.Snapshot, objectIdentity, new(requested, requested + Exposure), policy);
                var observed = OrbitalRecordSelector.Select(input.Snapshot, objectIdentity, new(shutter, shutter + Exposure), policy);
                Assert.AreEqual(input.Tracks[(3, CatalogNumbers[0])].Lineage.RecordSha256, nominal.SelectedRecord!.RecordSha256);
                Assert.AreNotEqual(nominal.SelectedRecord.RecordSha256, observed.SelectedRecord!.RecordSha256);
                Assert.AreEqual(objectIdentity, observed.SelectedRecord.ObjectIdentity);
                Assert.HasCount(2, observed.Candidates.Where(static candidate => candidate.Eligibility == OrbitalSelectionEligibility.Eligible));
            }
            var clock = new ExposureTestTimeProvider(shutter);
            var invalidTiming = metadataCase is "missing-shutter" or "contradictory-shutter";
            var retainedCoarsePlan = metadataCase == "retained-coarse-plan";
            var malformedLineage = metadataCase is not null && !ordinaryTransient && !invalidTiming && !retainedCoarsePlan;
            var missingSnapshot = unavailablePinReason == CaptureSatelliteReason.PinnedSnapshotUnavailable;
            var bright = input.Tracks[(3, CatalogNumbers[0])];
            CaptureLoopSubmission submission;
            var provider = CreateProvider(ingressRoot, clock, new InMemoryCelestialCatalog([]), input);
            await using (provider.ConfigureAwait(false))
            {
                await InitializeCaptureServicesAsync(provider, config, ct).ConfigureAwait(false);
                var installed = missingSnapshot
                    ? OrbitalSnapshot.Create(input.Snapshot.Records, [input.Snapshot.Manifest.Sources.Single() with
                    {
                        TermsReference = "Active fixture snapshot with a different identity from the package pin."
                    }], input.Snapshot.Manifest.Coverage)
                    : input.Snapshot;
                await InstallSnapshotAsync(provider, installed, ct).ConfigureAwait(false);
                var module = provider.GetRequiredService<ICameraModuleFactory>().Create(config.ModuleType);
                await using var moduleLifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(config, ct).ConfigureAwait(false);
                var request = new CaptureRequest(requested, Exposure, CaptureMode.Still, new(Exposure, 0, null, null));
                var exposureTask = module.CaptureAsync(request, ct);
                await clock.TimerCreated.Task.WaitAsync(ct).ConfigureAwait(false);
                clock.Advance(Exposure);
                var capture = await exposureTask.ConfigureAwait(false);
                var rendered = capture.Frame!.Metadata.Scene!;
                var expectedScenarioStart = ordinaryTransient ? requested : shutter;
                Assert.AreEqual(expectedScenarioStart, rendered.VirtualExposure!.ScenarioStartUtc);
                Assert.AreEqual(requested, rendered.VirtualExposure.RequestedStartUtc);
                Assert.AreEqual(expectedScenarioStart, rendered.TransientScenario!.IntegrationStartUtc);
                Assert.AreEqual(clouds ? shutter : (DateTimeOffset?)null, rendered.CloudScenario?.IntegrationStartUtc);
                Assert.AreEqual(ordinaryTransient, VirtualSkyNamedPass.TryReadPredictionLineage(config, null, out _));
                Assert.IsTrue(VirtualSkyNamedPass.TryReadPredictionLineage(config, rendered, out var lineage));
                if (ordinaryTransient)
                {
                    Assert.AreEqual(genericPackageHash, rendered.TransientScenario.Parameters.TryGetProperty("sourcePackageSha256", out var hash));
                    if (genericPackageHash)
                        Assert.AreEqual(input.PackageSha256, hash.GetString());
                    Assert.IsNull(lineage);
                }
                else
                {
                    Assert.AreEqual(bright.Lineage.RecordSha256, lineage!.Inputs.ExpectedRecords![CatalogNumbers[0]]);
                }
                if (crossesSelectionBoundary)
                {
                    var pixels = capture.Frame.PixelData.ToArray();
                    var brightCodes = SampleCorridor(pixels, bright, bayer);
                    var darkCodes = SampleCorridor(pixels, input.Tracks[(3, CatalogNumbers[1])], bayer);
                    Assert.IsGreaterThan(50, brightCodes.RedOrMonoMedian - darkCodes.RedOrMonoMedian);
                }
                if (invalidTiming)
                {
                    var scenario = rendered.TransientScenario;
                    if (metadataCase == "contradictory-shutter")
                        scenario = scenario with
                        {
                            IntegrationStartUtc = scenario.IntegrationStartUtc.AddSeconds(10),
                            IntegrationEndUtc = scenario.IntegrationEndUtc.AddSeconds(10)
                        };
                    capture = capture with
                    {
                        Frame = capture.Frame with
                        {
                            Metadata = capture.Frame.Metadata with
                            {
                                Scene = rendered with { VirtualExposure = null, TransientScenario = scenario }
                            }
                        }
                    };
                }
                if (malformedLineage)
                {
                    // Persist an internally consistent raw sidecar with incomplete named-pass metadata.
                    var scenario = rendered.TransientScenario;
                    var parameters = JsonNode.Parse(scenario.Parameters.GetRawText())!.AsObject();
                    switch (metadataCase)
                    {
                        case "missing-marker":
                            Assert.IsTrue(parameters.Remove("sourcePackageSha256"));
                            Assert.IsTrue(parameters.Remove("namedPassPredictionV1"));
                            break;
                        case "missing-inputs":
                            Assert.IsTrue(parameters["namedPassPredictionV1"]!.AsObject().Remove("inputs"));
                            break;
                        case "null-inputs":
                            parameters["namedPassPredictionV1"]!["inputs"] = null;
                            break;
                    }
                    var modified = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(parameters));
                    capture = capture with
                    {
                        Frame = capture.Frame with
                        {
                            Metadata = capture.Frame.Metadata with
                            {
                                Scene = rendered with
                                {
                                    TransientScenario = scenario with
                                    {
                                        Parameters = modified,
                                        ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(modified)
                                    }
                                }
                            }
                        }
                    };
                }

                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                var host = new CaptureHostContext(config, ingress, provider.GetRequiredService<ICaptureDistributor>(), null,
                    provider.GetRequiredService<IProjectedSceneStagingStore>(), provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
                    provider.GetRequiredService<CaptureProjectedSceneStager>());
                submission = new(request, capture, shutter, Exposure, Exposure);
                await host.PublishAsync(submission, ct).ConfigureAwait(false);
            }

            // Resolve the admitted scene from the durable raw sidecar using newly opened services.
            FilePin? leapOverride = null;
            FilePin? earthOrientationOverride = null;
            if (unavailablePinReason == CaptureSatelliteReason.PinnedTimeInputsUnavailable)
            {
                // Change one file's bytes while keeping its parsed rows usable.
                var original = overrideEarthOrientation ? input.Time.ExportEarthOrientation() : input.Time.ExportLeapSeconds();
                byte[] changed = [.. original, (byte)'\n'];
                var overridePin = new FilePin(Path.Combine(root, overrideEarthOrientation ? "actual-earth-orientation" : "actual-leaps"),
                    Hash(changed));
                Assert.AreNotEqual(Hash(original), overridePin.Sha256,
                    "The usable time-input override must differ from the retained pin.");
                await File.WriteAllBytesAsync(overridePin.Path, changed, ct).ConfigureAwait(false);
                if (overrideEarthOrientation)
                    earthOrientationOverride = overridePin;
                else
                    leapOverride = overridePin;
            }
            var reopened = CreateProvider(ingressRoot, new ExposureTestTimeProvider(clock.GetUtcNow()),
                new InMemoryCelestialCatalog([]), input, leapOverride, earthOrientationOverride);
            await using var reopenedLifetime = reopened.ConfigureAwait(false);
            await InitializeCaptureServicesAsync(reopened, config, ct).ConfigureAwait(false);
            var lane = reopened.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
            var queue = reopened.GetRequiredService<ICaptureLaneStore>();
            var lease = await queue.ClaimAsync(lane, Agent, config, ct).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var manifest = lease.Context.RawCapture.Manifest;
            Assert.IsTrue(manifest.Validate().IsValid);
            var descriptorStart = DateTimeOffset.FromUnixTimeMilliseconds(shutter.ToUnixTimeMilliseconds());
            Assert.AreEqual(descriptorStart, manifest.Descriptor.Timing.ExposureStartedUtc);
            Assert.AreEqual(requested, manifest.Descriptor.Timing.RequestedStartUtc);
            if (unavailablePinReason is not null)
            {
                Assert.IsTrue(VirtualSkyNamedPass.TryReadPredictionLineage(config, manifest.Scene, out var retainedLineage));
                Assert.IsNotNull(retainedLineage);
                Assert.AreEqual(input.Snapshot.Manifest.ManifestSha256, retainedLineage.Inputs.SnapshotManifestSha256);
                Assert.AreEqual(input.Time.EarthOrientationSha256, retainedLineage.Inputs.EarthOrientationSha256);
                Assert.AreEqual(input.Time.LeapSecondsSha256, retainedLineage.Inputs.LeapSecondsSha256);
                var snapshots = reopened.GetRequiredService<IOrbitalSnapshotReader>();
                var active = await snapshots.ReadCurrentAsync(ct).ConfigureAwait(false);
                Assert.IsNotNull(active.Snapshot);
                var exact = await snapshots.ReadExactAsync(retainedLineage.Inputs.SnapshotManifestSha256, ct).ConfigureAwait(false);
                if (missingSnapshot)
                {
                    Assert.AreNotEqual(retainedLineage.Inputs.SnapshotManifestSha256, active.Snapshot.Manifest.ManifestSha256);
                    Assert.AreEqual(OrbitalSnapshotUnavailableReason.UnknownIdentity, exact.UnavailableReason);
                }
                else
                {
                    Assert.IsNotNull(exact.Snapshot);
                    var time = await reopened.GetRequiredService<ICameraAgentOrbitalTimeSource>()
                        .ResolveAsync(new OrbitalUtcInterval(shutter, shutter + Exposure), ct).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Override, time.Source);
                    Assert.IsNotNull(time.Data);
                    Assert.AreEqual(earthOrientationOverride?.Sha256 ?? retainedLineage.Inputs.EarthOrientationSha256,
                        time.Data.EarthOrientationSha256);
                    Assert.AreEqual(leapOverride?.Sha256 ?? retainedLineage.Inputs.LeapSecondsSha256, time.Data.LeapSecondsSha256);
                }
            }
            if (invalidTiming)
            {
                Assert.IsNull(manifest.Scene!.VirtualExposure);
                var integrationStart = metadataCase == "contradictory-shutter" ? shutter.AddSeconds(10) : shutter;
                Assert.AreEqual(integrationStart, manifest.Scene.TransientScenario!.IntegrationStartUtc);
                Assert.AreEqual(integrationStart + Exposure, manifest.Scene.TransientScenario.IntegrationEndUtc);
                Assert.IsTrue(VirtualSkyNamedPass.TryReadPredictionLineage(config, manifest.Scene, out var retainedLineage));
                Assert.AreEqual(bright.Lineage.RecordSha256, retainedLineage!.Inputs.ExpectedRecords![CatalogNumbers[0]]);
            }
            if (retainedCoarsePlan)
            {
                var work = reopened.GetRequiredService<CaptureSatelliteWorkStore>();
                var rawContext = new CaptureProcessingContext(config, submission, lease.Context.RawCapture);
                await RetainCoarsePlanAsync(work, rawContext, input, bright, ct).ConfigureAwait(false);
            }
            var result = await reopened.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, ct).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await queue.CompleteAsync(lease, ct).ConfigureAwait(false);
            var store = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
            var node = await store.ReadNodeAsync(manifest.Descriptor.Capture.CaptureId, "satellite", ct).ConfigureAwait(false);
            Assert.IsNotNull(node);
            var status = node.Outputs.Single(static output => output.ProductSchemaVersion == CaptureSatelliteProducts.StatusSchema);
            var prediction = CaptureSatelliteProducts.ParseStatus(await File.ReadAllBytesAsync(
                Path.Combine(ingressRoot, status.PayloadRelativePath), ct).ConfigureAwait(false));
            TestContext.WriteLine($"observed={shutter:O} descriptor={descriptorStart:O} mapping={manifest.Scene?.VirtualExposure?.TimeMapping} " +
                $"integration={manifest.Scene?.TransientScenario?.IntegrationStartUtc:O} prediction={prediction.Exposure.StartUtc:O} " +
                $"reason={prediction.Reason} renderable={prediction.Renderable} tracks={prediction.TrackCount}");
            var expectedReason = unavailablePinReason?.ToString() ?? (invalidTiming ? "NamedPassPredictionTimingUnavailable" :
                malformedLineage ? "NamedPassPredictionLineageUnavailable" :
                retainedCoarsePlan ? "NamedPassPredictionTimingMismatch" :
                crossesSelectionBoundary ? "PinnedRecordSelectionMismatch" :
                wrongExpectedRecord ? "NamedPassPredictionInputsMismatch" : "None");
            Assert.AreEqual(expectedReason, prediction.Reason);
            var expectedStart = invalidTiming ? descriptorStart : shutter;
            Assert.AreEqual(new OrbitalUtcInterval(expectedStart, expectedStart + Exposure), prediction.Exposure);
            Assert.AreEqual(expectedReason == "None" ? 2 : 0, prediction.TrackCount);
            Assert.AreEqual(expectedReason == "None", prediction.Renderable);
            var evidence = node.Outputs.Single(static output => output.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
            var retained = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(
                Path.Combine(ingressRoot, evidence.PayloadRelativePath), ct).ConfigureAwait(false));
            Assert.AreEqual(shutterDelayTicks == 5_000 ? manifest.Scene!.VirtualExposure : null, retained.VirtualShutter);
            var orbital = node.Outputs.SingleOrDefault(static output =>
                output.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion);
            if (prediction.Renderable)
            {
                Assert.IsNotNull(orbital);
                var exact = await reopened.GetRequiredService<IOrbitalSnapshotReader>()
                    .ReadExactAsync(input.Snapshot.Manifest.ManifestSha256, ct).ConfigureAwait(false);
                var restored = CaptureSatelliteProducts.Restore(retained, exact);
                Assert.AreEqual(prediction.Exposure, restored.Plan.Timing.Exposure);
                var tracks = CaptureOrbitalPredictionJson.Parse(await File.ReadAllBytesAsync(
                    Path.Combine(ingressRoot, orbital.PayloadRelativePath), ct).ConfigureAwait(false), restored);
                Assert.HasCount(2, tracks.Tracks);
                Assert.IsTrue(tracks.Tracks.All(track => track.RequestedInterval == prediction.Exposure));
            }
            else
            {
                Assert.AreEqual("Unavailable", prediction.Status);
                Assert.IsNull(retained.Plan);
                Assert.IsNull(orbital);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RetainCoarsePlanAsync(CaptureSatelliteWorkStore store, CaptureProcessingContext context,
        PreparedInput input, OrbitalExposureTrackV1 bright, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor!;
        var rawTiming = descriptor.Timing;
        var timing = new OrbitalTimingFacts(new(rawTiming.ExposureStartedUtc, rawTiming.ExposureEndedUtc),
            OrbitalTimingBasis.Reported, rawTiming.ReadoutCompletedUtc, OrbitalTimingBasis.Reported,
            "capture-descriptor-command-status-observations-v1", AstrometricClockFacts.NotSupplied);
        var available = First.AddDays(-1);
        var options = new CaptureSatelliteTracksOptions();
        var plan = new CaptureOrbitalPredictionInput(descriptor, bright.Observer, bright.Geometry, timing, bright.Model,
            options.Bounds, new(CaptureOrbitalAssessmentMode.OriginalAvailability, rawTiming.DurableIngressUtc,
                rawTiming.DurableIngressUtc, available), OrbitalSnapshotReadResult.Available(input.Snapshot),
            CaptureSatelliteTracksOptions.CreateSelectionPolicy(false), CatalogNumbers.Select(static n => new OrbitalObjectIdentity("norad", n)));
        Assert.AreEqual(bright.Lineage.RecordSha256, plan.Plan.Selections.Single(selection =>
            selection.ObjectIdentity.CatalogNumber == CatalogNumbers[0]).SelectedRecord!.RecordSha256);
        var coarse = new CaptureSatelliteInputV1(CaptureSatelliteProducts.InputSchema, descriptor, context.Config.Rig,
            bright.Observer, plan.SerializePlan(), input.Snapshot.SerializeManifest(), null,
            input.Time.ExportEarthOrientation(), input.Time.ExportLeapSeconds(), "Override", available, null);
        var prepared = await store.PrepareAsync(context, "satellite", options.OutputVariant, () => ValueTask.FromResult(coarse),
            cancellationToken).ConfigureAwait(false);
        _ = await store.PinAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PinRejectsAlteredBytesWrongObjectShutterAndMirror()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-named-pass-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ct = TestContext.CancellationToken;
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-prediction-v1");
            var config = MakeConfiguration(fixture, bayer: true);
            var package = await PreparePackageAsync(root, fixture, config, ct).ConfigureAwait(false);
            var pin = new VirtualSkyNamedPassOptions(package.PackagePath, package.PackageSha256);
            var options = new VirtualSkyCameraModuleOptions();
            _ = VirtualSkyNamedPass.Prepare(pin, config, options, null, ct);
            Reject(pin with { PackageSha256 = new string('A', 64) }, config, "checksum");
            Reject(pin, config with
            {
                Rig = config.Rig with
                { Optics = config.Rig.Optics with { HorizontalFlip = !config.Rig.Optics.HorizontalFlip } }
            }, "projection");
            Reject(pin, config with
            {
                DeploymentLocation = null,
                Observatory = config.Observatory with
                { LatitudeDegrees = config.Observatory.LatitudeDegrees + .1 }
            }, "observer");

            var original = JsonNode.Parse(await File.ReadAllBytesAsync(package.PackagePath, ct).ConfigureAwait(false))!;
            var wrongObject = original.DeepClone();
            wrongObject["objects"]![0]!["catalogNumber"] = "25544";
            Reject(await WritePinAsync("wrong-object.json", wrongObject).ConfigureAwait(false), config, "record");
            var wrongTime = original.DeepClone();
            wrongTime["objects"]![0]!["tracks"]![0]!["exposureStartUtc"] = First;
            Reject(await WritePinAsync("wrong-time.json", wrongTime).ConfigureAwait(false), config, "time");
            var wrongInput = original.DeepClone();
            wrongInput["objects"]![0]!["tracks"]![0]!["track"]!["sha256"] = new string('C', 64);
            Reject(await WritePinAsync("wrong-input.json", wrongInput).ConfigureAwait(false), config, "checksum");
            var alternateRoot = Path.Combine(root, "competing");
            Directory.CreateDirectory(alternateRoot);
            var competing = await PreparePackageAsync(alternateRoot, fixture, config, ct, competingRecord: true).ConfigureAwait(false);
            Reject(new(competing.PackagePath, competing.PackageSha256), config, "selection policy");
            Assert.ThrowsExactly<OperationCanceledException>(() => VirtualSkyNamedPass.Prepare(pin, config, options, null, new(true)));

            var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(First, autoAdvance: true),
                new InMemoryCelestialCatalog([]), new ProjectedSceneStore());
            await using var moduleLifetime = module.ConfigureAwait(false);
            var preflight = (ICameraModuleConfigurationPreflight)module;
            var badPath = Path.Combine(root, "missing.json");
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                preflight.ValidateConfiguration(WithPass(config, pin with { PackagePath = badPath })));
            if (OperatingSystem.IsLinux())
            {
                var fifo = Path.Combine(root, "package.fifo");
                using var command = Process.Start(new ProcessStartInfo("mkfifo", [fifo]));
                Assert.IsNotNull(command);
                await command.WaitForExitAsync(ct).ConfigureAwait(false);
                Assert.AreEqual(0, command.ExitCode);
                _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    preflight.ValidateConfiguration(WithPass(config, pin with { PackagePath = fifo })));
            }

            var seedOnly = original.DeepClone();
            foreach (var entry in seedOnly["objects"]!.AsArray())
            {
                var source = Path.Combine(Path.GetDirectoryName(package.PackagePath)!, entry!["optics"]!["path"]!.GetValue<string>());
                var optics = JsonNode.Parse(await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false))!;
                optics["seed"] = 528;
                var changed = await WritePinAsync("seed-" + Path.GetFileName(source), optics).ConfigureAwait(false);
                entry["optics"] = JsonSerializer.SerializeToNode(new FilePin(Path.GetFileName(changed.PackagePath), changed.PackageSha256), Json);
            }
            var changedSeed = await WritePinAsync("changed-seed.json", seedOnly).ConfigureAwait(false);
            var baselinePixels = await RenderAsync(pin).ConfigureAwait(false);
            var changedPixels = await RenderAsync(changedSeed).ConfigureAwait(false);
            Assert.AreNotEqual(baselinePixels, changedPixels);

            async Task<string> RenderAsync(VirtualSkyNamedPassOptions reference)
            {
                var clock = new ExposureTestTimeProvider(First, autoAdvance: true);
                var camera = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());
                await using var cameraLifetime = camera.ConfigureAwait(false);
                await camera.InitializeAsync(WithPass(config, reference), ct).ConfigureAwait(false);
                var frame = await camera.CaptureAsync(new(First, Exposure, CaptureMode.Still, new(Exposure, 0, null, null)), ct)
                    .ConfigureAwait(false);
                return Hash(frame.Frame!.PixelData.ToArray());
            }

            void Reject(VirtualSkyNamedPassOptions reference, CameraModuleConfig configuration, string reason)
            {
                var error = Assert.ThrowsExactly<InvalidDataException>(() =>
                    VirtualSkyNamedPass.Prepare(reference, configuration, options, null, ct));
                StringAssert.Contains(error.Message, reason, StringComparison.OrdinalIgnoreCase);
            }

            async Task<VirtualSkyNamedPassOptions> WritePinAsync(string name, JsonNode content)
            {
                var path = Path.Combine(Path.GetDirectoryName(package.PackagePath)!, name);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(content, Json);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                return new(path, Hash(bytes));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraModuleConfig WithPass(CameraModuleConfig config, VirtualSkyNamedPassOptions reference)
    {
        var options = JsonNode.Parse(config.Module.Options!.Value.GetRawText())!.AsObject();
        options["namedOrbitalPass"] = JsonSerializer.SerializeToNode(reference, Json);
        return config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options, Json) } };
    }

    private static CameraModuleConfig MakeConfiguration(string fixture, bool bayer)
    {
        var basis = JsonSerializer.Deserialize<CameraModuleConfig>(
            File.ReadAllBytes(Path.Combine(fixture, "virtual-camera", "settings-hvo.json")), Json)!;
        var rig = basis.Rig with
        {
            ProfileVersion = bayer ? "named-pass-rggb16-v1" : "named-pass-mono16-v1",
            Pipeline = basis.Rig.Pipeline with { CaptureInterval = Exposure, NightExposure = Exposure },
            ControlPolicy = new()
            {
                ExposureControl = AutomaticControlOwnership.Disabled,
                GainControl = AutomaticControlOwnership.Disabled
            },
            Sensor = bayer ? basis.Rig.Sensor : basis.Rig.Sensor with
            {
                PixelFormat = CameraPixelFormat.Mono16,
                ColorMode = SensorColorMode.Mono,
                ResponseMode = SensorResponseMode.Monochrome
            }
        };
        var site = basis.Observatory;
        return basis with
        {
            AgentId = Agent,
            Rig = rig,
            DeploymentLocation = DeploymentLocationSnapshot.Create("named-pass-site", 1, "fixture", null,
                DateTimeOffset.UnixEpoch, null, site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters, site.TimeZoneId)
        };
    }

    private static CameraModuleConfig ConfigureGraph(CameraModuleConfig config, PreparedInput input, string storageRoot,
        bool wrongExpectedRecord = false, bool omitGraphPin = false)
    {
        var module = JsonNode.Parse(config.Module.Options!.Value.GetRawText())!.AsObject();
        module["illuminationMode"] = "ControlledNight";
        // Keep independent catalog masks compatible across the causal window while the named pass follows its shutter.
        module["fixedSceneUtc"] = First;
        module["maximumResults"] = 128;
        module["renderSolarSystemDisks"] = false;
        module["namedOrbitalPass"] = JsonSerializer.SerializeToNode(new VirtualSkyNamedPassOptions(input.PackagePath, input.PackageSha256), Json);
        module["transientScenario"] = JsonSerializer.SerializeToNode(new VirtualTransientScenarioDefinition
        {
            EpochUtc = First,
            SkyTracks = [new()
            {
                PrimitiveId = "unrelated-control",
                Keyframes = [ControlPoint(20, 240), ControlPoint(25, 250)]
            }]
        }, Json);
        var expectedRecords = CatalogNumbers.ToDictionary(static number => number,
            number => input.Tracks[(3, number)].Lineage.RecordSha256);
        if (wrongExpectedRecord)
            expectedRecords[CatalogNumbers[0]] = expectedRecords[CatalogNumbers[1]];
        var pin = new CaptureSatellitePinnedInputs(input.Snapshot.Manifest.ManifestSha256, input.Time.EarthOrientationSha256!, input.Time.LeapSecondsSha256,
            expectedRecords);
        return config with
        {
            Module = config.Module with { Options = JsonSerializer.SerializeToElement(module, Json) },
            Pipeline = new([
                Step("ProjectedScene", "scene", ["$raw"]),
                Step("Calibration", "linear", ["$raw"]),
                Step("RollingCombination", "single-contributor-base", ["linear"], new { windowSize = 1 }),
                Step("Preview", "preview", ["single-contributor-base"]),
                Step("ScenePresentationLayer", "scene-layer", ["scene"]),
                Step("EnvironmentPresentationLayer", "environment", ["scene", "preview"],
                    new { widthPixels = 640, heightPixels = 640, stackPreviewVariant = "default" }),
                Step("CaptureSatelliteTracks", "satellite", ["$raw"], new CaptureSatelliteTracksOptions
                {
                    CatalogNumbers = CatalogNumbers, PinnedInputs = omitGraphPin ? null : pin, OverrideTimeDataAvailableUtc = First.AddDays(-1)
                }),
                Step("CaptureSatelliteLayer", "satellite-layer", ["satellite"]),
                Step("OverlayManifest", "overlay", ["preview", "scene-layer", "environment", "satellite-layer", "satellite"],
                    new { basePreviewVariant = "default", satelliteVariant = "capture-satellites-v1-layer",
                        satelliteStatusVariant = "capture-satellites-v1-status" }),
                new("Storage", "storage", DependsOn: ["$raw", "scene", "linear", "single-contributor-base", "preview", "scene-layer", "environment",
                    "satellite", "satellite-layer", "overlay"], Options: JsonSerializer.SerializeToElement(
                        new FileStorageCaptureProcessingStepOptions
                        {
                            StorageRoot = storageRoot, UpdateLatestFrame = false
                        }, Json))
            ])
        };
    }

    private static VirtualTransientSkyKeyframe ControlPoint(double offset, double azimuth)
        => new() { OffsetSeconds = offset, AltitudeDegrees = 54, AzimuthDegrees = azimuth, Magnitude = -3, AngularWidthDegrees = .09 };

    private static CaptureProcessingStepConfig Step(string type, string id, IReadOnlyList<string> dependencies, object? options = null)
        => new(type, id, DependsOn: dependencies, Options: options is null ? null : JsonSerializer.SerializeToElement(options, Json),
            Publication: new(CaptureProcessingPersistenceMode.DurableLocal));

    private static ServiceProvider CreateProvider(string root, TimeProvider clock, ICelestialCatalog catalog, PreparedInput input,
        FilePin? leapOverride = null, FilePin? earthOrientationOverride = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock);
        services.AddSingleton(catalog);
        var packageRoot = Path.GetDirectoryName(input.PackagePath)!;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:AgentId"] = Agent,
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:TransientDetection:Mode"] = "Edge",
            ["CameraAgent:TransientDetection:ExposureIntegratedStarMask"] = "true",
            ["CameraAgent:TransientDetection:StarMaximumResults"] = "32768",
            ["CameraAgent:TransientDetection:StarMaximumMagnitude"] = "6.5",
            ["CameraAgent:TransientDetection:StarSupportRadiusSourcePixels"] = "2",
            ["CameraAgent:SatellitePlanning:Enabled"] = "true",
            ["CameraAgent:SatellitePlanning:EarthOrientationPath"] = earthOrientationOverride?.Path ?? Path.Combine(packageRoot, "earth-orientation"),
            ["CameraAgent:SatellitePlanning:EarthOrientationSha256"] = earthOrientationOverride?.Sha256 ?? input.Time.EarthOrientationSha256,
            ["CameraAgent:SatellitePlanning:LeapSecondsPath"] = leapOverride?.Path ?? Path.Combine(packageRoot, "leaps"),
            ["CameraAgent:SatellitePlanning:LeapSecondsSha256"] = leapOverride?.Sha256 ?? input.Time.LeapSecondsSha256
        }).Build();
        services.AddCameraAgentInfrastructure(configuration);
        services.AddCameraAgentSatellitePlanning(configuration);
        services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
        services.AddSingleton<IDeploymentLocationProtector>(new OrbitalPassThroughLocationProtector());
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<CaptureTiming>> CaptureSequenceAsync(ServiceProvider provider, CameraModuleConfig config, ExposureTestTimeProvider clock,
        CancellationToken cancellationToken)
    {
        await InitializeCaptureServicesAsync(provider, config, cancellationToken).ConfigureAwait(false);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(config, cancellationToken).ConfigureAwait(false);
        var module = provider.GetRequiredService<ICameraModuleFactory>().Create(config.ModuleType);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
        var host = new CaptureHostContext(config, ingress, provider.GetRequiredService<ICaptureDistributor>(), null,
            provider.GetRequiredService<IProjectedSceneStagingStore>(), provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
            provider.GetRequiredService<CaptureProjectedSceneStager>());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bounded = new SixCaptureHost(host, stop);
        var runner = new CameraModuleRunner(new DelayedFirstShutter(module, clock), bounded, clock, NullLogger.Instance);
        await runner.RunAsync(stop.Token).WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.HasCount(6, bounded.Timings);
        return bounded.Timings;
    }

    private static async Task InitializeCaptureServicesAsync(
        ServiceProvider provider, CameraModuleConfig config, CancellationToken cancellationToken)
    {
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
        var site = config.DeploymentLocation!;
        await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(new(site.LocationId, site.Source,
            site.HorizontalAccuracyMeters, site.EffectiveFromUtc, site.EffectiveUntilUtc, config.Observatory),
            cancellationToken).ConfigureAwait(false);
        await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallSnapshotAsync(ServiceProvider provider, OrbitalSnapshot snapshot, CancellationToken cancellationToken)
    {
        var orbital = provider.GetRequiredService<IOrbitalElementsStore>();
        await orbital.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var attempt = await orbital.TryBeginAttemptAsync("named-pass-fixture", OrbitalAttemptTrigger.Manual, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, attempt.Status);
        await orbital.PublishAndActivateAsync(attempt.AttemptId!, snapshot, snapshot.SerializePayload(), cancellationToken).ConfigureAwait(false);
    }

    private sealed class SixCaptureHost(ICaptureHostContext inner, CancellationTokenSource stop) : ICaptureHostContext
    {
        public CameraModuleConfig Configuration => inner.Configuration;
        public List<CaptureTiming> Timings { get; } = [];

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(submission, cancellationToken).ConfigureAwait(false);
            var extra = submission.Result.Frame!.Metadata.Extra!;
            Timings.Add(new(submission.Request.RequestedStartUtc, extra["stellarRenderMilliseconds"],
                extra["stellarRenderProcessCpuMilliseconds"], extra["stellarProjectionMilliseconds"]));
            if (Timings.Count == 6) await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed record CaptureTiming(DateTimeOffset StartUtc, string RenderMilliseconds, string RenderCpuMilliseconds, string ProjectionMilliseconds);

    private sealed class DelayedFirstShutter(ICameraModule module, ExposureTestTimeProvider clock) : ICameraModule
    {
        private bool _first = true;
        public string Id => module.Id;
        public string DisplayName => module.DisplayName;
        public string ModuleType => module.ModuleType;
        public CameraModuleCapabilities Capabilities => module.Capabilities;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => module.InitializeAsync(config, cancellationToken);
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                clock.Advance(ShutterDelay);
            }
            return module.CaptureAsync(request, cancellationToken);
        }
        public ValueTask DisposeAsync() => module.DisposeAsync();
    }

    private static async Task<PreparedInput> PreparePackageAsync(
        string root, string fixture, CameraModuleConfig config, CancellationToken ct, bool competingRecord = false,
        DateTimeOffset? selectionBoundary = null)
    {
        var packageRoot = Path.Combine(root, "private-inputs");
        Directory.CreateDirectory(packageRoot);
        var original = Path.Combine(fixture, "virtual-camera", "snapshots", "two-starlinks");
        var source = OrbitalSnapshot.Parse(await File.ReadAllBytesAsync(Path.Combine(original, "manifest.json"), ct).ConfigureAwait(false),
            await File.ReadAllBytesAsync(Path.Combine(original, "records.json"), ct).ConfigureAwait(false)).Snapshot!;
        var carrier = source.SerializePayload();
        // The store verifies original raw bytes. This self-contained fixture deliberately uses a new source identity.
        var snapshot = OrbitalSnapshot.Create(source.Records, [source.Manifest.Sources.Single() with
        {
            RawLengthBytes = carrier.Length, RawSha256 = Hash(carrier),
            TermsReference = "Normalized-record fixture carrier from the retained #529 Starlinks. No provider download."
        }], source.Manifest.Coverage);
        var combined = snapshot;
        if (competingRecord || selectionBoundary is not null)
        {
            var selected = snapshot.RecordsFor(new("norad", CatalogNumbers[0])).Single();
            var preferred = OrbitalContract.CreateRecord(selected with
            {
                EpochUtc = selected.EpochUtc.AddHours(1),
                ValidFromUtc = selectionBoundary
            });
            var withCompeting = OrbitalSnapshot.Create([.. snapshot.Records, preferred], snapshot.Manifest.Sources, snapshot.Manifest.Coverage);
            var combinedCarrier = withCompeting.SerializePayload();
            combined = OrbitalSnapshot.Create(withCompeting.Records, [snapshot.Manifest.Sources.Single() with
            {
                RawLengthBytes = combinedCarrier.Length,
                RawSha256 = Hash(combinedCarrier)
            }], snapshot.Manifest.Coverage);
        }
        var manifest = Put("snapshot.json", combined.SerializeManifest());
        var records = Put("records.json", combined.SerializePayload());
        var timeRoot = Path.Combine(fixture, "time");
        var eop = await File.ReadAllBytesAsync(Path.Combine(timeRoot, "predicted-2026.finals2000A"), ct).ConfigureAwait(false);
        var leaps = await File.ReadAllBytesAsync(Path.Combine(timeRoot, "leap-seconds.list"), ct).ConfigureAwait(false);
        var time = OrbitalTimeData.FromFinals2000A(eop, leaps);
        var model = OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true);
        var rig = config.Rig;
        var projection = RigProjectionContextFactory.Create(rig);
        var bayer = rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16;
        var readout = new FrameReadoutDescriptor(640, 640, 0, 0, 640, 640, 1, 1, FrameBinningAlgorithm.IdentityV1,
            bayer ? 0 : null, bayer ? 0 : null);
        var geometry = new OrbitalGeometryFacts(OrbitalGeometryBasis.ConfiguredRig, RigProjectionContextFactory.CreateProfileHashSha256(rig),
            CaptureContractJson.ComputeCanonicalJsonSha256(readout), readout, CaptureContractJson.ComputeCanonicalJsonSha256(rig.Optics),
            null, null, OrbitalProjectionCoordinates.ReadoutView, projection, ProjectedSceneImageTransformV1.Identity(640, 640), HorizonPolicy.GeometricHorizon);
        var observer = VirtualSkyNamedPass.ToObserver(config.Observatory);
        var policy = new OrbitalSelectionPolicy([new("celestrak:gp:active", 0)]);
        var engine = new OrbitalPredictionEngine(time);
        var tracks = new Dictionary<(int, string), OrbitalExposureTrackV1>();
        var groups = new List<object>();
        foreach (var number in CatalogNumbers)
        {
            var identity = new OrbitalObjectIdentity("norad", number);
            var trackEntries = new List<object>();
            for (var index = 1; index < 6; index++)
            {
                if (selectionBoundary is not null && index != 3)
                    continue;
                var interval = new OrbitalUtcInterval(First + index * Exposure, First + (index + 1) * Exposure);
                var timing = new OrbitalTimingFacts(interval, OrbitalTimingBasis.Estimated, null, OrbitalTimingBasis.Unknown,
                    "named-pass-virtual-shutter", AstrometricClockFacts.NotSupplied);
                var result = await engine.PredictExposureAsync(new(
                    new(OrbitalPredictionUse.Diagnostic, snapshot, policy, interval, [identity], observer, model,
                        new(1, 20, 10_000, 1, 100, 1000, 1000, 1, .01)), geometry, timing), ct).ConfigureAwait(false);
                var prediction = Assert.ContainsSingle(result.Products);
                if (combined != snapshot)
                {
                    prediction = OrbitalPredictionJson.CreateTrack(prediction with
                    {
                        Lineage = prediction.Lineage with
                        {
                            SnapshotManifestSha256 = combined.Manifest.ManifestSha256,
                            NormalizedPayloadSha256 = combined.Manifest.NormalizedPayloadSha256
                        }
                    });
                }
                if (number == "56838" && index == 3)
                {
                    // Compatible projection/time from retained virtual-camera/tracks/starlink-56838/track-03.json.
                    Assert.AreEqual(291.5576252182425, prediction.Samples[0].Pixel!.Value.X, 1e-6);
                    Assert.AreEqual(296.6289829988315, prediction.Samples[^1].Pixel!.Value.Y, 1e-6);
                    Assert.AreEqual(494.89476821308625, prediction.Samples[0].RangeKilometers!.Value, 1e-6);
                    Assert.AreEqual(498.10855185325573, prediction.Samples[^1].RangeKilometers!.Value, 1e-6);
                }
                tracks.Add((index, number), prediction);
                var reference = Put($"track-{number}-{index}.json", OrbitalPredictionJson.SerializeTrack(prediction));
                trackEntries.Add(new { track = reference, exposureStartUtc = interval.StartUtc, transmission = number == CatalogNumbers[0] && index < 5 ? 1 : 0 });
            }
            var optical = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "virtual-camera", "optical-5s.json"), ct).ConfigureAwait(false))!.AsObject();
            optical["angularWidthDegrees"] = .09;
            optical["redWeight"] = 1.8;
            optical["blueWeight"] = .35;
            groups.Add(new
            {
                catalogNumber = number,
                recordSha256 = snapshot.RecordsFor(identity).Single().RecordSha256,
                optics = Put($"optics-{number}.json", JsonSerializer.SerializeToUtf8Bytes(optical, Json)),
                tracks = trackEntries
            });
        }
        var package = Put("package.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "virtual-sky-named-pass-v1",
            epochUtc = First,
            snapshotManifest = manifest,
            snapshotRecords = records,
            earthOrientation = Put("earth-orientation", eop),
            leapSeconds = Put("leaps", leaps),
            objects = groups
        }, Json));
        return new(Path.Combine(packageRoot, package.Path), package.Sha256, time, combined, tracks);

        FilePin Put(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(packageRoot, name), bytes);
            return new(name, Hash(bytes));
        }
    }

    private static PixelPoint[] TrackPixels(OrbitalExposureTrackV1 track)
        => track.Samples.Where(static sample => sample.Pixel.HasValue).Select(static sample => sample.Pixel!.Value).ToArray();

    private static bool Matches(CameraAgentCaptureTrail trail, IEnumerable<PixelPoint> expected)
    {
        var transform = trail.DetectorInput.SourceToDetectorTransform;
        var path = expected.Select(point => new TransientPointV1(point.X * transform.ScaleX + transform.OffsetX,
            point.Y * transform.ScaleY + transform.OffsetY)).ToArray();
        var first = trail.Geometry.Polyline[0];
        var last = trail.Geometry.Polyline[^1];
        var expectedX = path[^1].X - path[0].X;
        var expectedY = path[^1].Y - path[0].Y;
        var measuredX = last.X - first.X;
        var measuredY = last.Y - first.Y;
        var expectedLength = double.Hypot(expectedX, expectedY);
        var projectedExtent = Math.Abs(measuredX * expectedX + measuredY * expectedY) / expectedLength;
        return projectedExtent >= expectedLength * .25 &&
            projectedExtent >= .85 * double.Hypot(measuredX, measuredY) && NearPath(first) && NearPath(last);

        bool NearPath(TransientPointV1 endpoint)
            => path.Any(point => double.Hypot(point.X - endpoint.X, point.Y - endpoint.Y) <= 4 * transform.ScaleX);
    }

    private static CameraAgentCaptureTrail PerpendicularControl(CameraAgentCaptureTrail trail, PixelPoint[] path)
    {
        var transform = trail.DetectorInput.SourceToDetectorTransform;
        var chordX = path[^1].X - path[0].X;
        var chordY = path[^1].Y - path[0].Y;
        var length = double.Hypot(chordX, chordY);
        var x = -3 * chordY / length;
        var y = 3 * chordX / length;
        var center = path[path.Length / 2];
        TransientPointV1 Endpoint(double sign) => new((center.X + sign * x) * transform.ScaleX + transform.OffsetX,
            (center.Y + sign * y) * transform.ScaleY + transform.OffsetY);
        return trail with { Geometry = trail.Geometry with { Polyline = [Endpoint(-1), Endpoint(1)] } };
    }

    // Independently select detector photosites near the expected path using pixel-edge coordinates.
    private static CorridorCodes SampleCorridor(byte[] raw, OrbitalExposureTrackV1 track, bool bayer)
    {
        const int width = 640;
        const int height = 640;
        var pixels = new HashSet<(int X, int Y)>();
        foreach (var sample in track.Samples)
        {
            if (sample.Pixel is not { } point) continue;
            for (var y = Math.Max(0, (int)point.Y - 1); y <= Math.Min(height - 1, (int)point.Y + 1); y++)
                for (var x = Math.Max(0, (int)point.X - 1); x <= Math.Min(width - 1, (int)point.X + 1); x++)
                    if (double.Hypot(x + .5 - point.X, y + .5 - point.Y) <= 1.3) pixels.Add((x, y));
        }
        var redOrMono = new List<int>();
        var blue = new List<int>();
        foreach (var (x, y) in pixels)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((y * width + x) * 2, 2)) - 64;
            if (!bayer || ((x | y) & 1) == 0) redOrMono.Add(value);
            else if (((x & y) & 1) != 0) blue.Add(value);
        }
        redOrMono.Sort();
        blue.Sort();
        return new(redOrMono.Count, redOrMono[redOrMono.Count / 2], blue.Count, bayer ? blue[blue.Count / 2] : null);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Task WriteAsync<T>(string path, T value, CancellationToken ct)
        => File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

    private sealed record FilePin(string Path, string Sha256);
    private sealed record CorridorCodes(int RedOrMonoPhotosites, int RedOrMonoMedian, int BluePhotosites, int? BlueMedian);
    private sealed record PreparedInput(string PackagePath, string PackageSha256, OrbitalTimeData Time, OrbitalSnapshot Snapshot,
        IReadOnlyDictionary<(int Slot, string Number), OrbitalExposureTrackV1> Tracks);
}
