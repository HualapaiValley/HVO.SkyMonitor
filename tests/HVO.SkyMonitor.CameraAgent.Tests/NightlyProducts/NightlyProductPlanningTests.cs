using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
public sealed class NightlyProductPlanningTests
{
    [TestMethod]
    public void Admission_UsesActualTimeCapturedLocationRigAndPinnedFixedTransfer()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var night = NightlyProductFixture.Frame(1, new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero)).Candidate;
        var twilight = NightlyProductFixture.Frame(2, new(2026, 10, 2, 1, 50, 0, TimeSpan.Zero)).Candidate;
        var day = NightlyProductFixture.Frame(3, new(2026, 10, 1, 21, 0, 0, TimeSpan.Zero)).Candidate;
        var wrongRig = NightlyProductFixture.Frame(4, night.ExposureStartedUtc, new string('C', 64)).Candidate;
        var unlocated = NightlyProductFixture.Frame(5, night.ExposureStartedUtc).Candidate;
        var stretched = NightlyProductFixture.Frame(6, night.ExposureStartedUtc).Candidate with { UsesFixedDisplayTransfer = false };
        var changedRecipe = NightlyProductFixture.Frame(7, night.ExposureStartedUtc).Candidate with { RecipeIdentitySha256 = new('D', 64) };
        ObservatoryLocation? Locate(NightlyProductCandidate candidate) => candidate == unlocated ? null : NightlyProductFixture.Observatory;
        var keogram = NightlyProductAdmission.Admit([night, twilight, day, wrongRig, unlocated, stretched, changedRecipe],
            NightlyProductFixture.RigProfileSha256, NightlyProductFixture.Occurrence(NightlyProductKind.Keogram).SourceWindow!,
            NightlyProductFixture.PreviewRecipe, Locate, ephemeris);
        var trail = NightlyProductAdmission.Admit([night, twilight, day], NightlyProductFixture.RigProfileSha256,
            NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail).SourceWindow!, NightlyProductFixture.PreviewRecipe, Locate, ephemeris);
        CollectionAssert.AreEqual(new[] { night, twilight, day }, keogram.Admitted.ToArray());
        Assert.AreEqual(1, keogram.Exclusions[NightlyProductContract.ExcludedRigReasonCode]);
        Assert.AreEqual(1, keogram.Exclusions[NightlyProductContract.ExcludedLocationReasonCode]);
        Assert.AreEqual(2, keogram.Exclusions[NightlyProductContract.ExcludedTransferReasonCode]);
        CollectionAssert.AreEqual(new[] { night }, trail.Admitted.ToArray());
        Assert.AreEqual(2, trail.Exclusions[NightlyProductContract.ExcludedSolarAltitudeReasonCode]);
    }

    [TestMethod]
    public void Admission_RejectsCaptureAtAnotherSiteEvenForADaytimeKeogram()
    {
        var candidate = NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(1)).Candidate;
        var admission = NightlyProductAdmission.Admit([candidate], NightlyProductFixture.RigProfileSha256,
            NightlyProductFixture.Occurrence(NightlyProductKind.Keogram).SourceWindow!, NightlyProductFixture.PreviewRecipe,
            static _ => new(34, -114, 520, "America/Phoenix"), new AstronomyEnginePlanetEphemeris());
        Assert.IsEmpty(admission.Admitted);
        Assert.AreEqual(1, admission.Exclusions[NightlyProductContract.ExcludedLocationReasonCode]);
    }

    [TestMethod]
    public void Preset_ChangesForSourceRecipeRigSamplingAndBoundsAndRejectsBareTargets()
    {
        var options = NightlyProductFixture.Options();
        var target = NightlyProductPreset.Target(NightlyProductKind.Keogram, options);
        Assert.IsTrue(NightlyProductPreset.TryParseTarget(target, out var kind));
        Assert.AreEqual(NightlyProductKind.Keogram, kind);
        Assert.IsFalse(NightlyProductPreset.TryParseTarget("keogram", out _));
        Assert.IsFalse(NightlyProductPreset.TryParseTarget("keogram:" + new string('Z', 64), out _));
        Assert.AreNotEqual(target, NightlyProductPreset.Target(NightlyProductKind.Keogram, new()
        { SourceNodeId = options.SourceNodeId, SourceRecipeIdentitySha256 = new('B', 64), RigProfileSha256 = options.RigProfileSha256 }));
        Assert.AreNotEqual(target, NightlyProductPreset.Target(NightlyProductKind.Keogram, new()
        { SourceNodeId = options.SourceNodeId, SourceRecipeIdentitySha256 = options.SourceRecipeIdentitySha256, RigProfileSha256 = options.RigProfileSha256,
            KeogramColumnSeconds = 120 }));
    }

    [TestMethod]
    public void Options_EnabledRequiresPinnedInputsAndFiniteBoundedParameters()
    {
        Assert.IsTrue(Valid(new NightlyProductOptions()));
        Assert.IsTrue(Valid(NightlyProductFixture.Options()));
        Assert.IsFalse(Valid(new NightlyProductOptions { Enabled = true, SourceNodeId = "preview" }));
        Assert.IsFalse(Valid(new NightlyProductOptions { Enabled = true, SourceNodeId = " preview",
            SourceRecipeIdentitySha256 = NightlyProductFixture.PreviewRecipe, RigProfileSha256 = NightlyProductFixture.RigProfileSha256 }));
        Assert.IsFalse(Valid(new NightlyProductOptions { MaximumSegmentSources = 513 }));
        Assert.IsFalse(Valid(new NightlyProductOptions { KeogramColumnSeconds = 0 }));
        Assert.IsFalse(Valid(new NightlyProductOptions { KeogramMaximumGapColumnCount = 100, KeogramMaximumColumnCount = 50 }));
    }

    private static bool Valid(object options) => Validator.TryValidateObject(options, new(options), [], true);
}
