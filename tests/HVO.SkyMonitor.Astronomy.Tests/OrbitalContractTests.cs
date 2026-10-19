using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalContractTests
{
    [TestMethod]
    public void CanonicalFixturesRoundTripWithPinnedByteHashes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "contract-v1");
        using var pack = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "fixture-manifest.json")));
        foreach (var file in pack.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, file.GetProperty("path").GetString()!));
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
            Assert.AreEqual(file.GetProperty("lengthBytes").GetInt32(), bytes.Length);
        }
        var snapshot = OrbitalContractExamples.Snapshot();
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "snapshot-manifest.json")), snapshot.SerializeManifest());
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "records.json")), snapshot.SerializePayload());
        var parsed = OrbitalSnapshot.Parse(snapshot.SerializeManifest(), snapshot.SerializePayload());
        Assert.IsTrue(parsed.IsValid, parsed.Reason);
        CollectionAssert.AreEqual(snapshot.SerializePayload(), parsed.Snapshot!.SerializePayload());
        var track = OrbitalContractExamples.Track(snapshot);
        var pass = OrbitalContractExamples.Pass(snapshot);
        var trackBytes = File.ReadAllBytes(Path.Combine(root, "exposure-track.json"));
        var passBytes = File.ReadAllBytes(Path.Combine(root, "pass.json"));
        CollectionAssert.AreEqual(trackBytes, OrbitalPredictionJson.SerializeTrack(track));
        CollectionAssert.AreEqual(passBytes, OrbitalPredictionJson.SerializePass(pass));
        Assert.AreEqual(track.ProductSha256, OrbitalPredictionJson.ParseTrack(trackBytes).ProductSha256);
        Assert.AreEqual(pass.ProductSha256, OrbitalPredictionJson.ParsePass(passBytes).ProductSha256);
        Assert.AreNotEqual(track.ProductSha256, pass.ProductSha256);
        var measuredGeometry = JsonSerializer.Deserialize<OrbitalGeometryFacts>(File.ReadAllBytes(Path.Combine(root, "measured-geometry.json")), OrbitalContract.JsonOptions)!;
        using var measuredCalibration = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "measured-calibration.json")));
        var calibrationJson = measuredCalibration.RootElement;
        var calibration = new AstrometricCalibration(calibrationJson.GetProperty("projection").Deserialize<ProjectionContext>(OrbitalContract.JsonOptions),
            calibrationJson.GetProperty("calibrationVersion").GetString()!, calibrationJson.GetProperty("readoutIdentitySha256").GetString()!);
        var assessment = AstrometricEvidenceJson.Parse(File.ReadAllBytes(Path.Combine(root, "measured-assessment.json")));
        measuredGeometry.ValidateMeasuredEvidence(calibration, assessment);
    }

    [TestMethod]
    public void ObjectRecordAndAcquisitionIdentitiesHaveSeparateLifetimes()
    {
        var records = new[] { OrbitalContractExamples.Record(), OrbitalContractExamples.Record(epochHours: -1), OrbitalContractExamples.Record("999999999", name: null) };
        var sources = new[] { OrbitalContractExamples.Source() };
        var exclusions = new[] { "inactive" };
        var snapshot = OrbitalSnapshot.Create(records, sources, OrbitalContractExamples.Coverage with { KnownExclusions = exclusions });
        var refreshed = OrbitalSnapshot.Create(records.Reverse(), [sources[0] with { RetrievedUtc = sources[0].RetrievedUtc.AddHours(2), RawSha256 = OrbitalContractExamples.Sha('D') }],
            OrbitalContractExamples.Coverage with { KnownExclusions = exclusions });
        Assert.AreEqual(snapshot.Manifest.NormalizedPayloadSha256, refreshed.Manifest.NormalizedPayloadSha256);
        Assert.AreNotEqual(snapshot.Manifest.ManifestSha256, refreshed.Manifest.ManifestSha256);
        Assert.AreEqual(3, snapshot.Manifest.RecordCount); Assert.AreEqual(2, snapshot.Manifest.ObjectCount);
        var original = snapshot.Records[0];
        records[0] = OrbitalContractExamples.Record("100001"); sources[0] = sources[0] with { RawLengthBytes = 5 }; exclusions[0] = "changed";
        Assert.AreEqual(original, snapshot.Records[0]); Assert.AreEqual(1234, snapshot.Manifest.Sources[0].RawLengthBytes);
        Assert.AreEqual("inactive", snapshot.Manifest.Coverage.KnownExclusions[0]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<OrbitalRecordV1>)snapshot.Records).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)snapshot.Manifest.Coverage.KnownExclusions).Clear());
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalContractExamples.Snapshot(original, original));
    }

    [TestMethod]
    public void TypedNormalizationFreezesUnitsAnglesUtcAndNegativeZero()
    {
        var baseline = OrbitalContractExamples.Record();
        var draft = baseline with
        {
            EpochUtc = baseline.EpochUtc.ToOffset(TimeSpan.FromHours(7)),
            Elements = baseline.Elements with { AscendingNodeDegrees = 485, MeanAnomalyDegrees = -90, Eccentricity = -0d, MeanMotionDdot = -0d }
        };
        var normalized = OrbitalContract.CreateRecord(draft);
        Assert.AreEqual(TimeSpan.Zero, normalized.EpochUtc.Offset);
        Assert.AreEqual(125, normalized.Elements.AscendingNodeDegrees); Assert.AreEqual(270, normalized.Elements.MeanAnomalyDegrees);
        Assert.AreEqual(0L, BitConverter.DoubleToInt64Bits(normalized.Elements.Eccentricity));
        Assert.AreEqual(0L, BitConverter.DoubleToInt64Bits(normalized.Elements.MeanMotionDdot));
        Assert.AreEqual(normalized, OrbitalContract.CreateRecord(normalized));
        Assert.AreEqual(.00002, baseline.Elements.MeanMotionDot); Assert.AreEqual(-.0000003, baseline.Elements.MeanMotionDdot);
        Assert.AreNotEqual(baseline.RecordSha256, OrbitalContract.CreateRecord(baseline with { ObjectName = "another alias" }).RecordSha256);
    }

    [TestMethod]
    public void RejectsMalformedIdsModelsRangesAndNonfiniteElements()
    {
        var r = OrbitalContractExamples.Record();
        foreach (var id in new[] { "0", "0001", "1234567890", "A1234", " 1", "١" })
            Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(r with { ObjectIdentity = new("norad", id) }));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(r with { Elements = r.Elements with { MeanMotionDdot = invalid } }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(r with { Model = r.Model with { Frame = "J2000" } }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(r with { Elements = r.Elements with { Eccentricity = 1 } }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(r with { ValidFromUtc = r.EpochUtc, ValidToUtc = r.EpochUtc.AddSeconds(-1) }));
        Assert.IsTrue(OrbitalContract.IsObjectIdentity(new("norad", "100001")));
        Assert.IsTrue(OrbitalContract.IsObjectIdentity(new("norad", "999999999")));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lowercase hash is an intentionally invalid conformance input.")]
    public void StrictParserRejectsCorruptionUnknownDuplicateMissingAndNoncanonicalInputs()
    {
        var s = OrbitalContractExamples.Snapshot();
        var manifest = Encoding.UTF8.GetString(s.SerializeManifest()); var payload = Encoding.UTF8.GetString(s.SerializePayload());
        foreach (var bad in new[] { "null", "{", manifest.Replace("hvo-orbital-elements-v1", "future-v2", StringComparison.Ordinal),
            manifest[..^1] + ",\"unknown\":1}", manifest[..^1] + ",\"recordCount\":4}",
            manifest.Replace("\"recordCount\":4,", "", StringComparison.Ordinal),
            manifest.Replace(s.Manifest.ManifestSha256, s.Manifest.ManifestSha256.ToLowerInvariant(), StringComparison.Ordinal) })
            Assert.IsFalse(OrbitalSnapshot.Parse(Encoding.UTF8.GetBytes(bad), s.SerializePayload()).IsValid, bad);
        foreach (var bad in new[] { "null", "[]", "[null]", payload[..^1] + ",{}]", payload.Replace("15.5", "1.55e1", StringComparison.Ordinal),
            payload.Replace("15.5", "1e999", StringComparison.Ordinal), payload.Replace("\"meanMotionDot\":", "\"unknown\":0,\"meanMotionDot\":", StringComparison.Ordinal) })
            Assert.IsFalse(OrbitalSnapshot.Parse(s.SerializeManifest(), Encoding.UTF8.GetBytes(bad)).IsValid, bad);
        Assert.IsFalse(OrbitalSnapshot.Parse(new byte[OrbitalContract.MaximumManifestBytes + 1], s.SerializePayload()).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(s.Records, [OrbitalContractExamples.Source() with { SourceUri = new("https://host.test/feed?token=secret") }], OrbitalContractExamples.Coverage));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(s.Records, [OrbitalContractExamples.Source() with { Parser = OrbitalContractExamples.Parser with { Version = "changed" } }], OrbitalContractExamples.Coverage));
    }

    [TestMethod]
    public void SnapshotBoundsAndNullInputsFailBeforeBecomingUsable()
    {
        var r = OrbitalContractExamples.Record();
        Assert.ThrowsExactly<ArgumentNullException>(() => OrbitalSnapshot.Create([null!], [OrbitalContractExamples.Source()], OrbitalContractExamples.Coverage));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create([r], [null!], OrbitalContractExamples.Coverage));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(Enumerable.Repeat(r, OrbitalContract.MaximumRecords + 1), [OrbitalContractExamples.Source()], OrbitalContractExamples.Coverage));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalSelectionPolicy([null!]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create([r], Enumerable.Repeat(OrbitalContractExamples.Source(), 33), OrbitalContractExamples.Coverage));
    }
    [TestMethod]
    public void DerivativeFixtureFreezesReferenceInitializerUnitsWithoutTwoOrSixFactors()
    {
        using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "contract-v1", "derivative-coefficients.json")));
        var values = file.RootElement;
        var r = OrbitalContractExamples.Record();
        Assert.AreEqual(r.Elements.MeanMotionDot, values.GetProperty("meanMotionDotGp").GetDouble());
        Assert.AreEqual(r.Elements.MeanMotionDdot, values.GetProperty("meanMotionDdotGp").GetDouble());
        Assert.AreEqual(6.0601710138692e-11, values.GetProperty("expectedNdotRadiansPerMinuteSquared").GetDouble(), 1e-24);
        Assert.AreEqual(-6.312678139447083e-16, values.GetProperty("expectedNddotRadiansPerMinuteCubed").GetDouble(), 1e-29);
    }

    [TestMethod]
    public void ReaderResultHasExactlyOneOutcomeAndManifestKeysAreStrict()
    {
        var snapshot = OrbitalContractExamples.Snapshot();
        var available = OrbitalSnapshotReadResult.Available(snapshot);
        Assert.AreSame(snapshot, available.Snapshot); Assert.IsNull(available.UnavailableReason);
        foreach (var reason in Enum.GetValues<OrbitalSnapshotUnavailableReason>())
        {
            var unavailable = OrbitalSnapshotReadResult.Unavailable(reason);
            Assert.IsNull(unavailable.Snapshot); Assert.AreEqual(reason, unavailable.UnavailableReason);
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => OrbitalSnapshotReadResult.Available(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OrbitalSnapshotReadResult.Unavailable((OrbitalSnapshotUnavailableReason)999));
        Assert.IsTrue(OrbitalContract.IsSha256(snapshot.Manifest.ManifestSha256));
        Assert.IsFalse(OrbitalContract.IsSha256(new string('a', 64)));
        Assert.IsFalse(OrbitalContract.IsSha256(new string('G', 64)));
    }

}
