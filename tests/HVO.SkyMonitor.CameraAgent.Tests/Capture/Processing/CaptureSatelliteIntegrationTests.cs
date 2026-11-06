using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class CaptureSatelliteIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StandaloneCapturePublishesRestartReadableIndependentlyExportedSatelliteProducts()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            RawCaptureReceipt receipt;
            CameraModuleConfig config;
            Dictionary<string, string> checksums;
            using (var provider = CreateProvider(root, f, clock))
            {
                config = Configuration(root, f);
                receipt = await CaptureAsync(provider, config, f).ConfigureAwait(false);
                var output = await ProcessAsync(provider, config).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, output.Outcome, output.Reason);
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var node = await store.ReadNodeAsync(receipt.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.HasCount(3, node.Outputs);
                var layerNode = await store.ReadNodeAsync(receipt.Manifest.Descriptor.Capture.CaptureId, "satellite-layer", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(layerNode);
                Assert.HasCount(2, layerNode.Outputs);
                checksums = node.Outputs.ToDictionary(static p => p.Artifact.Variant, static p => p.Artifact.ChecksumSha256);
                var evidence = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Path.Combine(root,
                    node.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema).PayloadRelativePath)).ConfigureAwait(false));
                var prediction = node.Outputs.Single(p => p.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion);
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, prediction.PayloadRelativePath)).ConfigureAwait(false);
                var independentRaw = f.Raw with
                {
                    ArtifactId = receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    CaptureId = receipt.Manifest.Descriptor.Capture.CaptureId,
                    DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(receipt.Manifest.Descriptor),
                    RecipeIdentitySha256 = ProcessingIdentity.CreateRecipeIdentity(receipt.Manifest.Descriptor.Artifact.Recipe).IdentitySha256,
                    Variant = receipt.Manifest.Descriptor.Artifact.Variant,
                    MediaType = receipt.Manifest.Descriptor.Artifact.MediaType,
                    CreatedUtc = receipt.Manifest.Descriptor.Artifact.CreatedUtc,
                    Layout = receipt.Manifest.Descriptor.Layout,
                    ObservationStartedUtc = receipt.Manifest.Descriptor.Timing.ExposureStartedUtc,
                    ObservationEndedUtc = receipt.Manifest.Descriptor.Timing.ExposureEndedUtc
                };
                var reconstructed = await CaptureSatelliteProducts.PredictAsync(evidence, independentRaw,
                    "capture-satellites-v1", CancellationToken.None, exactSnapshot: await provider.GetRequiredService<IOrbitalSnapshotReader>()
                        .ReadExactAsync(evidence.InputManifest(), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsNotNull(reconstructed);
                CollectionAssert.AreEqual(bytes, reconstructed.Payload.ToArray());
                Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(f.Raw.Payload), receipt.Manifest.Descriptor.Artifact.ChecksumSha256);
                Assert.IsTrue(await provider.GetRequiredService<IOrbitalElementsStore>().AddReferenceAsync(
                    evidence.InputManifest(), "test-observer", CancellationToken.None).ConfigureAwait(false));
                await provider.GetRequiredService<IOrbitalElementsStore>().RemoveReferenceAsync(evidence.InputManifest(),
                    "test-observer", CancellationToken.None).ConfigureAwait(false);
            }
            using (var restarted = CreateProvider(root, f, clock, unavailable: "throw"))
            {
                await restarted.GetRequiredService<RawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var node = await restarted.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                    receipt.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                foreach (var product in node.Outputs)
                    Assert.AreEqual(checksums[product.Artifact.Variant], ProcessingIdentity.ComputePayloadSha256(
                        await File.ReadAllBytesAsync(Path.Combine(root, product.PayloadRelativePath)).ConfigureAwait(false)));
                var presentation = await restarted.GetRequiredService<ICameraAgentLayeredPresentationService>()
                    .GetAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, presentation.Status, presentation.Reason);
                Assert.IsNotNull(presentation.Presentation);
                var layer = Assert.ContainsSingle(presentation.Presentation.Layers);
                Assert.AreEqual(OrbitalTrackPresentation.LayerKind, layer.Kind);
                Assert.IsFalse(layer.EnabledByDefault);
                Assert.IsTrue(layer.Satellite!.Renderable);
                await AssertAuthenticatedReadAsync(restarted, presentation.Presentation).ConfigureAwait(false);
                StringAssert.Contains(Encoding.UTF8.GetString(presentation.Presentation.Svg.Span), "hvo-layer-0", StringComparison.Ordinal);
                var materializer = restarted.GetRequiredService<ICameraAgentPresentationMaterializer>();
                var without = await materializer.SaveFrozenAsync(presentation.Presentation.CaptureId,
                    presentation.Presentation.ManifestIdentitySha256, [], "test-owner", CancellationToken.None).ConfigureAwait(false);
                var with = await materializer.SaveFrozenAsync(presentation.Presentation.CaptureId,
                    presentation.Presentation.ManifestIdentitySha256, [layer.IdentitySha256], "test-owner", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, without.Status, without.Reason);
                Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, with.Status, with.Reason);
                Assert.AreNotEqual(without.Receipt!.ChecksumSha256, with.Receipt!.ChecksumSha256);
                var exportedPreview = await restarted.GetRequiredService<ICameraAgentArtifactService>()
                    .GetPreviewAsync(with.Receipt.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, exportedPreview.Status);
                Assert.AreEqual(640, exportedPreview.Width);
                Assert.AreEqual((byte)0xFF, exportedPreview.Content.Span[0]);
                Assert.AreEqual((byte)0xD8, exportedPreview.Content.Span[1]);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("snapshot", "NoActiveSnapshot")]
    [DataRow("time", "TimeInputsUnavailable:Disabled")]
    [DataRow("expired", "TimeCoverageUnavailable")]
    public async Task MissingAndExpiredInputsPublishTruthfulDisabledRetainedStates(string unavailable, string expected)
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, f, new Clock(f.Descriptor.Timing.DurableIngressUtc), unavailable);
            var config = Configuration(root, f);
            var receipt = await CaptureAsync(provider, config, f).ConfigureAwait(false);
            var result = await ProcessAsync(provider, config).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var view = await provider.GetRequiredService<ICameraAgentLayeredPresentationService>()
                .GetAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, view.Status, view.Reason);
            var status = Assert.ContainsSingle(view.Presentation!.Layers).Satellite!;
            Assert.AreEqual("Unavailable", status.Status);
            Assert.AreEqual(expected, status.Reason);
            Assert.IsFalse(status.Renderable);
            Assert.AreEqual(0, status.TrackCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("outputs")]
    [DataRow("input")]
    public async Task FailedPublicationRestartReusesTheOriginalInputsAndFirstResult(string boundary)
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            CameraModuleConfig config;
            byte[]? frozen = null;
            byte[] frozenInput;
            using (var first = CreateProvider(root, f, clock, fault: boundary == "outputs", inputFault: boundary == "input"))
            {
                config = Configuration(root, f);
                await CaptureAsync(first, config, f).ConfigureAwait(false);
                if (boundary == "outputs")
                {
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessAsync(first, config)).ConfigureAwait(false);
                    frozen = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(root, "journal", "capture-satellites"),
                        "result.json", SearchOption.AllDirectories).Single()).ConfigureAwait(false);
                }
                else Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure,
                    (await ProcessAsync(first, config).ConfigureAwait(false)).Outcome);
                frozenInput = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(root, "journal", "capture-satellites"),
                    "input.json", SearchOption.AllDirectories).Single()).ConfigureAwait(false);
            }
            clock.Now = clock.Now.AddSeconds(130);
            using (var second = CreateProvider(root, f, clock, unavailable: "throw"))
            {
                await second.GetRequiredService<RawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var resumed = await ProcessAsync(second, config).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, resumed.Outcome, resumed.Reason);
                var node = await second.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                    RawCaptureDescriptorFactory.CreateStableIds(config, Submission(f)).CaptureId,
                    "satellite", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                if (frozen is not null)
                    CollectionAssert.AreEqual(frozen, await File.ReadAllBytesAsync(Path.Combine(root,
                        node.Outputs.Single(p => p.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion).PayloadRelativePath)).ConfigureAwait(false));
                CollectionAssert.AreEqual(frozenInput, await File.ReadAllBytesAsync(Path.Combine(root,
                    node.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema).PayloadRelativePath)).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private string CreateRoot()
    {
        var path = Path.Combine(TestContext.TestRunDirectory!, "capture-satellites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static ServiceProvider CreateProvider(string root, CaptureSatelliteFixture f, Clock clock,
        string? unavailable = null, bool fault = false, bool inputFault = false)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
        }).Build());
        services.AddSingleton<ICameraAgentOrbitalTimeSource>(new TimeSource(f, unavailable));
        services.AddSingleton<HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.IDeploymentLocationProtector>(
            new HVO.SkyMonitor.CameraAgent.Tests.Orbital.OrbitalPassThroughLocationProtector());
        if (unavailable is "snapshot" or "throw") services.AddSingleton<IOrbitalSnapshotReader>(p =>
            new MissingReader(unavailable == "throw", p.GetRequiredService<OrbitalSnapshotReader>()));
        services.AddSingleton(new CaptureProcessingStepRegistration("SatelliteFixtureManifest", typeof(ManifestStep), typeof(ManifestOptions), 90, false));
        if (fault) services.AddSingleton<ICaptureProcessingFaultInjector>(new PublicationFault());
        var provider = services.BuildServiceProvider();
        if (inputFault) provider.GetRequiredService<CaptureSatelliteWorkStore>().BeforeHeaderPublicationForTest =
            () => throw new IOException("Injected interruption before satellite intent publication.");
        return provider;
    }

    internal static CameraModuleConfig Configuration(string root, CaptureSatelliteFixture f, OrbitalQueryBounds? bounds = null) => f.Configuration with
    {
        Pipeline = new([
            new(CaptureSatelliteTracksProcessingStep.Alias, "satellite", DependsOn: ["$raw"],
                Options: JsonSerializer.SerializeToElement(new CaptureSatelliteTracksOptions
                { CatalogNumbers = ["25544"], Bounds = bounds ?? f.Input.Plan.Bounds, OverrideTimeDataAvailableUtc = f.Input.Plan.Availability.TimeDataAvailableUtc }),
                Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
            new("Preview", "preview", DependsOn: ["$raw"], Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
            new(CaptureSatelliteLayerProcessingStep.Alias, "satellite-layer", DependsOn: ["satellite"],
                Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
            new("SatelliteFixtureManifest", "manifest", DependsOn: ["preview", "satellite", "satellite-layer"], Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
            new("Storage", "storage", DependsOn: ["$raw", "preview", "satellite", "satellite-layer", "manifest"],
                Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                { StorageRoot = root, QueueForUpload = false, UpdateLatestFrame = false }))
        ])
    };

    internal static async Task<RawCaptureReceipt> CaptureAsync(ServiceProvider provider, CameraModuleConfig config, CaptureSatelliteFixture f,
        int sequenceOffset = 0)
    {
        var site = f.Configuration.DeploymentLocation!;
        await provider.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.IDeploymentLocationStore>()
            .InitializeAsync(new(site.LocationId, site.Source, site.HorizontalAccuracyMeters, site.EffectiveFromUtc,
                site.EffectiveUntilUtc, f.Configuration.Observatory), CancellationToken.None).ConfigureAwait(false);
        var configuration = provider.GetRequiredService<ICameraAgentConfigurationAccessor>();
        if (!configuration.IsConfigured) configuration.SetConfiguration(config);
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var orbital = provider.GetRequiredService<IOrbitalElementsStore>();
        await orbital.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        if ((await orbital.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).CurrentManifestSha256 is null)
        {
            var raw = f.Snapshot.SerializePayload();
            var source = f.Snapshot.Manifest.Sources.Single() with
            {
                RawSha256 = ProcessingIdentity.ComputePayloadSha256(raw),
                RawLengthBytes = raw.Length,
                TermsReference = "Synthetic normalized-fact carrier for capture integration tests; derived from retained #529 fixtures, no provider fetch."
            };
            var snapshot = OrbitalSnapshot.Create(f.Snapshot.Records, [source], f.Snapshot.Manifest.Coverage);
            var attempt = await orbital.TryBeginAttemptAsync("capture-fixture", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.Admitted, attempt.Status);
            await orbital.PublishAndActivateAsync(attempt.AttemptId!, snapshot, raw, CancellationToken.None).ConfigureAwait(false);
        }
        return await ingress.AcceptAsync(config, Submission(f, sequenceOffset), CancellationToken.None).ConfigureAwait(false)
            ?? throw new AssertFailedException("Raw capture admission failed.");
    }

    internal static CaptureLoopSubmission Submission(CaptureSatelliteFixture f, int sequenceOffset = 0)
    {
        var d = f.Descriptor;
        var start = d.Timing.ExposureStartedUtc.AddSeconds(sequenceOffset);
        var end = d.Timing.ExposureEndedUtc.AddSeconds(sequenceOffset);
        var frame = new CameraFrame(start, 640, 640, CameraPixelFormat.BayerRggb16, f.Raw.Payload,
            new(TimeSpan.FromSeconds(5), 1, 0, "StandaloneFixture"), 1280)
        { Layout = d.Layout };
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(5), 1, null, null);
        return new(new(start, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint),
            new(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            { AcquisitionTiming = new(start, end, d.Timing.ReadoutCompletedUtc.AddSeconds(sequenceOffset)) },
            start, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal static async Task<CaptureLaneHandlerResult> ProcessAsync(ServiceProvider provider, CameraModuleConfig config)
    {
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static l => l.Name == "standard");
        var lease = await ingress.ClaimAsync(lane, "satellite-integration", config, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome == CaptureLaneHandlerOutcome.Completed) await ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        else await ingress.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TimeSource(CaptureSatelliteFixture f, string? unavailable) : ICameraAgentOrbitalTimeSource
    {
        public ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken)
            => ResolveAsync(new(f.Descriptor.Timing.ExposureStartedUtc, f.Descriptor.Timing.ExposureEndedUtc), cancellationToken);
        public ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(OrbitalUtcInterval window, CancellationToken cancellationToken)
        {
            if (unavailable == "throw") throw new AssertFailedException("Restart attempted current time resolution.");
            return ValueTask.FromResult(new CameraAgentOrbitalTimeResolution(CameraAgentOrbitalTimeDataSource.Override,
                unavailable == "time" ? CameraAgentOrbitalTimeDataProblem.Disabled : CameraAgentOrbitalTimeDataProblem.None,
                unavailable == "time" ? null : unavailable == "expired" ? OrbitalTimeData.CreateNominalZeroDiagnostic(f.Time.ExportLeapSeconds()) : f.Time));
        }
    }
    private sealed class MissingReader(bool fail, IOrbitalSnapshotReader exactReader) : IOrbitalSnapshotReader
    {
        public ValueTask<OrbitalSnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken)
            => fail ? throw new AssertFailedException("Restart attempted current snapshot resolution.") :
                ValueTask.FromResult(OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot));
        public ValueTask<OrbitalSnapshotReadResult> ReadExactAsync(string snapshotSha256, CancellationToken cancellationToken)
            => exactReader.ReadExactAsync(snapshotSha256, cancellationToken);
    }
    private sealed class PublicationFault : ICaptureProcessingFaultInjector
    {
        private bool fired;
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!fired && point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit && nodeId == "satellite")
            { fired = true; throw new InvalidOperationException("Injected process interruption after satellite publication."); }
        }
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the configured graph factory.")]
    internal sealed class ManifestOptions { }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the configured graph factory.")]
    internal sealed class ManifestStep(CaptureProcessingStepMetadata metadata, ManifestOptions options)
        : ConfigurableCaptureProcessingStep<ManifestOptions>(metadata, options), ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
    {
        public bool Enabled => true;
        public string RecipeName => PresentationProcessingProducts.ManifestRecipeName;
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "satellite-test-manifest";
        public string? OutputSchemaVersion => OverlayManifestV1.CurrentSchemaVersion;
        public string? OutputMediaType => PresentationProcessingProducts.ManifestMediaType;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata, FrameArtifactRole.Preview };
        public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
        [
            new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview }),
            new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
                new HashSet<string> { PresentationProcessingProducts.LayerRecipeName }, Variant: "capture-satellites-v1-layer", Required: false),
            new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
                new HashSet<string> { "capture-satellite-status" }, Variant: "capture-satellites-v1-status", Required: false)
        ];
        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var products = context.GetDependencyProducts();
            var image = products.Single(static p => p.Role == FrameArtifactRole.Preview);
            var imageArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, image);
            var payload = products.SingleOrDefault(static p => p.Variant == "capture-satellites-v1-layer");
            var status = products.SingleOrDefault(static p => p.SchemaVersion == CaptureSatelliteProducts.StatusSchema);
            var coordinateIdentity = new string('A', 64);
            var reference = PresentationProcessingProducts.CreateReference(imageArtifact, coordinateIdentity);
            if (payload is null || status is null)
            {
                LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateManifestProduct(
                    reference, coordinateIdentity, [], OutputVariant, imageArtifact));
                return ValueTask.CompletedTask;
            }
            var layer = LayeredPresentationCaptureProcessing.Layer(payload, OrbitalTrackPresentation.LayerKind,
                coordinateIdentity, 45, PresentationBlendMode.Normal, 1_000_000, false, context, reference.Compatibility);
            using var document = JsonDocument.Parse(status.Payload);
            layer = layer with
            {
                Layer = LayeredPresentationJson.CreateLayer(layer.Layer.LayerKind, layer.Layer.SourceProduct,
                coordinateIdentity, PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
                OrbitalTrackPresentation.ProducerVersion, 45, PresentationBlendMode.Normal, 1_000_000, false,
                JsonSerializer.SerializeToElement(new { Satellite = document.RootElement }))
            };
            LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateManifestProduct(
                reference, coordinateIdentity, [layer], OutputVariant, imageArtifact));
            return ValueTask.CompletedTask;
        }
    }
}

internal static class CaptureSatelliteTestExtensions
{
    internal static string InputManifest(this CaptureSatelliteInputV1 input) => CaptureSatelliteProducts.ReadSnapshotManifest(input)!.ManifestSha256;
}
