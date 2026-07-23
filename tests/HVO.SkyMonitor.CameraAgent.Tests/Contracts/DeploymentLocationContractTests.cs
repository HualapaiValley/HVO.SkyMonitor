using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentLocationContractTests
{
    [TestMethod]
    public void Snapshot_CreateProducesStableValidatedIdentity()
    {
        var first = CreateHualapai();
        var second = CreateHualapai();

        Assert.IsTrue(first.Validate().IsValid);
        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.CanonicalSha256.Length);
        Assert.AreEqual(first.CanonicalSha256, first.CanonicalSha256.ToUpperInvariant());
        Assert.AreEqual(first.ToProvenance(), first.ToProvenance());
    }

    [TestMethod]
    public void Snapshot_ChangedCoordinateChangesCanonicalIdentity()
    {
        var original = CreateHualapai();
        var changed = DeploymentLocationSnapshot.Create(
            original.LocationId,
            original.Version,
            original.Source,
            original.HorizontalAccuracyMeters,
            original.EffectiveFromUtc,
            original.EffectiveUntilUtc,
            original.LatitudeDegrees + 0.001,
            original.LongitudeDegrees,
            original.ElevationMeters,
            original.TimeZoneId);

        Assert.AreNotEqual(original.CanonicalSha256, changed.CanonicalSha256);
    }

    [TestMethod]
    public void CameraModuleConfig_RejectsCaptureOutsideLocationInterval()
    {
        var location = DeploymentLocationSnapshot.Create(
            "bounded", 1, "test", null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
            0, 0, 0, "UTC");
        var config = new CameraModuleConfig(
            location.ToObservatoryLocation(),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1), 1, 1)))
        {
            DeploymentLocation = location
        };

        Assert.AreEqual(location.ToObservatoryLocation(), config.ResolveObservatory(DateTimeOffset.UnixEpoch));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            config.ResolveObservatory(DateTimeOffset.UnixEpoch.AddHours(1)));
    }

    [TestMethod]
    public void Snapshot_InvalidValuesReturnStableLocationReasons()
    {
        var valid = CreateHualapai();

        Assert.AreEqual(CaptureContractReasonCodes.InvalidLocation,
            (valid with { LatitudeDegrees = 91 }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLocationTimeZone,
            DeploymentLocationSnapshot.Create(
                valid.LocationId, valid.Version, valid.Source, valid.HorizontalAccuracyMeters,
                valid.EffectiveFromUtc, valid.EffectiveUntilUtc, valid.LatitudeDegrees,
                valid.LongitudeDegrees, valid.ElevationMeters, "Pacific Standard Time").Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLocationInterval,
            DeploymentLocationSnapshot.Create(
                valid.LocationId, valid.Version, valid.Source, valid.HorizontalAccuracyMeters,
                valid.EffectiveFromUtc, valid.EffectiveFromUtc, valid.LatitudeDegrees,
                valid.LongitudeDegrees, valid.ElevationMeters, valid.TimeZoneId).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.LocationHashMismatch,
            (valid with { CanonicalSha256 = new string('0', 64) }).Validate().ReasonCode);
    }

    [TestMethod]
    public void ManifestV2_LocationProvenanceIsAdditiveAndCoordinateFree()
    {
        var original = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var snapshot = CreateHualapai();
        var enriched = original with
        {
            Descriptor = original.Descriptor with { Location = snapshot.ToProvenance() }
        };

        var originalJson = CaptureContractJson.Serialize(original);
        var enrichedJson = CaptureContractJson.Serialize(enriched);
        var parsed = CaptureContractJson.ParseManifest(enrichedJson);
        var encoded = Encoding.UTF8.GetString(enrichedJson);

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(snapshot.ToProvenance(), parsed.Document!.Manifest!.Descriptor.Location);
        Assert.AreNotEqual(original.IdempotencyKey, enriched.IdempotencyKey);
        Assert.AreNotEqual(
            CaptureContractJson.ComputeDescriptorSha256(original.Descriptor),
            CaptureContractJson.ComputeDescriptorSha256(enriched.Descriptor));
        Assert.IsFalse(Encoding.UTF8.GetString(originalJson).Contains("location", StringComparison.Ordinal));
        Assert.IsFalse(encoded.Contains("35.347", StringComparison.Ordinal));
        Assert.IsFalse(encoded.Contains("-113.878", StringComparison.Ordinal));
        Assert.IsFalse(encoded.Contains("America/Phoenix", StringComparison.Ordinal));

        var outsideInterval = enriched with
        {
            Descriptor = enriched.Descriptor with
            {
                Location = snapshot.ToProvenance() with
                {
                    EffectiveFromUtc = enriched.Descriptor.Timing.ExposureStartedUtc.AddSeconds(1)
                }
            }
        };
        var invalid = outsideInterval.Validate();
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLocationInterval, invalid.ReasonCode);
        Assert.AreEqual("descriptor.location.effectiveInterval", invalid.FieldPath);
    }

    [TestMethod]
    public void SidingSpringFixture_PinsSyntheticLocationCameraAndOptics()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "siding-spring-synthetic-deployment-v1.json")));
        var root = document.RootElement;
        var snapshot = DeploymentLocationSnapshot.Create(
            root.GetProperty("locationId").GetString()!,
            root.GetProperty("version").GetInt64(),
            root.GetProperty("source").GetString()!,
            null,
            root.GetProperty("effectiveFromUtc").GetDateTimeOffset(),
            null,
            root.GetProperty("latitudeDegrees").GetDouble(),
            root.GetProperty("longitudeDegreesEastPositive").GetDouble(),
            root.GetProperty("elevationMeters").GetDouble(),
            root.GetProperty("timeZoneId").GetString()!);

        Assert.IsTrue(snapshot.Validate().IsValid);
        Assert.AreEqual(root.GetProperty("canonicalSha256").GetString(), snapshot.CanonicalSha256);
        Assert.AreEqual(-31.2733, snapshot.LatitudeDegrees, 1e-12);
        Assert.AreEqual(149.0700, snapshot.LongitudeDegrees, 1e-12);
        Assert.AreEqual(1165, snapshot.ElevationMeters, 1e-12);
        Assert.AreEqual("Australia/Sydney", snapshot.TimeZoneId);
        StringAssert.Contains(root.GetProperty("cameraLabel").GetString()!, "Synthetic", StringComparison.Ordinal);
        StringAssert.Contains(root.GetProperty("opticsLabel").GetString()!, "Synthetic", StringComparison.Ordinal);
    }

    private static DeploymentLocationSnapshot CreateHualapai()
        => DeploymentLocationSnapshot.Create(
            "hualapai-canonical",
            1,
            "tests/fixtures/astronomy/hualapai-asi174-conformance-v1.json",
            null,
            DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            null,
            35.347,
            -113.878,
            0,
            "America/Phoenix");
}
