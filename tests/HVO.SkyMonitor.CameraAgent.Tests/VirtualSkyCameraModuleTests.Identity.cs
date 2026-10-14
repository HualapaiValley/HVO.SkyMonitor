using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void RawSceneIdentityKeepsLegacyTypedBytesAcrossAnnotationSettings(int fixture)
    {
        var request = new VisibleSceneRequest(FixtureUtc.AddTicks(1234500), new(35.347, -113.878, 0),
            new EquidistantProjectionContext(32, 32, 20, 30, WidthPixels: 64, HeightPixels: 64),
            new(6.5, 128), new("fixture", "v1", new Uri("https://example.test/catalog"),
                new string('A', 64), "license", "1"));
        var setpoint = new CaptureSetpoint(TimeSpan.FromMilliseconds(1234), 1.25, null, null);
        var sensor = CreateConfig(CameraPixelFormat.Mono16, 64, 64).Rig.Sensor;
        var topology = new ConstellationTopologyMetadata("topology <é>\"", "v1",
            new Uri("https://example.test/topology?q=%E6%98%9F&x=1"), new string('B', 64), "license", "p1");
        var legacy = RawIdentityOptions(fixture, null);
        var expected = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(LegacyIdentityDocument(
            request, setpoint, legacy, sensor, "ephemeris-v1", topology)));
        var expectedHash = Convert.ToHexString(SHA256.HashData(expected));
        var serialize = typeof(VirtualSkyCameraModule).GetMethod("SerializeSceneIdentity", BindingFlags.Static | BindingFlags.NonPublic);
        var identity = typeof(VirtualSkyCameraModule).GetMethod("CreateSceneId", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(serialize);
        Assert.IsNotNull(identity);
        foreach (var annotation in new CuratedDeepSkyOptions?[] { null, new(Enabled: false), new(Enabled: true) })
        {
            var options = RawIdentityOptions(fixture, annotation);
            options.Validate();
            object?[] arguments = [request, setpoint, options, sensor, "ephemeris-v1", topology];
            var actual = (string)serialize.Invoke(null, arguments)!;
            CollectionAssert.AreEqual(expected, Encoding.UTF8.GetBytes(actual));
            Assert.AreEqual(expectedHash, (string)identity.Invoke(null, arguments)!);
            Assert.IsFalse(actual.Contains(nameof(VirtualSkyCameraModuleOptions.CuratedAnnotations), StringComparison.Ordinal));
        }
        if (fixture != 0)
        {
            // The legacy typed date writer deliberately retains literal '+'; a DOM string roundtrip does not.
            StringAssert.Contains(JsonSerializer.Serialize(legacy), "+00:00", StringComparison.Ordinal);
        }
    }

    private static VirtualSkyCameraModuleOptions RawIdentityOptions(int fixture, CuratedDeepSkyOptions? annotation)
    {
        if (fixture == 0) return new() { CuratedAnnotations = annotation };
        var utc = FixtureUtc.AddTicks(1234500);
        return new()
        {
            CuratedAnnotations = annotation,
            IlluminationMode = VirtualSkyIlluminationMode.SolarDriven,
            FixedSceneUtc = utc,
            FixedSequenceStartUtc = utc.AddTicks(1),
            MaximumMagnitude = 1.23456789,
            BackgroundElectronsPerSecond = fixture == 1 ? null : 1e-10,
            CatalogName = fixture == 1 ? "HYG" : "catalog <é>\\星\"+",
            CatalogLicense = fixture == 1 ? "CC BY-SA 4.0" : "license \"<é>\\星😀+",
            CatalogSourceUrl = new("https://example.test/catalog?q=%E6%98%9F&x=1"),
            ConstellationIds = ["ORI", "AND"],
            CloudScenario = new()
            {
                ScenarioId = "cloud <é>+",
                EpochUtc = utc,
                Keyframes =
                [
                    new() { Coverage = .1, MaximumOpacity = .2 },
                    new() { OffsetSeconds = 1.25, Coverage = .3, MaximumOpacity = .4 }
                ]
            },
            TransientScenario = new()
            {
                ScenarioId = "transient <é>+",
                EpochUtc = utc,
                SensorTracks =
                [
                    new()
                    {
                        PrimitiveId = "parity+",
                        Keyframes =
                        [
                            new() { PixelX = .5, PixelY = 1.25, ElectronsPerSecond = 1e-10 },
                            new() { OffsetSeconds = 1.25, PixelX = 2.5, PixelY = 3.75, ElectronsPerSecond = 123.456 }
                        ]
                    }
                ]
            }
        };
    }

    // Independent default-typed preimage from before #525: the null new property is omitted by its declared attribute.
    // Never derive this oracle from the removed JsonElement/dictionary pipeline or production serializer metadata.
    private static object LegacyIdentityDocument(VisibleSceneRequest request, CaptureSetpoint setpoint,
        VirtualSkyCameraModuleOptions options, SensorProfile sensor, string ephemerisModelVersion,
        ConstellationTopologyMetadata constellationTopologyMetadata) => new
        {
            request.Utc,
            request.Observer,
            request.Projection,
            request.CatalogQuery,
            request.CatalogMetadata,
            request.Refraction,
            request.HorizonPolicy,
            request.ProjectionVersion,
            request.AlgorithmVersion,
            request.ConstellationIds,
            request.SolarSystemBodies,
            StellarMotionModel = StellarExposureGeometry.AlgorithmVersion,
            StellarRasterModel = TemporalPointSpreadRaster.AlgorithmVersion,
            StellarVisibilityModel = HVO.SkyMonitor.Imaging.StellarVisibilityModel.AlgorithmVersion,
            StellarRenderModel = StellarExposureRenderPlan.AlgorithmVersion,
            StellarSkyModel = options.IlluminationMode == VirtualSkyIlluminationMode.SolarDriven &&
            !options.BackgroundElectronsPerSecond.HasValue
            ? SolarSkyIllumination.AlgorithmVersion + (options.RenderSolarSystemDisks
                ? "+" + SolarDiskEphemeris.AlgorithmVersion + "+" + SolarDiskRenderPlan.AlgorithmVersion : "")
            : StellarSkyBackgroundModel.AlgorithmVersion + (options.RenderSolarSystemDisks
                ? "+" + SolarDiskEphemeris.AlgorithmVersion + "+" + SolarDiskRenderPlan.AlgorithmVersion : ""),
            SolarBackgroundEphemerisModel = new AstronomyEnginePlanetEphemeris().ModelVersion,
            Setpoint = setpoint,
            Options = options,
            Sensor = sensor,
            EphemerisModelVersion = ephemerisModelVersion,
            ConstellationTopology = constellationTopologyMetadata
        };
}
