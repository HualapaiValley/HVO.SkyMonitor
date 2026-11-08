using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
[TestCategory("Unit")]
public sealed class HorizonObstructionTests
{
    internal static readonly ObserverLocation Observer = new(35, -114, 1200);
    internal static CameraRigConfig Rig(string model = "EquidistantFisheye", int size = 64) => new(
        new("synthetic", size, size, 2, SensorColorMode.Mono, CameraPixelFormat.Mono16),
        new(model, 0, model == "Perspective" ? 100 : 180, 0,
            model == "Perspective" ? LensKind.Rectilinear : LensKind.Fisheye, CalibrationVersion: "synthetic-v1"),
        new(60, 20, 15), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1));

    internal static HorizonObstructionProfile Profile(CameraRigConfig rig,
        IReadOnlyList<HorizonObstructionSample>? samples = null, double gap = 90, long revision = 1, CaptureLocationProvenance? location = null) => new(
        HorizonObstructionProfile.CurrentSchemaVersion, "synthetic-profile", revision, "synthetic-installation",
        HorizonObstructionProfile.ComputeSiteIdentitySha256(Observer.LatitudeDegrees, Observer.LongitudeDegrees, Observer.ElevationMeters, location),
        HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, gap,
        samples ?? [new(0, 30), new(90, 30), new(180, 30), new(270, 30)]);

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(0d)]
    [DataRow(10d)]
    public void RetainedRefractionInversionPreservesGeometricEligibility(double minimumAltitude)
    {
        var options = new RefractionOptions(true, minimumAltitude);
        foreach (var altitude in new[] { 0d, .01, 1, 5, 20, 80, 90 })
        {
            var apparent = AtmosphericRefraction.Apply(altitude, options);
            Assert.AreEqual(altitude, AtmosphericRefraction.RemoveAboveHorizon(apparent, options)!.Value, 1e-10);
        }
        Assert.IsNull(AtmosphericRefraction.RemoveAboveHorizon(-1, options));
        Assert.IsNull(AtmosphericRefraction.RemoveAboveHorizon(double.NaN, options));
        if (minimumAltitude <= 0) Assert.IsNull(AtmosphericRefraction.RemoveAboveHorizon(0, options));
    }

    [TestMethod]
    [DataRow("peak", 2)]
    [DataRow("negative", 4)]
    [DataRow("null", 0)]
    [DataRow("gap", 0)]
    [DataRow("wrap", 2)]
    public void PreviewIntervalsMatchEvaluatorAtFractionalKnotsAndNeverBridgeUnknown(string kind, int count)
    {
        var rig = Rig();
        HorizonObstructionSample[] samples = kind switch
        {
            "peak" => [new(0, 0), new(.5, 80), new(1, 0)],
            "negative" => [new(0, -10), new(.5, 10), new(1, -10)],
            "null" => [new(0, 10), new(.5, null), new(1, 10)],
            "gap" => [new(0, 10), new(1, 10)],
            _ => [new(.5, 30), new(359.5, 10)]
        };
        var evaluator = new HorizonObstructionEvaluator(Profile(rig, samples, kind == "wrap" ? 1 : .5), rig, Observer);
        var intervals = evaluator.KnownIntervals();
        Assert.HasCount(count, intervals);
        foreach (var interval in intervals)
            foreach (var fraction in new[] { 0d, .25, .5, .75, 1d })
            {
                var az = interval.FromAzimuthDegrees + fraction * (interval.ToAzimuthDegrees - interval.FromAzimuthDegrees);
                var elevation = interval.FromElevationDegrees + fraction * (interval.ToElevationDegrees - interval.FromElevationDegrees);
                Assert.AreEqual(elevation, evaluator.HorizonElevation(az)!.Value, 1e-10);
            }
        if (kind == "peak") Assert.AreEqual(80d, intervals[0].ToElevationDegrees);
        if (kind is "gap" or "null") Assert.IsNull(evaluator.HorizonElevation(.5));
        if (kind == "wrap") Assert.AreEqual(20d, evaluator.HorizonElevation(0));
    }

    [TestMethod]
    public void NorthWrapAndExactCrossingUseGeometricElevation()
    {
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig, [new(10, 30), new(350, 10)], 25), rig, Observer);
        Assert.AreEqual(20d, evaluator.HorizonElevation(0));
        Assert.AreEqual(20d, evaluator.HorizonElevation(360));
        Assert.AreEqual(15d, evaluator.HorizonElevation(-5));
        Assert.AreEqual(HorizonObstructionEligibility.Obstructed, evaluator.Evaluate(new(20, 0)));
        Assert.AreEqual(HorizonObstructionEligibility.Clear, evaluator.Evaluate(new(20.001, 0)));
        Assert.AreEqual(HorizonObstructionEligibility.Unknown, evaluator.Evaluate(new(80, 180)));
        Assert.AreEqual(HorizonObstructionEligibility.Obstructed, evaluator.Evaluate(new(-1, 180)));
        var tiny = new HorizonObstructionEvaluator(Profile(rig, [new(0, 80), new(1e-15, 80)]), rig, Observer);
        Assert.AreEqual(80d, tiny.HorizonElevation(5e-16));
        Assert.AreEqual(HorizonObstructionEligibility.Obstructed, tiny.Evaluate(new(50, 5e-16)));
    }

    [TestMethod]
    public void EmptyNullSingleAndOversizedGapsNeverClaimClearSky()
    {
        var rig = Rig();
        foreach (var samples in new HorizonObstructionSample[][] { [], [new(0, 20)], [new(0, null), new(10, 20)], [new(0, 0), new(100, 0)] })
        {
            var evaluator = new HorizonObstructionEvaluator(Profile(rig, samples, 30), rig, Observer);
            Assert.AreEqual(HorizonObstructionEligibility.Unknown, evaluator.Evaluate(new(80, 5)));
        }
        var knot = new HorizonObstructionEvaluator(Profile(rig, [new(0, null), new(10, 20)], 30), rig, Observer);
        Assert.AreEqual(20d, knot.HorizonElevation(10));
        Assert.IsNull(knot.HorizonElevation(0));
        var fractional = new HorizonObstructionEvaluator(Profile(rig, [new(10.1, 20)], 1), rig, Observer);
        Assert.AreEqual(20d, fractional.HorizonElevation(10.1));
    }

    [TestMethod]
    public void SegmentsCannotBridgeNorthPeakUnknownTerrainOrInterpolationGaps()
    {
        var rig = Rig();
        var peak = new HorizonObstructionEvaluator(Profile(rig, [new(0, 80), new(10, 10), new(350, 10)], 30), rig, Observer);
        Assert.AreEqual(HorizonObstructionEligibility.Clear, peak.Evaluate(new(50, 350)));
        Assert.AreEqual(HorizonObstructionEligibility.Clear, peak.Evaluate(new(50, 10)));
        Assert.IsFalse(peak.IsSegmentClear(new(50, 350), new(50, 10)));
        Assert.IsTrue(peak.IsSegmentClear(new(85, 350), new(85, 10)));
        var gap = new HorizonObstructionEvaluator(Profile(rig, [new(0, 0), new(90, 0)], 30), rig, Observer);
        Assert.IsFalse(gap.IsSegmentClear(new(50, 0), new(50, 90)));
        var unknown = new HorizonObstructionEvaluator(Profile(rig, [new(0, 0), new(10, null), new(20, 0)], 30), rig, Observer);
        Assert.IsFalse(unknown.IsSegmentClear(new(50, 0), new(50, 20)));
    }

    [TestMethod]
    public void InvalidGeometryAndLimitsAreRejectedBeforeGeneration()
    {
        var rig = Rig();
        foreach (var samples in new HorizonObstructionSample[][]
        {
            [new(0, 1), new(0, 2)], [new(10, 1), new(0, 2)], [new(360, 0)], [new(-1, 0)],
            [new(double.NaN, 0)], [new(0, double.PositiveInfinity)], [new(0, 91)],
            Enumerable.Range(0, 721).Select(i => new HorizonObstructionSample(i / 3d, 0)).ToArray()
        }) Assert.ThrowsExactly<ArgumentException>(() => Profile(rig, samples));
        Assert.ThrowsExactly<ArgumentException>(() => Profile(rig, gap: 0));
        Assert.ThrowsExactly<ArgumentException>(() => Profile(rig, gap: 91));
        Assert.ThrowsExactly<ArgumentException>(() => Profile(rig, revision: 0));
    }

    [TestMethod]
    public void SnapshotAndCanonicalRoundtripPreserveIdentityAndFreezeCallerSamples()
    {
        var rig = Rig();
        var samples = new[] { new HorizonObstructionSample(0, 10), new HorizonObstructionSample(90, 20) };
        var profile = Profile(rig, samples);
        var hash = profile.ComputeIdentitySha256();
        samples[0] = new(0, 89);
        Assert.AreEqual(10d, profile.Samples[0].ElevationDegrees);
        var roundtrip = JsonSerializer.Deserialize<HorizonObstructionProfile>(JsonSerializer.Serialize(profile));
        Assert.AreEqual(hash, roundtrip!.ComputeIdentitySha256());
        Assert.AreNotEqual(hash, Profile(rig, revision: 2).ComputeIdentitySha256());
        Assert.AreNotEqual(CameraRigProfileIdentity.ComputeSha256(rig),
            CameraRigProfileIdentity.ComputeSha256(rig with { HorizonObstruction = profile }));
    }

    [TestMethod]
    public void SitePointingCalibrationAndReadoutChangesRequireRevalidation()
    {
        var rig = Rig();
        var profile = Profile(rig);
        foreach (var changed in new[]
        {
            rig with { Orientation = rig.Orientation with { BoresightAzimuthDegrees = 21 } },
            rig with { Optics = rig.Optics with { HorizontalFlip = true } },
            rig with { Optics = rig.Optics with { CalibrationVersion = "next" } },
            rig with { Sensor = rig.Sensor with { WidthPixels = 66 } },
            rig with { Readout = new(new(4, 8, 48, 40), 2, 2, FrameBinningAlgorithm.DigitalAverageV1,
                CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer, 0, 65535) }
        })
        {
            var evaluator = new HorizonObstructionEvaluator(profile, changed, Observer);
            Assert.IsFalse(evaluator.IsCompatible);
            Assert.AreEqual(HorizonObstructionEligibility.Incompatible, evaluator.Evaluate(new(80, 0)));
            Assert.ThrowsExactly<ArgumentException>(() => HorizonObstructionMask.Create(evaluator,
                ProjectedSceneImageTransformV1.Identity(evaluator.Projection.WidthPixels, evaluator.Projection.HeightPixels)));
        }
        Assert.IsFalse(new HorizonObstructionEvaluator(profile, rig, Observer with { ElevationMeters = 1201 }).IsCompatible);
        var site = new CaptureLocationProvenance("installed-site", 1, "synthetic", null, DateTimeOffset.UnixEpoch, null);
        var versioned = Profile(rig, location: site);
        Assert.IsTrue(new HorizonObstructionEvaluator(versioned, rig, Observer, site).IsCompatible);
        Assert.IsFalse(new HorizonObstructionEvaluator(versioned, rig, Observer, site with { Version = 2 }).IsCompatible);
        Assert.IsFalse(new HorizonObstructionEvaluator(versioned, rig, Observer, site with { LocationId = "other-site" }).IsCompatible);
    }

    [TestMethod]
    [DataRow("EquidistantFisheye")]
    [DataRow("EquisolidFisheye")]
    [DataRow("OrthographicFisheye")]
    [DataRow("StereographicFisheye")]
    [DataRow("Perspective")]
    public void MasksNumericallyMatchProjectorsAcrossCropBinMirrorAndRotations(string model)
    {
        var rig = Rig(model);
        var evaluator = new HorizonObstructionEvaluator(Profile(rig), rig, Observer);
        var projector = ProjectorFactory.Create(evaluator.Projection);
        foreach (var rotation in Enum.GetValues<ProjectedSceneQuarterRotation>())
            foreach (var mirror in new[] { false, true })
            {
                var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
                    64, 64, 4, 8, 48, 40, 2, 2, mirror, !mirror, rotation,
                    rotation is ProjectedSceneQuarterRotation.Degrees90 or ProjectedSceneQuarterRotation.Degrees270 ? 20 : 24,
                    rotation is ProjectedSceneQuarterRotation.Degrees90 or ProjectedSceneQuarterRotation.Degrees270 ? 24 : 20);
                var mask = HorizonObstructionMask.Create(evaluator, transform);
                for (var y = 0; y < transform.OutputHeightPixels; y++)
                    for (var x = 0; x < transform.OutputWidthPixels; x++)
                    {
                        var source = ProjectedSceneImageTransform.Inverse(transform, new(x + .5, y + .5));
                        var direction = projector.Unproject(source);
                        Assert.AreEqual(direction is null, Linear16MaskOperations.IsExcluded(mask.OutsideDomain, x, y));
                        Assert.AreEqual(direction is { AltitudeDegrees: <= 30 }, Linear16MaskOperations.IsExcluded(mask.Obstructed, x, y));
                        Assert.IsFalse(Linear16MaskOperations.IsExcluded(mask.Unknown, x, y));
                        if (direction is { } point)
                        {
                            var projected = projector.Project(point)!.Value;
                            Assert.AreEqual(source.X, projected.X, 1e-8);
                            Assert.AreEqual(source.Y, projected.Y, 1e-8);
                        }
                    }
            }
    }

    [TestMethod]
    [DataRow("satellite")]
    [DataRow("meteor")]
    [DataRow("star")]
    public void ObjectEligibilityAndMasksAgreeWithoutChangingRawEvidence(string objectKind)
    {
        Assert.IsFalse(string.IsNullOrEmpty(objectKind));
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig), rig, Observer);
        var binding = HorizonObstructionProducts.Bind(new string('A', 64), evaluator, rig.Optics.CalibrationVersion,
            ProjectedSceneImageTransformV1.Identity(64, 64));
        var raw = new byte[64 * 64 * 2];
        for (var x = 28; x <= (objectKind == "star" ? 28 : 35); x++) raw[(32 * 64 + x) * 2] = 120;
        var before = raw.ToArray();
        var product = HorizonObstructionProducts.Predict(evaluator, binding, [new(20, 0), new(40, 0), new(30, 0)]);
        CollectionAssert.AreEqual(new[] { HorizonObstructionEligibility.Obstructed, HorizonObstructionEligibility.Clear,
            HorizonObstructionEligibility.Obstructed }, product.Eligibility.ToArray());
        var mask = HorizonObstructionProducts.CreateMask(evaluator, binding);
        Assert.AreEqual(binding.ProfileIdentitySha256, mask.Binding.ProfileIdentitySha256);
        Assert.AreEqual(64, mask.UnknownChecksumSha256.Length);
        var blockedEvaluator = new HorizonObstructionEvaluator(Profile(rig,
            [new(0, 90), new(90, 90), new(180, 90), new(270, 90)]), rig, Observer);
        var blocked = HorizonObstructionMask.Create(blockedEvaluator, binding.ImageTransform);
        var hard = Linear16MaskOperations.Combine([blocked.Obstructed, blocked.Unknown, blocked.OutsideDomain]);
        var target = new Linear16Frame(64, 64, 128, CameraPixelFormat.Mono16, raw);
        var background = target with { PixelData = new byte[raw.Length] };
        var empty = Linear16MaskOperations.Empty(64, 64);
        var options = new Linear16TransientExtractionOptions(20, 1, 20, 8, 4, 8, 4096, 0, .95);
        Assert.HasCount(1, Linear16TransientExtraction.Extract(target, background, empty, empty, options).Components);
        Assert.IsEmpty(Linear16TransientExtraction.Extract(target, background, hard, empty, options).Components);
        CollectionAssert.AreEqual(before, raw);
    }

    [TestMethod]
    public void AnnotationUsesTheSameHorizonWithoutMutatingSceneObjects()
    {
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig), rig, Observer);
        var projector = ProjectorFactory.Create(evaluator.Projection);
        var blocked = projector.Project(new(20, 0))!.Value;
        var clear = projector.Project(new(60, 0))!.Value;
        var objects = new[] { new ProjectedAnnotationObject("blocked-star", "Blocked", blocked),
            new ProjectedAnnotationObject("clear-star", "Clear", clear) };
        var result = HorizonObstructionAnnotation.Filter(evaluator, ProjectedSceneImageTransformV1.Identity(64, 64),
            objects, [new("figure", blocked, clear)]);
        Assert.HasCount(1, result.Objects);
        Assert.AreEqual("clear-star", result.Objects[0].Id);
        Assert.IsEmpty(result.Segments);
        Assert.HasCount(2, objects);
    }

    [TestMethod]
    public void SourceProfileAndTransformChangesCannotReuseProductIdentities()
    {
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig), rig, Observer);
        var transform = ProjectedSceneImageTransformV1.Identity(64, 64);
        var first = HorizonObstructionProducts.Bind(new string('A', 64), evaluator, rig.Optics.CalibrationVersion, transform);
        var second = first with { SourceIdentitySha256 = new string('B', 64) };
        Assert.AreNotEqual(HorizonObstructionProducts.CreateMask(evaluator, first).IdentitySha256,
            HorizonObstructionProducts.CreateMask(evaluator, second).IdentitySha256);
        Assert.ThrowsExactly<ArgumentException>(() => HorizonObstructionProducts.CreateMask(evaluator,
            first with { ProfileIdentitySha256 = new string('C', 64) }));
        Assert.ThrowsExactly<ArgumentException>(() => HorizonObstructionProducts.Bind(new string('A', 64), evaluator, "wrong-calibration", transform));
        Assert.AreNotEqual(HorizonObstructionProducts.CreateMask(evaluator, first).IdentitySha256,
            HorizonObstructionProducts.CreateMask(evaluator, first with { ImageTransform = transform with { HorizontalMirror = true } }).IdentitySha256);
    }

    [TestMethod]
    public void EmptyProfileHasUnknownBitPlaneAndGenerationHonorsCancellationAndAreaBound()
    {
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig, []), rig, Observer);
        var transform = ProjectedSceneImageTransformV1.Identity(64, 64);
        var planes = HorizonObstructionMask.Create(evaluator, transform);
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(planes.Unknown, 32, 32));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => HorizonObstructionMask.Create(evaluator, transform, cts.Token));
        var huge = Rig(size: 8192);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HorizonObstructionMask.Create(
            new(Profile(huge), huge, Observer), ProjectedSceneImageTransformV1.Identity(8192, 8192)));
    }
}
