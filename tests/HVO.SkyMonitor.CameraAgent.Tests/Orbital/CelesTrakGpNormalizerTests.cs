using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using static HVO.SkyMonitor.CameraAgent.Tests.Orbital.CelesTrakGpTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpNormalizerTests
{
    // Pinned identities of the valid fixture's candidate at RetrievedUtc with the default synthetic validators. A change
    // to the parser rules, normalizer rules, contract serialization or fixture bytes must change these deliberately.
    private const string ValidNormalizedPayloadSha256 = "F3BDD6215306A5188CBDF1D518B0EC51A7AD55336B15388864B48B49BDABB8C8";
    private const string ValidManifestSha256 = "0009A4DE70965F6C70F881576EADCB3E9D345B0BFCF4CAE0BC7453C3CCF5FE00";
    private const string ParserImplementationSha256 = "79FEEE50CCC7D0D5108E8040D95A8048332C5C9AD4A13B17B1B2CC2249A8BF7A";
    private const string NormalizerImplementationSha256 = "0D1E2F09E4F375287BC9353ECA0FBBE4BACED8782DF5E240DF564C5812A17583";

    private static readonly string[] ExpectedKnownExclusions =
    [
        "analyst-objects", "debris", "inactive-payloads", "missing-records", "supplemental-gp",
        "unattached-rocket-bodies", "unpublicized-objects"
    ];

    private static readonly string[] ExpectedObjectOrder = ["1", "123456", "25544", "43013", "48274", "5", "999999999"];

    [TestMethod]
    public void ValidFixtureBecomesOneSnapshotWithFullProvenance()
    {
        var raw = ReadFixture(ValidFixture);

        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw));

        Assert.IsNull(result.RejectionReasonCode, result.Detail);
        Assert.AreEqual("records=8 objects=7", result.Detail);
        var manifest = result.Snapshot!.Manifest;
        Assert.AreEqual(OrbitalContract.SchemaVersion, manifest.SchemaVersion);
        Assert.AreEqual(OrbitalContract.CelesTrakGpProfile, manifest.ProfileVersion);
        Assert.AreEqual(8, manifest.RecordCount);
        Assert.AreEqual(7, manifest.ObjectCount);
        Assert.AreEqual(new DateTimeOffset(1958, 3, 17, 12, 0, 0, TimeSpan.Zero), manifest.MinimumEpochUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), manifest.MaximumEpochUtc);
        Assert.AreEqual("celestrak:gp:GROUP=active", manifest.Coverage.RequestedPopulation);
        Assert.AreEqual(OrbitalPopulationCompleteness.CompleteForRequestedPopulation, manifest.Coverage.Completeness);
        CollectionAssert.AreEqual(ExpectedKnownExclusions, manifest.Coverage.KnownExclusions.ToArray());

        var source = manifest.Sources.Single();
        Assert.AreEqual("celestrak:gp:active", source.DatasetKey);
        Assert.AreEqual("celestrak", source.Provider);
        Assert.AreEqual(CelesTrakGpSource.UnderlyingLineage, source.UnderlyingLineage);
        Assert.AreEqual(CelesTrakGpSource.RequestUri, source.SourceUri);
        Assert.AreEqual(RetrievedUtc, source.RetrievedUtc);
        Assert.AreEqual(TimeSpan.Zero, source.RetrievedUtc.Offset);
        Assert.IsNull(source.ProviderGeneratedUtc);
        Assert.AreEqual("\"synthetic-etag-1\"", source.EntityTag);
        Assert.AreEqual("Mon, 19 Oct 2026 17:00:00 GMT", source.LastModified);
        Assert.AreEqual(Sha256(raw), source.RawSha256);
        Assert.AreEqual(raw.LongLength, source.RawLengthBytes);
        Assert.AreEqual(CelesTrakGpNormalizer.Parser, source.Parser);
        Assert.AreEqual(CelesTrakGpNormalizer.Normalizer, source.Normalizer);
        Assert.AreEqual(CelesTrakGpSource.TermsUri, source.TermsUri);
        Assert.AreEqual(CelesTrakGpSource.TermsReference, source.TermsReference);
    }

    [TestMethod]
    public void ValidFixtureNormalizedIdentitiesArePinned()
    {
        var snapshot = CelesTrakGpNormalizer.CreateCandidate(Retrieved(ReadFixture(ValidFixture))).Snapshot!;

        Assert.AreEqual(ValidNormalizedPayloadSha256, snapshot.Manifest.NormalizedPayloadSha256);
        Assert.AreEqual(ValidManifestSha256, snapshot.Manifest.ManifestSha256);
        Assert.AreEqual(Sha256(snapshot.SerializePayload()), snapshot.Manifest.NormalizedPayloadSha256);
    }

    [TestMethod]
    public void SoftwareIdentitiesArePinnedToTheirRuleDescriptors()
    {
        Assert.AreEqual(new OrbitalSoftwareIdentity("hvo-celestrak-gp-json-parser", "1", ParserImplementationSha256),
            CelesTrakGpNormalizer.Parser);
        Assert.AreEqual(new OrbitalSoftwareIdentity("hvo-celestrak-gp-normalizer", "1", NormalizerImplementationSha256),
            CelesTrakGpNormalizer.Normalizer);
        Assert.AreEqual(ParserImplementationSha256, Sha256(Encoding.UTF8.GetBytes(CelesTrakGpParser.RuleDescriptor)));
        Assert.AreEqual(NormalizerImplementationSha256, Sha256(Encoding.UTF8.GetBytes(CelesTrakGpNormalizer.RuleDescriptor)));
        foreach (var rule in CelesTrakGpParser.Rules)
        {
            Assert.Contains($"member={rule.Name};", CelesTrakGpParser.RuleDescriptor);
        }
        Assert.Contains(CelesTrakGpSource.TermsReference, CelesTrakGpNormalizer.RuleDescriptor);
    }

    [TestMethod]
    public void CandidateRoundTripsThroughTheSharedSnapshotParser()
    {
        var snapshot = CelesTrakGpNormalizer.CreateCandidate(Retrieved(ReadFixture(ValidFixture))).Snapshot!;

        var parsed = OrbitalSnapshot.Parse(snapshot.SerializeManifest(), snapshot.SerializePayload());

        Assert.IsTrue(parsed.IsValid, parsed.Reason);
        Assert.AreEqual(snapshot.Manifest.ManifestSha256, parsed.Snapshot!.Manifest.ManifestSha256);
        CollectionAssert.AreEqual(snapshot.SerializePayload(), parsed.Snapshot.SerializePayload());
    }

    [TestMethod]
    public void RawBoundEqualsTheSharedContractRawLengthBound()
    {
        var snapshot = CelesTrakGpNormalizer.CreateCandidate(Retrieved(ReadFixture(ValidFixture))).Snapshot!;
        var source = snapshot.Manifest.Sources.Single();

        var atBound = OrbitalSnapshot.Create(snapshot.Records,
            [source with { RawLengthBytes = CelesTrakGpSource.MaximumRawBytes }], snapshot.Manifest.Coverage);
        var overBound = Assert.ThrowsExactly<ArgumentException>(() => OrbitalSnapshot.Create(snapshot.Records,
            [source with { RawLengthBytes = CelesTrakGpSource.MaximumRawBytes + 1L }], snapshot.Manifest.Coverage));

        Assert.AreEqual(CelesTrakGpSource.MaximumRawBytes, atBound.Manifest.Sources.Single().RawLengthBytes);
        Assert.AreEqual("source-provenance", overBound.Message);
    }

    [TestMethod]
    public void CandidatesAreDeterministic()
    {
        var raw = ReadFixture(ValidFixture);

        var first = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw)).Snapshot!;
        var second = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw.ToArray())).Snapshot!;

        CollectionAssert.AreEqual(first.SerializeManifest(), second.SerializeManifest());
        CollectionAssert.AreEqual(first.SerializePayload(), second.SerializePayload());
    }

    [TestMethod]
    public void ValidFixtureRecordsMapEveryFieldWithoutInference()
    {
        var snapshot = CelesTrakGpNormalizer.CreateCandidate(Retrieved(ReadFixture(ValidFixture))).Snapshot!;

        var station = snapshot.RecordsFor(Norad("25544"));
        Assert.HasCount(2, station);
        var newest = station.Single(static r => r.EpochUtc == new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        Assert.AreEqual("celestrak:gp:active", newest.SourceDataset);
        Assert.IsNull(newest.SourceRecordId);
        Assert.AreEqual("HVO SYNTHETIC A", newest.ObjectName);
        Assert.AreEqual("1998-067A", newest.InternationalDesignator);
        Assert.IsNull(newest.SourceAssertedType);
        Assert.IsNull(newest.SourceAssertedStatus);
        Assert.IsNull(newest.ValidFromUtc);
        Assert.IsNull(newest.ValidToUtc);
        Assert.AreEqual(new OrbitalElementModel("EARTH", "TEME", "UTC", "SGP4", OrbitalContract.GpDerivativeConvention,
            OrbitalContract.CelesTrakGpProfile, CelesTrakGpNormalizer.Parser, CelesTrakGpNormalizer.Normalizer), newest.Model);
        Assert.AreEqual(new OrbitalMeanElements(15.50103472, 0.0006703, 51.6416, 247.4627, 130.536, 325.0288, 0.00034123,
            0.00016717, 0, 0, "U", 999, 47001), newest.Elements);
        Assert.AreEqual(newest.RecordSha256, OrbitalContract.ComputeRecordIdentity(newest with { RecordSha256 = string.Empty }));

        var minimum = snapshot.RecordsFor(Norad("1")).Single();
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), minimum.EpochUtc);
        Assert.AreEqual(1.2e-5, minimum.Elements.BStarInverseEarthRadii);
        Assert.AreEqual(2.5e-6, minimum.Elements.MeanMotionDot);
        Assert.AreEqual(1.5e-11, minimum.Elements.MeanMotionDdot);

        var maximum = snapshot.RecordsFor(Norad("999999999")).Single();
        Assert.AreEqual(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), maximum.EpochUtc);
        Assert.AreEqual(0L, maximum.Elements.RevolutionNumber);

        var full = snapshot.RecordsFor(Norad("123456")).Single();
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 8, 15, 30, 500, TimeSpan.Zero), full.EpochUtc);
        Assert.AreEqual(-1.1e-4, full.Elements.BStarInverseEarthRadii);
        Assert.AreEqual(-3.0e-7, full.Elements.MeanMotionDot);
        Assert.AreEqual(0.7123, full.Elements.Eccentricity);

        var omitted = snapshot.RecordsFor(Norad("43013")).Single();
        Assert.IsNull(omitted.ObjectName);
        Assert.IsNull(omitted.InternationalDesignator);
        Assert.IsNull(omitted.Elements.ElementSetNumber);
        Assert.IsNull(omitted.Elements.RevolutionNumber);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), omitted.EpochUtc);

        var blank = snapshot.RecordsFor(Norad("48274")).Single();
        Assert.IsNull(blank.ObjectName);
        Assert.IsNull(blank.InternationalDesignator);
        Assert.IsNull(blank.Elements.ElementSetNumber);
        Assert.AreEqual(0L, blank.Elements.RevolutionNumber);
        Assert.AreEqual(0d, blank.Elements.Eccentricity);
        Assert.AreEqual(359.9999, blank.Elements.MeanAnomalyDegrees);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), blank.EpochUtc);

        var old = snapshot.RecordsFor(Norad("5")).Single();
        Assert.AreEqual("HVO SYNTHETIC OLD EPOCH \u00C9", old.ObjectName);
        Assert.AreEqual(new DateTimeOffset(1958, 3, 17, 12, 0, 0, TimeSpan.Zero), old.EpochUtc);

        CollectionAssert.AreEqual(ExpectedObjectOrder, snapshot.ObjectIdentities.Select(static i => i.CatalogNumber).ToArray());
        Assert.IsTrue(snapshot.ObjectIdentities.All(static i => i.Authority == "norad"));
    }

    [TestMethod]
    [DataRow("invalid-unknown-member.json", "orbital.unknown-member", "record=1 member=DECAY_DATE")]
    [DataRow("invalid-duplicate-member.json", "orbital.duplicate-member", "record=1 member=MEAN_MOTION")]
    [DataRow("invalid-conflicting-ref-frame.json", "orbital.conflicting-metadata", "record=1 member=REF_FRAME")]
    [DataRow("invalid-norad-string.json", "orbital.member-type", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-norad-zero.json", "orbital.invalid-object-identity", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-norad-negative.json", "orbital.invalid-object-identity", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-norad-ten-digits.json", "orbital.invalid-object-identity", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-norad-fraction.json", "orbital.invalid-object-identity", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-norad-exponent.json", "orbital.invalid-object-identity", "record=1 member=NORAD_CAT_ID")]
    [DataRow("invalid-duplicate-record.json", "orbital.duplicate-record", "record=1 duplicate-of=0")]
    [DataRow("invalid-root-object.json", "orbital.json-root", "root-not-array")]
    public void InvalidFixtureRejectsTheWholeCandidate(string fixture, string reasonCode, string detail) =>
        AssertRejected(ReadFixture(fixture), reasonCode, detail);

    [TestMethod]
    [DataRow("EPOCH")]
    [DataRow("MEAN_MOTION")]
    [DataRow("ECCENTRICITY")]
    [DataRow("INCLINATION")]
    [DataRow("RA_OF_ASC_NODE")]
    [DataRow("ARG_OF_PERICENTER")]
    [DataRow("MEAN_ANOMALY")]
    [DataRow("EPHEMERIS_TYPE")]
    [DataRow("CLASSIFICATION_TYPE")]
    [DataRow("NORAD_CAT_ID")]
    [DataRow("BSTAR")]
    [DataRow("MEAN_MOTION_DOT")]
    [DataRow("MEAN_MOTION_DDOT")]
    public void MissingRequiredMemberRejects(string member) =>
        AssertRejected(Payload(Record(), RecordFor(60002, (member, null))), "orbital.missing-member",
            $"record=1 member={member}");

    [TestMethod]
    [DataRow("OBJECT_NAME")]
    [DataRow("OBJECT_ID")]
    [DataRow("ELEMENT_SET_NO")]
    [DataRow("REV_AT_EPOCH")]
    public void OptionalMembersMayBeOmitted(string member) => AssertAccepted(Payload(Record((member, null))));

    [TestMethod]
    [DataRow("MEAN_MOTION", "\"15.2\"")]
    [DataRow("ECCENTRICITY", "true")]
    [DataRow("INCLINATION", "null")]
    [DataRow("RA_OF_ASC_NODE", "\"NaN\"")]
    [DataRow("BSTAR", "[0]")]
    [DataRow("MEAN_MOTION_DOT", "{\"VALUE\":0}")]
    [DataRow("EPOCH", "1729350000")]
    [DataRow("EPOCH", "null")]
    [DataRow("OBJECT_NAME", "42")]
    [DataRow("OBJECT_ID", "false")]
    [DataRow("OBJECT_NAME", "[\"A\"]")]
    [DataRow("CLASSIFICATION_TYPE", "null")]
    [DataRow("EPHEMERIS_TYPE", "\"0\"")]
    [DataRow("NORAD_CAT_ID", "null")]
    [DataRow("ELEMENT_SET_NO", "\"999\"")]
    [DataRow("REV_AT_EPOCH", "true")]
    [DataRow("CENTER_NAME", "0")]
    [DataRow("REF_FRAME", "null")]
    [DataRow("CCSDS_OMM_VERS", "3.0")]
    [DataRow("COMMENT", "null")]
    [DataRow("ORIGINATOR", "[]")]
    public void MistypedMemberRejects(string member, string json) =>
        AssertRejected(Payload(Record((member, json))), "orbital.member-type", $"record=0 member={member}");

    [TestMethod]
    [DataRow("MEAN_MOTION", "1e400")]
    [DataRow("BSTAR", "-1e309")]
    [DataRow("MEAN_MOTION_DDOT", "1E+999")]
    [DataRow("ELEMENT_SET_NO", "-1")]
    [DataRow("ELEMENT_SET_NO", "1.0")]
    [DataRow("ELEMENT_SET_NO", "1e3")]
    [DataRow("REV_AT_EPOCH", "1234567890123456789")]
    [DataRow("REV_AT_EPOCH", "-0")]
    [DataRow("EPHEMERIS_TYPE", "0.0")]
    [DataRow("EPHEMERIS_TYPE", "-0")]
    [DataRow("EPHEMERIS_TYPE", "0e0")]
    public void NonFiniteOrNonCanonicalNumbersReject(string member, string json) =>
        AssertRejected(Payload(Record((member, json))), "orbital.invalid-number", $"record=0 member={member}");

    [TestMethod]
    [DataRow("2")]
    [DataRow("4")]
    [DataRow("1")]
    public void UnsupportedEphemerisTypeRejects(string json) =>
        AssertRejected(Payload(Record(("EPHEMERIS_TYPE", json))), "orbital.unsupported-ephemeris-type",
            "record=0 member=EPHEMERIS_TYPE");

    [TestMethod]
    [DataRow("\"C\"")]
    [DataRow("\"S\"")]
    [DataRow("\"u\"")]
    [DataRow("\"\"")]
    [DataRow("\"U \"")]
    public void UnsupportedClassificationRejects(string json) =>
        AssertRejected(Payload(Record(("CLASSIFICATION_TYPE", json))), "orbital.unsupported-classification",
            "record=0 member=CLASSIFICATION_TYPE");

    [TestMethod]
    [DataRow("CENTER_NAME", "\"MOON\"")]
    [DataRow("CENTER_NAME", "\"earth\"")]
    [DataRow("REF_FRAME", "\"ITRF\"")]
    [DataRow("REF_FRAME", "\"\"")]
    [DataRow("TIME_SYSTEM", "\"TAI\"")]
    [DataRow("TIME_SYSTEM", "\"UTC \"")]
    [DataRow("MEAN_ELEMENT_THEORY", "\"SGP4-XP\"")]
    [DataRow("MEAN_ELEMENT_THEORY", "\"DSST\"")]
    [DataRow("CCSDS_OMM_VERS", "\"1.0\"")]
    [DataRow("CCSDS_OMM_VERS", "\"3\"")]
    [DataRow("CCSDS_OMM_VERS", "\"3.1\"")]
    public void ConflictingMetadataRejects(string member, string json) =>
        AssertRejected(Payload(Record((member, json))), "orbital.conflicting-metadata", $"record=0 member={member}");

    [TestMethod]
    [DataRow("CENTER_NAME", "\"EARTH\"")]
    [DataRow("CENTER_NAME", "\"\\u0045ARTH\"")]
    [DataRow("REF_FRAME", "\"TEME\"")]
    [DataRow("TIME_SYSTEM", "\"UTC\"")]
    [DataRow("MEAN_ELEMENT_THEORY", "\"SGP4\"")]
    [DataRow("CCSDS_OMM_VERS", "\"2.0\"")]
    [DataRow("CCSDS_OMM_VERS", "\"3.0\"")]
    [DataRow("COMMENT", "\"GENERATED BY HVO SYNTHETIC\"")]
    [DataRow("ORIGINATOR", "\"HVO\"")]
    public void ConsistentOptionalMetadataIsAcceptedAndDoesNotChangeTheRecord(string member, string json)
    {
        var plain = AssertAccepted(Payload(Record()));
        var withMetadata = AssertAccepted(Payload(Record((member, json))));

        Assert.AreEqual(plain.Records.Single().RecordSha256, withMetadata.Records.Single().RecordSha256);
    }

    [TestMethod]
    [DataRow("OBJECT_NAME", "\"BAD\\u0001NAME\"")]
    [DataRow("OBJECT_NAME", "\"BAD\\nNAME\"")]
    [DataRow("OBJECT_NAME", "\"BAD\\u007FNAME\"")]
    [DataRow("OBJECT_ID", "\"\\u0085\"")]
    [DataRow("OBJECT_ID", "\"\\uD800\"")]
    [DataRow("OBJECT_NAME", "\"\\uDC00X\"")]
    [DataRow("COMMENT", "\"TAB\\tINSIDE\"")]
    [DataRow("ORIGINATOR", "\"\\u0000\"")]
    public void TextWithControlCharactersOrLoneSurrogatesRejects(string member, string json) =>
        AssertRejected(Payload(Record((member, json))), "orbital.invalid-text", $"record=0 member={member}");

    [TestMethod]
    public void TextIsBoundedAtTwoHundredFiftySixCharacters()
    {
        var name = new string('N', 256);
        var accepted = AssertAccepted(Payload(Record(("OBJECT_NAME", $"\"{name}\""))));
        Assert.AreEqual(name, accepted.Records.Single().ObjectName);

        var escaped = string.Concat(Enumerable.Repeat("\\u0041", 256));
        Assert.AreEqual(new string('A', 256), AssertAccepted(Payload(Record(("OBJECT_NAME", $"\"{escaped}\"")))).Records.Single().ObjectName);

        AssertRejected(Payload(Record(("OBJECT_NAME", $"\"{new string('N', 257)}\""))), "orbital.invalid-text",
            "record=0 member=OBJECT_NAME");
        AssertRejected(Payload(Record(("OBJECT_NAME", $"\"{escaped}\\u0041\""))), "orbital.invalid-text",
            "record=0 member=OBJECT_NAME");
        AssertRejected(Payload(Record(("COMMENT", $"\"{new string('C', 257)}\""))), "orbital.invalid-text",
            "record=0 member=COMMENT");
    }

    [TestMethod]
    [DataRow("2026-10-19 06:00:00")]
    [DataRow("2026-10-19T06:00:00+00:00")]
    [DataRow("2026-10-19T06:00:00-07:00")]
    [DataRow("2026-10-19T06:00:00.12345678")]
    [DataRow("2026-10-19T06:00:00.")]
    [DataRow("2026-10-19T06:00:00,5")]
    [DataRow("2026-10-19t06:00:00")]
    [DataRow("2026-10-19T06:00:00z")]
    [DataRow("2026-10-19T06:00:00ZZ")]
    [DataRow("2026-10-19T06:00:00.5Z ")]
    [DataRow(" 2026-10-19T06:00:00")]
    [DataRow("2026-10-19T06:00:60")]
    [DataRow("2026-10-19T24:00:00")]
    [DataRow("2026-10-19T06:60:00")]
    [DataRow("2026-02-29T00:00:00")]
    [DataRow("2026-13-01T00:00:00")]
    [DataRow("2026-00-01T00:00:00")]
    [DataRow("2026-10-00T00:00:00")]
    [DataRow("0000-01-01T00:00:00")]
    [DataRow("2026-10-19T06:00")]
    [DataRow("2026-10-19")]
    [DataRow("")]
    [DataRow("20261019T060000")]
    [DataRow("+2026-10-19T06:00:00")]
    [DataRow("2026-10-19T06:00:00.123456789012345678901234")]
    [DataRow("\\uFF12026-10-19T06:00:00")]
    public void EpochOutsideTheDocumentedGrammarRejects(string epoch) =>
        AssertRejected(Payload(Record(("EPOCH", $"\"{epoch}\""))), "orbital.invalid-epoch", "record=0 member=EPOCH");

    [TestMethod]
    [DataRow("2026-10-19T06:00:00", 0L)]
    [DataRow("2026-10-19T06:00:00Z", 0L)]
    [DataRow("2026-10-19T06:00:00.5", 5_000_000L)]
    [DataRow("2026-10-19T06:00:00.1234567Z", 1_234_567L)]
    [DataRow("2026-10-19T06:00:00.000001", 10L)]
    [DataRow("2026-10-19T06\\u003A00:00", 0L)]
    public void EpochInTheDocumentedGrammarIsUtc(string epoch, long fractionTicks)
    {
        var record = AssertAccepted(Payload(Record(("EPOCH", $"\"{epoch}\"")))).Records.Single();

        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.Zero).AddTicks(fractionTicks), record.EpochUtc);
        Assert.AreEqual(TimeSpan.Zero, record.EpochUtc.Offset);
    }

    [TestMethod]
    public void LeapDayIsAValidEpoch()
    {
        var record = AssertAccepted(Payload(Record(("EPOCH", "\"2024-02-29T23:59:59\"")))).Records.Single();
        Assert.AreEqual(new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero), record.EpochUtc);
    }

    [TestMethod]
    [DataRow("1957-10-04T00:00:00", true)]
    [DataRow("1957-10-03T23:59:59.9999999", false)]
    [DataRow("0001-01-01T00:00:00", false)]
    [DataRow("2026-11-18T17:28:18", true)]
    [DataRow("2026-11-18T17:28:18.0000001", false)]
    [DataRow("9999-12-31T23:59:59.9999999", false)]
    public void EpochMustFallBetweenSputnikAndThirtyDaysAfterRetrieval(string epoch, bool accepted)
    {
        var payload = Payload(Record(("EPOCH", $"\"{epoch}\"")));
        if (accepted)
        {
            AssertAccepted(payload);
        }
        else
        {
            AssertRejected(payload, "orbital.epoch-range", "record=0 member=EPOCH");
        }
    }

    [TestMethod]
    [DataRow("DECAY_DATE", "DECAY_DATE")]
    [DataRow("decay_date", "__________")]
    [DataRow("Object_Name", "O______N___")]
    [DataRow("MEAN MOTION", "MEAN_MOTION")]
    [DataRow("EPOCH ", "EPOCH_")]
    [DataRow("\\u0000X", "__0000X")]
    [DataRow("\\u00C9POCH", "__00C9POCH")]
    [DataRow("\u00C9POCH", "__POCH")]
    [DataRow("", "_")]
    [DataRow("A\\\"B", "A__B")]
    [DataRow("<SCRIPT>", "_SCRIPT_")]
    [DataRow("<script>", "________")]
    public void UnknownMemberRejectsWithASanitizedName(string name, string reported)
    {
        var record = Record().TrimEnd('}') + $",\"{name}\":1}}";
        AssertRejected(Payload(record), "orbital.unknown-member", $"record=0 member={reported}");
    }

    [TestMethod]
    public void ReportedUnknownMemberNameIsTruncatedToSixtyFourCharacters()
    {
        var name = new string('X', 200);
        var record = Record().TrimEnd('}') + $",\"{name}\":1}}";
        AssertRejected(Payload(record), "orbital.unknown-member", $"record=0 member={new string('X', 64)}");

        Assert.AreEqual(new string('_', 64), CelesTrakGpParser.SanitizeMemberName(Encoding.UTF8.GetBytes(new string('x', 65))));
    }

    [TestMethod]
    public void EscapedSpellingsOfKnownMembersAreThoseMembers()
    {
        var escaped = Record().Replace("\"EPOCH\"", "\"\\u0045POCH\"", StringComparison.Ordinal);
        Assert.AreEqual(AssertAccepted(Payload(Record())).Records.Single().RecordSha256,
            AssertAccepted(Payload(escaped)).Records.Single().RecordSha256);

        var duplicated = Record().TrimEnd('}') + ",\"\\u0045POCH\":\"2026-10-19T06:00:00\"}";
        AssertRejected(Payload(duplicated), "orbital.duplicate-member", "record=0 member=EPOCH");
    }

    [TestMethod]
    [DataRow("ECCENTRICITY", "1.0")]
    [DataRow("ECCENTRICITY", "1.5")]
    [DataRow("ECCENTRICITY", "-0.0001")]
    [DataRow("MEAN_MOTION", "0")]
    [DataRow("MEAN_MOTION", "-15.2")]
    [DataRow("INCLINATION", "180.5")]
    [DataRow("INCLINATION", "-0.1")]
    public void ElementsOutsideTheSharedContractRejectTheCandidate(string member, string json) =>
        AssertRejected(Payload(Record(), RecordFor(60002, (member, json))), "orbital.record-contract",
            "record=1 contract=element-range");

    [TestMethod]
    [DataRow("INCLINATION", "180")]
    [DataRow("INCLINATION", "0")]
    [DataRow("ECCENTRICITY", "0.9999999")]
    [DataRow("MEAN_MOTION", "0.0001")]
    [DataRow("MEAN_ANOMALY", "-90")]
    [DataRow("RA_OF_ASC_NODE", "720.5")]
    public void ElementsAtTheContractBoundsAreAccepted(string member, string json) =>
        AssertAccepted(Payload(Record((member, json))));

    [TestMethod]
    public void AnglesAndNegativeZeroNormalizeToOneRecordIdentity()
    {
        var canonical = Record(("RA_OF_ASC_NODE", "0"), ("ARG_OF_PERICENTER", "0"), ("MEAN_ANOMALY", "0.5"),
            ("BSTAR", "0"), ("MEAN_MOTION_DOT", "0"));
        var spelled = Record(("RA_OF_ASC_NODE", "360"), ("ARG_OF_PERICENTER", "-0.0"), ("MEAN_ANOMALY", "720.5"),
            ("BSTAR", "-0.0"), ("MEAN_MOTION_DOT", "-0"));

        var record = AssertAccepted(Payload(spelled)).Records.Single();
        Assert.AreEqual(0d, record.Elements.AscendingNodeDegrees);
        Assert.IsFalse(double.IsNegative(record.Elements.ArgumentOfPericenterDegrees));
        Assert.AreEqual(0.5, record.Elements.MeanAnomalyDegrees);
        Assert.IsFalse(double.IsNegative(record.Elements.BStarInverseEarthRadii));
        Assert.IsFalse(double.IsNegative(record.Elements.MeanMotionDot));
        Assert.AreEqual(AssertAccepted(Payload(canonical)).Records.Single().RecordSha256, record.RecordSha256);
        AssertRejected(Payload(canonical, spelled), "orbital.duplicate-record", "record=1 duplicate-of=0");
    }

    [TestMethod]
    public void DistinctRecordsForOneObjectAreAllKept()
    {
        var snapshot = AssertAccepted(Payload(Record(), Record(("EPOCH", "\"2026-10-18T06:00:00\"")),
            Record(("ELEMENT_SET_NO", "998"))));

        Assert.AreEqual(3, snapshot.Manifest.RecordCount);
        Assert.AreEqual(1, snapshot.Manifest.ObjectCount);
        Assert.HasCount(3, snapshot.RecordsFor(Norad("60001")));
    }

    [TestMethod]
    [DataRow("HVO SYNTHETIC DEB")]
    [DataRow("HVO SYNTHETIC R/B")]
    [DataRow("HVO SYNTHETIC [-]")]
    public void ObjectTypeAndStatusAreNeverInferredFromNames(string name)
    {
        var record = AssertAccepted(Payload(Record(("OBJECT_NAME", $"\"{name}\"")))).Records.Single();

        Assert.AreEqual(name, record.ObjectName);
        Assert.IsNull(record.SourceAssertedType);
        Assert.IsNull(record.SourceAssertedStatus);
    }

    [TestMethod]
    public void MemberOrderAndWhitespaceDoNotChangeTheNormalizedPayload()
    {
        var compact = AssertAccepted(Payload(Record(), RecordFor(60002)));
        var reordered = "{" + string.Join(',', Record().Trim('{', '}').Split(',').Reverse()) + "}";
        var spaced = Encoding.UTF8.GetBytes("\n[\r\n  " + RecordFor(60002).Replace(",", " ,\t", StringComparison.Ordinal) +
            " ,\n  " + reordered + "\n]\n");

        var other = AssertAccepted(spaced);

        Assert.AreEqual(compact.Manifest.NormalizedPayloadSha256, other.Manifest.NormalizedPayloadSha256);
        CollectionAssert.AreEqual(compact.SerializePayload(), other.SerializePayload());
        Assert.AreNotEqual(compact.Manifest.Sources.Single().RawSha256, other.Manifest.Sources.Single().RawSha256);
    }

    [TestMethod]
    [DataRow("bom")]
    [DataRow("block-comment")]
    [DataRow("line-comment")]
    [DataRow("trailing-comma-array")]
    [DataRow("trailing-comma-object")]
    [DataRow("trailing-array")]
    [DataRow("trailing-text")]
    [DataRow("unterminated-array")]
    [DataRow("unterminated-object")]
    [DataRow("unterminated-string")]
    [DataRow("nan")]
    [DataRow("infinity")]
    [DataRow("negative-infinity")]
    [DataRow("leading-zero")]
    [DataRow("single-quotes")]
    [DataRow("raw-tab-in-string")]
    [DataRow("missing-colon")]
    [DataRow("escaped-lone-surrogate-name")]
    [DataRow("invalid-utf8")]
    [DataRow("overlong-utf8")]
    [DataRow("utf16")]
    public void MalformedJsonRejectsAsSyntax(string shape)
    {
        var record = Record();
        var payload = shape switch
        {
            "bom" => [0xEF, 0xBB, 0xBF, .. Payload(record)],
            "block-comment" => Encoding.UTF8.GetBytes("[/* HVO */" + record + "]"),
            "line-comment" => Encoding.UTF8.GetBytes("// HVO\n[" + record + "]"),
            "trailing-comma-array" => Encoding.UTF8.GetBytes("[" + record + ",]"),
            "trailing-comma-object" => Encoding.UTF8.GetBytes("[" + record.TrimEnd('}') + ",}]"),
            "trailing-array" => Encoding.UTF8.GetBytes("[" + record + "][]"),
            "trailing-text" => Encoding.UTF8.GetBytes("[" + record + "] x"),
            "unterminated-array" => Encoding.UTF8.GetBytes("[" + record),
            "unterminated-object" => Encoding.UTF8.GetBytes("[" + record.TrimEnd('}')),
            "unterminated-string" => Encoding.UTF8.GetBytes("[{\"EPOCH\":\"2026"),
            "nan" => Payload(Record(("MEAN_MOTION", "NaN"))),
            "infinity" => Payload(Record(("BSTAR", "Infinity"))),
            "negative-infinity" => Payload(Record(("BSTAR", "-Infinity"))),
            "leading-zero" => Payload(Record(("REV_AT_EPOCH", "007"))),
            "single-quotes" => Payload(Record(("OBJECT_NAME", "'HVO'"))),
            "raw-tab-in-string" => Payload(Record(("OBJECT_NAME", "\"HVO\tSYNTHETIC\""))),
            "missing-colon" => Encoding.UTF8.GetBytes("[" + record.Replace("\"EPOCH\":", "\"EPOCH\" ", StringComparison.Ordinal) + "]"),
            "escaped-lone-surrogate-name" => Payload(record.TrimEnd('}') + ",\"\\uD800\":1}"),
            "invalid-utf8" => [.. Encoding.UTF8.GetBytes("[" + record.TrimEnd('}') + ",\"COMMENT\":\""), 0xFF, .. "\"}]"u8.ToArray()],
            "overlong-utf8" => [.. Encoding.UTF8.GetBytes("[" + record.TrimEnd('}') + ",\"COMMENT\":\""), 0xC0, 0xAF, .. "\"}]"u8.ToArray()],
            "utf16" => Encoding.Unicode.GetBytes("[" + record + "]"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(payload));

        Assert.IsNull(result.Snapshot);
        Assert.AreEqual("orbital.json-syntax", result.RejectionReasonCode, $"{shape}: {result.RejectionReasonCode} {result.Detail}");
        Assert.DoesNotContain("HVO", result.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TrailingWhitespaceIsNotContent() => AssertAccepted(Encoding.UTF8.GetBytes("[" + Record() + "]\r\n\t "));

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"records\":[]}")]
    [DataRow("\"[]\"")]
    [DataRow("42")]
    [DataRow("null")]
    [DataRow("true")]
    public void NonArrayRootRejects(string json) =>
        AssertRejected(Encoding.UTF8.GetBytes(json), "orbital.json-root", "root-not-array");

    [TestMethod]
    [DataRow("[]")]
    [DataRow("[ ]")]
    [DataRow("\n[\n]\n")]
    public void EmptyArrayRejects(string json) =>
        AssertRejected(Encoding.UTF8.GetBytes(json), "orbital.empty-payload", "records=0");

    [TestMethod]
    public void EmptyPayloadIsRejectedByTheParserAndTheNormalizer()
    {
        var parsed = CelesTrakGpParser.Parse([], RetrievedUtc);
        Assert.AreEqual("orbital.empty-payload", parsed.ReasonCode);
        Assert.AreEqual("raw-bytes=0", parsed.Detail);

        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved([]));
        Assert.AreEqual("orbital.raw-bound", result.RejectionReasonCode);
        Assert.AreEqual($"raw-bytes=0 limit={CelesTrakGpSource.MaximumRawBytes}", result.Detail);
    }

    [TestMethod]
    [DataRow("1", 0, "Number")]
    [DataRow("null", 0, "Null")]
    [DataRow("[]", 0, "StartArray")]
    [DataRow("\"record\"", 0, "String")]
    [DataRow("RECORD,true", 1, "True")]
    public void NonObjectRecordRejects(string elements, int index, string token)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var json = "[" + elements.Replace("RECORD", Record(), StringComparison.Ordinal) + "]";
        AssertRejected(Encoding.UTF8.GetBytes(json), "orbital.record-shape", $"record={index} token={token}");
    }

    [TestMethod]
    public void MoreThanOneHundredThousandRecordsRejectBeforeContractWork()
    {
        var payload = RepeatedRecords(OrbitalContract.MaximumRecords + 1);
        Assert.IsLessThanOrEqualTo(CelesTrakGpSource.MaximumRawBytes, payload.Length);

        AssertRejected(payload, "orbital.record-bound", "records>100000");
    }

    [TestMethod]
    public void ParserAcceptsExactlyOneHundredThousandRecords()
    {
        var parsed = CelesTrakGpParser.Parse(RepeatedRecords(OrbitalContract.MaximumRecords), RetrievedUtc);

        Assert.IsNull(parsed.ReasonCode, parsed.Detail);
        Assert.HasCount(OrbitalContract.MaximumRecords, parsed.ElementSets!);
        Assert.AreEqual("records=100000", parsed.Detail);
        Assert.AreEqual(OrbitalContract.MaximumRecords - 1, parsed.ElementSets![^1].Index);
    }

    [TestMethod]
    public void RawOverTheSharedSourceBoundRejects()
    {
        var raw = new byte[CelesTrakGpSource.MaximumRawBytes + 1];
        var retrieved = new OrbitalFetchResult(OrbitalFetchDisposition.Retrieved, 200, RetrievedUtc, RetrievedUtc, raw,
            new string('0', 64), raw.Length, null, null, null, "orbital.retrieved", "status=200");

        var result = CelesTrakGpNormalizer.CreateCandidate(retrieved);

        Assert.AreEqual("orbital.raw-bound", result.RejectionReasonCode);
        Assert.AreEqual($"raw-bytes={raw.Length} limit={CelesTrakGpSource.MaximumRawBytes}", result.Detail);
    }

    [TestMethod]
    [DataRow("lowercase")]
    [DataRow("other")]
    [DataRow("null")]
    [DataRow("truncated")]
    public void RawBytesThatDoNotMatchTheirHashReject(string shape)
    {
        var raw = ReadFixture(ValidFixture);
        var hash = Sha256(raw);
        var claimed = shape switch
        {
            "lowercase" => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)),
            "other" => Sha256([.. raw, (byte)' ']),
            "null" => null,
            "truncated" => hash[..63],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw) with { RawSha256 = claimed });

        Assert.IsNull(result.Snapshot);
        Assert.AreEqual("orbital.raw-integrity", result.RejectionReasonCode);
        Assert.AreEqual("raw-sha256-mismatch", result.Detail);
    }

    [TestMethod]
    public void OnlyARetrievedResultWithBytesCanBecomeACandidate()
    {
        var retrieved = Retrieved(ReadFixture(ValidFixture));

        Assert.ThrowsExactly<ArgumentNullException>(() => CelesTrakGpNormalizer.CreateCandidate(null!));
        foreach (var disposition in Enum.GetValues<OrbitalFetchDisposition>().Where(static d => d != OrbitalFetchDisposition.Retrieved))
        {
            var exception = Assert.ThrowsExactly<ArgumentException>(
                () => CelesTrakGpNormalizer.CreateCandidate(retrieved with { Disposition = disposition }));
            Assert.AreEqual("retrieved", exception.ParamName);
        }
        Assert.AreEqual("retrieved", Assert.ThrowsExactly<ArgumentException>(
            () => CelesTrakGpNormalizer.CreateCandidate(retrieved with { RawBytes = null })).ParamName);
    }

    [TestMethod]
    public void ValidatorsThatTheContractCannotHoldAreDroppedNotRejected()
    {
        var raw = ReadFixture(ValidFixture);

        var nonAscii = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw, entityTag: "\"caf\u00E9\"",
            lastModified: "Mon,\u000B19 Oct 2026"));
        var oversized = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw, entityTag: new string('e', 1025),
            lastModified: new string('m', 129)));
        var absent = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw, entityTag: null, lastModified: null));

        foreach (var result in new[] { nonAscii, oversized, absent })
        {
            Assert.IsNull(result.RejectionReasonCode, result.Detail);
            var source = result.Snapshot!.Manifest.Sources.Single();
            Assert.IsNull(source.EntityTag);
            Assert.IsNull(source.LastModified);
        }
        Assert.AreEqual(absent.Snapshot!.Manifest.ManifestSha256, nonAscii.Snapshot!.Manifest.ManifestSha256);
    }

    [TestMethod]
    public void RetrievalTimeIsStoredAsUtcWhateverTheOffset()
    {
        var raw = ReadFixture(ValidFixture);

        var local = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw, RetrievedUtc.ToOffset(TimeSpan.FromHours(-7)))).Snapshot!;
        var utc = CelesTrakGpNormalizer.CreateCandidate(Retrieved(raw)).Snapshot!;

        Assert.AreEqual(TimeSpan.Zero, local.Manifest.Sources.Single().RetrievedUtc.Offset);
        Assert.AreEqual(utc.Manifest.ManifestSha256, local.Manifest.ManifestSha256);
    }

    [TestMethod]
    public void EpochRangeIsMeasuredFromTheRetrievalTime()
    {
        var payload = Payload(Record(("EPOCH", "\"2026-10-19T06:00:00\"")));

        AssertRejected(payload, "orbital.epoch-range", "record=0 member=EPOCH",
            completedUtc: new DateTimeOffset(2026, 9, 19, 5, 59, 59, TimeSpan.Zero));
        AssertAccepted(payload, completedUtc: new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
    }

    [TestMethod]
    [DataRow("element-range", "element-range")]
    [DataRow("record-source-binding", "record-source-binding")]
    [DataRow("Value cannot be null. (Parameter 'x')", "unrecognized")]
    [DataRow("UPPER", "unrecognized")]
    [DataRow("", "unrecognized")]
    [DataRow(null, "unrecognized")]
    public void ContractReasonsAreReportedOnlyAsShortKebabCodes(string? reason, string expected) =>
        Assert.AreEqual(expected, CelesTrakGpNormalizer.SanitizeContractReason(reason));

    [TestMethod]
    public void OverlongContractReasonIsUnrecognized() =>
        Assert.AreEqual("unrecognized", CelesTrakGpNormalizer.SanitizeContractReason(new string('a', 65)));

    [TestMethod]
    public void RejectionDetailNeverEchoesRecordText()
    {
        var payloads = new[]
        {
            Payload(Record(("OBJECT_NAME", "\"SECRET-TEXT\\u0001\""))),
            Payload(Record(("SECRET_TEXT", "\"SECRET-TEXT\""))),
            Payload(Record(("EPOCH", "\"SECRET-TEXT\""))),
            Payload(Record(("CENTER_NAME", "\"SECRET-TEXT\""))),
            Payload(Record(("CLASSIFICATION_TYPE", "\"SECRET-TEXT\"")))
        };

        foreach (var payload in payloads)
        {
            var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(payload));
            Assert.IsNotNull(result.RejectionReasonCode);
            Assert.DoesNotContain("SECRET-TEXT", result.Detail, StringComparison.Ordinal);
        }
    }

    private static OrbitalObjectIdentity Norad(string catalogNumber) => new("norad", catalogNumber);

    private static OrbitalSnapshot AssertAccepted(byte[] payload, DateTimeOffset? completedUtc = null)
    {
        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(payload, completedUtc));
        Assert.IsNull(result.RejectionReasonCode, $"{result.RejectionReasonCode} {result.Detail}");
        Assert.IsNotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static void AssertRejected(byte[] payload, string reasonCode, string detail, DateTimeOffset? completedUtc = null)
    {
        var result = CelesTrakGpNormalizer.CreateCandidate(Retrieved(payload, completedUtc));
        Assert.IsNull(result.Snapshot);
        Assert.AreEqual(reasonCode, result.RejectionReasonCode, result.Detail);
        Assert.AreEqual(detail, result.Detail);
    }

    private static byte[] RepeatedRecords(int count)
    {
        var record = Encoding.UTF8.GetBytes(Record(("OBJECT_NAME", null), ("OBJECT_ID", null), ("ELEMENT_SET_NO", null),
            ("REV_AT_EPOCH", null)));
        var payload = new byte[2 + (count * (record.Length + 1)) - 1];
        payload[0] = (byte)'[';
        var position = 1;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                payload[position++] = (byte)',';
            }
            record.CopyTo(payload, position);
            position += record.Length;
        }
        payload[position] = (byte)']';
        return payload;
    }
}
