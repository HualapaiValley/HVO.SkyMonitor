using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualCloudFieldTests
{
    private static readonly DateTimeOffset Epoch = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Definition_ValidatesEveryBoundAndCanonicalIdentity()
    {
        var valid = Definition(0.5, 0.8);
        valid.Validate();
        var firstHash = valid.ComputeParametersSha256();
        var canonicalId = valid.ComputeCanonicalScenarioId();

        Assert.AreEqual(64, firstHash.Length);
        StringAssert.StartsWith(canonicalId, "scn-", StringComparison.Ordinal);
        Assert.AreEqual(canonicalId, (valid with { ScenarioId = "clear" }).ComputeCanonicalScenarioId());
        Assert.AreNotEqual(canonicalId, (valid with { Seed = valid.Seed + 1 }).ComputeCanonicalScenarioId());
        Assert.AreNotEqual(firstHash, (valid with { Seed = valid.Seed + 1 }).ComputeParametersSha256());

        VirtualCloudScenarioDefinition[] invalid =
        [
            valid with { SchemaVersion = "unknown" },
            valid with { ScenarioId = " " },
            valid with { ScenarioVersion = "clear" },
            valid with { ScenarioVersion = new string('x', 65) },
            valid with { EpochUtc = Epoch.ToOffset(TimeSpan.FromHours(1)) },
            valid with { SpatialFrequency = 0 },
            valid with { DriftEastCellsPerSecond = double.NaN },
            valid with { DriftNorthCellsPerSecond = 11 },
            valid with { EvolutionCellsPerSecond = double.PositiveInfinity },
            valid with { Octaves = 0 },
            valid with { EdgeSoftness = 0 },
            valid with { HorizonFadeDegrees = 31 },
            valid with { TemporalSampleCount = 0 },
            valid with { Keyframes = [] },
            valid with { Keyframes = [new() { Coverage = -0.1, MaximumOpacity = 1 }] },
            valid with { Keyframes = [new() { Coverage = 1, MaximumOpacity = 1, ScatterFraction = 1.1 }] }
        ];
        foreach (var definition in invalid)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(definition.Validate);
        }
        Assert.ThrowsExactly<ArgumentException>(() => (valid with
        {
            Keyframes =
            [
                new() { OffsetSeconds = 1, Coverage = 0.2, MaximumOpacity = 0.5 },
                new() { OffsetSeconds = 1, Coverage = 0.8, MaximumOpacity = 1 }
            ]
        }).Validate());
    }

    [TestMethod]
    public void Evaluate_IsSeededBoundedAndIndependentOfCallOrder()
    {
        var definition = Definition(0.55, 0.85) with
        {
            DriftEastCellsPerSecond = 0.02,
            DriftNorthCellsPerSecond = -0.01,
            EvolutionCellsPerSecond = 0.005,
            EdgeSoftness = 1
        };
        var field = new VirtualCloudField(definition);
        var direction = new AltAzPoint(45, 120);

        var first = field.Evaluate(direction, Epoch.AddSeconds(10));
        _ = field.Evaluate(new AltAzPoint(20, 250), Epoch.AddHours(2));
        var repeated = field.Evaluate(direction, Epoch.AddSeconds(10));
        var moved = field.Evaluate(direction, Epoch.AddSeconds(40));
        var changedSeed = new VirtualCloudField(definition with { Seed = definition.Seed + 1 })
            .Evaluate(direction, Epoch.AddSeconds(10));
        var evolving = new VirtualCloudField(definition with
        {
            DriftEastCellsPerSecond = 0,
            DriftNorthCellsPerSecond = 0,
            EvolutionCellsPerSecond = 0.005
        }).Evaluate(direction, Epoch.AddSeconds(40));
        var equivalentRigidDrift = new VirtualCloudField(definition with
        {
            DriftEastCellsPerSecond = 0.005,
            DriftNorthCellsPerSecond = 0.005 * 0.6180339887498948,
            EvolutionCellsPerSecond = 0
        }).Evaluate(direction, Epoch.AddSeconds(40));

        Assert.AreEqual(first, repeated);
        Assert.AreEqual(0.41287330385871945, first.Opacity, 1e-15);
        Assert.AreEqual(0.5150424755413003, moved.Opacity, 1e-15);
        Assert.AreEqual(0.41897316100141935, evolving.Opacity, 1e-15);
        Assert.AreEqual(0.3410412256297163, equivalentRigidDrift.Opacity, 1e-15);
        Assert.AreNotEqual(first, moved);
        Assert.AreNotEqual(first, changedSeed);
        Assert.AreNotEqual(evolving, equivalentRigidDrift, "Evolution must deform the field rather than alias rigid drift.");
        Assert.IsTrue(first.Opacity is >= 0 and <= 1);
        Assert.IsTrue(first.Transmission is >= 0 and <= 1);
        Assert.IsTrue(first.Scatter is >= 0 and <= 1);
        Assert.AreEqual(1, first.Opacity + first.Transmission, 1e-12);

        var mutableKeyframes = definition.Keyframes.ToArray();
        var isolated = new VirtualCloudField(definition with { Keyframes = mutableKeyframes });
        var isolatedBeforeMutation = isolated.Evaluate(direction, Epoch.AddSeconds(10));
        mutableKeyframes[0] = mutableKeyframes[0] with { Coverage = 0, MaximumOpacity = 0 };
        Assert.AreEqual(isolatedBeforeMutation, isolated.Evaluate(direction, Epoch.AddSeconds(10)));
    }

    [TestMethod]
    public void Integrate_HandlesClearHorizonAndTransitionOverlap()
    {
        var clear = new VirtualCloudField(Definition(0, 0));
        var transition = new VirtualCloudField(Definition(0, 0) with
        {
            TemporalSampleCount = 8,
            Keyframes =
            [
                new() { OffsetSeconds = 0, Coverage = 0, MaximumOpacity = 0 },
                new() { OffsetSeconds = 10, Coverage = 1, MaximumOpacity = 1 }
            ]
        });
        var direction = new AltAzPoint(60, 45);

        Assert.AreEqual(VirtualCloudEffect.Clear, clear.Evaluate(direction, Epoch));
        Assert.AreEqual(VirtualCloudEffect.Clear, transition.Evaluate(new AltAzPoint(-1, 45), Epoch.AddSeconds(10)));
        Assert.AreEqual(0, transition.Evaluate(new AltAzPoint(0, 45), Epoch.AddSeconds(10)).Opacity, 1e-12);
        var noFade = new VirtualCloudField(Definition(1, 1) with { HorizonFadeDegrees = 0 });
        Assert.AreEqual(VirtualCloudEffect.Clear, noFade.Evaluate(new AltAzPoint(0, 45), Epoch));

        var before = transition.Integrate(direction, Epoch.AddSeconds(-10), TimeSpan.FromSeconds(5));
        var crossing = transition.Integrate(direction, Epoch, TimeSpan.FromSeconds(10));
        var after = transition.Integrate(direction, Epoch.AddSeconds(10), TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, before.Opacity, 1e-12);
        Assert.AreEqual(0.4191576855236423, crossing.Opacity, 1e-15);
        Assert.AreEqual(1, after.Opacity, 1e-15);
    }

    [TestMethod]
    public void ComputeSkyCoverage_DistinguishesClearPartialAndOvercast()
    {
        var clear = new VirtualCloudField(Definition(0, 0)).ComputeSkyCoverage(Epoch, TimeSpan.FromSeconds(2));
        var partial = new VirtualCloudField(Definition(0.5, 0.8)).ComputeSkyCoverage(Epoch, TimeSpan.FromSeconds(2));
        var overcast = new VirtualCloudField(Definition(1, 1)).ComputeSkyCoverage(Epoch, TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, clear);
        Assert.IsTrue(partial is > 0 and < 1);
        Assert.IsTrue(overcast > partial);
        Assert.IsTrue(overcast <= 1);
    }

    private static VirtualCloudScenarioDefinition Definition(double coverage, double opacity) => new()
    {
        ScenarioId = "fixture-cloud",
        ScenarioVersion = "1",
        Seed = 104,
        EpochUtc = Epoch,
        SpatialFrequency = 3,
        Octaves = 3,
        EdgeSoftness = 0.12,
        HorizonFadeDegrees = 5,
        TemporalSampleCount = 4,
        Keyframes = [new() { Coverage = coverage, MaximumOpacity = opacity, ScatterFraction = 0.1 }]
    };
}
