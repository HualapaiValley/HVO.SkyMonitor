using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualSkyCloudScenarioTests
{
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions FixtureSerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ProcessingPipeline_RequiresExplicitCloudObservationStep()
    {
        var publisher = new RecordingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton<IEnvironmentalObservationPublisher>(publisher);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var config = Config(CameraPixelFormat.Mono16, Definition());

        var legacy = factory.CreateGraph(config);
        Assert.IsFalse(legacy.Nodes.Any(static node => node.Id == "CloudObservation"));

        var configured = factory.CreateGraph(config with
        {
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "VirtualSkyCloudObservation",
                    "CloudObservation",
                    10,
                    Required: true)
            ]
        });
        var node = configured.Nodes.Single();
        Assert.AreEqual("CloudObservation", node.Id);
        Assert.IsInstanceOfType<VirtualSkyCloudObservationProcessingStep>(node.Step);
        Assert.IsTrue(node.Required);
        legacy.DisposeSteps();
        configured.DisposeSteps();
    }

    [TestMethod]
    public async Task FixtureScenarios_HaveDocumentedCoverageAndStableRawChecksums()
    {
        var manifest = JsonSerializer.Deserialize<CloudFixtureManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cloud-scenarios-v1.json"))
                .ConfigureAwait(false),
            FixtureSerializerOptions)!;
        Assert.AreEqual("virtual-cloud-fixture-v1", manifest.SchemaVersion);
        var results = new List<(CloudFixtureCase Scenario, string Checksum, ushort Minimum, ushort Maximum, double Mean)>();
        foreach (var scenario in manifest.Cases)
        {
            var field = new VirtualCloudField(scenario.Definition);
            var coverage = field.ComputeSkyCoverage(manifest.Utc, TimeSpan.FromSeconds(manifest.ExposureSeconds));
            Assert.IsFalse(scenario.Definition.ScenarioId.Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(JsonSerializer.Serialize(scenario.Definition).Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(scenario.ExpectedCoverage, coverage, 1e-15, scenario.Id);
            Assert.IsTrue(coverage >= scenario.MinimumCoverage && coverage <= scenario.MaximumCoverage,
                $"{scenario.Id} coverage {coverage:R} was outside [{scenario.MinimumCoverage:R}, {scenario.MaximumCoverage:R}].");
            var module = Module();
            var config = Config(CameraPixelFormat.Mono16, scenario.Definition);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(
                new CaptureRequest(
                    manifest.Utc,
                    TimeSpan.FromSeconds(5),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                CancellationToken.None).ConfigureAwait(false);
            var checksum = Convert.ToHexString(SHA256.HashData(capture.Frame!.PixelData.Span));
            var rawStatistics = ComputeRawStatistics(capture.Frame.PixelData.Span);
            Assert.AreEqual(
                scenario.Definition.ComputeCanonicalScenarioId(),
                capture.Frame.Metadata.Scene!.CloudScenario!.ScenarioId);
            TestContext.WriteLine(
                $"{scenario.Id}: coverage={coverage:R}, min={rawStatistics.Minimum}, max={rawStatistics.Maximum}, " +
                $"mean={rawStatistics.Mean:R}, sha256={checksum}");
            results.Add((scenario, checksum, rawStatistics.Minimum, rawStatistics.Maximum, rawStatistics.Mean));
        }
        foreach (var result in results)
        {
            Assert.AreEqual(result.Scenario.ExpectedMono16Sha256, result.Checksum, result.Scenario.Id);
            Assert.AreEqual(result.Scenario.ExpectedRawMinimum, result.Minimum, result.Scenario.Id);
            Assert.AreEqual(result.Scenario.ExpectedRawMaximum, result.Maximum, result.Scenario.Id);
            Assert.AreEqual(result.Scenario.ExpectedRawMean, result.Mean, 1e-12, result.Scenario.Id);
        }
    }

    [TestMethod]
    public async Task CaptureAsync_CloudScenarioIsDeterministicMovesAndRetainsOpaqueProvenance()
    {
        var definition = Definition();
        var config = Config(CameraPixelFormat.Mono16, definition);
        var firstModule = Module();
        var repeatedModule = Module();
        await firstModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(4), 1, null, null);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint);

        var first = await firstModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeated = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var moved = await repeatedModule.CaptureAsync(
            request with { RequestedStartUtc = FixtureUtc.AddSeconds(30) }, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(first.Frame!.PixelData.ToArray(), repeated.Frame!.PixelData.ToArray());
        CollectionAssert.AreNotEqual(first.Frame.PixelData.ToArray(), moved.Frame!.PixelData.ToArray());
        var checksum = Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span));
        TestContext.WriteLine($"Cloud Mono16 SHA-256: {checksum}");
        Assert.AreEqual("FE46FC9C3BF0CA72CAA63BA405C2FDAB8E7E388A466995E73F78ABE099A946A1", checksum);
        var provenance = first.Frame.Metadata.Scene!.CloudScenario!;
        Assert.AreEqual(definition.ComputeCanonicalScenarioId(), provenance.ScenarioId);
        var canonicalDefinition = definition with { ScenarioId = definition.ComputeCanonicalScenarioId() };
        Assert.AreEqual(canonicalDefinition.ComputeParametersSha256(), provenance.ParametersSha256);
        Assert.AreEqual(FixtureUtc, provenance.IntegrationStartUtc);
        Assert.AreEqual(FixtureUtc.AddSeconds(4), provenance.IntegrationEndUtc);
        Assert.AreEqual(definition.TemporalSampleCount, provenance.TemporalSampleCount);
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("clear", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("overcast", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(provenance.ParametersSha256, first.Frame.Metadata.Extra!["cloudParametersSha256"]);
        StringAssert.EndsWith(
            first.Frame.Metadata.Extra["renderAlgorithm"],
            Mono16SceneRenderer.CloudAlgorithmSuffix,
            StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CaptureAsync_CloudScenarioRunsThroughEverySupportedRawFormat(CameraPixelFormat format)
    {
        var config = Config(format, Definition());
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(
                FixtureUtc,
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(2), format == CameraPixelFormat.BayerRggb16 ? 100 : 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(format, result.Frame!.PixelFormat);
        Assert.IsNotNull(result.Frame.Metadata.Scene!.CloudScenario);
        Assert.AreEqual(FixtureUtc, result.AcquisitionTiming!.ExposureStartedUtc);
        Assert.AreEqual(FixtureUtc, result.AcquisitionTiming.ExposureEndedUtc);
        Assert.AreEqual(FixtureUtc.AddSeconds(2), result.Frame.Metadata.Scene.CloudScenario.IntegrationEndUtc);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CaptureAsync_PhysicalSensorCloudFramesAreStableAcrossRestartAndCaptureOrder(CameraPixelFormat format)
    {
        var config = Config(format, Definition(), physicalMono: format == CameraPixelFormat.Mono16);
        var ordered = Module();
        var reversed = Module();
        var restarted = Module();
        await ordered.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await reversed.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await restarted.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(4), 100, null, null);
        var firstRequest = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint);
        var secondRequest = firstRequest with { RequestedStartUtc = FixtureUtc.AddSeconds(30) };

        var orderedFirst = await ordered.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);
        var orderedSecond = await ordered.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedSecond = await reversed.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedFirst = await reversed.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);
        var restartedFirst = await restarted.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(orderedFirst.Frame!.PixelData.ToArray(), reversedFirst.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(orderedFirst.Frame.PixelData.ToArray(), restartedFirst.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(orderedSecond.Frame!.PixelData.ToArray(), reversedSecond.Frame!.PixelData.ToArray());
        CollectionAssert.AreNotEqual(orderedFirst.Frame.PixelData.ToArray(), orderedSecond.Frame.PixelData.ToArray());
        Assert.AreEqual(
            orderedFirst.Frame.Metadata.Extra!["captureSequence"],
            reversedFirst.Frame.Metadata.Extra!["captureSequence"]);
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsUnknownAndInvalidCloudConfiguration()
    {
        using var unknown = JsonDocument.Parse("""
            {
              "cloudScenario": {
                "schemaVersion": "virtual-cloud-scenario-v1",
                "scenarioId": "scenario-104-a",
                "scenarioVersion": "1",
                "seed": 104,
                "epochUtc": "2025-01-15T08:00:00Z",
                "spatialFrequency": 3,
                "octaves": 2,
                "edgeSoftness": 0.1,
                "horizonFadeDegrees": 5,
                "temporalSampleCount": 2,
                "unknown": true,
                "keyframes": [{ "coverage": 0.5, "maximumOpacity": 0.8, "scatterFraction": 0.1 }]
              }
            }
            """);
        var unknownConfig = Config(CameraPixelFormat.Mono16, null) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", unknown.RootElement.Clone())
        };
        var invalidConfig = Config(CameraPixelFormat.Mono16, Definition() with { TemporalSampleCount = 17 });

        await Assert.ThrowsAsync<JsonException>(() => Module().InitializeAsync(unknownConfig, CancellationToken.None))
            .ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Module().InitializeAsync(invalidConfig, CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ObservationStep_ProducesRestartStableTargetlessFactAndNoOpsWithoutCloud()
    {
        var definition = Definition();
        var config = Config(CameraPixelFormat.Mono16, definition);
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(5),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(4), 1, null, null));
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var publisher = new RecordingPublisher();
        var step = new VirtualSkyCloudObservationProcessingStep(
            new CaptureProcessingStepMetadata("CloudObservation", typeof(VirtualSkyCloudObservationProcessingStep).FullName!, 10),
            new VirtualSkyCloudObservationProcessingStepOptions(),
            publisher);
        var context = Context(config, request, result);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var aliasConfig = config with { Module = config.Module with { Type = "virtualsky" } };
        await step.ProcessAsync(Context(aliasConfig, request, result), CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(3, publisher.Facts);
        var first = publisher.Facts[0];
        var repeated = publisher.Facts[1];
        Assert.AreEqual(first.ObservationId, repeated.ObservationId);
        Assert.AreEqual(first.Value.NumericValue, repeated.Value.NumericValue);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, first.Source.Kind);
        Assert.AreEqual(EnvironmentalObservationKind.CloudCover, first.Value.Kind);
        Assert.AreEqual(EnvironmentalObservationUnit.Fraction, first.Value.Unit);
        Assert.IsTrue(first.Value.NumericValue is > 0 and < 1);
        Assert.AreEqual(FixtureUtc, first.ObservedFromUtc);
        Assert.AreEqual(FixtureUtc.AddSeconds(4), first.ObservedThroughUtc);
        var canonicalDefinition = definition with { ScenarioId = definition.ComputeCanonicalScenarioId() };
        Assert.AreEqual(canonicalDefinition.ComputeParametersSha256(), first.Source.Provenance.ParametersSha256);
        Assert.IsTrue(EnvironmentalObservationJson.Validate(publisher.Observations[0]).IsValid);

        var cloudlessConfig = Config(CameraPixelFormat.Mono16, null);
        var cloudlessModule = Module();
        await cloudlessModule.InitializeAsync(cloudlessConfig, CancellationToken.None).ConfigureAwait(false);
        var cloudless = await cloudlessModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        await step.ProcessAsync(Context(cloudlessConfig, request, cloudless), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(3, publisher.Facts);
    }

    [TestMethod]
    public async Task ObservationStep_RejectsConflictingProvenanceBeforePublishing()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition());
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var cloud = result.Frame!.Metadata.Scene!.CloudScenario! with { ParametersSha256 = new string('0', 64) };
        var frame = result.Frame with
        {
            Metadata = result.Frame.Metadata with
            {
                Scene = result.Frame.Metadata.Scene with { CloudScenario = cloud }
            }
        };
        var corrupted = result with { Frame = frame, Artifacts = new FrameArtifactSet(frame) };
        var publisher = new RecordingPublisher();
        var step = new VirtualSkyCloudObservationProcessingStep(
            new CaptureProcessingStepMetadata("CloudObservation", typeof(VirtualSkyCloudObservationProcessingStep).FullName!, 10),
            new VirtualSkyCloudObservationProcessingStepOptions(),
            publisher);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            step.ProcessAsync(Context(config, request, corrupted), CancellationToken.None).AsTask()).ConfigureAwait(false);

        var shiftedCloud = result.Frame.Metadata.Scene.CloudScenario with
        {
            IntegrationStartUtc = result.Frame.Metadata.Scene.CloudScenario.IntegrationStartUtc.AddSeconds(1),
            IntegrationEndUtc = result.Frame.Metadata.Scene.CloudScenario.IntegrationEndUtc.AddSeconds(1)
        };
        var shiftedFrame = result.Frame with
        {
            Metadata = result.Frame.Metadata with
            {
                Scene = result.Frame.Metadata.Scene with { CloudScenario = shiftedCloud }
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            step.ProcessAsync(
                Context(config, request, result with { Frame = shiftedFrame, Artifacts = new FrameArtifactSet(shiftedFrame) }),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

        var conflictingConfig = Config(CameraPixelFormat.Mono16, Definition() with { Seed = 105 });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            step.ProcessAsync(Context(conflictingConfig, request, result), CancellationToken.None).AsTask()).ConfigureAwait(false);

        var shiftedTimestampFrame = result.Frame with { TimestampUtc = result.Frame.TimestampUtc.AddSeconds(1) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            step.ProcessAsync(
                Context(config, request, result with
                {
                    Frame = shiftedTimestampFrame,
                    Artifacts = new FrameArtifactSet(shiftedTimestampFrame)
                }),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.IsEmpty(publisher.Facts);
    }

    [TestMethod]
    public async Task ObservationStep_PropagatesDurabilityFailureAndCancellation()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition());
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var metadata = new CaptureProcessingStepMetadata(
            "CloudObservation",
            typeof(VirtualSkyCloudObservationProcessingStep).FullName!,
            10);
        var failing = new VirtualSkyCloudObservationProcessingStep(
            metadata,
            new VirtualSkyCloudObservationProcessingStepOptions(),
            new FailingPublisher());

        await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(() =>
            failing.ProcessAsync(Context(config, request, result), CancellationToken.None).AsTask()).ConfigureAwait(false);

        var recording = new RecordingPublisher();
        var cancellable = new VirtualSkyCloudObservationProcessingStep(
            metadata,
            new VirtualSkyCloudObservationProcessingStepOptions(),
            recording);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancellable.ProcessAsync(Context(config, request, result), cancellation.Token).AsTask()).ConfigureAwait(false);
        Assert.IsEmpty(recording.Facts);
    }

    public TestContext TestContext { get; set; } = null!;

    private static VirtualCloudScenarioDefinition Definition() => new()
    {
        ScenarioId = "scenario-104-a",
        ScenarioVersion = "1",
        Seed = 104,
        EpochUtc = FixtureUtc,
        SpatialFrequency = 3,
        DriftEastCellsPerSecond = 0.02,
        DriftNorthCellsPerSecond = -0.01,
        EvolutionCellsPerSecond = 0.002,
        Octaves = 3,
        EdgeSoftness = 0.5,
        HorizonFadeDegrees = 5,
        TemporalSampleCount = 4,
        Keyframes = [new() { Coverage = 0.55, MaximumOpacity = 0.8, ScatterFraction = 0.1 }]
    };

    private static VirtualSkyCameraModule Module()
        => new(TimeProvider.System, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());

    private static CameraModuleConfig Config(
        CameraPixelFormat format,
        VirtualCloudScenarioDefinition? definition,
        bool physicalMono = false)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            magnitudeZeroElectronsPerSecond = 1000,
            backgroundElectronsPerSecond = 100d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0d,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            cloudScenario = definition,
            asi174Sensor = new { enabled = physicalMono, blackLevelAdu = 64d },
            asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16, blackLevelContainerAdu = 64d }
        });
        var color = format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color;
        var response = format switch
        {
            CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
            CameraPixelFormat.Rgb24 => SensorResponseMode.RenderedRgb,
            CameraPixelFormat.BayerRggb16 => SensorResponseMode.BayerRaw,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile("CloudFixture", 64, 48, 5.86, color, format, response, SensorRecipeVersion: "cloud-fixture-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    32, 24, 23, CalibrationVersion: "cloud-fixture-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));
    }

    private static CaptureProcessingContext Context(
        CameraModuleConfig config,
        CaptureRequest request,
        CaptureResult result)
        => new(config, new CaptureLoopSubmission(request, result, request.RequestedStartUtc, request.TargetInterval, TimeSpan.Zero));

    private static (ushort Minimum, ushort Maximum, double Mean) ComputeRawStatistics(ReadOnlySpan<byte> pixels)
    {
        var minimum = ushort.MaxValue;
        ushort maximum = 0;
        long sum = 0;
        for (var offset = 0; offset < pixels.Length; offset += sizeof(ushort))
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pixels[offset..]);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sum += value;
        }
        return (minimum, maximum, sum / (pixels.Length / (double)sizeof(ushort)));
    }

    private sealed class RecordingPublisher : IEnvironmentalObservationPublisher
    {
        public List<EnvironmentalObservationFactV1> Facts { get; } = [];
        public List<EnvironmentalObservationV1> Observations { get; } = [];

        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Facts.Add(fact);
            var observation = fact.Enrich(new EnvironmentalObservationTarget(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")));
            Observations.Add(observation);
            return ValueTask.FromResult(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued,
                observation));
        }
    }

    private sealed class FailingPublisher : IEnvironmentalObservationPublisher
    {
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<EnvironmentalObservationPublishResult>(
                new EnvironmentalObservationOutboxCapacityException("test capacity failure"));
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record CloudFixtureManifest(
        string SchemaVersion,
        DateTimeOffset Utc,
        double ExposureSeconds,
        IReadOnlyList<CloudFixtureCase> Cases);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record CloudFixtureCase(
        string Id,
        string OracleLabel,
        double ExpectedCoverage,
        double MinimumCoverage,
        double MaximumCoverage,
        ushort ExpectedRawMinimum,
        ushort ExpectedRawMaximum,
        double ExpectedRawMean,
        string ExpectedMono16Sha256,
        VirtualCloudScenarioDefinition Definition);
}
