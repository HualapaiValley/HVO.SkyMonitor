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
    private const string FrozenPreIdentityPropertyJson = "{\"algorithms\":[{\"name\":\"cloud-transmission\",\"version\":\"v1\"}],\"calibration\":{\"blackLevel\":0,\"calibrationIdentity\":\"calibration\",\"maskIdentity\":\"mask\",\"processingProfileIdentity\":\"processing\",\"saturationLevel\":65535,\"sensorIdentity\":\"sensor\",\"whiteLevel\":65535},\"clearReference\":{\"artifactId\":\"10000000-0000-0000-0000-000000000002\",\"recipeIdentitySha256\":\"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\",\"role\":\"Calibrated\",\"variant\":\"clear-v1\"},\"confidenceMillionths\":750000,\"coverageMillionths\":500000,\"current\":{\"artifactId\":\"10000000-0000-0000-0000-000000000001\",\"recipeIdentitySha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"role\":\"Calibrated\",\"variant\":\"linear-v1\"},\"environment\":{\"inputIdentitySha256\":null,\"precipitationContentSha256\":null,\"precipitationDetected\":false,\"precipitationObservationId\":null,\"precipitationStatus\":\"Missing\",\"schemaVersion\":\"cloud-assessment-environment-v1\",\"solarRegime\":\"Night\"},\"grid\":{\"cloudyRegionCount\":1,\"cloudySampleCount\":4,\"columns\":2,\"rows\":1,\"transmissionThresholdMillionths\":750000,\"validRegionCount\":2,\"validSampleCount\":8},\"mask\":{\"bits\":\"Ag==\",\"encoding\":\"row-major-lsb-first\",\"height\":1,\"width\":2},\"quality\":\"Degraded\",\"reasonCodes\":[\"cloud.environment-missing\"],\"recipeIdentitySha256\":\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\",\"regions\":[{\"acceptedSampleCount\":4,\"column\":0,\"consideredSampleCount\":4,\"height\":2,\"isCloudy\":false,\"row\":0,\"saturatedSampleCount\":0,\"transmissionMillionths\":1000000,\"width\":2,\"x\":0,\"y\":0},{\"acceptedSampleCount\":4,\"column\":1,\"consideredSampleCount\":4,\"height\":2,\"isCloudy\":true,\"row\":0,\"saturatedSampleCount\":0,\"transmissionMillionths\":500000,\"width\":2,\"x\":2,\"y\":0}],\"schemaVersion\":\"cloud-assessment-v1\",\"status\":\"Quantified\"}";
    [TestMethod]
    public void QuantifiedAssessmentRoundTripsCanonicalMaskAndLineage()
    {
        var assessment = CreateValid();

        var json = CloudAssessmentJson.Serialize(assessment);
        Assert.AreEqual(FrozenPreIdentityPropertyJson, Encoding.UTF8.GetString(json));
        var parsed = CloudAssessmentJson.Parse(json);

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.IsNotNull(parsed.Assessment);
        Assert.AreEqual(500_000, parsed.Assessment.CoverageMillionths);
        Assert.AreEqual(CloudAssessmentMaskV1.RowMajorLsbFirst, parsed.Assessment.Mask!.Encoding);
        CollectionAssert.AreEqual(new byte[] { 0b0000_0010 }, parsed.Assessment.Mask.Bits.ToArray());
        CollectionAssert.AreEqual(json, CloudAssessmentJson.Serialize(parsed.Assessment));
        Assert.AreEqual((byte)'{', json[0]);
        StringAssert.Contains(Encoding.UTF8.GetString(json), "\"bits\":\"Ag==\"", StringComparison.Ordinal);
        Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(FrozenPreIdentityPropertyJson)),
            assessment.AssessmentIdentitySha256);
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
