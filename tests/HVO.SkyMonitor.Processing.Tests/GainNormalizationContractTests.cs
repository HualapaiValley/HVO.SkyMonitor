using System.Diagnostics.CodeAnalysis;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class GainNormalizationContractTests
{
    private const string ApprovedPolicyJson =
        "{\"calibrationAvailability\":\"strict-exclusion-v1\"," +
        "\"coefficientRepresentation\":\"electrons-per-adu-fixed-e12-v1\"," +
        "\"combinationSpace\":\"calibrated-electrons-v1\"," +
        "\"maximumCoefficientRatioMilli\":4000," +
        "\"outputScale\":\"fixed-reference-gain-v1\"," +
        "\"permittedResponseProvenance\":[\"virtual-simulation-truth\"]," +
        "\"referenceGainControl\":0," +
        "\"schemaVersion\":\"hvo-gain-normalization-policy-v1\"," +
        "\"weighting\":\"equal-valid-sample-weights-v1\"}";

    private const string ApprovedPolicySha256 = "62A9E5130D9844B5EE5DE16DD6C1F96F30BDE832AC07ABC5938920D0E72E65B2";

    [TestMethod]
    public void ApprovedPolicyCarriesTheOwnerApprovedChoices()
    {
        var policy = GainNormalizationPolicyV1.Approved;

        Assert.IsTrue(GainNormalizationContract.Validate(policy).IsValid);
        Assert.AreEqual(GainNormalizationPolicyV1.CalibratedElectronsV1, policy.CombinationSpace);
        Assert.AreEqual(GainNormalizationPolicyV1.FixedReferenceGainV1, policy.OutputScale);
        Assert.AreEqual(0d, policy.ReferenceGainControl);
        Assert.AreEqual(4000, policy.MaximumCoefficientRatioMilli);
        Assert.AreEqual(GainNormalizationPolicyV1.EqualValidSampleWeightsV1, policy.Weighting);
        Assert.AreEqual(GainNormalizationPolicyV1.StrictExclusionV1, policy.CalibrationAvailability);
        CollectionAssert.AreEqual(
            new[] { GainResponseProvenance.VirtualSimulationTruth },
            policy.PermittedResponseProvenance.ToArray());
        Assert.AreEqual(ElectronsPerAduFixedPoint.RepresentationVersion, policy.CoefficientRepresentation);
    }

    [TestMethod]
    public void ApprovedPolicySerializesToPinnedCanonicalBytesAndIdentity()
    {
        var bytes = GainNormalizationContractJson.Serialize(GainNormalizationPolicyV1.Approved);

        Assert.AreEqual(ApprovedPolicyJson, Encoding.UTF8.GetString(bytes));
        Assert.AreEqual(ApprovedPolicySha256, GainNormalizationContractJson.ComputeIdentitySha256(GainNormalizationPolicyV1.Approved));
        var negativeZero = GainNormalizationPolicyV1.Approved with { ReferenceGainControl = -0.0 };
        CollectionAssert.AreEqual(bytes, GainNormalizationContractJson.Serialize(negativeZero));
    }

    [TestMethod]
    public void PolicyParseAcceptsOnlyCanonicalBytes()
    {
        var parsed = GainNormalizationContractJson.ParsePolicy(Encoding.UTF8.GetBytes(ApprovedPolicyJson));
        Assert.IsTrue(parsed.Validation.IsValid);
        CollectionAssert.AreEqual(
            GainNormalizationContractJson.Serialize(GainNormalizationPolicyV1.Approved),
            GainNormalizationContractJson.Serialize(parsed.Value!));

        AssertPolicyRejected(ApprovedPolicyJson.Replace(",", ", ", StringComparison.Ordinal), GainNormalizationReasonCodes.NonCanonicalJson);
        AssertPolicyRejected(
            ApprovedPolicyJson.Replace("\"referenceGainControl\":0", "\"referenceGainControl\":0.0", StringComparison.Ordinal),
            GainNormalizationReasonCodes.NonCanonicalJson);
        AssertPolicyRejected(
            ApprovedPolicyJson.Replace("{", "{\"extra\":1,", StringComparison.Ordinal),
            GainNormalizationReasonCodes.NonCanonicalJson);
        AssertPolicyRejected(
            ApprovedPolicyJson.Replace("4000", "4001", StringComparison.Ordinal),
            GainNormalizationReasonCodes.InvalidContract);
        AssertPolicyRejected(
            ApprovedPolicyJson.Replace("policy-v1", "policy-v2", StringComparison.Ordinal),
            GainNormalizationReasonCodes.UnsupportedSchema);
        AssertPolicyRejected(
            ApprovedPolicyJson.Replace("\"weighting\":\"equal-valid-sample-weights-v1\"", "\"weighting\":null", StringComparison.Ordinal),
            GainNormalizationReasonCodes.InvalidContract);
        AssertPolicyRejected("[]", GainNormalizationReasonCodes.InvalidContract);
        AssertPolicyRejected("{", GainNormalizationReasonCodes.InvalidContract);
        AssertPolicyRejected(new string(' ', GainNormalizationContractJson.MaximumContractBytes + 1), GainNormalizationReasonCodes.InvalidContract);
    }

    [TestMethod]
    public void PolicyValidationRejectsChoicesOutsideTheApprovedPolicy()
    {
        var approved = GainNormalizationPolicyV1.Approved;
        GainNormalizationPolicyV1[] rejected =
        [
            approved with { SchemaVersion = "hvo-gain-normalization-policy-v2" },
            approved with { CombinationSpace = "raw-adu-v1" },
            approved with { OutputScale = "newest-frame-gain-v1" },
            approved with { ReferenceGainControl = double.NaN },
            approved with { MaximumCoefficientRatioMilli = 4001 },
            approved with { MaximumCoefficientRatioMilli = 999 },
            approved with { Weighting = "inverse-variance-v1" },
            approved with { CalibrationAvailability = "nearest-gain-v1" },
            approved with { PermittedResponseProvenance = [] },
            approved with { PermittedResponseProvenance = ["physical-measured-v1"] },
            approved with
            {
                PermittedResponseProvenance =
                    [GainResponseProvenance.VirtualSimulationTruth, GainResponseProvenance.VirtualSimulationTruth]
            },
            approved with { CoefficientRepresentation = "double-v1" }
        ];

        foreach (var policy in rejected)
        {
            Assert.IsFalse(GainNormalizationContract.Validate(policy).IsValid, policy.ToString());
            Assert.ThrowsExactly<ArgumentException>(() => GainNormalizationContractJson.Serialize(policy));
        }
        Assert.IsTrue(GainNormalizationContract.Validate(approved with { MaximumCoefficientRatioMilli = 1000 }).IsValid);
    }

    [TestMethod]
    [DataRow(0.0, 7_900_000_000_000L)]
    [DataRow(100.0, 2_498_199_351_533L)]
    [DataRow(120.0, 1_984_390_280_893L)]
    [DataRow(121.0, 1_961_675_153_184L)]
    [DataRow(200.0, 790_000_000_000L)]
    [DataRow(400.0, 79_000_000_000L)]
    public void VirtualAsi174MmResolvesHandComputedCoefficients(double gain, long expected)
    {
        // k(g) = 7.9 / 10^(g / 200) electrons per ADU, rounded to 10^-12.
        var response = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null);

        var coefficient = response.ResolveCoefficient(gain).Coefficient!;

        Assert.AreEqual(expected, coefficient.ElectronsPerAduE12);
        Assert.AreEqual(gain, coefficient.GainControl);
        Assert.AreEqual(GainConversionRegimes.FixedConversionGain, coefficient.ConversionRegime);
        Assert.AreEqual(response.IdentitySha256, coefficient.ResponseIdentitySha256);
    }

    [TestMethod]
    public void VirtualAsi174MmUsesTheModelThatRenderedTheFrame()
    {
        var sensor = Asi174Sensor();
        var response = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, sensor, null);
        var descriptor = response.Descriptor;

        Assert.AreEqual(GainResponseProvenance.VirtualSimulationTruth, descriptor.Provenance);
        Assert.AreEqual(Asi174MmSensorModel.Version, descriptor.ResponseModelVersion);
        Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(sensor), descriptor.SensorProfileSha256);
        Assert.AreEqual(0d, descriptor.MinimumGainControl);
        Assert.AreEqual(400d, descriptor.MaximumGainControl);
        Assert.IsNull(descriptor.HighConversionGainControl);
        Assert.AreEqual(12, descriptor.NativeSampleDepthBits);
        Assert.AreEqual(16, descriptor.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, descriptor.StoredCodeTransform);
        foreach (var gain in new[] { 0d, 37.5d, 150d, 400d })
        {
            Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(Asi174MmSensorModel.Resolve(gain).ElectronsPerAdu, out var rendered));
            Assert.AreEqual(rendered, response.ResolveCoefficient(gain).Coefficient!.ElectronsPerAduE12);
        }
        Assert.IsTrue(GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualAsi174MmModel,
            sensor,
            Mono16Readout(12, FrameStoredCodeTransform.RightAlignedV1)).IsAvailable);
    }

    [TestMethod]
    [DataRow(-1.0)]
    [DataRow(400.5)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void VirtualAsi174MmRejectsGainsOutsideTheValidityRange(double gain)
    {
        var response = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null);

        var result = response.ResolveCoefficient(gain);

        Assert.IsFalse(result.IsAvailable);
        Assert.AreEqual(GainNormalizationReasonCodes.GainOutOfRange, result.ReasonCode);
    }

    [TestMethod]
    [DataRow(0.0, 228_989_517_052L)]
    [DataRow(120.0, 57_519_566_084L)]
    [DataRow(121.0, 56_861_145_056L)]
    [DataRow(400.0, 2_289_895_171L)]
    public void ConfiguredAsi178McAppliesTheDeclaredFullRangeMapping(double gain, long expected)
    {
        // Native k(g) = 0.916 / 10^(g / 200) per 14-bit code; one full-range 16-bit stored code carries
        // 16383 / 65535 of a native code, so k(0) = 0.916 × 16383 / 65535 = 0.228989517052 per stored code.
        var (sensor, readout) = Asi178McProfile();
        var response = ResolveAvailable(GainResponseSourceKind.VirtualConfiguredResponse, sensor, readout);

        var coefficient = response.ResolveCoefficient(gain).Coefficient!;

        Assert.AreEqual(expected, coefficient.ElectronsPerAduE12);
        Assert.AreEqual("zwo-asi178mc-14bit-raw16-v2", response.Descriptor.ResponseModelVersion);
        Assert.AreEqual(14, response.Descriptor.NativeSampleDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.FullRangeScaledV1, response.Descriptor.StoredCodeTransform);
        Assert.AreEqual(GainConversionRegimes.FixedConversionGain, coefficient.ConversionRegime);
    }

    [TestMethod]
    public void ConfiguredAsi676KeepsLowAndHighConversionGainApart()
    {
        var (sensor, readout) = Asi676McProfile();
        var response = ResolveAvailable(GainResponseSourceKind.VirtualConfiguredResponse, sensor, readout);

        var zero = response.ResolveCoefficient(0).Coefficient!;
        var mid = response.ResolveCoefficient(100).Coefficient!;
        var low = response.ResolveCoefficient(179).Coefficient!;
        var high = response.ResolveCoefficient(180).Coefficient!;

        Assert.AreEqual(2_576_312_576_313L, zero.ElectronsPerAduE12);
        Assert.AreEqual(328_094_200_360L, low.ElectronsPerAduE12);
        Assert.AreEqual(324_338_537_104L, high.ElectronsPerAduE12);
        Assert.AreEqual(GainConversionRegimes.LowConversionGain, low.ConversionRegime);
        Assert.AreEqual(GainConversionRegimes.HighConversionGain, high.ConversionRegime);
        Assert.AreEqual(180d, response.Descriptor.HighConversionGainControl);
        Assert.AreEqual(GainNormalizationReasonCodes.GainOutOfRange, response.ResolveCoefficient(181).ReasonCode);
        Assert.IsNull(GainNormalizationContract.EvaluateCoefficientBound(GainNormalizationPolicyV1.Approved, [mid, low]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.CoefficientRatioExceeded,
            GainNormalizationContract.EvaluateCoefficientBound(GainNormalizationPolicyV1.Approved, [zero, low]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.MixedConversionRegime,
            GainNormalizationContract.EvaluateCoefficientBound(GainNormalizationPolicyV1.Approved, [low, high]));
    }

    [TestMethod]
    public void CoefficientBoundComparesCoefficientsAndAcceptsGainZero()
    {
        // k(0) / k(120) = 10^0.6 ≈ 3.981 is inside 4:1; k(0) / k(121) = 10^0.605 ≈ 4.027 is not.
        var policy = GainNormalizationPolicyV1.Approved;
        var response = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null);
        var gain0 = response.ResolveCoefficient(0).Coefficient!;
        var gain60 = response.ResolveCoefficient(60).Coefficient!;
        var gain120 = response.ResolveCoefficient(120).Coefficient!;
        var gain121 = response.ResolveCoefficient(121).Coefficient!;

        Assert.IsNull(GainNormalizationContract.EvaluateCoefficientBound(policy, [gain0]));
        Assert.IsNull(GainNormalizationContract.EvaluateCoefficientBound(policy, [gain120, gain0, gain60]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.CoefficientRatioExceeded,
            GainNormalizationContract.EvaluateCoefficientBound(policy, [gain0, gain60, gain121]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.CoefficientRatioExceeded,
            GainNormalizationContract.EvaluateCoefficientBound(policy with { MaximumCoefficientRatioMilli = 2000 }, [gain0, gain120]));
    }

    [TestMethod]
    public void CoefficientBoundDesignNoteCoefficientsAndTheExactLimit()
    {
        var policy = GainNormalizationPolicyV1.Approved;
        var identity = new string('A', 64);
        var one = ElectronsPerAduFixedPoint.Scale;

        Assert.IsNull(GainNormalizationContract.EvaluateCoefficientBound(
            policy,
            [Coefficient(identity, 2 * one), Coefficient(identity, one)]));
        Assert.IsNull(GainNormalizationContract.EvaluateCoefficientBound(
            policy,
            [Coefficient(identity, 4 * one), Coefficient(identity, one)]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.CoefficientRatioExceeded,
            GainNormalizationContract.EvaluateCoefficientBound(
                policy,
                [Coefficient(identity, (4 * one) + 1), Coefficient(identity, one)]));
    }

    [TestMethod]
    public void CoefficientBoundRejectsMixedResponsesInvalidCoefficientsAndBadArguments()
    {
        var policy = GainNormalizationPolicyV1.Approved;
        var one = ElectronsPerAduFixedPoint.Scale;

        Assert.AreEqual(
            GainNormalizationReasonCodes.MixedResponse,
            GainNormalizationContract.EvaluateCoefficientBound(
                policy,
                [Coefficient(new string('A', 64), one), Coefficient(new string('B', 64), one)]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.InvalidCoefficient,
            GainNormalizationContract.EvaluateCoefficientBound(policy, [Coefficient(new string('A', 64), 0)]));
        Assert.AreEqual(
            GainNormalizationReasonCodes.InvalidCoefficient,
            GainNormalizationContract.EvaluateCoefficientBound(
                policy,
                [Coefficient(new string('A', 64), ElectronsPerAduFixedPoint.MaximumScaledValue + 1)]));
        Assert.ThrowsExactly<ArgumentException>(() => GainNormalizationContract.EvaluateCoefficientBound(policy, []));
        Assert.ThrowsExactly<ArgumentException>(() => GainNormalizationContract.EvaluateCoefficientBound(
            policy with { MaximumCoefficientRatioMilli = 8000 },
            [Coefficient(new string('A', 64), one)]));
    }

    [TestMethod]
    public void ResolveReferenceUsesTheFixedPolicyGain()
    {
        var policy = GainNormalizationPolicyV1.Approved;
        var asi174 = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null);
        var (asi178Sensor, asi178Readout) = Asi178McProfile();
        var asi178 = ResolveAvailable(GainResponseSourceKind.VirtualConfiguredResponse, asi178Sensor, asi178Readout);

        var asi174Reference = GainNormalizationContract.ResolveReference(policy, asi174).Coefficient!;
        var asi178Reference = GainNormalizationContract.ResolveReference(policy, asi178).Coefficient!;
        var outside = GainNormalizationContract.ResolveReference(policy with { ReferenceGainControl = 500 }, asi174);

        Assert.AreEqual(0d, asi174Reference.GainControl);
        Assert.AreEqual(7_900_000_000_000L, asi174Reference.ElectronsPerAduE12);
        Assert.AreEqual(228_989_517_052L, asi178Reference.ElectronsPerAduE12);
        Assert.IsFalse(outside.IsAvailable);
        Assert.AreEqual(GainNormalizationReasonCodes.ReferenceGainOutOfRange, outside.ReasonCode);
    }

    [TestMethod]
    public void ResolveReportsAStableUnavailableReasonWithoutValidResponseData()
    {
        var asi174 = Asi174Sensor();
        var (asi178, asi178Readout) = Asi178McProfile();
        var invalidResponse = asi178 with
        {
            SimulationResponse = asi178.SimulationResponse! with { ElectronsPerAduAtZeroGain = -1 }
        };

        AssertUnavailable(GainResponseSourceKind.PhysicalCamera, asi178, asi178Readout, GainNormalizationReasonCodes.PhysicalResponseNotEnabled);
        AssertUnavailable(GainResponseSourceKind.PhysicalCamera, asi174, null, GainNormalizationReasonCodes.PhysicalResponseNotEnabled);
        AssertUnavailable(GainResponseSourceKind.Unspecified, asi178, asi178Readout, GainNormalizationReasonCodes.UnsupportedResponseSource);
        AssertUnavailable(GainResponseSourceKind.VirtualConfiguredResponse, asi174, null, GainNormalizationReasonCodes.MissingResponse);
        AssertUnavailable(GainResponseSourceKind.VirtualConfiguredResponse, invalidResponse, asi178Readout, GainNormalizationReasonCodes.InvalidResponse);
        AssertUnavailable(GainResponseSourceKind.VirtualConfiguredResponse, asi178, null, GainNormalizationReasonCodes.UnsupportedCodeMapping);
        AssertUnavailable(GainResponseSourceKind.VirtualAsi174MmModel, asi178, asi178Readout, GainNormalizationReasonCodes.UnsupportedResponseSource);
        AssertUnavailable(
            GainResponseSourceKind.VirtualAsi174MmModel,
            asi174 with { PixelFormat = CameraPixelFormat.BayerRggb16, ColorMode = SensorColorMode.Color },
            null,
            GainNormalizationReasonCodes.UnsupportedCodeMapping);
    }

    [TestMethod]
    public void ResolveRejectsReadoutsThatChangeThePerCodeResponse()
    {
        var sensor = Asi174Sensor();
        var identity = Mono16Readout(12, FrameStoredCodeTransform.RightAlignedV1);
        SensorReadoutProfile[] unsupported =
        [
            identity with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalSumV1, StrideBytes = 32 },
            identity with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1, StrideBytes = 32 },
            identity with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1, LevelCodeSpace = FrameLevelCodeSpace.NativeSample },
            identity with { StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1 },
            identity with { SampleDepthBits = 10, WhiteLevel = 1023 },
            identity with
            {
                PixelFormat = CameraPixelFormat.Mono8,
                SampleDepthBits = 8,
                ContainerDepthBits = 8,
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                BlackLevel = 4,
                WhiteLevel = 255,
                StrideBytes = 32
            },
            identity with { Roi = new SensorCrop(0, 0, 64, 16) }
        ];

        foreach (var readout in unsupported)
        {
            AssertUnavailable(GainResponseSourceKind.VirtualAsi174MmModel, sensor, readout, GainNormalizationReasonCodes.UnsupportedCodeMapping);
        }
    }

    [TestMethod]
    public void DescriptorSerializationIsDeterministicAndIdentityTracksEveryField()
    {
        var sensor = Asi174Sensor();
        var first = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, sensor, null);
        var second = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null);
        var bytes = GainNormalizationContractJson.Serialize(first.Descriptor);
        var sensorSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(sensor);

        Assert.AreEqual(
            "{\"coefficientRepresentation\":\"electrons-per-adu-fixed-e12-v1\",\"containerDepthBits\":16," +
            "\"gainUnits\":\"ZWO 0.1 dB\",\"highConversionGainControl\":null,\"maximumGainControl\":400," +
            "\"minimumGainControl\":0,\"nativeSampleDepthBits\":12,\"provenance\":\"virtual-simulation-truth\"," +
            "\"responseModelVersion\":\"zwo-asi174mm-12bit-v1\",\"schemaVersion\":\"hvo-gain-response-v1\"," +
            $"\"sensorProfileSha256\":\"{sensorSha256}\",\"storedCodeTransform\":\"RightAlignedV1\"}}",
            Encoding.UTF8.GetString(bytes));
        CollectionAssert.AreEqual(bytes, GainNormalizationContractJson.Serialize(second.Descriptor));
        Assert.AreEqual(first.IdentitySha256, second.IdentitySha256);
        Assert.AreEqual(GainNormalizationContractJson.ComputeIdentitySha256(first.Descriptor), first.IdentitySha256);

        var parsed = GainNormalizationContractJson.ParseDescriptor(bytes);
        Assert.IsTrue(parsed.Validation.IsValid);
        CollectionAssert.AreEqual(bytes, GainNormalizationContractJson.Serialize(parsed.Value!));
        Assert.AreEqual(
            GainNormalizationReasonCodes.NonCanonicalJson,
            GainNormalizationContractJson.ParseDescriptor(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes) + " ")).Validation.ReasonCode);
        Assert.AreEqual(
            GainNormalizationReasonCodes.UnsupportedSchema,
            GainNormalizationContractJson.ParseDescriptor(GainNormalizationContractJson.Serialize(GainNormalizationPolicyV1.Approved)).Validation.ReasonCode);

        var descriptor = first.Descriptor;
        GainResponseDescriptorV1[] changed =
        [
            descriptor with { SensorProfileSha256 = new string('0', 64) },
            descriptor with { ResponseModelVersion = "zwo-asi174mm-12bit-v2" },
            descriptor with { GainUnits = "native" },
            descriptor with { MinimumGainControl = 1 },
            descriptor with { MaximumGainControl = 399 },
            descriptor with { HighConversionGainControl = 300 },
            descriptor with { NativeSampleDepthBits = 14, StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1 }
        ];
        var identities = changed.Select(GainNormalizationContractJson.ComputeIdentitySha256).Append(first.IdentitySha256);
        Assert.AreEqual(changed.Length + 1, identities.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void DescriptorValidationRejectsInvalidFields()
    {
        var descriptor = ResolveAvailable(GainResponseSourceKind.VirtualAsi174MmModel, Asi174Sensor(), null).Descriptor;
        GainResponseDescriptorV1[] rejected =
        [
            descriptor with { SchemaVersion = "hvo-gain-response-v2" },
            descriptor with { Provenance = "physical-measured-v1" },
            descriptor with { SensorProfileSha256 = "not-a-hash" },
            descriptor with { ResponseModelVersion = " " },
            descriptor with { GainUnits = new string('x', 129) },
            descriptor with { MinimumGainControl = 401 },
            descriptor with { MaximumGainControl = double.PositiveInfinity },
            descriptor with { HighConversionGainControl = 401 },
            descriptor with { ContainerDepthBits = 8 },
            descriptor with { NativeSampleDepthBits = 0 },
            descriptor with { StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1 },
            descriptor with { StoredCodeTransform = FrameStoredCodeTransform.IdentityV1 },
            descriptor with { NativeSampleDepthBits = 16, StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1 },
            descriptor with { CoefficientRepresentation = "double-v1" }
        ];

        foreach (var value in rejected)
        {
            Assert.IsFalse(GainNormalizationContract.Validate(value).IsValid, value.ToString());
            Assert.ThrowsExactly<ArgumentException>(() => GainNormalizationContractJson.Serialize(value));
        }
    }

    private static GainResponse ResolveAvailable(
        GainResponseSourceKind source,
        SensorProfile sensor,
        SensorReadoutProfile? readout)
    {
        var resolution = GainResponseResolver.Resolve(source, sensor, readout);
        Assert.IsTrue(resolution.IsAvailable, resolution.ReasonCode ?? string.Empty);
        Assert.IsNull(resolution.ReasonCode);
        return resolution.Response!;
    }

    private static void AssertUnavailable(
        GainResponseSourceKind source,
        SensorProfile sensor,
        SensorReadoutProfile? readout,
        string reasonCode)
    {
        var resolution = GainResponseResolver.Resolve(source, sensor, readout);
        Assert.IsFalse(resolution.IsAvailable);
        Assert.IsNull(resolution.Response);
        Assert.AreEqual(reasonCode, resolution.ReasonCode);
    }

    private static void AssertPolicyRejected(string json, string reasonCode)
    {
        var parsed = GainNormalizationContractJson.ParsePolicy(Encoding.UTF8.GetBytes(json));
        Assert.IsNull(parsed.Value);
        Assert.IsFalse(parsed.Validation.IsValid);
        Assert.AreEqual(reasonCode, parsed.Validation.ReasonCode, json);
    }

    private static GainCoefficientV1 Coefficient(string identity, long electronsPerAduE12)
        => new(identity, 0, GainConversionRegimes.FixedConversionGain, electronsPerAduE12);

    private static SensorProfile Asi174Sensor()
        => new(
            "VirtualAsi174Mm",
            32,
            16,
            5.86,
            SensorColorMode.Mono,
            CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome,
            64,
            SampleByteOrder.LittleEndian,
            "virtual-asi174mm-electron-domain-v2");

    private static SensorReadoutProfile Mono16Readout(int sampleDepthBits, FrameStoredCodeTransform transform)
        => new(
            new SensorCrop(0, 0, 32, 16),
            1,
            1,
            FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16,
            sampleDepthBits,
            16,
            FrameSamplePacking.ByteAligned,
            transform,
            FrameLevelCodeSpace.NativeSample,
            64,
            (1 << sampleDepthBits) - 1,
            64);

    // Response values pinned in src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json, on a small frame.
    private static (SensorProfile Sensor, SensorReadoutProfile Readout) Asi178McProfile()
    {
        var sensor = new SensorProfile(
            "VirtualAsi178McRaw16",
            32,
            16,
            2.4,
            SensorColorMode.Color,
            CameraPixelFormat.BayerRggb16,
            SensorResponseMode.BayerRaw,
            64,
            SampleByteOrder.LittleEndian,
            "virtual-asi178mc-rggb16-v2")
        {
            SimulationResponse = new ConfiguredSensorResponseProfile(
                "zwo-asi178mc-14bit-raw16-v2",
                14,
                0,
                400,
                0.916,
                200,
                15_000,
                [
                    new(0, 2.25), new(50, 1.92), new(100, 1.72), new(150, 1.57),
                    new(200, 1.44), new(270, 1.37), new(300, 1.37), new(400, 1.35)
                ],
                16,
                "ZWO 0.1 dB",
                "ASI178MC native 14-bit ADU in full-range RAW16",
                ClampFullWellToAdcRange: true,
                ColorResponse: new ConfiguredColorResponseProfile(0.94, 1.0, 0.8, "legacy-asi178-relative-response"))
        };
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 32, 16),
            1,
            1,
            FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.BayerRggb16,
            14,
            16,
            FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.FullRangeScaledV1,
            FrameLevelCodeSpace.StoredContainer,
            64,
            65_535,
            64,
            CfaPattern: ColorFilterArrayPattern.Rggb,
            CfaOriginX: 0,
            CfaOriginY: 0);
        return (sensor, readout);
    }

    // Response values pinned in src/HVO.SkyMonitor.CameraAgent/virtual-asi676mc.full.json, on a small frame.
    private static (SensorProfile Sensor, SensorReadoutProfile Readout) Asi676McProfile()
    {
        var sensor = new SensorProfile(
            "VirtualAsi676McRggbRaw16Provisional",
            32,
            16,
            2.0,
            SensorColorMode.Color,
            CameraPixelFormat.BayerRggb16,
            SensorResponseMode.BayerRaw,
            64,
            SampleByteOrder.LittleEndian,
            "virtual-asi676mc-rggb12-in-raw16-provisional-v2")
        {
            SimulationResponse = new ConfiguredSensorResponseProfile(
                "zwo-asi676-12bit-published-envelope-v1",
                12,
                0,
                180,
                2.5763125763125763,
                200,
                10_550,
                [new(0, 2.9), new(82, 1.8), new(180, 1.8)],
                64,
                "ZWO 0.1 dB",
                "ASI676 native 12-bit ADU in Mono16 container",
                ClampFullWellToAdcRange: true,
                ColorResponse: new ConfiguredColorResponseProfile(1.0, 1.0, 1.0, "neutral-uncharacterized"),
                ExcludeBlackLevelFromFullWellRange: true,
                HighConversionGainControl: 180,
                HighConversionReadNoiseElectrons: 0.65,
                CalibrationStatus: "provisional-published-envelope")
        };
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 32, 16),
            1,
            1,
            FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.BayerRggb16,
            12,
            16,
            FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1,
            FrameLevelCodeSpace.NativeSample,
            64,
            4_095,
            64,
            CfaPattern: ColorFilterArrayPattern.Rggb,
            CfaOriginX: 0,
            CfaOriginY: 0);
        return (sensor, readout);
    }
}
