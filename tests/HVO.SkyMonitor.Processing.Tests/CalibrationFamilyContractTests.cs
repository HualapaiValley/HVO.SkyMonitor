using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CalibrationFamilyContractTests
{
    private static readonly string RigSha256 = string.Concat(Enumerable.Repeat("A1", 32));
    private static readonly string SensorSha256 = string.Concat(Enumerable.Repeat("B2", 32));
    private static readonly string MaskSha256 = string.Concat(Enumerable.Repeat("C3", 32));
    private static readonly string OtherMaskSha256 = string.Concat(Enumerable.Repeat("D4", 32));
    private static readonly string ModelSha256 = string.Concat(Enumerable.Repeat("E5", 32));
    private static readonly ProfileIdentityDescriptor Model =
        new("virtual-calibration-source-model", "virtual-calibration-source-model-v1", ModelSha256);
    private static readonly ProfileIdentityDescriptor SyntheticModel =
        new("synthetic-calibration-model", "synthetic-calibration-model-v1", ModelSha256);
    private static readonly DateTimeOffset EffectiveFrom = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void EvaluationReturnsTheExactMemberForEachGain()
    {
        var family = Family(Member(0), Member(100), Member(200));

        foreach (var gain in new[] { 0d, 100d, 200d })
        {
            var resolution = CalibrationFamilyContract.Evaluate(family, Input(gain));

            Assert.IsTrue(resolution.IsApplicable, resolution.ReasonCode ?? string.Empty);
            Assert.IsNull(resolution.ReasonCode);
            Assert.AreEqual(FormattableString.Invariant($"gain-{gain}"), resolution.Member!.MemberId);
            Assert.AreEqual(gain, resolution.Member.Gain);
            CollectionAssert.AreEqual(
                CalibrationReferenceKinds.All.Select(kind => ArtifactId(kind, gain)).ToArray(),
                resolution.Member.Masters.Select(static master => master.ArtifactId).ToArray());
        }
        Assert.AreEqual(
            "gain-0",
            CalibrationFamilyContract.Evaluate(family, Input(-0.0)).Member!.MemberId);
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(99.999)]
    [DataRow(100.001)]
    [DataRow(150.0)]
    [DataRow(199.0)]
    [DataRow(201.0)]
    [DataRow(400.0)]
    public void EvaluationNeverSelectsTheNearestGain(double gain)
    {
        var family = Family(Member(0), Member(100), Member(200));

        AssertExcluded(family, Input(gain), CalibrationFamilyReasonCodes.MissingMember);
        AssertExcluded(Family(Member(100)), Input(gain), CalibrationFamilyReasonCodes.MissingMember);
    }

    [TestMethod]
    public void EvaluationRequiresTheExactOffset()
    {
        var family = Family(Member(100));

        AssertExcluded(family, Input(100) with { Offset = 11 }, CalibrationFamilyReasonCodes.MissingMember);
        AssertExcluded(family, Input(100) with { Offset = null }, CalibrationFamilyReasonCodes.MissingMember);

        var withoutOffset = Family(Member(100) with { Offset = null });
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(withoutOffset, Input(100) with { Offset = null }).IsApplicable);
        AssertExcluded(withoutOffset, Input(100), CalibrationFamilyReasonCodes.MissingMember);
    }

    [TestMethod]
    public void EvaluationExcludesAReadoutOrLayoutMismatch()
    {
        var family = Family(Member(100));
        var layout = Layout();
        FrameLayoutDescriptor[] mismatched =
        [
            layout with { Readout = layout.Readout! with { RoiX = 2, NativeWidth = 6 } },
            layout with { Readout = layout.Readout! with { NativeWidth = 6, RoiX = 1, CfaOriginX = 1 } },
            layout with { StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1 },
            layout with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1 },
            layout with { LevelCodeSpace = FrameLevelCodeSpace.StoredContainer },
            layout with { BlackLevel = 65 },
            layout with { SampleDepthBits = 14, WhiteLevel = 16_383 }
        ];

        foreach (var value in mismatched)
        {
            Assert.IsTrue(value.Validate().IsValid, value.ToString());
            AssertExcluded(family, Input(100) with { Layout = value }, CalibrationFamilyReasonCodes.ReadoutMismatch);
        }
    }

    [TestMethod]
    public void EvaluationExcludesTemperatureOutsideTheRange()
    {
        var family = Family(Member(100));

        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { TemperatureC = -5 }).IsApplicable);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { TemperatureC = 5 }).IsApplicable);
        AssertExcluded(family, Input(100) with { TemperatureC = -5.01 }, CalibrationFamilyReasonCodes.TemperatureOutOfRange);
        AssertExcluded(family, Input(100) with { TemperatureC = 5.01 }, CalibrationFamilyReasonCodes.TemperatureOutOfRange);
        AssertExcluded(family, Input(100) with { TemperatureC = null }, CalibrationFamilyReasonCodes.TemperatureUnavailable);
    }

    [TestMethod]
    public void EvaluationExcludesExposureOutsideTheRange()
    {
        var family = Family(Member(100) with
        {
            MinimumLightExposure = TimeSpan.FromSeconds(5),
            MaximumLightExposure = TimeSpan.FromSeconds(20)
        });

        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { Exposure = TimeSpan.FromSeconds(5) }).IsApplicable);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { Exposure = TimeSpan.FromSeconds(20) }).IsApplicable);
        AssertExcluded(
            family,
            Input(100) with { Exposure = TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1) },
            CalibrationFamilyReasonCodes.ExposureOutOfRange);
        AssertExcluded(
            family,
            Input(100) with { Exposure = TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(1) },
            CalibrationFamilyReasonCodes.ExposureOutOfRange);
    }

    [TestMethod]
    public void EvaluationExcludesAStaleMember()
    {
        var until = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var family = Family(Member(100) with { EffectiveUntilUtc = until });

        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { ObservedUtc = EffectiveFrom }).IsApplicable);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, Input(100) with { ObservedUtc = until.AddTicks(-1) }).IsApplicable);
        AssertExcluded(family, Input(100) with { ObservedUtc = EffectiveFrom.AddTicks(-1) }, CalibrationFamilyReasonCodes.Stale);
        AssertExcluded(family, Input(100) with { ObservedUtc = until }, CalibrationFamilyReasonCodes.Stale);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(Family(Member(100)), Input(100) with { ObservedUtc = until }).IsApplicable);
    }

    [TestMethod]
    public void EvaluationExcludesAnotherMaskFamilyOrIdentity()
    {
        var family = Family(Member(100));

        AssertExcluded(family, Input(100) with { MaskProfileSha256 = OtherMaskSha256 }, CalibrationFamilyReasonCodes.MaskFamilyMismatch);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(
            family, Input(100) with { MaskProfileSha256 = Lowercase(MaskSha256) }).IsApplicable);
        CalibrationFamilyInputV1[] otherIdentity =
        [
            Input(100) with { AgentId = "agent-b" },
            Input(100) with { RigId = "rig-b" },
            Input(100) with { RigProfileSha256 = OtherMaskSha256 },
            Input(100) with { SensorProfileSha256 = OtherMaskSha256 }
        ];
        foreach (var input in otherIdentity)
        {
            AssertExcluded(family, input, CalibrationFamilyReasonCodes.IdentityMismatch);
        }
    }

    [TestMethod]
    public void EvaluationExcludesALightFromAnotherAcquisitionModel()
    {
        var family = Family(Member(100));

        Assert.IsTrue(CalibrationFamilyContract.Evaluate(
            family, Input(100) with { CalibrationProfile = Model with { Sha256 = Lowercase(ModelSha256) } }).IsApplicable);
        Assert.IsTrue(CalibrationFamilyContract.Evaluate(
            family with { AcquisitionModel = SyntheticModel },
            Input(100) with { CalibrationProfile = SyntheticModel }).IsApplicable);
        ProfileIdentityDescriptor[] otherModel =
        [
            Model with { Sha256 = OtherMaskSha256 },
            Model with { Version = "virtual-calibration-source-model-v2" },
            Model with { Name = "Virtual-Calibration-Source-Model" },
            SyntheticModel,
            new("calibration", "none-v1", ModelSha256),
            new("configured-calibration", "configured-v1", ModelSha256)
        ];
        foreach (var profile in otherModel)
        {
            AssertExcluded(family, Input(100) with { CalibrationProfile = profile }, CalibrationFamilyReasonCodes.IdentityMismatch);
        }
    }

    [TestMethod]
    public void EvaluationExcludesAnInvalidFamilyOrInvalidConditions()
    {
        var overlapping = Family(Member(100), Member(100, "gain-100-b") with { MaximumTemperatureC = 10 });
        AssertExcluded(overlapping, Input(100), CalibrationFamilyReasonCodes.InvalidFamily);
        AssertExcluded(Family(Member(100)) with { SchemaVersion = "hvo-calibration-family-v2" }, Input(100), CalibrationFamilyReasonCodes.InvalidFamily);

        var family = Family(Member(100));
        CalibrationFamilyInputV1[] invalid =
        [
            Input(double.NaN),
            Input(double.PositiveInfinity),
            Input(100) with { Offset = double.NaN },
            Input(100) with { TemperatureC = double.NegativeInfinity },
            Input(100) with { Exposure = TimeSpan.Zero },
            Input(100) with { Layout = Layout() with { StrideBytes = 1 } },
            Input(100) with { Layout = null! },
            Input(100) with { AgentId = " " },
            Input(100) with { SensorProfileSha256 = "not-a-hash" },
            Input(100) with { CalibrationProfile = null! },
            Input(100) with { CalibrationProfile = Model with { Name = " " } },
            Input(100) with { CalibrationProfile = Model with { Version = string.Empty } },
            Input(100) with { CalibrationProfile = Model with { Sha256 = "not-a-hash" } }
        ];
        foreach (var input in invalid)
        {
            AssertExcluded(family, input, CalibrationFamilyReasonCodes.InvalidConditions);
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => CalibrationFamilyContract.Evaluate(null!, Input(100)));
        Assert.ThrowsExactly<ArgumentNullException>(() => CalibrationFamilyContract.Evaluate(family, null!));
    }

    [TestMethod]
    public void EvaluationReportsTheEarlierOfTwoFailingChecks()
    {
        var until = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var family = Family(Member(100) with
        {
            MinimumLightExposure = TimeSpan.FromSeconds(5),
            MaximumLightExposure = TimeSpan.FromSeconds(20),
            EffectiveUntilUtc = until
        });
        var input = Input(100);
        var otherLayout = Layout() with { BlackLevel = 65 };
        var longExposure = TimeSpan.FromSeconds(30);
        (CalibrationFamilyInputV1 Both, CalibrationFamilyInputV1 LaterOnly, string Earlier, string Later)[] pairs =
        [
            (input with { Gain = double.NaN, AgentId = "agent-b" }, input with { AgentId = "agent-b" },
                CalibrationFamilyReasonCodes.InvalidConditions, CalibrationFamilyReasonCodes.IdentityMismatch),
            (input with { AgentId = "agent-b", MaskProfileSha256 = OtherMaskSha256 }, input with { MaskProfileSha256 = OtherMaskSha256 },
                CalibrationFamilyReasonCodes.IdentityMismatch, CalibrationFamilyReasonCodes.MaskFamilyMismatch),
            (input with { CalibrationProfile = SyntheticModel, MaskProfileSha256 = OtherMaskSha256 }, input with { MaskProfileSha256 = OtherMaskSha256 },
                CalibrationFamilyReasonCodes.IdentityMismatch, CalibrationFamilyReasonCodes.MaskFamilyMismatch),
            (input with { MaskProfileSha256 = OtherMaskSha256, Gain = 101 }, input with { Gain = 101 },
                CalibrationFamilyReasonCodes.MaskFamilyMismatch, CalibrationFamilyReasonCodes.MissingMember),
            (input with { Gain = 101, Layout = otherLayout }, input with { Layout = otherLayout },
                CalibrationFamilyReasonCodes.MissingMember, CalibrationFamilyReasonCodes.ReadoutMismatch),
            (input with { Layout = otherLayout, TemperatureC = null }, input with { TemperatureC = null },
                CalibrationFamilyReasonCodes.ReadoutMismatch, CalibrationFamilyReasonCodes.TemperatureUnavailable),
            (input with { TemperatureC = null, Exposure = longExposure }, input with { Exposure = longExposure },
                CalibrationFamilyReasonCodes.TemperatureUnavailable, CalibrationFamilyReasonCodes.ExposureOutOfRange),
            (input with { TemperatureC = 50, Exposure = longExposure }, input with { Exposure = longExposure },
                CalibrationFamilyReasonCodes.TemperatureOutOfRange, CalibrationFamilyReasonCodes.ExposureOutOfRange),
            (input with { Exposure = longExposure, ObservedUtc = until }, input with { ObservedUtc = until },
                CalibrationFamilyReasonCodes.ExposureOutOfRange, CalibrationFamilyReasonCodes.Stale)
        ];
        foreach (var (both, laterOnly, earlier, later) in pairs)
        {
            AssertExcluded(family, laterOnly, later);
            AssertExcluded(family, both, earlier);
        }

        var invalidFamily = family with { SchemaVersion = "hvo-calibration-family-v2" };
        AssertExcluded(family, input with { Gain = double.NaN }, CalibrationFamilyReasonCodes.InvalidConditions);
        AssertExcluded(invalidFamily, input with { Gain = double.NaN }, CalibrationFamilyReasonCodes.InvalidFamily);
    }

    [TestMethod]
    public void EvaluationSplitsOneGainByTemperatureAndExposureIndependentlyOfMemberOrder()
    {
        var cold = Member(100, "gain-100-cold") with { MinimumTemperatureC = -10, MaximumTemperatureC = -0.5 };
        var warm = Member(100, "gain-100-warm") with { MinimumTemperatureC = 0, MaximumTemperatureC = 10 };
        var longExposure = Member(100, "gain-100-long") with
        {
            MinimumTemperatureC = -10,
            MaximumTemperatureC = 10,
            MinimumLightExposure = TimeSpan.FromSeconds(30),
            MaximumLightExposure = TimeSpan.FromSeconds(30)
        };
        var members = new[] { cold, warm, longExposure };

        foreach (var family in new[] { Family(members), Family([.. Enumerable.Reverse(members)]) })
        {
            Assert.AreEqual("gain-100-cold", CalibrationFamilyContract.Evaluate(family, Input(100) with { TemperatureC = -5 }).Member!.MemberId);
            Assert.AreEqual("gain-100-warm", CalibrationFamilyContract.Evaluate(family, Input(100) with { TemperatureC = 5 }).Member!.MemberId);
            Assert.AreEqual(
                "gain-100-long",
                CalibrationFamilyContract.Evaluate(family, Input(100) with { Exposure = TimeSpan.FromSeconds(30) }).Member!.MemberId);
            AssertExcluded(family, Input(100) with { TemperatureC = 50 }, CalibrationFamilyReasonCodes.TemperatureOutOfRange);
            AssertExcluded(
                family,
                Input(100) with { TemperatureC = 7, Exposure = TimeSpan.FromSeconds(20) },
                CalibrationFamilyReasonCodes.ExposureOutOfRange);
        }
    }

    [TestMethod]
    public void ValidationRejectsDuplicateMembers()
    {
        AssertInvalid(Family(Member(0), Member(100, "gain-0")), CalibrationFamilyReasonCodes.DuplicateMember, "members.memberId");
        AssertInvalid(Family(Member(100), Member(100, "gain-100-copy")), CalibrationFamilyReasonCodes.DuplicateMember, "members");
        AssertInvalid(
            Family(Member(100), Member(100, "gain-100-copy") with { Masters = Masters(101) }),
            CalibrationFamilyReasonCodes.DuplicateMember,
            "members");
    }

    [TestMethod]
    public void ValidationRejectsOverlappingMembersOnly()
    {
        var member = Member(100) with
        {
            MinimumLightExposure = TimeSpan.FromSeconds(5),
            MaximumLightExposure = TimeSpan.FromSeconds(20)
        };
        CalibrationFamilyMemberV1[] overlapping =
        [
            member with { MemberId = "touching-temperature", MinimumTemperatureC = 5, MaximumTemperatureC = 10 },
            member with { MemberId = "inner-temperature", MinimumTemperatureC = -1, MaximumTemperatureC = 1 },
            member with { MemberId = "touching-exposure", MinimumLightExposure = TimeSpan.FromSeconds(20), MaximumLightExposure = TimeSpan.FromSeconds(30) },
            member with { MemberId = "wider-exposure", MinimumLightExposure = TimeSpan.FromSeconds(1) }
        ];
        foreach (var other in overlapping)
        {
            AssertInvalid(Family(member, other with { Masters = Masters(101) }), CalibrationFamilyReasonCodes.OverlappingMembers, "members");
        }

        CalibrationFamilyMemberV1[] disjoint =
        [
            member with { MemberId = "warmer", MinimumTemperatureC = 5.01, MaximumTemperatureC = 10 },
            member with { MemberId = "longer", MinimumLightExposure = TimeSpan.FromSeconds(20.001), MaximumLightExposure = TimeSpan.FromSeconds(30) },
            member with { MemberId = "other-gain", Gain = 101 },
            member with { MemberId = "other-offset", Offset = 11 },
            member with { MemberId = "other-readout", InputLayout = Layout() with { SampleDepthBits = 14, WhiteLevel = 16_383 } }
        ];
        foreach (var other in disjoint)
        {
            Assert.IsTrue(CalibrationFamilyContract.Validate(Family(member, other with { Masters = Masters(101) })).IsValid, other.MemberId);
        }
    }

    [TestMethod]
    public void ValidationRejectsABiasDarkOrFlatSharedAcrossGainOrOffset()
    {
        foreach (var kind in new[] { CalibrationReferenceKinds.Bias, CalibrationReferenceKinds.Dark, CalibrationReferenceKinds.Flat })
        {
            var shared = Masters(0).Single(master => master.Kind == kind);
            var sharing = Masters(100).Select(master => master.Kind == kind ? shared : master).ToArray();

            AssertInvalid(Family(Member(0), Member(100) with { Masters = sharing }), CalibrationFamilyReasonCodes.InvalidContract, "members.masters");
            AssertInvalid(
                Family(Member(0), Member(0, "offset-11") with { Offset = 11, Masters = sharing }),
                CalibrationFamilyReasonCodes.InvalidContract,
                "members.masters");
            AssertInvalid(
                Family(Member(0), Member(0, "no-offset") with { Offset = null, Masters = sharing }),
                CalibrationFamilyReasonCodes.InvalidContract,
                "members.masters");
        }

        var cold = Member(0, "gain-0-cold") with { MinimumTemperatureC = -10, MaximumTemperatureC = -6 };
        Assert.IsTrue(CalibrationFamilyContract.Validate(Family(cold, Member(0))).IsValid);
        Assert.IsTrue(CalibrationFamilyContract.Validate(Family(cold, Member(-0.0) with { Masters = Masters(0) })).IsValid);
    }

    [TestMethod]
    public void ValidationReportsTheSameFieldInEitherMemberOrder()
    {
        var invalidOffset = Member(0) with { Offset = double.NaN };
        var invalidTemperature = Member(100) with { MinimumTemperatureC = double.NaN };

        AssertInvalid(Family(invalidOffset, invalidTemperature), CalibrationFamilyReasonCodes.InvalidContract, "members.offset");
        AssertInvalid(Family(invalidTemperature, invalidOffset), CalibrationFamilyReasonCodes.InvalidContract, "members.offset");
    }

    [TestMethod]
    public void ValidationRejectsABiasDarkOrFlatSharedAcrossInputLayouts()
    {
        var otherLayout = Layout() with { SampleDepthBits = 14, WhiteLevel = 16_383 };
        foreach (var kind in new[] { CalibrationReferenceKinds.Bias, CalibrationReferenceKinds.Dark, CalibrationReferenceKinds.Flat })
        {
            var shared = Masters(100).Single(master => master.Kind == kind);
            var sharing = Masters(101).Select(master => master.Kind == kind ? shared : master).ToArray();

            AssertInvalid(
                Family(Member(100), Member(100, "gain-100-14-bit") with { InputLayout = otherLayout, Masters = sharing }),
                CalibrationFamilyReasonCodes.InvalidContract,
                "members.masters");
        }

        var defect = Masters(100).Single(master => master.Kind == CalibrationReferenceKinds.Defect);
        Assert.IsTrue(CalibrationFamilyContract.Validate(Family(
            Member(100),
            Member(100, "gain-100-14-bit") with
            {
                InputLayout = otherLayout,
                Masters = [.. Masters(101).Select(master => master.Kind == CalibrationReferenceKinds.Defect ? defect : master)]
            })).IsValid);
    }

    [TestMethod]
    public void ValidationReportsADuplicateMemberIdBeforeAnyMemberField()
    {
        var invalidLayout = Member(100, "duplicate") with { InputLayout = Layout() with { Readout = null } };
        var invalidTemperature = Member(100, "duplicate") with { MinimumTemperatureC = double.NaN };

        AssertInvalid(Family(invalidLayout, invalidTemperature), CalibrationFamilyReasonCodes.DuplicateMember, "members.memberId");
        AssertInvalid(Family(invalidTemperature, invalidLayout), CalibrationFamilyReasonCodes.DuplicateMember, "members.memberId");
    }

    [TestMethod]
    public void ValidationRejectsInconsistentMaskFamilies()
    {
        AssertInvalid(
            Family(Member(0), Member(100) with { MaskProfileSha256 = OtherMaskSha256 }),
            CalibrationFamilyReasonCodes.InconsistentMaskFamily,
            "members.maskProfileSha256");
        AssertInvalid(
            Family(Member(0), Member(100)) with { MaskProfileSha256 = OtherMaskSha256 },
            CalibrationFamilyReasonCodes.InconsistentMaskFamily,
            "members.maskProfileSha256");
        Assert.IsTrue(CalibrationFamilyContract.Validate(
            Family(Member(0), Member(100) with { MaskProfileSha256 = Lowercase(MaskSha256) })).IsValid);
    }

    [TestMethod]
    public void ValidationRejectsMalformedFamilies()
    {
        var family = Family(Member(0), Member(100));
        var member = Member(100);
        var masters = Masters(100);
        (CalibrationFamilyV1 Family, string Path)[] rejected =
        [
            (family with { SchemaVersion = "hvo-calibration-family-v2" }, "schemaVersion"),
            (family with { FamilyId = " " }, "familyId"),
            (family with { FamilyId = new string('f', 129) }, "familyId"),
            (family with { FamilyId = "family-\uD800" }, "familyId"),
            (family with { AgentId = string.Empty }, "agentId"),
            (family with { RigId = null! }, "rigId"),
            (family with { RigProfileSha256 = "not-a-hash" }, "rigProfileSha256"),
            (family with { SensorProfileSha256 = new string('G', 64) }, "sensorProfileSha256"),
            (family with { AcquisitionModel = null! }, "acquisitionModel"),
            (family with { AcquisitionModel = Model with { Sha256 = "not-a-hash" } }, "acquisitionModel"),
            (family with { AcquisitionModel = Model with { Version = "virtual-calibration-source-model-v2" } }, "acquisitionModel"),
            (family with { AcquisitionModel = Model with { Version = SyntheticModel.Version } }, "acquisitionModel"),
            (family with { AcquisitionModel = new ProfileIdentityDescriptor("calibration", "none-v1", ModelSha256) }, "acquisitionModel"),
            (family with { MaskProfileSha256 = MaskSha256[1..] }, "maskProfileSha256"),
            (family with { Members = [] }, "members"),
            (family with { Members = null! }, "members"),
            (family with { Members = [member, null!] }, "members"),
            (family with
            {
                Members = [.. Enumerable.Range(0, CalibrationFamilyV1.MaximumMembers + 1).Select(gain => Member(gain))]
            }, "members"),
            (Family(member with { MemberId = " " }), "members.memberId"),
            (Family(member with { MemberId = "gain-\uDC00-100" }), "members.memberId"),
            (Family(member with { Gain = double.NaN }), "members.gain"),
            (Family(member with { Gain = -1 }), "members.gain"),
            (Family(member with { Offset = double.PositiveInfinity }), "members.offset"),
            (Family(member with { InputLayout = null! }), "members.inputLayout"),
            (Family(member with { InputLayout = Layout() with { Readout = null } }), "members.inputLayout"),
            (Family(member with
            {
                InputLayout = Layout() with { SampleDepthBits = 16, WhiteLevel = 65_535, StoredCodeTransform = null, LevelCodeSpace = null }
            }), "members.inputLayout"),
            (Family(member with { InputLayout = Layout() with { StoredCodeTransform = null } }), "members.inputLayout"),
            (Family(member with { InputLayout = Layout() with { LevelCodeSpace = null } }), "members.inputLayout"),
            (Family(member with { InputLayout = Layout() with { ByteOrder = FrameByteOrder.BigEndian } }), "members.inputLayout"),
            (Family(member with { InputLayout = Layout() with { StrideBytes = 1 } }), "members.inputLayout"),
            (Family(member with { MinimumTemperatureC = double.NaN }), "members.temperatureC"),
            (Family(member with { MinimumTemperatureC = 6 }), "members.temperatureC"),
            (Family(member with { MinimumLightExposure = TimeSpan.Zero }), "members.lightExposure"),
            (Family(member with { MaximumLightExposure = TimeSpan.FromSeconds(9) }), "members.lightExposure"),
            (Family(member with { EffectiveFromUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-7)) }), "members.effectiveUtc"),
            (Family(member with { EffectiveUntilUtc = EffectiveFrom }), "members.effectiveUtc"),
            (Family(member with { EffectiveUntilUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(1)) }), "members.effectiveUtc"),
            (Family(member with { MaskProfileSha256 = "not-a-hash" }), "members.maskProfileSha256"),
            (Family(member with { Masters = [.. masters.Take(3)] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), masters[0] with { ArtifactId = Guid.NewGuid() }] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), masters[3] with { Kind = "mask" }] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), null!] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), masters[3] with { ArtifactId = Guid.Empty }] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), masters[3] with { PayloadSha256 = "not-a-hash" }] }), "members.masters"),
            (Family(member with { Masters = [.. masters.Take(3), masters[3] with { ArtifactId = masters[0].ArtifactId }] }), "members.masters"),
            (Family(Member(0), member with { Masters = [masters[0] with { ArtifactId = ArtifactId(CalibrationReferenceKinds.Bias, 0) }, .. masters.Skip(1)] }), "members.masters")
        ];

        foreach (var (value, path) in rejected)
        {
            AssertInvalid(value, CalibrationFamilyReasonCodes.InvalidContract, path);
        }
        Assert.IsTrue(CalibrationFamilyContract.Validate(
            Family(Member(0), member with { Masters = [.. masters.Take(3), Member(0).Masters[3]] })).IsValid);
    }

    [TestMethod]
    public void FamilySerializesToPinnedCanonicalBytesAndIdentity()
    {
        var family = Family(Member(100));
        var expectedJson =
            "{\"acquisitionModel\":{\"name\":\"virtual-calibration-source-model\"," +
            $"\"sha256\":\"{ModelSha256}\",\"version\":\"virtual-calibration-source-model-v1\"}}," +
            "\"agentId\":\"agent-a\",\"familyId\":\"virtual-family\"," +
            $"\"maskProfileSha256\":\"{MaskSha256}\"," +
            "\"members\":[{\"effectiveFromUtc\":\"2026-10-01T00:00:00\\u002B00:00\",\"effectiveUntilUtc\":null,\"gain\":100," +
            "\"inputLayout\":{\"blackLevel\":64,\"byteLength\":16,\"byteOrder\":\"LittleEndian\",\"cfaPattern\":\"Rggb\"," +
            "\"containerDepthBits\":16,\"height\":2,\"levelCodeSpace\":\"NativeSample\",\"packing\":\"ByteAligned\"," +
            "\"pixelFormat\":\"BayerRggb16\",\"readout\":{\"binX\":1,\"binY\":1,\"binningAlgorithm\":\"IdentityV1\"," +
            "\"cfaOriginX\":0,\"cfaOriginY\":0,\"nativeHeight\":2,\"nativeWidth\":4,\"roiHeight\":2,\"roiWidth\":4," +
            "\"roiX\":0,\"roiY\":0},\"sampleDepthBits\":12,\"storedCodeTransform\":\"RightAlignedV1\",\"strideBytes\":8," +
            "\"whiteLevel\":4095,\"width\":4}," +
            $"\"maskProfileSha256\":\"{MaskSha256}\"," +
            "\"masters\":[" +
            $"{{\"artifactId\":\"{ArtifactId(CalibrationReferenceKinds.Bias, 100)}\",\"kind\":\"bias\",\"payloadSha256\":\"{PayloadSha256(CalibrationReferenceKinds.Bias, 100)}\"}}," +
            $"{{\"artifactId\":\"{ArtifactId(CalibrationReferenceKinds.Dark, 100)}\",\"kind\":\"dark\",\"payloadSha256\":\"{PayloadSha256(CalibrationReferenceKinds.Dark, 100)}\"}}," +
            $"{{\"artifactId\":\"{ArtifactId(CalibrationReferenceKinds.Flat, 100)}\",\"kind\":\"flat\",\"payloadSha256\":\"{PayloadSha256(CalibrationReferenceKinds.Flat, 100)}\"}}," +
            $"{{\"artifactId\":\"{ArtifactId(CalibrationReferenceKinds.Defect, 100)}\",\"kind\":\"defect\",\"payloadSha256\":\"{PayloadSha256(CalibrationReferenceKinds.Defect, 100)}\"}}]," +
            "\"maximumLightExposure\":\"00:00:10\",\"maximumTemperatureC\":5,\"memberId\":\"gain-100\"," +
            "\"minimumLightExposure\":\"00:00:10\",\"minimumTemperatureC\":-5,\"offset\":10}]," +
            $"\"rigId\":\"rig-a\",\"rigProfileSha256\":\"{RigSha256}\",\"schemaVersion\":\"hvo-calibration-family-v1\"," +
            $"\"sensorProfileSha256\":\"{SensorSha256}\"}}";

        var bytes = CalibrationFamilyContractJson.Serialize(family);

        Assert.AreEqual(expectedJson, Encoding.UTF8.GetString(bytes));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedJson))), CalibrationFamilyContractJson.ComputeIdentitySha256(family));
        Assert.AreEqual("C1DF89728362BD59149A4AD5F20DDC13A57EF023D38AC61EDAC039D178D4DB3B", CalibrationFamilyContractJson.ComputeIdentitySha256(family));
    }

    [TestMethod]
    public void FamilyIdentityIsIndependentOfOrderCaseAndNegativeZero()
    {
        var family = Family(Member(0), Member(100), Member(200));
        var bytes = CalibrationFamilyContractJson.Serialize(family);
        var reordered = Family(
            Member(200) with { Masters = [.. Enumerable.Reverse(Masters(200))] },
            Member(0) with { Gain = -0.0, MinimumTemperatureC = -5, InputLayout = Layout() },
            Member(100) with { MaskProfileSha256 = Lowercase(MaskSha256) }) with
        {
            RigProfileSha256 = Lowercase(RigSha256)
        };

        CollectionAssert.AreEqual(bytes, CalibrationFamilyContractJson.Serialize(reordered));
        Assert.AreEqual(
            CalibrationFamilyContractJson.ComputeIdentitySha256(family),
            CalibrationFamilyContractJson.ComputeIdentitySha256(reordered));
        var parsed = CalibrationFamilyContractJson.Parse(bytes);
        Assert.IsTrue(parsed.Validation.IsValid);
        Assert.AreEqual("gain-0,gain-100,gain-200", string.Join(',', parsed.Value!.Members.Select(static member => member.MemberId)));
    }

    [TestMethod]
    public void FamilyIdentityNormalizesEachFieldOnItsOwn()
    {
        var member = Member(100);
        var zeroLevels = Layout() with { BlackLevel = 0, WhiteLevel = 0 };
        (CalibrationFamilyV1 Normalized, CalibrationFamilyV1 Variant)[] cases =
        [
            (Family(member with { Gain = 0 }), Family(member with { Gain = -0.0 })),
            (Family(member with { Offset = 0 }), Family(member with { Offset = -0.0 })),
            (Family(member with { InputLayout = zeroLevels }), Family(member with { InputLayout = zeroLevels with { BlackLevel = -0.0 } })),
            (Family(member with { InputLayout = zeroLevels }), Family(member with { InputLayout = zeroLevels with { WhiteLevel = -0.0 } })),
            (Family(member with { MinimumTemperatureC = 0 }), Family(member with { MinimumTemperatureC = -0.0 })),
            (Family(member with { MaximumTemperatureC = 0 }), Family(member with { MaximumTemperatureC = -0.0 })),
            (Family(member), Family(member) with { RigProfileSha256 = Lowercase(RigSha256) }),
            (Family(member), Family(member) with { SensorProfileSha256 = Lowercase(SensorSha256) }),
            (Family(member), Family(member) with { AcquisitionModel = Model with { Sha256 = Lowercase(ModelSha256) } }),
            (Family(member), Family(member) with { MaskProfileSha256 = Lowercase(MaskSha256) }),
            (Family(member), Family(member with { MaskProfileSha256 = Lowercase(MaskSha256) })),
            (Family(member), Family(member with
            {
                Masters = [.. member.Masters.Select(static master => master with { PayloadSha256 = Lowercase(master.PayloadSha256) })]
            }))
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var (normalized, variant) = cases[index];
            var message = FormattableString.Invariant($"case {index}");
            CollectionAssert.AreEqual(CalibrationFamilyContractJson.Serialize(normalized), CalibrationFamilyContractJson.Serialize(variant), message);
            Assert.AreEqual(
                CalibrationFamilyContractJson.ComputeIdentitySha256(normalized),
                CalibrationFamilyContractJson.ComputeIdentitySha256(variant),
                message);
        }
    }

    [TestMethod]
    public void FamilyIdentityChangesWithEveryApplicabilityField()
    {
        var member = Member(100);
        var family = Family(member);
        CalibrationFamilyV1[] changed =
        [
            family with { FamilyId = "virtual-family-2" },
            family with { AgentId = "agent-b" },
            family with { RigId = "rig-b" },
            family with { RigProfileSha256 = OtherMaskSha256 },
            family with { SensorProfileSha256 = OtherMaskSha256 },
            family with { AcquisitionModel = Model with { Sha256 = OtherMaskSha256 } },
            family with { AcquisitionModel = SyntheticModel },
            Family(member with { MaskProfileSha256 = OtherMaskSha256 }) with { MaskProfileSha256 = OtherMaskSha256 },
            Family(member with { MemberId = "gain-100-b" }),
            Family(member with { Gain = 101 }),
            Family(member with { Offset = 11 }),
            Family(member with { Offset = null }),
            Family(member with { InputLayout = Layout() with { BlackLevel = 65 } }),
            Family(member with { MinimumTemperatureC = -6 }),
            Family(member with { MaximumTemperatureC = 6 }),
            Family(member with { MinimumLightExposure = TimeSpan.FromSeconds(9) }),
            Family(member with { MaximumLightExposure = TimeSpan.FromSeconds(11) }),
            Family(member with { EffectiveFromUtc = EffectiveFrom.AddDays(1) }),
            Family(member with { EffectiveUntilUtc = EffectiveFrom.AddDays(30) }),
            Family(member with { Masters = [.. Masters(100).Take(3), Masters(100)[3] with { ArtifactId = ArtifactId("defect", 101) }] }),
            Family(member with { Masters = [.. Masters(100).Take(3), Masters(100)[3] with { PayloadSha256 = OtherMaskSha256 }] })
        ];

        var identities = changed.Select(CalibrationFamilyContractJson.ComputeIdentitySha256)
            .Append(CalibrationFamilyContractJson.ComputeIdentitySha256(family));
        Assert.AreEqual(changed.Length + 1, identities.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void FamilyParseAcceptsOnlyCanonicalBytes()
    {
        var bytes = CalibrationFamilyContractJson.Serialize(Family(Member(0), Member(100)));
        var json = Encoding.UTF8.GetString(bytes);

        var parsed = CalibrationFamilyContractJson.Parse(bytes);
        Assert.IsTrue(parsed.Validation.IsValid);
        CollectionAssert.AreEqual(bytes, CalibrationFamilyContractJson.Serialize(parsed.Value!));

        var reversedMembers = JsonNode.Parse(json)!;
        reversedMembers["members"] = new JsonArray([.. reversedMembers["members"]!.AsArray().Reverse().Select(static node => node!.DeepClone())]);
        var reversedMasters = JsonNode.Parse(json)!;
        var firstMember = reversedMasters["members"]![0]!;
        firstMember["masters"] = new JsonArray([.. firstMember["masters"]!.AsArray().Reverse().Select(static node => node!.DeepClone())]);
        var bias = PayloadSha256(CalibrationReferenceKinds.Bias, 0);

        AssertParseRejected(json + " ", CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace(",", ", ", StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(reversedMembers.ToJsonString(), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(reversedMasters.ToJsonString(), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace(bias, Lowercase(bias), StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace("\"gain\":100,", "\"gain\":100.0,", StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace("\"00:00:10\"", "\"0:00:10\"", StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace("{\"acquisitionModel\"", "{\"extra\":1,\"acquisitionModel\"", StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(json.Replace("{\"acquisitionModel\"", "{\"familyId\":\"other\",\"acquisitionModel\"", StringComparison.Ordinal), CalibrationFamilyReasonCodes.NonCanonicalJson);
        AssertParseRejected(
            json.Replace("\"gain\":100,", "\"gain\":0,", StringComparison.Ordinal),
            CalibrationFamilyReasonCodes.DuplicateMember);
        AssertParseRejected(
            json.Replace($"\"maskProfileSha256\":\"{MaskSha256}\",\"members\"", $"\"maskProfileSha256\":\"{OtherMaskSha256}\",\"members\"", StringComparison.Ordinal),
            CalibrationFamilyReasonCodes.InconsistentMaskFamily);
        AssertParseRejected(json.Replace("\"gain\":100,", "\"gain\":-1,", StringComparison.Ordinal), CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected(json.Replace("family-v1", "family-v2", StringComparison.Ordinal), CalibrationFamilyReasonCodes.UnsupportedSchema);
        AssertParseRejected(json.Replace("\"LittleEndian\"", "1", StringComparison.Ordinal), CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected(json.Replace("\"memberId\":\"gain-0\",", string.Empty, StringComparison.Ordinal), CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected("[]", CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected("null", CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected("{", CalibrationFamilyReasonCodes.InvalidContract);
        AssertParseRejected(new string(' ', CalibrationFamilyContractJson.MaximumContractBytes + 1), CalibrationFamilyReasonCodes.InvalidContract);
    }

    [TestMethod]
    public void TheLargestValidFamilyRoundTripsWithinTheContractBound()
    {
        var family = LargestFamily();
        Assert.IsTrue(CalibrationFamilyContract.Validate(family).IsValid);

        var bytes = CalibrationFamilyContractJson.Serialize(family);
        var parsed = CalibrationFamilyContractJson.Parse(bytes);

        Assert.IsLessThanOrEqualTo(CalibrationFamilyContractJson.MaximumContractBytes, bytes.Length);
        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        CollectionAssert.AreEqual(bytes, CalibrationFamilyContractJson.Serialize(parsed.Value!));
        Assert.AreEqual(
            CalibrationFamilyContractJson.ComputeIdentitySha256(family),
            CalibrationFamilyContractJson.ComputeIdentitySha256(parsed.Value!));
    }

    /// <summary>
    /// The most members, each token at the length limit with every UTF-16 unit escaped to six bytes, and numbers, times
    /// and layout values in their longest forms.
    /// </summary>
    private static CalibrationFamilyV1 LargestFamily()
    {
        static string Token(int index)
            => string.Concat(Enumerable.Repeat("<+\u00E9\uD83D\uDE00", 25)) +
               string.Concat(new[] { index / 64, index / 8 % 8, index % 8 }.Select(static digit => (char)('\u4E00' + digit)));

        var layout = new FrameLayoutDescriptor(
            1_073_741_823, int.MaxValue, 2_147_483_646, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb,
            2.2250738585072014E-308, 0.30000000000000004, 2_147_483_646L * int.MaxValue)
        {
            StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
            Readout = new FrameReadoutDescriptor(
                int.MaxValue, int.MaxValue, 1_073_741_824, 0, 1_073_741_823, int.MaxValue, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0)
        };
        var members = Enumerable.Range(0, CalibrationFamilyV1.MaximumMembers)
            .Select(index => new CalibrationFamilyMemberV1(
                Token(index),
                BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(double.MaxValue) - index),
                -double.MaxValue,
                layout,
                -double.MaxValue,
                double.MaxValue,
                TimeSpan.MaxValue - TimeSpan.FromTicks(1),
                TimeSpan.MaxValue,
                DateTimeOffset.MaxValue.AddTicks(-1),
                DateTimeOffset.MaxValue,
                MaskSha256,
                Masters(index)))
            .ToArray();
        return Family(members) with { FamilyId = Token(0), AgentId = Token(1), RigId = Token(2) };
    }

    private static void AssertExcluded(CalibrationFamilyV1 family, CalibrationFamilyInputV1 input, string reasonCode)
    {
        var resolution = CalibrationFamilyContract.Evaluate(family, input);
        Assert.IsFalse(resolution.IsApplicable, input.ToString());
        Assert.IsNull(resolution.Member);
        Assert.AreEqual(reasonCode, resolution.ReasonCode, input.ToString());
    }

    private static void AssertInvalid(CalibrationFamilyV1 family, string reasonCode, string path)
    {
        var validation = CalibrationFamilyContract.Validate(family);
        Assert.IsFalse(validation.IsValid, path);
        Assert.AreEqual(reasonCode, validation.ReasonCode, path);
        Assert.AreEqual(path, validation.FieldPath);
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationFamilyContractJson.Serialize(family), path);
    }

    private static void AssertParseRejected(string json, string reasonCode)
    {
        var parsed = CalibrationFamilyContractJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsNull(parsed.Value);
        Assert.IsFalse(parsed.Validation.IsValid);
        Assert.AreEqual(reasonCode, parsed.Validation.ReasonCode, json.Length > 256 ? json[..256] : json);
    }

    private static string Lowercase(string value)
        => new([.. value.Select(char.ToLowerInvariant)]);

    private static CalibrationFamilyV1 Family(params CalibrationFamilyMemberV1[] members)
        => new(
            CalibrationFamilyV1.CurrentSchemaVersion,
            "virtual-family",
            "agent-a",
            "rig-a",
            RigSha256,
            SensorSha256,
            Model,
            MaskSha256,
            members);

    private static CalibrationFamilyMemberV1 Member(double gain, string? memberId = null)
        => new(
            memberId ?? FormattableString.Invariant($"gain-{gain}"),
            gain,
            10,
            Layout(),
            -5,
            5,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            EffectiveFrom,
            null,
            MaskSha256,
            Masters(gain));

    private static CalibrationFamilyMasterV1[] Masters(double gain)
        => [.. CalibrationReferenceKinds.All.Select(kind => new CalibrationFamilyMasterV1(kind, ArtifactId(kind, gain), PayloadSha256(kind, gain)))];

    private static Guid ArtifactId(string kind, double gain)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"artifact:{kind}:{gain:R}")))[..16]);

    private static string PayloadSha256(string kind, double gain)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"payload:{kind}:{gain:R}"))));

    private static CalibrationFamilyInputV1 Input(double gain)
        => new("agent-a", "rig-a", RigSha256, SensorSha256, Model, MaskSha256, Layout(), gain, 10, 0, TimeSpan.FromSeconds(10), Observed);

    private static FrameLayoutDescriptor Layout()
        => new(
            4, 2, 8, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb,
            64, 4095, 16)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(4, 2, 0, 0, 4, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0)
        };
}
