using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualSkyTransientScenarioTests
{
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions FixtureSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<CameraPixelFormat, (string First, string Later)> ExpectedChecksums =
        new Dictionary<CameraPixelFormat, (string First, string Later)>
        {
            [CameraPixelFormat.Mono16] = (
                "F703A678B0F2D6C4133D216E1AED8F06DD57E287A6A06F4DC17DC890788AF77A",
                "FFC5B7A47D26EBE277FCC8F6C23389615EEB31845459E499CB7A4390B544DCE4"),
            [CameraPixelFormat.Rgb24] = (
                "3BD387EDC357C1C5B19129EA8DAB1329103D620DE20794D8600C1ABACDD9D22A",
                "1035AB2C2CFA287BDC71D8F14F591027F0B6A4026517A7F6018BF74C44AB1265"),
            [CameraPixelFormat.BayerRggb16] = (
                "32DCB2000A7B01EC85D92C33B2FA70AE1DA74BA6365522282DA8035C080140F6",
                "D9D898DFC347F9C8272C6B31338AD76ED2900E288A3A761FC1C0DCBB55FEFEAB")
        };

    [TestMethod]
    public async Task FixtureMatrix_HasStableOpaqueRawEvidence()
    {
        var manifest = JsonSerializer.Deserialize<TransientFixtureManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "transient-scenarios-v1.json"))
                .ConfigureAwait(false),
            FixtureSerializerOptions)!;
        Assert.AreEqual("virtual-transient-fixture-v1", manifest.SchemaVersion);
        foreach (var scenario in manifest.Cases)
        {
            var serializedDefinition = JsonSerializer.Serialize(scenario.Definition);
            Assert.IsFalse(serializedDefinition.Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            var module = Module();
            await module.InitializeAsync(
                Config(CameraPixelFormat.Mono16, scenario.Definition), CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(
                new CaptureRequest(
                    manifest.Utc.AddSeconds(scenario.CaptureOffsetSeconds),
                    TimeSpan.FromSeconds(5),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                CancellationToken.None).ConfigureAwait(false);
            var checksum = Convert.ToHexString(SHA256.HashData(capture.Frame!.PixelData.Span));
            var statistics = RawStatistics(capture.Frame.PixelData.Span);
            var parameters = capture.Frame.Metadata.Scene!.TransientScenario!.Parameters.GetRawText();
            Assert.IsFalse(parameters.Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            TestContext.WriteLine(
                $"{scenario.Id}: min={statistics.Minimum}, max={statistics.Maximum}, " +
                $"mean={statistics.Mean:R}, sha256={checksum}");
            if (scenario.ExpectedMono16Sha256.Length > 0)
            {
                Assert.AreEqual(scenario.ExpectedMono16Sha256, checksum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMinimum, statistics.Minimum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMaximum, statistics.Maximum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMean, statistics.Mean, 1e-12, scenario.Id);
            }
            if (scenario.ExpectedSecondMono16Sha256 is not null)
            {
                var second = await module.CaptureAsync(
                    new CaptureRequest(
                        manifest.Utc.AddSeconds(scenario.CaptureOffsetSeconds + manifest.ExposureSeconds),
                        TimeSpan.FromSeconds(5),
                        CaptureMode.Still,
                        new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                    CancellationToken.None).ConfigureAwait(false);
                var secondChecksum = Convert.ToHexString(SHA256.HashData(second.Frame!.PixelData.Span));
                TestContext.WriteLine($"{scenario.Id}: second-sha256={secondChecksum}");
                if (scenario.ExpectedSecondMono16Sha256.Length > 0)
                {
                    Assert.AreEqual(scenario.ExpectedSecondMono16Sha256, secondChecksum, scenario.Id);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, true)]
    [DataRow(CameraPixelFormat.Rgb24, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    public async Task CaptureAsync_TransientScenarioRunsThroughEveryRawFormat(
        CameraPixelFormat format,
        bool physicalMono)
    {
        var definition = Definition();
        var config = Config(format, definition, physicalMono);
        var firstModule = Module();
        var repeatedModule = Module();
        await firstModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(5),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), physicalMono || format == CameraPixelFormat.BayerRggb16 ? 100 : 1, null, null));

        var first = await firstModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeated = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var later = await repeatedModule.CaptureAsync(
            request with { RequestedStartUtc = FixtureUtc.AddSeconds(1) }, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(first.Frame!.PixelData.ToArray(), repeated.Frame!.PixelData.ToArray());
        CollectionAssert.AreNotEqual(first.Frame.PixelData.ToArray(), later.Frame!.PixelData.ToArray());
        Assert.AreEqual(format, first.Frame.PixelFormat);
        var provenance = first.Frame.Metadata.Scene!.TransientScenario!;
        var canonical = definition with { ScenarioId = definition.ComputeCanonicalScenarioId() };
        Assert.AreEqual(canonical.ScenarioId, provenance.ScenarioId);
        Assert.AreEqual(canonical.ComputeParametersSha256(), provenance.ParametersSha256);
        Assert.AreEqual(FixtureUtc, provenance.IntegrationStartUtc);
        Assert.AreEqual(FixtureUtc.AddSeconds(1), provenance.IntegrationEndUtc);
        Assert.AreEqual(1, provenance.SkyPrimitiveCount);
        Assert.AreEqual(1, provenance.SensorPrimitiveCount);
        Assert.Contains(VirtualTransientScenarioDefinition.CurrentAlgorithmVersion,
            first.Frame.Metadata.Extra!["renderAlgorithm"]);
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("aircraft", StringComparison.OrdinalIgnoreCase));
        var firstChecksum = Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span));
        var laterChecksum = Convert.ToHexString(SHA256.HashData(later.Frame.PixelData.Span));
        Assert.AreEqual(ExpectedChecksums[format].First, firstChecksum);
        Assert.AreEqual(ExpectedChecksums[format].Later, laterChecksum);
        TestContext.WriteLine(
            $"{format}: first={firstChecksum}, later={laterChecksum}");
    }

    [TestMethod]
    public async Task CaptureAsync_PhysicalFramesAreStableAcrossRestartAndCaptureOrder()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition(), physicalMono: true);
        var ordered = Module();
        var reversed = Module();
        await ordered.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await reversed.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 100, null, null);
        var firstRequest = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint);
        var secondRequest = firstRequest with { RequestedStartUtc = FixtureUtc.AddSeconds(1) };

        var orderedFirst = await ordered.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);
        var orderedSecond = await ordered.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedSecond = await reversed.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedFirst = await reversed.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(orderedFirst.Frame!.PixelData.ToArray(), reversedFirst.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(orderedSecond.Frame!.PixelData.ToArray(), reversedSecond.Frame!.PixelData.ToArray());
        Assert.AreEqual(
            orderedFirst.Frame.Metadata.Extra!["captureSequence"],
            reversedFirst.Frame.Metadata.Extra!["captureSequence"]);
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsUnknownAndOutOfBoundsTransientConfiguration()
    {
        using var unknown = JsonDocument.Parse("""
            {
              "transientScenario": {
                "schemaVersion": "virtual-transient-scenario-v1",
                "scenarioId": "scenario-61-a",
                "scenarioVersion": "1",
                "seed": 61,
                "epochUtc": "2025-01-15T08:00:00Z",
                "temporalSampleCount": 8,
                "unknown": true,
                "skyTracks": [],
                "sensorTracks": []
              }
            }
            """);
        var unknownConfig = Config(CameraPixelFormat.Mono16, null) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", unknown.RootElement.Clone())
        };
        var invalidConfig = Config(CameraPixelFormat.Mono16, Definition() with
        {
            SensorTracks =
            [
                Definition().SensorTracks[0] with
                {
                    Keyframes = Definition().SensorTracks[0].Keyframes.Select(
                        static keyframe => keyframe with { PixelX = 64 }).ToArray()
                }
            ]
        });

        await Assert.ThrowsAsync<JsonException>(() => Module().InitializeAsync(unknownConfig, CancellationToken.None))
            .ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Module().InitializeAsync(invalidConfig, CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RecipeAdapter_DoesNotExposeScenarioProvenanceAsDetectorInput()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition());
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            config,
            new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, result.Frame!),
            "source");

        var serialized = JsonSerializer.Serialize(artifact);
        Assert.IsFalse(serialized.Contains("transientScenario", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains(result.Frame!.Metadata.Scene!.TransientScenario!.ScenarioId,
            StringComparison.Ordinal));
    }

    public TestContext TestContext { get; set; } = null!;

    private static VirtualSkyCameraModule Module()
        => new(TimeProvider.System, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());

    private static CameraModuleConfig Config(
        CameraPixelFormat format,
        VirtualTransientScenarioDefinition? definition,
        bool physicalMono = false)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            magnitudeZeroElectronsPerSecond = 1000d,
            backgroundElectronsPerSecond = 1d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0.1,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            transientScenario = definition,
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
                new SensorProfile(
                    "TransientFixture", 64, 48, 5.86, color, format, response,
                    SensorRecipeVersion: "transient-fixture-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    32, 24, 23, CalibrationVersion: "transient-fixture-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));
    }

    private static VirtualTransientScenarioDefinition Definition() => new()
    {
        ScenarioId = "scenario-61-a",
        ScenarioVersion = "1",
        Seed = 61,
        EpochUtc = FixtureUtc,
        TemporalSampleCount = 8,
        SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "p-001",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 0,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 270,
                        Magnitude = -4,
                        AngularWidthDegrees = 0.25
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 2,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 90,
                        Magnitude = -1,
                        AngularWidthDegrees = 0.4
                    }
                ]
            }
        ],
        SensorTracks =
        [
            new VirtualTransientSensorTrack
            {
                PrimitiveId = "s-001",
                Keyframes =
                [
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 0,
                        PixelX = 1.5,
                        PixelY = 1.5,
                        ElectronsPerSecond = 10_000
                    },
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 3,
                        PixelX = 1.5,
                        PixelY = 1.5,
                        ElectronsPerSecond = 10_000
                    }
                ]
            }
        ]
    };

    private static (ushort Minimum, ushort Maximum, double Mean) RawStatistics(ReadOnlySpan<byte> pixels)
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

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureManifest(
        string SchemaVersion,
        DateTimeOffset Utc,
        double ExposureSeconds,
        IReadOnlyList<TransientFixtureCase> Cases);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureCase(
        string Id,
        string OracleLabel,
        double CaptureOffsetSeconds,
        ushort ExpectedRawMinimum,
        ushort ExpectedRawMaximum,
        double ExpectedRawMean,
        string ExpectedMono16Sha256,
        string? ExpectedSecondMono16Sha256,
        VirtualTransientScenarioDefinition Definition);
}
