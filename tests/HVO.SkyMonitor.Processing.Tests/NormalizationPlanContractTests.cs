using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NormalizationPlanContractTests
{
    private static readonly string FamilySha256 = string.Concat(Enumerable.Repeat("A1", 32));
    private static readonly string DescriptorSha256 = string.Concat(Enumerable.Repeat("B2", 32));
    private static readonly string PayloadSha256 = string.Concat(Enumerable.Repeat("C3", 32));

    [TestMethod]
    public void CanonicalBytesAndIdentityAreStable()
    {
        var plan = Plan();
        var bytes = NormalizationPlanContractJson.Serialize(plan);

        var variant = plan with
        {
            CalibrationFamilyIdentitySha256 = Lower(plan.CalibrationFamilyIdentitySha256),
            ResponseIdentitySha256 = Lower(plan.ResponseIdentitySha256),
            Inputs = [.. plan.Inputs.Select(static input => input with
            {
                DescriptorSha256 = Lower(input.DescriptorSha256),
                Gain = input.Gain == 0 ? -0.0 : input.Gain,
                Masters = input.Masters?.Select(static master => master with
                {
                    PayloadSha256 = Lower(master.PayloadSha256)
                }).ToArray()
            })]
        };
        CollectionAssert.AreEqual(bytes, NormalizationPlanContractJson.Serialize(variant));
        CollectionAssert.AreEqual(bytes, NormalizationPlanContractJson.Serialize(Plan()));
        var identity = NormalizationPlanContractJson.ComputeIdentitySha256(plan);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), identity);
        // Pinned: any change to the field set, names, order or number formatting changes every frozen identity.
        Assert.AreEqual(PinnedIdentitySha256, identity);

        var parsed = NormalizationPlanContractJson.Parse(bytes);
        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.FieldPath);
        CollectionAssert.AreEqual(bytes, NormalizationPlanContractJson.Serialize(parsed.Value!));
        Assert.AreEqual(3, parsed.Value!.Inputs.Count);
        Assert.AreEqual(
            Asi174Coefficient(40).ElectronsPerAduE12,
            parsed.Value.Inputs[1].Coefficient!.ElectronsPerAduE12);
        StringAssert.Contains(
            Encoding.UTF8.GetString(bytes), "\"algorithmVersion\":\"linear16-gain-normalized-mean-v2\"", StringComparison.Ordinal);
        StringAssert.Contains(Encoding.UTF8.GetString(bytes), "\"outputScale\":16", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParsingAcceptsOnlyTheCanonicalBytes()
    {
        var bytes = NormalizationPlanContractJson.Serialize(Plan());
        var text = Encoding.UTF8.GetString(bytes);

        AssertParseFails(" " + text, NormalizationPlanReasonCodes.NonCanonicalJson);
        AssertParseFails(
            text.Replace(NormalizationPlanV1.CurrentSchemaVersion, "hvo-normalization-plan-v2", StringComparison.Ordinal),
            NormalizationPlanReasonCodes.UnsupportedSchema);
        AssertParseFails(text[..^1], NormalizationPlanReasonCodes.InvalidContract);
        // A plan frozen before the output scale was a kernel field cannot be read as a scaled plan.
        AssertParseFails(
            text.Replace("\"outputScale\":16,", string.Empty, StringComparison.Ordinal),
            NormalizationPlanReasonCodes.InvalidContract);
        AssertParseFails("[]", NormalizationPlanReasonCodes.InvalidContract);
        AssertParseFails(
            text.Replace("\"disposition\":\"excluded\"", "\"disposition\":\"skipped\"", StringComparison.Ordinal),
            NormalizationPlanReasonCodes.InvalidContract);
        Assert.IsFalse(NormalizationPlanContractJson.Parse(
            new byte[NormalizationPlanContractJson.MaximumContractBytes + 1]).Validation.IsValid);
    }

    [TestMethod]
    public void ValidationRejectsAPlanThatCannotReproduceItsWindow()
    {
        var plan = Plan();
        var selected = plan.Inputs[0];
        var excluded = plan.Inputs[2];
        (string Path, NormalizationPlanV1 Plan)[] cases =
        [
            ("schemaVersion", plan with { SchemaVersion = "hvo-normalization-plan-v0" }),
            ("calibrationFamilyIdentitySha256", plan with { CalibrationFamilyIdentitySha256 = "family" }),
            ("policy", plan with { Policy = plan.Policy with { MaximumCoefficientRatioMilli = 4001 } }),
            ("response", plan with { Response = plan.Response with { GainUnits = " " } }),
            ("responseIdentitySha256", plan with { ResponseIdentitySha256 = FamilySha256 }),
            ("referenceCoefficient", plan with { ReferenceCoefficient = Asi174Coefficient(40) }),
            ("inputLayout", plan with { InputLayout = plan.InputLayout with { SampleDepthBits = 16 } }),
            ("inputLayout", plan with { InputLayout = plan.InputLayout with { Readout = null } }),
            ("kernel", plan with { Kernel = plan.Kernel with { SaturationCode = ushort.MaxValue } }),
            ("kernel", plan with { Kernel = plan.Kernel with { Rounding = "away-from-zero" } }),
            ("kernel", plan with { Kernel = plan.Kernel with { OutputScale = 1 } }),
            ("kernel", plan with { Kernel = plan.Kernel with { AlgorithmVersion = "linear16-gain-normalized-mean-v1" } }),
            ("inputs", plan with { Inputs = [] }),
            ("inputs", plan with { Inputs = [.. Enumerable.Range(0, 129).Select(ordinal => Selected(ordinal, 0))] }),
            ("inputs.ordinal", plan with { Inputs = [selected with { Ordinal = 1 }] }),
            ("inputs", plan with { Inputs = [selected, Selected(1, 40) with { WindowPosition = 0 }] }),
            ("inputs", plan with { Inputs = [selected, Selected(1, 40) with { CaptureId = selected.CaptureId }] }),
            ("inputs.identity", plan with { Inputs = [selected with { PayloadSha256 = "payload" }] }),
            ("inputs.conditions", plan with { Inputs = [selected with { Exposure = TimeSpan.Zero }] }),
            ("inputs.selection", plan with { Inputs = [selected with { Masters = null }] }),
            ("inputs.selection", plan with { Inputs = [selected with { Masters = [.. selected.Masters!.Reverse()] }] }),
            ("inputs.selection", plan with { Inputs = [selected with { ExclusionReason = "calibration.family.selected" }] }),
            ("inputs.coefficient", plan with { Inputs = [selected with { Coefficient = Asi174Coefficient(40) }] }),
            ("inputs.exclusionReason", plan with { Inputs = [selected, excluded with { Ordinal = 1, MemberId = "gain-90" }] }),
            ("inputs.exclusionReason", plan with { Inputs = [selected, excluded with { Ordinal = 1, ExclusionReason = "Missing" }] }),
            ("inputs.disposition", plan with { Inputs = [excluded with { Ordinal = 0 }] }),
            ("inputs.coefficient", plan with { Inputs = [selected, Selected(1, 200)] })
        ];

        foreach (var (path, invalid) in cases)
        {
            var validation = NormalizationPlanContract.Validate(invalid);

            Assert.IsFalse(validation.IsValid, path);
            Assert.AreEqual(NormalizationPlanReasonCodes.InvalidContract, validation.ReasonCode, path);
            Assert.AreEqual(path, validation.FieldPath, path);
            Assert.ThrowsExactly<ArgumentException>(() => NormalizationPlanContractJson.Serialize(invalid), path);
        }
        Assert.IsTrue(NormalizationPlanContract.Validate(plan).IsValid);
    }

    [TestMethod]
    public void TheLargestValidPlanFitsTheContractBound()
    {
        // 128 selected inputs whose member IDs escape every character to six bytes, the most a valid plan can hold.
        var memberId = new string('é', 128);
        var plan = Plan() with
        {
            Inputs = [.. Enumerable.Range(0, 128)
                .Select(ordinal => Selected(ordinal, ordinal % 2 == 0 ? 0 : 120) with
                {
                    MemberId = memberId,
                    WindowPosition = int.MinValue + ordinal,
                    Offset = -1.2345678901234567e-300,
                    TemperatureC = -1.2345678901234567e-300,
                    Exposure = TimeSpan.MaxValue
                })]
        };

        var bytes = NormalizationPlanContractJson.Serialize(plan);

        Assert.IsTrue(bytes.Length <= 320 * 1024, bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Four such plans and a frozen graph plan of up to 512 KiB still fit the 2 MiB frozen execution column.
        Assert.IsTrue(bytes.Length * 4 < 2 * 1024 * 1024 - NormalizationPlanContractJson.MaximumContractBytes);
        Assert.IsTrue(NormalizationPlanContractJson.Parse(bytes).Validation.IsValid);
        StringAssert.Contains(Encoding.UTF8.GetString(bytes), "\\u00E9", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheKernelSaturatesAtTheNativeFullScaleStoredCode()
    {
        var response = Asi174Response().Descriptor;

        Assert.AreEqual(4095, NormalizationKernelV1.Linear16(response).SaturationCode);
        Assert.AreEqual(
            ushort.MaxValue,
            NormalizationKernelV1.Linear16(response with { StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1 })
                .SaturationCode);
        Assert.AreEqual(
            ushort.MaxValue,
            NormalizationKernelV1.Linear16(response with
            {
                NativeSampleDepthBits = 16,
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1
            }).SaturationCode);
    }

    private const string PinnedIdentitySha256 = "CBD6B8DC0F1DC12B3D9F2ED3C28B391B40246F532E559933C3C2026C9F50D326";

    private static NormalizationPlanV1 Plan()
    {
        var response = Asi174Response();
        return new NormalizationPlanV1(
            NormalizationPlanV1.CurrentSchemaVersion,
            FamilySha256,
            GainNormalizationPolicyV1.Approved,
            response.Descriptor,
            response.IdentitySha256,
            Asi174Coefficient(0),
            Layout(),
            NormalizationKernelV1.Linear16(response.Descriptor),
            [
                Selected(0, 0),
                Selected(1, 40),
                new NormalizationPlanInputV1(
                    2, -2, Id(2, 1), Id(2, 2), DescriptorSha256, PayloadSha256, 90, TimeSpan.FromSeconds(2), null, -10,
                    NormalizationPlanInputDispositions.Excluded, CalibrationFamilyReasonCodes.MissingMember,
                    null, null, null)
            ]);
    }

    private static NormalizationPlanInputV1 Selected(int ordinal, double gain)
        => new(
            ordinal,
            -ordinal,
            Id(ordinal, 1),
            Id(ordinal, 2),
            DescriptorSha256,
            PayloadSha256,
            gain,
            TimeSpan.FromSeconds(2),
            null,
            -10,
            NormalizationPlanInputDispositions.Selected,
            null,
            FormattableString.Invariant($"gain-{gain}"),
            [.. CalibrationReferenceKinds.All.Select((kind, index) =>
                new CalibrationFamilyMasterV1(kind, Id(1000 + index, (int)gain), PayloadSha256))],
            Asi174Coefficient(gain));

    private static Guid Id(int first, int second)
        => new(first, (short)second, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static GainCoefficientV1 Asi174Coefficient(double gain)
        => Asi174Response().ResolveCoefficient(gain).Coefficient!;

    private static GainResponse Asi174Response()
        => GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualAsi174MmModel,
            new SensorProfile(
                "VirtualAsi174Mm",
                32,
                16,
                5.86,
                SensorColorMode.Mono,
                CameraPixelFormat.Mono16,
                SensorResponseMode.Monochrome,
                64,
                SampleByteOrder.LittleEndian,
                "virtual-asi174mm-electron-domain-v2"),
            null).Response!;

    private static FrameLayoutDescriptor Layout()
        => new(
            32,
            16,
            64,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            12,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            4095,
            1024)
        {
            Readout = new FrameReadoutDescriptor(32, 16, 0, 0, 32, 16, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null),
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };

    private static string Lower(string value)
        => string.Concat(value.Select(char.ToLowerInvariant));

    private static void AssertParseFails(string json, string reasonCode)
    {
        var parsed = NormalizationPlanContractJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsNull(parsed.Value, json);
        Assert.AreEqual(reasonCode, parsed.Validation.ReasonCode, json);
    }
}
