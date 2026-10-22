using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// The opt-in mixed-gain stack (#1475 slice F). Expected codes are computed by hand from the documented arithmetic:
/// a normalized master code <c>m</c> is the 12-bit level <c>m × 4095 / 65535</c>, so <c>m = 4369</c> is stored code
/// 273 exactly, and at the reference gain one input's output is <c>16 × (L − B − D)</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest methods do not require context-free awaits.")]
public sealed class MixedGainNormalizedMeanRecipeTests
{
    private const string PlanInputName = "normalization-plan";
    private const string OutputVariant = "mixed-gain-mean";
    private const double ReferenceGain = 0;
    private const double HigherGain = 60;
    private static readonly string FamilySha256 = string.Concat(Enumerable.Repeat("A1", 32));
    private static readonly string DescriptorSha256 = string.Concat(Enumerable.Repeat("B2", 32));
    private static readonly DateTimeOffset CaptureTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan LightExposure = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DarkExposure = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FlatExposure = TimeSpan.FromSeconds(1);
    private static readonly GainResponse Response = Asi174Response();

    [TestMethod]
    public async Task OneReferenceGainInputIsEncodedAtSixteenCodesPerCalibratedCode()
    {
        // Bias 4369 is stored code 273. Dark 8738 is 273 codes over bias in 4 s, so 136.5 codes in the 2 s light,
        // rounded half to even to 136. Every light therefore loses 409 codes before the ×16 encoding.
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 4369), Fill(8, 8738), Fill(8, 43690), [0, 0, 0, 0, 0, 1, 0, 0]);
        var light = Light(layout, [1000, 409, 300, 4094, 4095, 2000, 410, 3000], ReferenceGain);
        var plan = Plan(layout, [new(light, ReferenceGain, member)]);

        var outcome = await ExecuteAsync(Request(plan, [light, .. member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        // 16 × 591; zero signal; a negative mean clamps to 0; 16 × 3685 below saturation; saturated light has no
        // coverage; a defect has no coverage; one calibrated code is sixteen output codes; 16 × 2591.
        CollectionAssert.AreEqual(new ushort[] { 9456, 0, 0, 58960, 0, 0, 16, 41456 }, Values(product.Payload.Span));
        Assert.AreEqual(FrameArtifactRole.Combined, product.Role);
        Assert.AreEqual(OutputVariant, product.Variant);
        Assert.AreEqual(CalibrationMasterBuilder.CreateNormalizedLayout(layout), product.Layout);
        Assert.AreEqual(LightExposure, product.TotalIntegration);
        CollectionAssert.AreEqual(
            new[]
            {
                new ProcessingAlgorithmIdentity("gain-normalized-mean", "linear16-gain-normalized-mean-v2"),
                new ProcessingAlgorithmIdentity("mixed-gain-master-planes", "mixed-gain-master-planes-v1")
            },
            product.Algorithms.ToArray());
        CollectionAssert.AreEqual(
            new[] { light.ArtifactId, member.Bias.ArtifactId, member.Dark.ArtifactId, member.Flat.ArtifactId, member.Defect.ArtifactId },
            product.SourceArtifactIds.ToArray());
        var identity = GainNormalizationCompatibility.ComputeIdentitySha256(plan);
        Assert.AreEqual(FamilySha256, product.Compatibility.Calibration);
        Assert.AreEqual(
            $"exposure=2000;gain=normalized:{identity};offset=;temperatureSetpoint=-10",
            product.Compatibility.SetpointRegime);
        Assert.AreEqual(light.Compatibility.Mask, product.Compatibility.Mask);
    }

    [TestMethod]
    public async Task SignedSamplesAreAveragedSoNegativeNoiseReachesTheMean()
    {
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 4369), Fill(8, 8738), Fill(8, 43690), [0, 0, 0, 0, 0, 1, 0, 0]);
        var first = Light(layout, [300, 1000, 300, 4095, 4095, 1000, 410, 4094], ReferenceGain);
        var second = Light(layout, [600, 300, 300, 1000, 4095, 1000, 411, 4094], ReferenceGain);
        var plan = Plan(layout, [new(first, ReferenceGain, member), new(second, ReferenceGain, member)]);

        var outcome = await ExecuteAsync(Request(plan, [first, second, .. member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        // 8 × (−109 + 191); 8 × (591 − 109); both negative clamp to 0; only the unsaturated 591 counts; no coverage;
        // defect; 8 × (1 + 2); 8 × 2 × 3685 = 16 × 3685.
        CollectionAssert.AreEqual(
            new ushort[] { 656, 3856, 0, 9456, 0, 0, 24, 58960 },
            Values(outcome.Products.Single().Payload.Span));
        Assert.AreEqual(LightExposure * 2, outcome.Products.Single().TotalIntegration);
    }

    [TestMethod]
    public async Task FlatsAreUnityNormalizedByTheirFrameMeanAndTheOutputClampsAtTheTop()
    {
        // With a zero bias and dark the flat response is its code, mean 2000: flats of 0.5, 1.5 and 2.0, and a zero
        // response that leaves its photosite without coverage.
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 0), Fill(8, 0), [1000, 3000, 1000, 3000, 1000, 3000, 0, 4000], Fill(8, 0));
        var light = Light(layout, [100, 30, 4094, 3, 4095, 0, 500, 200], ReferenceGain);
        var plan = Plan(layout, [new(light, ReferenceGain, member)]);

        var outcome = await ExecuteAsync(Request(plan, [light, .. member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        // 32 × 100; 16 × 20; 32 × 4094 = 131008 clamps to 65535 instead of wrapping to 65472; 16 × 2; saturated;
        // zero signal; no valid flat; 8 × 200.
        CollectionAssert.AreEqual(
            new ushort[] { 3200, 320, 65535, 32, 0, 0, 0, 1600 },
            Values(outcome.Products.Single().Payload.Span));
    }

    [TestMethod]
    public async Task EachInputIsCalibratedAtItsOwnGainWithItsOwnMember()
    {
        var layout = Layout(4, 2);
        var reference = Member(layout, Fill(8, 4369), Fill(8, 8738), Fill(8, 43690), Fill(8, 0));
        var higher = Member(layout, Fill(8, 4369), Fill(8, 4369), Fill(8, 43690), Fill(8, 0));
        var first = Light(layout, [1000, 409, 2000, 3000, 500, 4094, 1500, 700], ReferenceGain);
        var second = Light(layout, [2000, 273, 100, 3000, 4000, 4094, 1500, 900], HigherGain);
        var plan = Plan(layout, [new(first, ReferenceGain, reference), new(second, HigherGain, higher)]);

        var outcome = await ExecuteAsync(Request(plan, [first, second, .. reference.All, .. higher.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var referenceCoefficient = Coefficient(ReferenceGain).ElectronsPerAduE12;
        var higherCoefficient = Coefficient(HigherGain).ElectronsPerAduE12;
        Assert.IsTrue(higherCoefficient < referenceCoefficient / 1.9 && higherCoefficient > referenceCoefficient / 2.1);
        var expected = new ushort[8];
        for (var index = 0; index < expected.Length; index++)
        {
            // The reference member's dark is 136 codes; the higher-gain member's dark equals its bias.
            Int128 electrons = (Int128)referenceCoefficient * (Code(first, index) - 273 - 136) +
                               (Int128)higherCoefficient * (Code(second, index) - 273);
            expected[index] = (ushort)Int128.Clamp(RoundHalfEven(16 * electrons, 2 * (Int128)referenceCoefficient), 0, 65535);
        }
        var actual = Values(outcome.Products.Single().Payload.Span);
        CollectionAssert.AreEqual(expected, actual);
        // The higher-gain input carries about half the electrons per code, so the mean is not the equal-gain mean.
        Assert.AreNotEqual((ushort)(8 * ((1000 - 409) + (2000 - 273))), actual[0]);
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId }
                .Concat(reference.All.Select(static master => master.ArtifactId))
                .Concat(higher.All.Select(static master => master.ArtifactId))
                .ToArray(),
            outcome.Products.Single().SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public async Task ABandedFrameMatchesOneFullFrameKernelCall()
    {
        // 64 × 600 runs in three bands of 256, 256 and 88 rows; a spatial defect mask and random lights expose any
        // band offset in the master planes or the lights.
        var layout = Layout(64, 600);
        var photosites = 64 * 600;
        var random = new Random(1475);
        var defect = new ushort[photosites];
        for (var index = 0; index < photosites; index += 97)
        {
            defect[index] = 1;
        }
        var member = Member(layout, Fill(photosites, 0), Fill(photosites, 0), Fill(photosites, 30000), defect);
        var first = Light(layout, [.. Enumerable.Range(0, photosites).Select(_ => (ushort)random.Next(0, 4096))], ReferenceGain);
        var second = Light(layout, [.. Enumerable.Range(0, photosites).Select(_ => (ushort)random.Next(0, 4096))], HigherGain);
        var plan = Plan(layout, [new(first, ReferenceGain, member), new(second, HigherGain, member)]);

        var outcome = await ExecuteAsync(Request(plan, [first, second, .. member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var mask = defect.Select(static value => (byte)(value == 0 ? 0 : 1)).ToArray();
        var expected = Linear16GainNormalizedMean.Compute(
            64,
            600,
            CameraPixelFormat.Mono16,
            [
                KernelInput(first, mask, Coefficient(ReferenceGain).ElectronsPerAduE12),
                KernelInput(second, mask, Coefficient(HigherGain).ElectronsPerAduE12)
            ],
            Coefficient(ReferenceGain).ElectronsPerAduE12,
            NormalizationKernelV1.ReferenceGainOutputScale,
            photosites,
            CancellationToken.None);
        CollectionAssert.AreEqual(expected.PixelData.ToArray(), outcome.Products.Single().Payload.ToArray());
    }

    [TestMethod]
    public async Task OutputBackedWindowsAreRejected()
    {
        var fixture = SingleInputFixture();

        var combined = await ExecuteAsync(fixture.Request with { Input = ProcessingInputSelector.Combined("source") });
        var recipeResult = await ExecuteAsync(fixture.Request with
        {
            Input = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Raw, "source", new string('E', 64))
        });
        // A plan that names an earlier combined output, beside a raw primary that the selector does match.
        var layout = fixture.Light.Layout!;
        var output = Light(layout, Fill(8, 1000), ReferenceGain) with { Role = FrameArtifactRole.Combined, Variant = "rolling-mean" };
        var outputPlan = Plan(layout, [new(output, ReferenceGain, fixture.Member), new(fixture.Light, ReferenceGain, fixture.Member)]);
        var outputInput = await ExecuteAsync(Request(outputPlan, [output, fixture.Light, .. fixture.Member.All]));
        var restoredProduct = fixture.Light with { ProductKind = ProcessingProductKind.Metadata };
        var structured = await ExecuteAsync(Request(fixture.Plan, [restoredProduct, .. fixture.Member.All]));

        foreach (var outcome in new[] { combined, recipeResult, outputInput, structured })
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
            Assert.AreEqual(ProcessingReasonCodes.OutputBackedWindow, outcome.ReasonCode);
        }
    }

    [TestMethod]
    public async Task AMasterWhoseBytesNoLongerMatchThePlanFailsAndIsNeverSubstituted()
    {
        var fixture = SingleInputFixture();
        var corruptBytes = fixture.Member.Dark.Payload.ToArray();
        corruptBytes[0] ^= 1;
        var corruptDark = fixture.Member.Dark with { Payload = corruptBytes };
        var replacement = fixture.Member.Dark with { ArtifactId = Guid.NewGuid() };

        var corrupt = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light, fixture.Member.Bias, corruptDark, fixture.Member.Flat, fixture.Member.Defect, replacement]));
        var missing = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light, fixture.Member.Bias, fixture.Member.Dark, fixture.Member.Defect]));

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, corrupt.Status);
        Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, corrupt.ReasonCode);
        Assert.AreEqual("dark-master", corrupt.Field);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, missing.Status);
        Assert.AreEqual(CalibrationFamilyReasonCodes.MasterMissing, missing.ReasonCode);
        Assert.AreEqual("flat-master", missing.Field);
    }

    [TestMethod]
    public async Task AMasterWithTheWrongLayoutOrExposureIsAMismatch()
    {
        var fixture = SingleInputFixture();
        var noExposure = fixture.Member.Flat with { Integration = TimeSpan.Zero };
        var otherLayout = fixture.Member.Bias with { Layout = fixture.Light.Layout };

        var exposure = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light, fixture.Member.Bias, fixture.Member.Dark, noExposure, fixture.Member.Defect]));
        var layout = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light, otherLayout, fixture.Member.Dark, fixture.Member.Flat, fixture.Member.Defect]));

        Assert.AreEqual(CalibrationFamilyReasonCodes.MasterMismatch, exposure.ReasonCode);
        Assert.AreEqual("flat-master", exposure.Field);
        Assert.AreEqual(CalibrationFamilyReasonCodes.MasterMismatch, layout.ReasonCode);
        Assert.AreEqual("bias-master", layout.Field);
    }

    [TestMethod]
    public async Task AFrozenPlanIsImmuneToLiveArtifactsAndRejectsChangedLights()
    {
        var fixture = SingleInputFixture();
        var baseline = await ExecuteAsync(fixture.Request);
        var liveMaster = Member(fixture.Light.Layout!, Fill(8, 0), Fill(8, 0), Fill(8, 43690), Fill(8, 0));
        var liveLight = Light(fixture.Light.Layout!, Fill(8, 4000), HigherGain);

        var withLiveArtifacts = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light, .. fixture.Member.All, liveLight, .. liveMaster.All]));
        var changedBytes = fixture.Light.Payload.ToArray();
        changedBytes[2] ^= 1;
        var changedLight = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light with { Payload = changedBytes }, .. fixture.Member.All]));
        var changedGain = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light with { Conditions = fixture.Light.Conditions! with { Gain = HigherGain } }, .. fixture.Member.All]));
        var changedExposure = await ExecuteAsync(Request(
            fixture.Plan,
            [fixture.Light with { Integration = TimeSpan.FromSeconds(3) }, .. fixture.Member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, withLiveArtifacts.Status, withLiveArtifacts.ReasonCode);
        Assert.AreEqual(baseline.Products.Single().ChecksumSha256, withLiveArtifacts.Products.Single().ChecksumSha256);
        Assert.AreEqual(baseline.Products.Single().OutputIdentitySha256, withLiveArtifacts.Products.Single().OutputIdentitySha256);
        CollectionAssert.AreEqual(
            baseline.Products.Single().SourceArtifactIds.ToArray(),
            withLiveArtifacts.Products.Single().SourceArtifactIds.ToArray());
        foreach (var outcome in new[] { changedLight, changedGain, changedExposure })
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
            Assert.AreEqual(ProcessingReasonCodes.NormalizationPlanInputMismatch, outcome.ReasonCode);
        }
    }

    [TestMethod]
    public async Task ExcludedInputsCarryNoSignalWeightOrLineage()
    {
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 4369), Fill(8, 8738), Fill(8, 43690), Fill(8, 0));
        var excluded = Light(layout, Fill(8, 4000), HigherGain);
        var primary = Light(layout, [1000, 409, 300, 4094, 4095, 2000, 410, 3000], ReferenceGain);
        var plan = Plan(layout, [new(excluded, HigherGain, null), new(primary, ReferenceGain, member)]);

        var outcome = await ExecuteAsync(Request(plan, [excluded, primary, .. member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        CollectionAssert.AreEqual(
            new ushort[] { 9456, 0, 0, 58960, 0, 25456, 16, 41456 },
            Values(outcome.Products.Single().Payload.Span));
        CollectionAssert.DoesNotContain(outcome.Products.Single().SourceArtifactIds.ToArray(), excluded.ArtifactId);
        Assert.AreEqual(LightExposure, outcome.Products.Single().TotalIntegration);
    }

    [TestMethod]
    public async Task AWindowWithoutAUsablePlanIsSkippedOrFailsWithAStableReason()
    {
        var fixture = SingleInputFixture();
        var payload = NormalizationPlanContractJson.Serialize(fixture.Plan);
        var indented = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            JsonDocument.Parse(payload).RootElement,
            new JsonSerializerOptions { WriteIndented = true }));
        var otherScale = Canonical(Encoding.UTF8.GetString(payload).Replace("\"outputScale\":16", "\"outputScale\":8", StringComparison.Ordinal));
        var noScale = Canonical(Encoding.UTF8.GetString(payload).Replace(",\"outputScale\":16", string.Empty, StringComparison.Ordinal));
        var kernelV1 = Canonical(Encoding.UTF8.GetString(payload).Replace(
            "linear16-gain-normalized-mean-v2", "linear16-gain-normalized-mean-v1", StringComparison.Ordinal));
        var unmatchedPrimary = Light(fixture.Light.Layout!, Fill(8, 1000), HigherGain);
        var excludedPrimary = Plan(
            fixture.Light.Layout!,
            [new(fixture.Light, ReferenceGain, fixture.Member), new(unmatchedPrimary, HigherGain, null)]);
        var other = SingleInputFixture();

        var missing = await ExecuteAsync(fixture.Request with { AuxiliaryInputs = [] });
        var nonCanonical = await ExecuteAsync(fixture.Request with
        {
            AuxiliaryInputs = [PlanAuxiliary(indented, ProcessingIdentity.ComputePayloadSha256(indented))]
        });
        var wrongIdentity = await ExecuteAsync(fixture.Request with
        {
            AuxiliaryInputs =
            [
                PlanAuxiliary(payload, NormalizationPlanContractJson.ComputeIdentitySha256(other.Plan)) with
                {
                    ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
                }
            ]
        });
        var scaleOutcomes = new List<ProcessingOutcome>();
        foreach (var variant in new[] { otherScale, noScale, kernelV1 })
        {
            CollectionAssert.AreNotEqual(payload, variant);
            scaleOutcomes.Add(await ExecuteAsync(fixture.Request with
            {
                AuxiliaryInputs = [PlanAuxiliary(variant, ProcessingIdentity.ComputePayloadSha256(variant))]
            }));
        }
        var wrongSchema = await ExecuteAsync(fixture.Request with
        {
            AuxiliaryInputs = [PlanAuxiliary(payload, ProcessingIdentity.ComputePayloadSha256(payload)) with { SchemaVersion = "hvo-normalization-plan-v2" }]
        });
        var otherWindow = await ExecuteAsync(Request(other.Plan, [fixture.Light, .. fixture.Member.All]));
        var excluded = await ExecuteAsync(Request(excludedPrimary, [fixture.Light, unmatchedPrimary, .. fixture.Member.All]));

        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, missing.Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingNormalizationPlan, missing.ReasonCode);
        // The executor admits only canonical JSON auxiliaries; the recipe then admits only a valid plan.
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, nonCanonical.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, nonCanonical.ReasonCode);
        foreach (var outcome in scaleOutcomes.Append(wrongIdentity).Append(wrongSchema))
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidNormalizationPlan, outcome.ReasonCode);
        }
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, otherWindow.Status);
        Assert.AreEqual(ProcessingReasonCodes.NormalizationPlanInputMismatch, otherWindow.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, excluded.Status);
        Assert.AreEqual(ProcessingReasonCodes.NormalizationPrimaryExcluded, excluded.ReasonCode);
    }

    [TestMethod]
    public void TheCompatibilityIdentityIgnoresGainAndBindsFamilyPolicyAndResponse()
    {
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 0), Fill(8, 0), Fill(8, 1), Fill(8, 0));
        var reference = Plan(layout, [new(Light(layout, Fill(8, 0), ReferenceGain), ReferenceGain, member)]);
        var higher = Plan(layout, [new(Light(layout, Fill(8, 0), HigherGain), HigherGain, member)]);
        var policy = GainNormalizationContractJson.ComputeIdentitySha256(reference.Policy);
        var identity = GainNormalizationCompatibility.ComputeIdentitySha256(reference);

        Assert.AreEqual(identity, GainNormalizationCompatibility.ComputeIdentitySha256(higher));
        Assert.AreEqual(
            identity,
            GainNormalizationCompatibility.ComputeIdentitySha256(
                FamilySha256.ToLowerInvariant(), policy, reference.ResponseIdentitySha256.ToLowerInvariant()));
        var changes = new[]
        {
            GainNormalizationCompatibility.ComputeIdentitySha256(new string('D', 64), policy, reference.ResponseIdentitySha256),
            GainNormalizationCompatibility.ComputeIdentitySha256(FamilySha256, new string('D', 64), reference.ResponseIdentitySha256),
            GainNormalizationCompatibility.ComputeIdentitySha256(FamilySha256, policy, new string('D', 64))
        };
        Assert.IsFalse(changes.Contains(identity));
        Assert.AreEqual(3, changes.Distinct().Count());
        Assert.ThrowsExactly<ArgumentException>(() =>
            GainNormalizationCompatibility.ComputeIdentitySha256(FamilySha256, policy, "not-a-sha"));

        var normalized = GainNormalizationCompatibility.NormalizeSetpointRegime(
            "exposure=2000;gain=0;offset=;temperatureSetpoint=-10", identity);
        Assert.AreEqual($"exposure=2000;gain=normalized:{identity};offset=;temperatureSetpoint=-10", normalized);
        Assert.AreEqual(
            normalized,
            GainNormalizationCompatibility.NormalizeSetpointRegime("exposure=2000;gain=60;offset=;temperatureSetpoint=-10", identity));
        Assert.AreNotEqual(
            normalized,
            GainNormalizationCompatibility.NormalizeSetpointRegime("exposure=3000;gain=60;offset=;temperatureSetpoint=-10", identity));
        Assert.ThrowsExactly<ArgumentException>(() =>
            GainNormalizationCompatibility.NormalizeSetpointRegime("exposure=2000;offset=", identity));
        Assert.ThrowsExactly<ArgumentException>(() =>
            GainNormalizationCompatibility.NormalizeSetpointRegime("gain=0;gain=60", identity));
    }

    private static SingleInput SingleInputFixture()
    {
        var layout = Layout(4, 2);
        var member = Member(layout, Fill(8, 4369), Fill(8, 8738), Fill(8, 43690), Fill(8, 0));
        var light = Light(layout, [1000, 409, 300, 4094, 4095, 2000, 410, 3000], ReferenceGain);
        var plan = Plan(layout, [new(light, ReferenceGain, member)]);
        return new SingleInput(Request(plan, [light, .. member.All]), plan, light, member);
    }

    private static async Task<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request)
        => await new ProcessingRecipeExecutor().ExecuteAsync(request);

    private static ProcessingExecutionRequest Request(NormalizationPlanV1 plan, IReadOnlyList<ProcessingArtifact> inputs)
    {
        var payload = NormalizationPlanContractJson.Serialize(plan);
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.MixedGainNormalizedMean,
            JsonSerializer.SerializeToElement(new MixedGainNormalizedMeanOptions()),
            ProcessingInputSelector.Raw("source"),
            inputs,
            OutputVariant,
            AuxiliaryInputs: [PlanAuxiliary(payload, NormalizationPlanContractJson.ComputeIdentitySha256(plan))]);
    }

    private static byte[] Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(document.RootElement));
    }

    private static ProcessingAuxiliaryInput PlanAuxiliary(byte[] payload, string identitySha256)
        => new(
            PlanInputName,
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: NormalizationPlanV1.CurrentSchemaVersion,
            IdentitySha256: identitySha256,
            Payload: payload);

    /// <summary>The trailing window of <paramref name="entries"/>: the last is the primary at window position 0.</summary>
    private static NormalizationPlanV1 Plan(FrameLayoutDescriptor layout, IReadOnlyList<Entry> entries)
        => new(
            NormalizationPlanV1.CurrentSchemaVersion,
            FamilySha256,
            GainNormalizationPolicyV1.Approved,
            Response.Descriptor,
            Response.IdentitySha256,
            Coefficient(ReferenceGain),
            layout,
            NormalizationKernelV1.Linear16(Response.Descriptor),
            [.. entries.Select((entry, ordinal) => entry.Member is { } member
                ? new NormalizationPlanInputV1(
                    ordinal,
                    ordinal - entries.Count + 1,
                    entry.Light.ArtifactId,
                    entry.Light.ArtifactId,
                    DescriptorSha256,
                    ProcessingIdentity.ComputePayloadSha256(entry.Light.Payload),
                    entry.Gain,
                    LightExposure,
                    null,
                    -10,
                    NormalizationPlanInputDispositions.Selected,
                    null,
                    FormattableString.Invariant($"gain-{entry.Gain}"),
                    [
                        Master(CalibrationReferenceKinds.Bias, member.Bias),
                        Master(CalibrationReferenceKinds.Dark, member.Dark),
                        Master(CalibrationReferenceKinds.Flat, member.Flat),
                        Master(CalibrationReferenceKinds.Defect, member.Defect)
                    ],
                    Coefficient(entry.Gain))
                : new NormalizationPlanInputV1(
                    ordinal,
                    ordinal - entries.Count + 1,
                    entry.Light.ArtifactId,
                    entry.Light.ArtifactId,
                    DescriptorSha256,
                    ProcessingIdentity.ComputePayloadSha256(entry.Light.Payload),
                    entry.Gain,
                    LightExposure,
                    null,
                    -10,
                    NormalizationPlanInputDispositions.Excluded,
                    CalibrationFamilyReasonCodes.MissingMember,
                    null,
                    null,
                    null))]);

    private static CalibrationFamilyMasterV1 Master(string kind, ProcessingArtifact artifact)
        => new(kind, artifact.ArtifactId, ProcessingIdentity.ComputePayloadSha256(artifact.Payload));

    private static MasterSet Member(
        FrameLayoutDescriptor layout,
        ushort[] bias,
        ushort[] dark,
        ushort[] flat,
        ushort[] defect)
    {
        var masterLayout = CalibrationMasterBuilder.CreateNormalizedLayout(layout);
        return new MasterSet(
            MasterArtifact("bias-master", masterLayout, bias, TimeSpan.Zero),
            MasterArtifact("dark-master", masterLayout, dark, DarkExposure),
            MasterArtifact("flat-master", masterLayout, flat, FlatExposure),
            MasterArtifact("defect-master", masterLayout, defect, TimeSpan.Zero));
    }

    private static ProcessingArtifact MasterArtifact(string variant, FrameLayoutDescriptor layout, ushort[] values, TimeSpan integration)
        => new(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            variant,
            new string('F', 64),
            "application/x-hvo-linear-frame",
            layout,
            Bytes(values),
            CaptureTime,
            integration,
            Compatibility(ReferenceGain));

    private static ProcessingArtifact Light(FrameLayoutDescriptor layout, ushort[] values, double gain)
        => new(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "source",
            new string('F', 64),
            "application/x-hvo-linear-frame",
            layout,
            Bytes(values),
            CaptureTime,
            LightExposure,
            Compatibility(gain),
            ObservationStartedUtc: CaptureTime,
            ObservationEndedUtc: CaptureTime.Add(LightExposure),
            Conditions: new ProcessingCaptureConditions(gain, null, -10));

    private static ProcessingCompatibilityIdentity Compatibility(double gain)
        => new(
            "rig",
            "orientation",
            "calibration",
            "mask",
            "sensor",
            FormattableString.Invariant($"exposure=2000;gain={gain};offset=;temperatureSetpoint=-10"),
            "processing");

    /// <summary>
    /// The kernel planes of a zero bias and dark and a uniform flat, built independently of the recipe's master
    /// mapping.
    /// </summary>
    private static GainNormalizedMeanInput KernelInput(ProcessingArtifact light, byte[] defect, long coefficient)
    {
        var count = defect.Length;
        return new GainNormalizedMeanInput(
            Values(light.Payload.Span),
            new ushort[count],
            new int[count],
            Enumerable.Repeat(Linear16GainNormalizedMean.FlatScale, count).ToArray(),
            defect,
            4095,
            coefficient);
    }

    private static FrameLayoutDescriptor Layout(int width, int height)
        => new(
            width,
            height,
            width * 2,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            12,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            4095,
            width * height * 2)
        {
            Readout = new FrameReadoutDescriptor(width, height, 0, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null),
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };

    private static GainCoefficientV1 Coefficient(double gain)
        => Response.ResolveCoefficient(gain).Coefficient!;

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

    private static int Code(ProcessingArtifact artifact, int index)
        => artifact.Payload.Span[index * 2] | artifact.Payload.Span[index * 2 + 1] << 8;

    private static Int128 RoundHalfEven(Int128 numerator, Int128 denominator)
    {
        var (quotient, remainder) = Int128.DivRem(numerator, denominator);
        var doubled = Int128.Abs(remainder) * 2;
        return doubled > denominator || doubled == denominator && !Int128.IsEvenInteger(quotient)
            ? quotient + Int128.Sign(numerator)
            : quotient;
    }

    private static ushort[] Fill(int count, ushort value)
        => Enumerable.Repeat(value, count).ToArray();

    private static byte[] Bytes(IEnumerable<ushort> values)
    {
        var source = values.ToArray();
        var bytes = new byte[source.Length * 2];
        for (var index = 0; index < source.Length; index++)
        {
            bytes[index * 2] = (byte)source[index];
            bytes[index * 2 + 1] = (byte)(source[index] >> 8);
        }
        return bytes;
    }

    private static ushort[] Values(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        }
        return values;
    }

    private sealed record MasterSet(ProcessingArtifact Bias, ProcessingArtifact Dark, ProcessingArtifact Flat, ProcessingArtifact Defect)
    {
        public IReadOnlyList<ProcessingArtifact> All => [Bias, Dark, Flat, Defect];
    }

    private sealed record Entry(ProcessingArtifact Light, double Gain, MasterSet? Member);

    private sealed record SingleInput(
        ProcessingExecutionRequest Request,
        NormalizationPlanV1 Plan,
        ProcessingArtifact Light,
        MasterSet Member);
}
