using System.Text.Json;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.AgentCore.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualExposureProvenanceTests
{
    private static readonly DateTimeOffset RequestUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(VirtualExposureTimeMapping.RequestUtc)]
    [DataRow(VirtualExposureTimeMapping.FixedCelestialUtc)]
    [DataRow(VirtualExposureTimeMapping.FixedScenarioUtc)]
    [DataRow(VirtualExposureTimeMapping.FixedCelestialAndScenarioUtc)]
    public void ResolvedMappingsRetainExactTicksAndRoundTrip(VirtualExposureTimeMapping mapping)
    {
        var scenario = mapping is VirtualExposureTimeMapping.FixedScenarioUtc or VirtualExposureTimeMapping.FixedCelestialAndScenarioUtc
            ? RequestUtc.AddHours(-1) : RequestUtc;
        var celestial = mapping is VirtualExposureTimeMapping.FixedCelestialUtc or VirtualExposureTimeMapping.FixedCelestialAndScenarioUtc
            ? RequestUtc.AddDays(-1) : scenario;
        var exposure = TimeSpan.FromTicks(10003);
        var value = VirtualExposureProvenance.Create(RequestUtc, scenario, celestial, exposure, mapping);
        Assert.IsTrue(value.IsValid(exposure));
        Assert.AreEqual(5001L, value.CelestialMidpointUtc.Ticks - celestial.Ticks);
        Assert.AreEqual(10003L, value.ScenarioEndUtc.Ticks - scenario.Ticks);
        var json = JsonSerializer.Serialize(value);
        Assert.AreEqual(value, JsonSerializer.Deserialize<VirtualExposureProvenance>(json));
        Assert.IsTrue(JsonSerializer.Deserialize<VirtualExposureProvenance>(json)!.IsValid(exposure));
    }

    [TestMethod]
    public void CorruptIntervalsMappingAndOffsetReject()
    {
        var value = VirtualExposureProvenance.Create(RequestUtc, RequestUtc, RequestUtc, TimeSpan.FromSeconds(20),
            VirtualExposureTimeMapping.RequestUtc);
        VirtualExposureProvenance[] invalid =
        [
            value with { SchemaVersion = "unknown" },
            value with { TimeMapping = (VirtualExposureTimeMapping)99 },
            value with { RequestedStartUtc = default },
            value with { ScenarioStartUtc = RequestUtc.ToOffset(TimeSpan.FromHours(1)) },
            value with { ScenarioEndUtc = RequestUtc.AddSeconds(19) },
            value with { CelestialEndUtc = RequestUtc.AddTicks(-1) },
            value with { CelestialMidpointUtc = RequestUtc.AddSeconds(10).AddTicks(1) },
            value with { ScenarioMidpointUtc = RequestUtc.AddSeconds(10).AddTicks(-1) },
            value with { RequestedStartUtc = RequestUtc.AddHours(1) }
        ];
        foreach (var corrupt in invalid) Assert.IsFalse(corrupt.IsValid(TimeSpan.FromSeconds(20)));
        Assert.IsFalse(value.IsValid(TimeSpan.FromSeconds(21)));
        Assert.IsFalse(value.IsValid(TimeSpan.FromTicks(-1)));
    }

    [TestMethod]
    public void ZeroExposureHasNoDurationAndOverflowNeverWraps()
    {
        var value = VirtualExposureProvenance.Create(RequestUtc, RequestUtc, RequestUtc, TimeSpan.Zero,
            VirtualExposureTimeMapping.RequestUtc);
        Assert.IsTrue(value.IsValid(TimeSpan.Zero));
        Assert.AreEqual(RequestUtc, value.CelestialEndUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualExposureProvenance.Create(RequestUtc, RequestUtc, RequestUtc,
            TimeSpan.FromTicks(-1), VirtualExposureTimeMapping.RequestUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualExposureProvenance.Create(DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, TimeSpan.FromSeconds(1), VirtualExposureTimeMapping.RequestUtc));
        Assert.Throws<ArgumentException>(() => VirtualExposureProvenance.Create(RequestUtc, RequestUtc.AddSeconds(1), RequestUtc,
            TimeSpan.FromSeconds(1), VirtualExposureTimeMapping.RequestUtc));
    }

    [TestMethod]
    public void ManifestRoundTripPreservesOperationalTimingAndRejectsConflictingLogicalFacts()
    {
        var original = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var request = original.Descriptor.Timing.RequestedStartUtc;
        var logical = VirtualExposureProvenance.Create(request, request.AddHours(-1), request.AddDays(-1),
            original.Descriptor.Controls.EffectiveExposure, VirtualExposureTimeMapping.FixedCelestialAndScenarioUtc);
        var scene = new SceneProvenance("scene", "rig-v1", "HYG", "4.2", new string('A', 64),
            "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1",
            SceneUtc: logical.CelestialMidpointUtc, VirtualExposure: logical);
        var manifest = original with { Scene = scene };
        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(manifest));
        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(logical, parsed.Document!.Manifest.Scene!.VirtualExposure);
        Assert.AreEqual(original.Descriptor.Timing, parsed.Document.Manifest.Descriptor.Timing);
        Assert.AreEqual(original.IdempotencyKey, manifest.IdempotencyKey);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(original), CaptureContractJson.ComputeManifestSha256(manifest));
        var corrupt = manifest with { Scene = scene with { SceneUtc = logical.CelestialStartUtc } };
        Assert.AreEqual("scene.virtualExposure", corrupt.Validate().FieldPath);
        corrupt = manifest with { Scene = scene with { VirtualExposure = logical with { ScenarioEndUtc = logical.ScenarioEndUtc.AddTicks(1) } } };
        Assert.IsFalse(CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(corrupt)).IsValid);
        Assert.IsTrue(original.Validate().IsValid, "Existing manifests have no virtual interval requirement.");
    }
}
