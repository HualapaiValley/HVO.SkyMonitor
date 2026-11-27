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
    public void CsvAndLegacyGpProvenanceCannotBeInterchanged()
    {
        var original = OrbitalContractExamples.Record();
        var record = OrbitalContract.CreateRecord(original with
        {
            Model = original.Model with { ProfileVersion = OrbitalContract.CelesTrakGpCsvProfile }
        });
        var source = OrbitalContractExamples.Source() with
        {
            SourceUri = new("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")
        };
        var coverage = OrbitalContractExamples.Coverage;
        OrbitalSnapshot Csv(OrbitalRecordV1 item, OrbitalSnapshotSource provenance) =>
            OrbitalSnapshot.Create([item], [provenance], coverage, OrbitalContract.CelesTrakGpCsvProfile);
        var snapshot = Csv(record, source);
        var parsed = OrbitalSnapshot.Parse(snapshot.SerializeManifest(), snapshot.SerializePayload());
        Assert.IsTrue(parsed.IsValid, parsed.Reason);
        Assert.AreEqual(record.RecordSha256, parsed.Snapshot!.Records.Single().RecordSha256);
        Assert.AreNotEqual(original.RecordSha256, record.RecordSha256);
        Assert.ThrowsExactly<ArgumentException>(() => Csv(original, source));
        Assert.ThrowsExactly<ArgumentException>(() => Csv(record, OrbitalContractExamples.Source()));
        Assert.ThrowsExactly<ArgumentException>(() => Csv(record, source with { SourceUri = new("https://example.invalid/") }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create([original], [source], coverage));
        Assert.ThrowsExactly<ArgumentException>(() => Csv(record, source with { TermsUri = source.SourceUri }));
        Assert.ThrowsExactly<ArgumentException>(() => Csv(record, source with { SourceUri = new(source.SourceUri + "&token=extra") }));
    }

    [TestMethod]
    [DataRow(OrbitalContract.CelesTrakGpProfile, true)]
    [DataRow(OrbitalContract.CelesTrakGpCsvProfile, true)]
    [DataRow("", false)]
    [DataRow(" ", false)]
    [DataRow("celestrak-gp-xml-v1", false)]
    [DataRow("celestrak-gp-csv-v2", false)]
    [DataRow("CELESTRAK-GP-CSV-V1", false)]
    [DataRow("CELESTRAK-GP-JSON-V1", false)]
    [DataRow("celestrak-gp-csv-v1 ", false)]
    [DataRow(null, false)]
    public void IsSupportedGpProfileAcceptsOnlyCanonicalJsonAndCsvProfiles(string? profile, bool expected)
    {
        Assert.AreEqual(expected, OrbitalContract.IsSupportedGpProfile(profile!));
        if (!expected)
        {
            var record = OrbitalContractExamples.Record();
            var recordError = Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.CreateRecord(record with
            {
                Model = record.Model with { ProfileVersion = profile! }
            }));
            Assert.AreEqual("unsupported-model", recordError.Message);

            var snapshotError = Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(
                [record], [OrbitalContractExamples.Source()], OrbitalContractExamples.Coverage, profile!));
            Assert.AreEqual("manifest-schema-identity", snapshotError.Message);
        }
    }

    [TestMethod]
    [DataRow("https://evil.example/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://celestrak.org.evil.example/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://www.celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://CELESTRAK.ORG/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("http://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://celestrak.org:443/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://user@celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")]
    [DataRow("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV#frag")]
    [DataRow("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON")]
    [DataRow("https://celestrak.org/NORAD/elements/gp.php")]
    public void CsvProfileRejectsNonCanonicalSourceHostSchemeAndQuery(string invalidSourceAddress)
    {
        var original = OrbitalContractExamples.Record();
        var record = OrbitalContract.CreateRecord(original with
        {
            Model = original.Model with { ProfileVersion = OrbitalContract.CelesTrakGpCsvProfile }
        });
        var canonicalSource = OrbitalContractExamples.Source() with
        {
            SourceUri = new("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=CSV")
        };
        var invalidSource = canonicalSource with { SourceUri = new(invalidSourceAddress) };

        var createError = Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(
            [record], [invalidSource], OrbitalContractExamples.Coverage, OrbitalContract.CelesTrakGpCsvProfile));
        Assert.AreEqual("source-provenance", createError.Message);

        var validSnapshot = OrbitalSnapshot.Create(
            [record], [canonicalSource], OrbitalContractExamples.Coverage, OrbitalContract.CelesTrakGpCsvProfile);
        var mutatedManifest = validSnapshot.Manifest with { Sources = [invalidSource] };
        mutatedManifest = mutatedManifest with { ManifestSha256 = OrbitalSnapshot.ComputeManifestIdentity(mutatedManifest) };
        var parsed = OrbitalSnapshot.Parse(OrbitalContract.SerializeCanonical(mutatedManifest), validSnapshot.SerializePayload());
        Assert.IsFalse(parsed.IsValid);
        Assert.AreEqual("source-provenance", parsed.Reason);
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
