#pragma warning disable CA5394 // Fixed seeds define reproducible synthetic centroids, never security material.
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OpticalCalibrationSessionTests
{
    private const int Size = 512;
    private static readonly AstrometricCatalogData Catalog = AstrometricTestFixture.Catalog();
    private static readonly ProjectionContext Nominal = AstrometricTestFixture.Truth() with { BoresightAltitudeDegrees = 90, BoresightAzimuthDegrees = 0, RollDegrees = 0 };

    // Preregistered synthetic truth: +1.2% focal, a (3.5, -3) px principal-point offset and k1 = -0.006.
    private static readonly ProjectionContext DistortedTruth = AstrometricTestFixture.Truth() with
    {
        FocalLengthXPixels = Nominal.FocalLengthXPixels * 1.012,
        FocalLengthYPixels = Nominal.FocalLengthYPixels * 1.012,
        PrincipalPointX = 259.5,
        PrincipalPointY = 253,
        RadialDistortionK1 = -.006
    };

    private static readonly FrameReadoutDescriptor FullFrame = Readout(0, 0, Size, Size, 1);
    private static readonly FrameReadoutDescriptor Binned = Readout(0, 0, Size, Size, 2);
    private static readonly FrameReadoutDescriptor CentralRoi = Readout(64, 64, 384, 384, 1);
    private static readonly OpticalCalibrationOptions WithDistortion = new(FitRadialDistortion: true);
    private static readonly OpticalCalibrationOptions WithoutDistortion = new();

    // A declared ±3% focal search keeps synthetic blind acquisition inside the unit-test budget.
    private static readonly AstrometricSolverOptions Search = new(MinimumFocalScale: .97, MaximumFocalScale: 1.03);

    private static readonly Lazy<OpticalCalibrationResult> Recovered = new(() => OpticalCalibrationSession.Fit(Nominal,
        [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, Binned), Frame(DistortedTruth, 3, FullFrame)],
        [Frame(DistortedTruth, 2.2, CentralRoi), Frame(DistortedTruth, 4.1, FullFrame)], Catalog, WithDistortion, Search));

    [TestMethod]
    public void Fit_RecoversFocalPrincipalPointAndDistortionWithinPreregisteredTolerances()
    {
        var result = Recovered.Value;
        Assert.AreEqual(OpticalCalibrationStatus.Accepted, result.Status, string.Join(",", result.Rejections));
        var calibrated = result.CalibratedNative!.Value;
        Assert.AreEqual(DistortedTruth.FocalLengthXPixels, calibrated.FocalLengthXPixels, .1);
        Assert.AreEqual(DistortedTruth.PrincipalPointX, calibrated.PrincipalPointX, .1);
        Assert.AreEqual(DistortedTruth.PrincipalPointY, calibrated.PrincipalPointY, .1);
        Assert.AreEqual(DistortedTruth.RadialDistortionK1, calibrated.RadialDistortionK1, .0005);
        Assert.IsLessThan(.1, result.Diagnostics.FittingRmsPixels!.Value);
        Assert.IsTrue(result.Diagnostics.Converged);
        Assert.IsLessThan(WithDistortion.MaximumConditionNumber, result.Diagnostics.ConditionNumber!.Value);
        Assert.IsTrue(result.Parameters.All(p => !p.AtBound && (!p.Fitted || p.StandardError is not null)));
        Assert.HasCount(2, result.Validations);
        Assert.IsTrue(result.Validations.All(v => v.Status == AstrometricAssessmentStatus.Accepted && Math.Abs(v.FocalScale!.Value - 1) < .003));
    }

    [TestMethod]
    public void Fit_KeepsPoseOutOfSessionOpticsAndReportsPerFramePose()
    {
        var result = Recovered.Value;
        var calibrated = result.CalibratedNative!.Value;
        Assert.AreEqual(Nominal.BoresightAltitudeDegrees, calibrated.BoresightAltitudeDegrees);
        Assert.AreEqual(Nominal.BoresightAzimuthDegrees, calibrated.BoresightAzimuthDegrees);
        Assert.AreEqual(Nominal.RollDegrees, calibrated.RollDegrees);
        Assert.AreEqual(Nominal.HorizontalFlip, calibrated.HorizontalFlip);
        Assert.AreEqual(Nominal.Model, calibrated.Model);
        foreach (var frame in result.Frames)
        {
            Assert.AreEqual("fitted", frame.Status);
            Assert.AreEqual(DistortedTruth.BoresightAltitudeDegrees, frame.BoresightAltitudeDegrees!.Value, .02);
            Assert.AreEqual(DistortedTruth.BoresightAzimuthDegrees, frame.BoresightAzimuthDegrees!.Value, .05);
        }
    }

    [TestMethod]
    public void Fit_DerivedReadoutViewsReproduceWithheldNativeMapping()
    {
        var calibrated = Recovered.Value.CalibratedNative!.Value;
        foreach (var readout in new[] { Binned, CentralRoi })
        {
            var view = RigProjectionContextFactory.CreateReadoutView(calibrated with { BoresightAltitudeDegrees = 72, BoresightAzimuthDegrees = 243, RollDegrees = 17 }, readout);
            var truth = ProjectorFactory.Create(DistortedTruth); var projector = ProjectorFactory.Create(view);
            var worst = 0d;
            foreach (var star in Catalog.Stars)
            {
                var h = AstrometricTestFixture.Horizontal(star, AstrometricTestFixture.Utc);
                if (h.AltitudeDegrees < 10 || truth.Project(h) is not { } native || projector.Project(h) is not { } derived) continue;
                var expected = new PixelPoint((native.X - readout.RoiX) / readout.BinX, (native.Y - readout.RoiY) / readout.BinY);
                if (expected.X < 0 || expected.Y < 0 || expected.X > view.WidthPixels || expected.Y > view.HeightPixels) continue;
                worst = Math.Max(worst, AstrometricTestFixture.Distance(expected, derived));
            }
            Assert.IsLessThan(.1, worst);
        }
    }

    [TestMethod]
    public void Fit_IsDeterministicAndIdentityCoversEveryOption()
    {
        var again = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, Binned), Frame(DistortedTruth, 3, FullFrame)],
            [Frame(DistortedTruth, 2.2, CentralRoi), Frame(DistortedTruth, 4.1, FullFrame)], Catalog, WithDistortion, Search);
        Assert.AreEqual(Recovered.Value.IdentitySha256, again.IdentitySha256);
        var defaults = new OpticalCalibrationOptions();
        var json = JsonSerializer.SerializeToElement(defaults);
        var members = typeof(OpticalCalibrationOptions).GetProperties().Where(p => p.Name != nameof(OpticalCalibrationOptions.IdentitySha256)).ToArray();
        Assert.HasCount(members.Length, json.EnumerateObject().ToArray());
        Assert.AreNotEqual(defaults.IdentitySha256, (defaults with { MaximumRadialBiasPixels = .21 }).IdentitySha256);
    }

    [TestMethod]
    public void InputIdentity_BindsEveryFitAndWithheldFrameInput()
    {
        var fit = new[] { Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, Binned), Frame(DistortedTruth, 3, FullFrame) };
        var withheld = new[] { Frame(DistortedTruth, 2.2, CentralRoi) };
        var baseline = OpticalCalibrationSession.InputIdentitySha256(fit, withheld);
        Assert.AreEqual(Recovered.Value.InputIdentitySha256, OpticalCalibrationSession.InputIdentitySha256(fit, [withheld[0], Frame(DistortedTruth, 4.1, FullFrame)]));

        var first = fit[0];
        var other = AstrometricTestFixture.Hash("other");
        var changed = new[]
        {
            first with { Frame = first.Frame with { SourcePayloadSha256 = other } },
            first with { Frame = first.Frame with { SourceDescriptorSha256 = other } },
            first with { Frame = first.Frame with { DetectionSettingsIdentitySha256 = other } },
            first with { Frame = first.Frame with { SourceArtifactId = Guid.NewGuid() } },
            first with { ReadoutIdentitySha256 = other },
            first with { Readout = first.Readout with { BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 } },
            first with { Detections = [.. first.Detections.Take(first.Detections.Count - 1)] },
            first with { Detections = [first.Detections[0] with { Flux = first.Detections[0].Flux * 2 }, .. first.Detections.Skip(1)] }
        };
        foreach (var frame in changed)
            Assert.AreNotEqual(baseline, OpticalCalibrationSession.InputIdentitySha256([frame, fit[1], fit[2]], withheld));
        Assert.AreNotEqual(baseline, OpticalCalibrationSession.InputIdentitySha256([fit[1], fit[0], fit[2]], withheld));
        Assert.AreNotEqual(baseline, OpticalCalibrationSession.InputIdentitySha256(fit, [withheld[0] with { Frame = withheld[0].Frame with { SourcePayloadSha256 = other } }]));

        // An early terminal outcome attempts no frame but still binds its unattempted inputs into the result identity.
        var early = OpticalCalibrationSession.Fit(Nominal, fit[..2], withheld, Catalog, WithDistortion, Search);
        var earlyChanged = OpticalCalibrationSession.Fit(Nominal, fit[..2], [withheld[0] with { ReadoutIdentitySha256 = other }], Catalog, WithDistortion, Search);
        Assert.AreEqual("insufficient-frames", early.ReasonCode);
        Assert.AreEqual(OpticalCalibrationSession.InputIdentitySha256(fit[..2], withheld), early.InputIdentitySha256);
        Assert.AreNotEqual(early.IdentitySha256, earlyChanged.IdentitySha256);
    }

    [TestMethod]
    public void Fit_OmittedDistortionLeavesSignificantRadialBias()
    {
        var result = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, FullFrame), Frame(DistortedTruth, 3, FullFrame)],
            [Frame(DistortedTruth, 2.2, FullFrame)], Catalog, WithoutDistortion, Search);
        Assert.AreEqual(OpticalCalibrationStatus.Rejected, result.Status);
        Assert.IsNull(result.CalibratedNative);
        Assert.IsTrue(result.Rejections.Any(r => r.StartsWith("radial-bias:", StringComparison.Ordinal)), string.Join(",", result.Rejections));
        Assert.IsFalse(result.Parameters.Single(p => p.Name == "radial-k1").Fitted);
    }

    [TestMethod]
    public void Fit_WrongProjectionFamilyIsRejected()
    {
        // Same focal length and aperture; only the radial law differs. Equisolid is about k1 = -1/24 in the
        // equidistant model, far outside the declared distortion bound, so no bounded fit can absorb it.
        var equisolid = AstrometricTestFixture.Truth() with { Model = ProjectionModel.EquisolidFisheye };
        var result = OpticalCalibrationSession.Fit(Nominal,
            [Frame(equisolid, 0, FullFrame), Frame(equisolid, 1.5, FullFrame), Frame(equisolid, 3, FullFrame)],
            [Frame(equisolid, 2.2, FullFrame)], Catalog, WithDistortion, Search);
        Assert.AreEqual(OpticalCalibrationStatus.Rejected, result.Status);
        Assert.IsNull(result.CalibratedNative);
        Assert.IsTrue(result.Rejections.Any(r => r is "parameter-bound:radial-k1" || r.StartsWith("radial-bias:", StringComparison.Ordinal)),
            string.Join(",", result.Rejections));
    }

    [TestMethod]
    public void Fit_InsufficientSkyRotationFramesOrRadialSupportIsRejected()
    {
        var sameSky = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, .05, FullFrame), Frame(DistortedTruth, .1, FullFrame)],
            [Frame(DistortedTruth, 2.2, FullFrame)], Catalog, WithDistortion, Search);
        Assert.AreEqual("insufficient-sky-rotation", sameSky.ReasonCode);
        Assert.AreEqual("not-attempted", sameSky.Frames[0].Status);

        var twoFrames = OpticalCalibrationSession.Fit(Nominal, [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 3, FullFrame)],
            [Frame(DistortedTruth, 2.2, FullFrame)], Catalog, WithDistortion, Search);
        Assert.AreEqual("insufficient-frames", twoFrames.ReasonCode);

        var noValidation = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, FullFrame), Frame(DistortedTruth, 3, FullFrame)], [], Catalog, WithDistortion, Search);
        Assert.AreEqual("insufficient-validation-frames", noValidation.ReasonCode);

        var core = Readout(160, 160, 192, 192, 1);
        var central = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, core), Frame(DistortedTruth, 1.5, core), Frame(DistortedTruth, 3, core)],
            [Frame(DistortedTruth, 2.2, FullFrame)], Catalog, WithDistortion, Search);
        Assert.AreEqual(OpticalCalibrationStatus.Rejected, central.Status);
        Assert.Contains("insufficient-radial-support", central.Rejections);
        Assert.IsNull(central.CalibratedNative);
    }

    [TestMethod]
    public void Fit_RotationallySymmetricSkyIsAmbiguous()
    {
        // Every star has a twin twelve hours away at the same declination. Seen from the pole, the sky is
        // invariant under a half turn about the zenith, so two poses explain every frame equally well.
        var random = new Random(51713);
        var half = Enumerable.Range(0, 200).Select(i => (Ra: random.NextDouble() * 12, Dec: 25 + 65 * random.NextDouble(), Mag: 1.8 + 3.4 * random.NextDouble())).ToArray();
        var stars = half.SelectMany((s, i) => new[]
        {
            new CelestialCatalogObject($"SYM{i:0000}A", $"Symmetric {i}A", s.Ra, s.Dec, s.Mag),
            new CelestialCatalogObject($"SYM{i:0000}B", $"Symmetric {i}B", s.Ra + 12, s.Dec, s.Mag)
        }).ToArray();
        var symmetric = new AstrometricCatalogData(new("Symmetric test catalog", "1", new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))), "test-generated", "1"), stars, true, 7);
        var pole = new ObserverLocation(89.9999, 0, 0);
        var truth = Nominal with { BoresightAltitudeDegrees = 80, BoresightAzimuthDegrees = 30, RollDegrees = 5 };
        var result = OpticalCalibrationSession.Fit(Nominal,
            [Frame(truth, 0, FullFrame, symmetric, pole), Frame(truth, 1.5, FullFrame, symmetric, pole), Frame(truth, 3, FullFrame, symmetric, pole)],
            [Frame(truth, 2.2, FullFrame, symmetric, pole)], symmetric, WithDistortion, Search);
        Assert.AreEqual("ambiguous", result.ReasonCode, string.Join(",", result.Rejections));
        Assert.IsNull(result.CalibratedNative);
    }

    [TestMethod]
    public void Fit_IncompleteCatalogIsUnavailableAndInvalidInputsThrow()
    {
        var incomplete = new AstrometricCatalogData(Catalog.Metadata, Catalog.Stars, false, 7);
        var frames = new[] { Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, FullFrame), Frame(DistortedTruth, 3, FullFrame) };
        Assert.AreEqual(OpticalCalibrationStatus.Unavailable, OpticalCalibrationSession.Fit(Nominal, frames, [], incomplete).Status);
        Assert.Throws<ArgumentException>(() => OpticalCalibrationSession.Fit(Nominal, [frames[0], frames[0], frames[1]], [], Catalog));
        Assert.Throws<ArgumentException>(() => OpticalCalibrationSession.Fit(Nominal with { RadialDistortionK1 = -.05 }, frames, [], Catalog, WithDistortion, Search));
        Assert.Throws<ArgumentException>(() => OpticalCalibrationSession.Fit(Nominal, [frames[0] with { Readout = Readout(0, 0, 256, 256, 1) with { NativeWidth = 1024 } }], [], Catalog));
        Assert.Throws<ArgumentException>(() => new OpticalCalibrationOptions(MaximumAbsoluteRadialDistortionK1: .6).Validate());
        Assert.Throws<ArgumentException>(() => new OpticalCalibrationOptions(FitRadialDistortion: true, MaximumAbsoluteRadialDistortionK1: 0).Validate());
    }

    [TestMethod]
    public void Fit_ExhaustedBudgetProposesNoCalibration()
    {
        var result = OpticalCalibrationSession.Fit(Nominal,
            [Frame(DistortedTruth, 0, FullFrame), Frame(DistortedTruth, 1.5, FullFrame), Frame(DistortedTruth, 3, FullFrame)],
            [Frame(DistortedTruth, 2.2, FullFrame)], Catalog, WithDistortion with { BudgetMilliseconds = .001 }, Search);
        Assert.AreEqual(OpticalCalibrationStatus.BudgetExceeded, result.Status);
        Assert.IsNull(result.CalibratedNative);
    }

    [TestMethod]
    public void Fit_AtTheDistortionDomainBoundaryIsRejectedWithoutPrecision()
    {
        // A centred perspective view with k1 = -0.02 whose corner sits just inside the supported radius. The truth sky
        // is slightly wider than that domain allows at nominal k1, so acquisition pins every frame at the minimum
        // physical scale: the corner then lies on the supported radius and either principal-point step leaves it.
        var focal = 1000d * (Size / 2d) / 1700;
        var nominal = new ProjectionContext(ProjectionModel.Perspective, Size / 2d, Size / 2d, focal, focal, Size, Size, ProjectionAperture.Rectangular, null,
            BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0, RollDegrees: 0, RadialDistortionK1: -.02);
        var minimum = AstrometricSolverCore.MinimumPhysicalScale(SolverOptics.From(nominal));
        var bounds = OpticalCalibrationSession.Bounds(nominal, WithDistortion);
        var boundary = new OpticalCalibrationSession.Shared(Math.Log(minimum), Size / 2d, Size / 2d, -.02);
        var scaled = Math.Exp(boundary.LogScale) * focal;
        _ = new AstrometricRayCamera(SolverOptics.From(nominal with { FocalLengthXPixels = scaled, FocalLengthYPixels = scaled }));
        Assert.IsNull(OpticalCalibrationSession.DerivativeCameras(nominal, boundary, bounds));
        var inside = OpticalCalibrationSession.DerivativeCameras(nominal, boundary with { LogScale = Math.Log(minimum * 1.001) }, bounds);
        Assert.IsNotNull(inside);
        Assert.HasCount(4, inside);

        // The joint fit reaches that point with real associations and reports it instead of throwing.
        var truth = nominal with { FocalLengthXPixels = focal * .998, FocalLengthYPixels = focal * .998, RadialDistortionK1 = -.0199 };
        var result = OpticalCalibrationSession.Fit(nominal, [Frame(truth, 0, FullFrame), Frame(truth, 1.5, FullFrame), Frame(truth, 3, FullFrame)],
            [Frame(truth, 2.2, FullFrame)], Catalog, WithDistortion, Search);
        Assert.IsTrue(result.Frames.Take(3).All(f => f.Status == "acquired"), string.Join(",", result.Frames.Select(f => f.Status)));
        Assert.AreEqual(OpticalCalibrationStatus.Rejected, result.Status);
        Assert.AreEqual("derivative-unsupported", result.ReasonCode, string.Join(",", result.Rejections));
        Assert.Contains("derivative-unsupported", result.Rejections);
        Assert.IsNull(result.CalibratedNative);
        Assert.IsTrue(result.Parameters.All(p => p.StandardError is null));
    }

    private static FrameReadoutDescriptor Readout(int x, int y, int width, int height, int bin) =>
        new(Size, Size, x, y, width, height, bin, bin, bin == 1 ? FrameBinningAlgorithm.IdentityV1 : FrameBinningAlgorithm.DigitalSumV1, null, null);

    /// <summary>Projects catalog stars through truth native optics into one readout, with ±0.02 px centroid noise.</summary>
    private static OpticalCalibrationFrame Frame(ProjectionContext truth, double hours, FrameReadoutDescriptor readout,
        AstrometricCatalogData? catalog = null, ObserverLocation? observer = null)
    {
        catalog ??= Catalog; var site = observer ?? AstrometricTestFixture.Observer;
        var utc = AstrometricTestFixture.Utc.AddHours(hours);
        var frame = AstrometricTestFixture.Frame(utc) with { Observer = site };
        var projector = ProjectorFactory.Create(truth);
        var random = new Random(812 + (int)Math.Round(hours * 100));
        var width = readout.RoiWidth / readout.BinX; var height = readout.RoiHeight / readout.BinY;
        var all = catalog.Stars
            .Select(s => (Star: s, Horizontal: CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(new(s.RightAscensionHours, s.DeclinationDegrees), utc), utc, site.LatitudeDegrees, site.LongitudeDegrees)))
            .Where(s => s.Horizontal.AltitudeDegrees > 5)
            .Select(s => (s.Star, Pixel: projector.Project(s.Horizontal) is { } p ? new PixelPoint((p.X - readout.RoiX) / readout.BinX, (p.Y - readout.RoiY) / readout.BinY) : (PixelPoint?)null))
            .Where(s => s.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < width - 6 && p.Y < height - 6).ToArray();
        var detections = all.Where(s => all.All(t => t.Star.Id == s.Star.Id || AstrometricTestFixture.Distance(t.Pixel!.Value, s.Pixel!.Value) > 13 / (double)readout.BinX))
            .Select((s, i) => new AstrometricDetection(i, new(s.Pixel!.Value.X + (random.NextDouble() - .5) * .04, s.Pixel.Value.Y + (random.NextDouble() - .5) * .04),
                100000 * Math.Pow(10, -.4 * s.Star.Magnitude))).ToArray();
        return new(frame, readout, AstrometricTestFixture.Hash($"readout:{readout}"), detections);
    }
}
