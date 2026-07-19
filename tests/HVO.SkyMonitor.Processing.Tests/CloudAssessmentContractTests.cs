using System.Text;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CloudAssessmentContractTests
{
    [TestMethod]
    public void QuantifiedAssessmentRoundTripsCanonicalMaskAndLineage()
    {
        var assessment = CreateValid();

        var json = CloudAssessmentJson.Serialize(assessment);
        var parsed = CloudAssessmentJson.Parse(json);

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.IsNotNull(parsed.Assessment);
        Assert.AreEqual(500_000, parsed.Assessment.CoverageMillionths);
        Assert.AreEqual(CloudAssessmentMaskV1.RowMajorLsbFirst, parsed.Assessment.Mask!.Encoding);
        CollectionAssert.AreEqual(new byte[] { 0b0000_0010 }, parsed.Assessment.Mask.Bits.ToArray());
        CollectionAssert.AreEqual(json, CloudAssessmentJson.Serialize(parsed.Assessment));
        Assert.AreEqual((byte)'{', json[0]);
        StringAssert.Contains(Encoding.UTF8.GetString(json), "\"bits\":\"Ag==\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public void MisleadingCoverageAndMaskBitsAreRejected()
    {
        var quantifiedWithoutCoverage = CreateValid() with { CoverageMillionths = null };
        Assert.IsFalse(CloudAssessmentJson.Validate(quantifiedWithoutCoverage).IsValid);

        var inconsistentCoverage = CreateValid() with { CoverageMillionths = 400_000 };
        Assert.IsFalse(CloudAssessmentJson.Validate(inconsistentCoverage).IsValid);

        var invalidMask = CreateValid() with
        {
            Mask = new CloudAssessmentMaskV1(
                CloudAssessmentMaskV1.RowMajorLsbFirst,
                2,
                1,
                new byte[] { 0b0000_0001 })
        };
        Assert.IsFalse(CloudAssessmentJson.Validate(invalidMask).IsValid);

        var insufficientWithZero = CreateValid() with
        {
            Status = CloudAssessmentStatus.InsufficientEvidence,
            Quality = CloudAssessmentQuality.Unusable,
            CoverageMillionths = 0,
            ClearReference = null
        };
        Assert.IsFalse(CloudAssessmentJson.Validate(insufficientWithZero).IsValid);
    }

    [TestMethod]
    public void UnknownDuplicateAndUnsupportedContractsAreRejected()
    {
        var json = Encoding.UTF8.GetString(CloudAssessmentJson.Serialize(CreateValid()));
        var unknown = Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":true,"));
        Assert.IsFalse(CloudAssessmentJson.Parse(unknown).Validation.IsValid);

        var duplicate = Encoding.UTF8.GetBytes(json.Insert(1, "\"schemaVersion\":\"cloud-assessment-v1\","));
        Assert.IsFalse(CloudAssessmentJson.Parse(duplicate).Validation.IsValid);

        var unsupported = Encoding.UTF8.GetBytes(json.Replace(
            "cloud-assessment-v1",
            "cloud-assessment-v2",
            StringComparison.Ordinal));
        Assert.IsFalse(CloudAssessmentJson.Parse(unsupported).Validation.IsValid);
    }

    private static CloudAssessmentV1 CreateValid()
    {
        var current = new CloudAssessmentSourceV1(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            FrameArtifactRole.Calibrated,
            "linear-v1",
            new string('A', 64));
        var reference = new CloudAssessmentSourceV1(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Calibrated,
            "clear-v1",
            new string('B', 64));
        var regions = new CloudAssessmentRegionV1[]
        {
            new(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false),
            new(1, 0, 2, 0, 2, 2, 4, 4, 0, 500_000, true)
        };
        return new CloudAssessmentV1(
            CloudAssessmentV1.CurrentSchemaVersion,
            CloudAssessmentStatus.Quantified,
            CloudAssessmentQuality.Degraded,
            [CloudAssessmentReasonCodes.EnvironmentMissing],
            500_000,
            750_000,
            new CloudAssessmentGridV1(2, 1, 750_000, 2, 1, 8, 4),
            regions,
            new CloudAssessmentMaskV1(
                CloudAssessmentMaskV1.RowMajorLsbFirst,
                2,
                1,
                new byte[] { 0b0000_0010 }),
            current,
            reference,
            new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue, "calibration", "mask", "sensor", "processing"),
            new CloudAssessmentEnvironmentV1(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                CaptureSolarRegime.Night,
                EnvironmentalObservationMatchStatus.Missing,
                null,
                null,
                false),
            new string('C', 64),
            [new ProcessingAlgorithmIdentity("cloud-transmission", "v1")]);
    }
}
