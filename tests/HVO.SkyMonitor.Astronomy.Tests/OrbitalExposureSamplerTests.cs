using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalExposureSamplerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(ProjectionModel.EquidistantFisheye)]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    [DataRow(ProjectionModel.Perspective)]
    public void SmoothIssLikePathCompletesAcrossOpticalFamiliesAndDenseOffPhaseChecks(ProjectionModel model)
    {
        var geometry = Geometry(model, distortion: .001);
        var query = Query(20, geometry: geometry);
        static OrbitalTopocentricState Path(double t) => State(new(300 * Math.Sin(.012 * t),
            500 * Math.Cos(.012 * t), 600 + 80 * Math.Sin(.005 * t)));
        var run = Run(query, Path);
        var track = Complete(run);
        Assert.IsLessThan(2000, run.Budget.Evaluations);
        Assert.IsGreaterThan(track.Samples.Count, run.Budget.Evaluations);
        Assert.AreEqual(0, run.Budget.Tracks);
        Assert.AreEqual(track.Samples.Count, run.Budget.Samples);
        Assert.AreEqual(1, track.Segments.Count);
        var maximumResidual = VerifyDense(track, Path);
        TestContext.WriteLine($"{model}: evaluations={run.Budget.Evaluations}, samples={track.Samples.Count}, estimate={track.Sampling.AchievedChordErrorPixels:R}px, dense={maximumResidual:R}px");
    }

    [TestMethod]
    public void SmallerRequestedToleranceRefinesAnSCurveWhoseMidpointLiesOnItsChord()
    {
        var geometry = Geometry(ProjectionModel.Perspective);
        var optics = ProjectorFactory.Create(geometry.Projection);
        OrbitalTopocentricState Path(double t)
        {
            var pixel = new PixelPoint(200 + 200 * t, 320 + 160 * t * (t - .5) * (t - 1));
            return State(CameraBasis.FromHorizontal(optics.Unproject(pixel)!.Value) * 1000);
        }
        var query = Query(1, OrbitalContractExamples.Bounds with { MaximumChordErrorPixels = .005 });
        query = new(query.Request, geometry, query.Timing);
        var track = Complete(Run(query, Path));
        Assert.IsGreaterThan(8, track.Samples.Count);
        Assert.IsLessThanOrEqualTo(.005, track.Sampling.AchievedChordErrorPixels!.Value);
        VerifyDense(track, Path, 251);
        var start = new OrbitalSampleProjector(geometry).Project(query.Request.Interval.StartUtc, Path(0)).Sample.Pixel!.Value;
        var middle = new OrbitalSampleProjector(geometry).Project(query.Request.Interval.StartUtc.AddSeconds(.5), Path(.5)).Sample.Pixel!.Value;
        var end = new OrbitalSampleProjector(geometry).Project(query.Request.Interval.EndUtc, Path(1)).Sample.Pixel!.Value;
        Assert.AreEqual(0, Distance(middle, new((start.X + end.X) / 2, (start.Y + end.Y) / 2)), 1e-10);
    }

    [TestMethod]
    public void NarrowPerspectiveDoubleCrossingBetweenAllNineSeedProbesIsRetained()
    {
        var geometry = Geometry(ProjectionModel.Perspective, focal: 200000);
        var query = Query(1, geometry: geometry);
        static OrbitalTopocentricState Path(double t) => State(new(40000 * Math.Pow(t - .33, 2) - .8, 0, 1000));
        var projector = new OrbitalSampleProjector(geometry);
        for (var i = 0; i < 9; i++) Assert.IsNull(projector.Project(query.Request.Interval.StartUtc, Path(i / 8d)).Sample.Pixel);
        var run = Run(query, Path);
        var track = Complete(run);
        TestContext.WriteLine($"narrow perspective: evaluations={run.Budget.Evaluations}, samples={track.Samples.Count}, segments={track.Segments.Count}, estimate={track.Sampling.AchievedChordErrorPixels:R}px");
        Assert.IsTrue(track.Segments.Any(static s => s.SampleCount > 1));
        Assert.IsTrue(track.Segments.Any(static s => s.StartReason == OrbitalClipReason.Sensor && s.EndReason == OrbitalClipReason.Sensor));
        Assert.IsTrue(track.Samples.Any(static s => s.Pixel is not null));
        VerifyDense(track, Path, 41);
    }

    [TestMethod]
    public void ShortHorizonExcursionWithSameSideSeedProbesIsFound()
    {
        var geometry = Geometry(ProjectionModel.EquidistantFisheye) with
        { Projection = Geometry(ProjectionModel.EquidistantFisheye).Projection with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 90 } };
        var query = Query(1, geometry: geometry);
        static OrbitalTopocentricState Path(double t) => State(new(1000, 0, .01 - 100 * Math.Pow(t - .33, 2)));
        var projector = new OrbitalSampleProjector(geometry);
        for (var i = 0; i < 9; i++) Assert.AreEqual(OrbitalClipReason.Horizon, projector.Project(query.Request.Interval.StartUtc, Path(i / 8d)).ClipReason);
        var track = Complete(Run(query, Path));
        Assert.IsTrue(track.Segments.Any(static s => s.StartReason == OrbitalClipReason.Horizon && s.EndReason == OrbitalClipReason.Horizon));
        VerifyDense(track, Path);
    }

    [TestMethod]
    public void HorizonCrossingExactlyOnSubdivisionEndpointCompletes()
    {
        var geometry = Geometry(ProjectionModel.EquidistantFisheye) with
        { Projection = Geometry(ProjectionModel.EquidistantFisheye).Projection with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 90 } };
        var track = Complete(Run(Query(1, geometry: geometry), t => State(new(1000, 0, 10 * (t - .5)))));
        Assert.AreEqual(OrbitalClipReason.Horizon, track.Segments[^1].StartReason);
        Assert.AreEqual(OrbitalClipReason.WindowBoundary, track.Segments[^1].EndReason);
    }

    [TestMethod]
    public void ExactTangentAndStationaryBoundaryRemainExplicitlyPartial()
    {
        var geometry = Geometry(ProjectionModel.EquidistantFisheye) with
        { Projection = Geometry(ProjectionModel.EquidistantFisheye).Projection with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 90 } };
        foreach (var tangent in new Func<double, OrbitalTopocentricState>[]
        { t => State(new(1000, 0, -100 * Math.Pow(t - .33, 2))), _ => State(new(1000, 0, 0)) })
        {
            var run = Run(Query(1, geometry: geometry), tangent);
            if (run.Result.Track is { } track) Assert.AreEqual(OrbitalExecutionStatus.Partial, track.Status);
            Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, run.Result.Reason);
            Assert.IsLessThan(2000, run.Budget.Evaluations);
        }
    }

    [TestMethod]
    public void ApertureAndSensorClippingPreserveTheirEndpointReasons()
    {
        var aperture = Geometry(ProjectionModel.EquidistantFisheye) with
        { Projection = Geometry(ProjectionModel.EquidistantFisheye).Projection with { ImageCircleRadiusPixels = 100 } };
        static OrbitalTopocentricState Path(double t) => State(new(1000 * (t - .5) * 2, 0, 1000));
        var apertureTrack = Complete(Run(Query(1, geometry: aperture), Path));
        Assert.IsTrue(apertureTrack.Segments.Any(static s => s.StartReason == OrbitalClipReason.Aperture && s.EndReason == OrbitalClipReason.Aperture));
        var cropped = Cropped(Geometry(ProjectionModel.Perspective));
        var cropTrack = Complete(Run(Query(1, geometry: cropped), Path));
        Assert.IsTrue(cropTrack.Segments.Any(static s => s.StartReason == OrbitalClipReason.Sensor && s.EndReason == OrbitalClipReason.Sensor));
        VerifyDense(cropTrack, Path);
    }

    [TestMethod]
    public void CropBinningMirrorRotationAndReadoutViewApplyRemainingTransformOnce()
    {
        var geometry = Cropped(Geometry(ProjectionModel.Perspective));
        static OrbitalTopocentricState Path(double t) => State(new(100 * t, 20 * t, 1000));
        var native = Complete(Run(Query(2, geometry: geometry), Path));
        var readout = geometry with
        {
            ProjectionCoordinates = OrbitalProjectionCoordinates.ReadoutView,
            Projection = geometry.Projection with
            { WidthPixels = 160, HeightPixels = 160, PrincipalPointX = 80, PrincipalPointY = 80, FocalLengthXPixels = 100, FocalLengthYPixels = 100 },
            ImageTransform = ProjectedSceneImageTransformV1.Identity(160, 160) with
            { HorizontalMirror = true, Rotation = ProjectedSceneQuarterRotation.Degrees90 }
        };
        var view = Complete(Run(Query(2, geometry: readout), Path));
        Assert.AreEqual(native.Samples.Count, view.Samples.Count);
        for (var i = 0; i < native.Samples.Count; i++)
            Assert.AreEqual(0, Distance(native.Samples[i].Pixel!.Value, view.Samples[i].Pixel!.Value), 1e-8);
        VerifyDense(native, Path);
        VerifyDense(view, Path);
    }

    [TestMethod]
    public void ZenithAndNorthWrapUseContinuousCartesianGeometry()
    {
        foreach (var path in new Func<double, OrbitalTopocentricState>[]
        { t => State(new(2 * (t - 1), 0, 1000)), t => State(new(2 * (t - 1), 100, 1000)) })
        {
            var track = Complete(Run(Query(2), path));
            VerifyDense(track, path);
        }
    }

    [TestMethod]
    public void FullyExcludedIntervalCostsEvaluationsButNoTrackOrSampleReservations()
    {
        var run = Run(Query(2.3), _ => State(new(100, 100, -1000)));
        Assert.IsNull(run.Result.Track);
        Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason);
        Assert.AreEqual("interval-geometrically-excluded", run.Result.Detail);
        Assert.AreEqual(0, run.Budget.Tracks);
        Assert.AreEqual(0, run.Budget.Samples);
        Assert.IsGreaterThan(9, run.Budget.Evaluations);
    }

    [TestMethod]
    public void ExcludedCropAndApertureConsumeNoSamplesAndUnresolvedCandidatesNeverBecomeEmptySuccess()
    {
        foreach (var geometry in new[] { Geometry(ProjectionModel.Perspective, focal: 200000), Cropped(Geometry(ProjectionModel.Perspective)) })
        {
            var run = Run(Query(1, geometry: geometry), _ => State(new(1000, 0, 1000)));
            Assert.IsNull(run.Result.Track);
            Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason);
            Assert.AreEqual(0, run.Budget.Samples);
        }
        var failed = Run(Query(1, OrbitalContractExamples.Bounds with { MaximumEvaluations = 1 }), _ => State(new(0, 0, -1000)));
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, failed.Result.Reason);
        Assert.AreEqual(0, failed.Budget.Samples);
        var singular = Run(Query(1), _ => State(new(0, 0, 1e-8)));
        Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, singular.Result.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, singular.Result.Track!.Status);
        Assert.IsLessThanOrEqualTo(9, singular.Budget.Evaluations);
    }

    [TestMethod]
    [DataRow(5d)]
    [DataRow(100d)]
    public void SmoothProfilesAndSmallerRequestedTimeStepRemainUseful(double seconds)
    {
        var query = Query(seconds, OrbitalContractExamples.Bounds with { MaximumSampleStepSeconds = .07 });
        static OrbitalTopocentricState Path(double t) => State(new(300 * Math.Sin(.012 * t), 500 * Math.Cos(.012 * t), 600));
        var run = Run(query, Path);
        var track = Complete(run);
        var residual = VerifyDense(track, Path, 11);
        Assert.IsLessThanOrEqualTo(.07, track.Sampling.AchievedMaximumStepSeconds);
        TestContext.WriteLine($"{seconds}s / 0.07s step: evaluations={run.Budget.Evaluations}, samples={track.Samples.Count}, estimate={track.Sampling.AchievedChordErrorPixels:R}px, dense={residual:R}px");
    }

    [TestMethod]
    public void FailedPointIntervalPreservesDisjointPortionsWithoutGapEdges()
    {
        var query = Query(2);
        var run = Run(query, t => t is > .45 and < .55 ? null : State(new(t, 0, 1000)));
        var track = run.Result.Track!;
        Assert.AreEqual(OrbitalExecutionStatus.Partial, track.Status);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, track.Reason);
        Assert.IsTrue(track.Samples.Any(s => (s.Utc - query.Request.Interval.StartUtc).TotalSeconds > .6));
        foreach (var segment in track.Segments)
        {
            var start = (track.Samples[segment.StartSampleIndex].Utc - query.Request.Interval.StartUtc).TotalSeconds;
            var end = (track.Samples[segment.StartSampleIndex + segment.SampleCount - 1].Utc - query.Request.Interval.StartUtc).TotalSeconds;
            Assert.IsFalse(start < .45 && end > .55);
        }
        Assert.AreEqual(track.Samples.Count, run.Budget.Samples);
        Assert.IsTrue(track.Segments.Any(static s => s.EndReason == OrbitalClipReason.ProjectionFailure));
        Assert.AreEqual(track.ProductSha256, OrbitalPredictionJson.ParseTrack(OrbitalPredictionJson.SerializeTrack(track)).ProductSha256);
    }

    [TestMethod]
    [DataRow(OrbitalPredictionReason.EvaluationBudget)]
    [DataRow(OrbitalPredictionReason.SampleBudget)]
    [DataRow(OrbitalPredictionReason.Deadline)]
    public void WorkLimitsPreserveUsefulSamplesAndChargeOnlySuccessfulReservations(OrbitalPredictionReason expected)
    {
        var bounds = OrbitalContractExamples.Bounds with
        {
            MaximumEvaluations = expected == OrbitalPredictionReason.EvaluationBudget ? 12 : 10000,
            MaximumSamples = expected == OrbitalPredictionReason.SampleBudget ? 2 : 10000,
            MaximumElapsedMilliseconds = expected == OrbitalPredictionReason.Deadline ? 10 : 10000
        };
        var clock = new TestClock();
        var run = Run(Query(4, bounds), t =>
        {
            if (expected == OrbitalPredictionReason.Deadline) clock.Ticks += TimeSpan.TicksPerMillisecond;
            return State(new(10 * t, 0, 1000));
        }, clock: clock);
        Assert.AreEqual(expected, run.Result.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, run.Result.Track!.Status);
        Assert.IsGreaterThanOrEqualTo(1, run.Result.Track.Samples.Count);
        Assert.AreEqual(run.Result.Track.Samples.Count, run.Budget.Samples);
        Assert.IsLessThanOrEqualTo(bounds.MaximumEvaluations, run.Budget.Evaluations);
        Assert.IsLessThanOrEqualTo(bounds.MaximumSamples, run.Budget.Samples);
        Assert.AreEqual(0, run.Budget.Tracks);
    }

    [TestMethod]
    public void OneEvaluationStillRetainsItsUsefulPointAndNoUsefulEvaluationReturnsNoProduct()
    {
        var limited = Query(1, OrbitalContractExamples.Bounds with { MaximumEvaluations = 1 });
        var one = Run(limited, _ => State(new(0, 0, 1000)));
        Assert.AreEqual(1, one.Result.Track!.Samples.Count);
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, one.Result.Reason);
        var none = Run(Query(1), _ => null);
        Assert.IsNull(none.Result.Track);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, none.Result.Reason);
    }

    [TestMethod]
    public void SerializedOutputAllowanceRetainsAValidPartialProductBeforeTheV1ByteLimit()
    {
        var query = Query(12000, OrbitalContractExamples.Bounds with
        { MaximumSamples = 100000, MaximumEvaluations = 1000000 });
        var run = Run(query, _ => State(new(0, 0, 1000)));
        Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, run.Result.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, run.Result.Track!.Status);
        Assert.AreEqual(OrbitalPredictionReason.None, run.Budget.Reason);
        Assert.IsLessThan(100000, run.Budget.Samples);
        Assert.AreEqual(run.Result.Track.Samples.Count, run.Budget.Samples);
        Assert.IsLessThan(OrbitalPredictionJson.MaximumProductBytes, OrbitalPredictionJson.SerializeTrack(run.Result.Track).Length);
    }

    [TestMethod]
    public void CancellationDuringRefinementThrowsThroughTheSharedBudget()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.ThrowsExactly<OperationCanceledException>(() => Run(Query(1), t =>
        {
            if (++calls == 11) cancellation.Cancel();
            return State(new(1000 * t * t, 0, 1000));
        }, token: cancellation.Token));
        Assert.AreEqual(11, calls);
    }

    [TestMethod]
    public void ZeroDurationAndShortSmoothExposuresAndClockFactsRemainHonest()
    {
        foreach (var duration in new[] { 0d, .00001, .2 })
        {
            var original = Query(duration);
            var timing = original.Timing with { Clock = new("test-clock", AstrometricClockSynchronization.Synchronized, .03, .01) };
            var track = Complete(Run(new(original.Request, original.Geometry, timing), _ => State(new(0, 0, 1000))));
            Assert.AreEqual(.03, track.Uncertainty.Clock.Value);
            Assert.IsNull(track.Uncertainty.Orbit.Value);
            Assert.IsNull(track.Uncertainty.Calibration.Value);
            Assert.IsTrue(track.Samples.All(static s => s.Illumination == OrbitalIllumination.Unknown));
            StringAssert.Contains(track.Uncertainty.Sampling.Reason, OrbitalNumericalPath.EstimatorVersion, StringComparison.Ordinal);
        }
        var query = Query(1);
        query = new(query.Request, query.Geometry, query.Timing with
        { Clock = new("estimate-only", AstrometricClockSynchronization.Unsynchronized, null, .2) });
        var estimated = Complete(Run(query, _ => State(new(0, 0, 1000))));
        Assert.IsNull(estimated.Uncertainty.Clock.Value);
        Assert.AreEqual(.2, estimated.Timing.Clock.EstimatedErrorSeconds);
        StringAssert.Contains(estimated.Uncertainty.Clock.Reason, "clock-error-unknown", StringComparison.Ordinal);
        Assert.IsNull(Complete(Run(Query(1), _ => State(new(0, 0, 1000)))).Uncertainty.Clock.Value);
    }

    [TestMethod]
    public void AcceptedMeasuredGeometryRetainsEvidenceWithoutInventingPixelCalibrationAccuracy()
    {
        var measured = OrbitalContractExamples.MeasuredGeometry();
        var frame = measured.Assessment.Frame;
        var interval = new OrbitalUtcInterval(frame.ExposureStartUtc, frame.ExposureEndUtc);
        var coverage = new OrbitalUtcInterval(interval.StartUtc.AddDays(-1), interval.EndUtc.AddDays(1));
        var model = OrbitalContractExamples.Model with
        {
            NominalZeroEarthOrientation = false,
            EarthOrientationSha256 = OrbitalContractExamples.Sha('C'),
            EarthOrientationCoverage = coverage,
            EarthOrientationStatus = OrbitalEarthOrientationStatus.Final,
            Ut1Convention = "utc-plus-pinned-dut1",
            PolarMotionConvention = "pinned-eop",
            LeapTableCoverage = coverage
        };
        var record = OrbitalContract.CreateRecord(OrbitalContractExamples.Record() with { EpochUtc = interval.StartUtc });
        var snapshot = OrbitalContractExamples.Snapshot(record);
        var policy = OrbitalContractExamples.Policy();
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.MeasuredAssociation, snapshot, policy, interval,
            [record.ObjectIdentity], frame.Observer, model, OrbitalContractExamples.Bounds);
        var timing = OrbitalContractExamples.Timing with { Exposure = interval, ExposureBasis = OrbitalTimingBasis.Actual, ReadoutCompletedUtc = interval.EndUtc };
        var query = new OrbitalExposureQuery(request, measured.Geometry, timing, measured.Calibration, measured.Assessment);
        var lineage = new OrbitalProductLineage(snapshot.Manifest.ManifestSha256, snapshot.Manifest.NormalizedPayloadSha256,
            record.RecordSha256, record.ObjectIdentity, record.SourceDataset, policy.IdentitySha256, record.EpochUtc,
            0, (interval.EndUtc - record.EpochUtc).TotalHours, OrbitalElementFreshness.Fresh, OrbitalSourceValidity.Unknown, snapshot.Manifest.Coverage);
        var budget = new OrbitalWorkBudget(request.Bounds, default, new TestClock());
        var boresight = new AltAzPoint(measured.Geometry.Projection.BoresightAltitudeDegrees, measured.Geometry.Projection.BoresightAzimuthDegrees);
        var result = OrbitalExposureSampler.Sample(query, lineage, _ =>
            budget.TryEvaluation() ? State(CameraBasis.FromHorizontal(boresight) * 1000) : null, budget);
        var track = Complete((result, budget));
        Assert.AreEqual(OrbitalGeometryBasis.AcceptedMeasured, track.Geometry.Basis);
        Assert.AreEqual(measured.Assessment.IdentitySha256.ToUpperInvariant(), track.Geometry.AssessmentIdentitySha256);
        Assert.IsNull(track.Uncertainty.Calibration.Value);
        StringAssert.Contains(track.Uncertainty.Calibration.Reason, "accepted-measured-geometry", StringComparison.Ordinal);
    }

    private static (OrbitalExposureSamplingResult Result, OrbitalWorkBudget Budget) Run(OrbitalExposureQuery query,
        Func<double, OrbitalTopocentricState?> path, TestClock? clock = null, CancellationToken token = default)
    {
        var budget = new OrbitalWorkBudget(query.Request.Bounds, token, clock ?? new TestClock());
        var observed = new HashSet<long>();
        var result = OrbitalExposureSampler.Sample(query, OrbitalContractExamples.Lineage(query.Request.Snapshot, query.Request.Interval), utc =>
        {
            Assert.IsTrue(observed.Add(utc.UtcTicks), "Repeated callback for a cached UTC tick.");
            if (!budget.TryEvaluation()) return null;
            return path((utc - query.Request.Interval.StartUtc).TotalSeconds);
        }, budget);
        return (result, budget);
    }

    private static OrbitalExposureTrackV1 Complete((OrbitalExposureSamplingResult Result, OrbitalWorkBudget Budget) run)
    {
        Assert.IsNotNull(run.Result.Track, run.Result.Detail);
        Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason, run.Result.Detail + " evaluations=" + run.Budget.Evaluations);
        var track = run.Result.Track;
        Assert.AreEqual(OrbitalExecutionStatus.Complete, track.Status);
        Assert.AreEqual(track.RequestedInterval, track.CoveredInterval);
        var maximum = track.Samples.Zip(track.Samples.Skip(1), static (a, b) => (b.Utc - a.Utc).TotalSeconds).DefaultIfEmpty().Max();
        Assert.AreEqual(maximum, track.Sampling.AchievedMaximumStepSeconds);
        Assert.IsLessThanOrEqualTo(track.Sampling.MaximumStepSeconds, maximum);
        Assert.AreEqual(track.Sampling.AchievedChordErrorPixels, track.Uncertainty.Sampling.Value);
        return track;
    }

    private static double VerifyDense(OrbitalExposureTrackV1 track, Func<double, OrbitalTopocentricState> path, int count = 73)
    {
        var projector = new OrbitalSampleProjector(track.Geometry);
        var maximumResidual = 0d;
        foreach (var segment in track.Segments)
        {
            for (var i = segment.StartSampleIndex + 1; i < segment.StartSampleIndex + segment.SampleCount; i++)
            {
                var a = track.Samples[i - 1]; var b = track.Samples[i];
                for (var j = 0; j < count; j++)
                {
                    // Deliberately off-phase with both quartic and eighth nodes.
                    var fraction = (j + .3819660112501051) / count;
                    var utc = a.Utc.AddTicks((long)((b.Utc - a.Utc).Ticks * fraction));
                    var actualFraction = (double)(utc.UtcTicks - a.Utc.UtcTicks) / (b.Utc.UtcTicks - a.Utc.UtcTicks);
                    var expected = new PixelPoint(a.Pixel!.Value.X * (1 - actualFraction) + b.Pixel!.Value.X * actualFraction,
                        a.Pixel.Value.Y * (1 - actualFraction) + b.Pixel.Value.Y * actualFraction);
                    var point = projector.Project(utc, path((utc - track.RequestedInterval.StartUtc).TotalSeconds));
                    Assert.IsNotNull(point.Sample.Pixel, "Drawable segment crosses an independently sampled clipping gap.");
                    var residual = Distance(expected, point.Sample.Pixel.Value);
                    maximumResidual = Math.Max(maximumResidual, residual);
                    Assert.IsLessThanOrEqualTo(track.Sampling.AchievedChordErrorPixels!.Value + 1e-8, residual,
                        $"Dense residual {residual:R} exceeds declared numerical estimate at {utc:O}.");
                    Assert.IsLessThanOrEqualTo(track.Sampling.MaximumChordErrorPixels, residual);
                }
            }
        }
        return maximumResidual;
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static OrbitalTopocentricState State(EnuVector position)
        => new(CameraBasis.ToHorizontal(position), position.Length, 0, .5, position.Normalize());

    private static OrbitalExposureQuery Query(double seconds, OrbitalQueryBounds? bounds = null, OrbitalGeometryFacts? geometry = null)
    {
        var interval = new OrbitalUtcInterval(OrbitalContractExamples.Exposure.StartUtc, OrbitalContractExamples.Exposure.StartUtc.AddSeconds(seconds));
        var snapshot = OrbitalContractExamples.Snapshot();
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(), interval,
            [new("norad", "25544")], OrbitalContractExamples.Observer, OrbitalContractExamples.Model,
            bounds ?? OrbitalContractExamples.Bounds with { MaximumElapsedMilliseconds = 30000 });
        return new(request, geometry ?? Geometry(ProjectionModel.EquidistantFisheye), OrbitalContractExamples.Timing with
        { Exposure = interval, ReadoutCompletedUtc = interval.EndUtc.AddMilliseconds(20) });
    }

    private static OrbitalGeometryFacts Geometry(ProjectionModel model, double focal = 200, double distortion = 0)
        => OrbitalContractExamples.Geometry with
        {
            Projection = new(model, 320, 320, focal, focal, 640, 640,
                model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
                model == ProjectionModel.Perspective ? null : model == ProjectionModel.OrthographicFisheye ? focal : 314,
                RadialDistortionK1: distortion)
        };

    private static OrbitalGeometryFacts Cropped(OrbitalGeometryFacts original) => original with
    {
        Readout = original.Readout with { RoiX = 160, RoiY = 160, RoiWidth = 320, RoiHeight = 320, BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalSumV1 },
        ImageTransform = new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 640, 160, 160, 320, 320, 2, 2,
            true, false, ProjectedSceneQuarterRotation.Degrees90, 160, 160)
    };

    private sealed class TestClock : TimeProvider
    {
        internal long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
