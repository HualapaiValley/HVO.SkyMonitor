namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPredictionContractTests
{
    [TestMethod]
    public void ProductsPreserveUnknownErrorsAndClippingGapsWithDefensiveCollections()
    {
        var original = OrbitalContractExamples.Track();
        var samples = original.Samples.ToArray(); var segments = original.Segments.ToArray();
        var input = original with { Samples = samples, Segments = segments };
        var track = OrbitalPredictionJson.CreateTrack(input);
        samples[0] = samples[0] with { Pixel = new(1, 1) }; segments[0] = new(0, 3, OrbitalClipReason.WindowBoundary, OrbitalClipReason.WindowBoundary);
        Assert.AreEqual(original.Samples[0], track.Samples[0]); Assert.AreEqual(2, track.Segments.Count);
        Assert.IsNull(track.Samples[1].Pixel); Assert.IsNull(track.Uncertainty.Orbit.Value); Assert.IsNull(track.Uncertainty.Clock.Value);
        Assert.AreEqual(OrbitalIllumination.Unknown, track.Samples[0].Illumination);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(track with { Segments = segments }));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<OrbitalTrackSample>)track.Samples).Clear());
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(track with { Samples = track.Samples.Reverse().ToArray() }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(track with { Samples = [track.Samples[0] with { RangeKilometers = double.NaN }, .. track.Samples.Skip(1)] }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(track with { Samples = [track.Samples[0] with { Illumination = OrbitalIllumination.Sunlit }, .. track.Samples.Skip(1)] }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(track with { Lineage = track.Lineage with { Freshness = OrbitalElementFreshness.Stale } }));
    }

    [TestMethod]
    public void CompleteProductsCannotConcealCoverageSamplingOrPassBoundaryFailures()
    {
        var t = OrbitalContractExamples.Track();
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(t with { CoveredInterval = t.CoveredInterval with { EndUtc = t.CoveredInterval.EndUtc.AddSeconds(-1) } }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(t with { Sampling = t.Sampling with { AchievedChordErrorPixels = null } }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(t with { Sampling = t.Sampling with { AchievedMaximumStepSeconds = 2 } }));
        var p = OrbitalContractExamples.Pass();
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreatePass(p with { StartBoundary = OrbitalPassBoundary.SearchWindow }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreatePass(p with { EndBoundary = OrbitalPassBoundary.WorkLimit }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreatePass(p with { PeakUtc = p.PassInterval.EndUtc.AddSeconds(1) }));
        var partial = OrbitalPredictionJson.CreatePass(p with { EndBoundary = OrbitalPassBoundary.WorkLimit, Status = OrbitalExecutionStatus.Partial, Reason = OrbitalPredictionReason.Deadline });
        Assert.AreEqual(OrbitalExecutionStatus.Partial, OrbitalPredictionJson.ParsePass(OrbitalPredictionJson.SerializePass(partial)).Status);
    }

    [TestMethod]
    public void QueryBoundsGeometryAndTimingDoNotPromoteNominalFactsToMeasured()
    {
        var g = OrbitalContractExamples.Geometry; g.Validate();
        var badReadout = g with { Readout = g.Readout with { RoiWidth = 320, BinX = 2 } };
        Assert.ThrowsExactly<ArgumentException>(badReadout.Validate);
        var measured = g with { Basis = OrbitalGeometryBasis.AcceptedMeasured, AssessmentIdentitySha256 = OrbitalContractExamples.Sha('A') };
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, OrbitalContractExamples.Snapshot(), OrbitalContractExamples.Policy(),
            OrbitalContractExamples.Exposure, [new("norad", "25544")], OrbitalContractExamples.Observer, OrbitalContractExamples.Model, OrbitalContractExamples.Bounds);
        new OrbitalExposureQuery(request, g, OrbitalContractExamples.Timing).Validate();
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalExposureQuery(request, measured, OrbitalContractExamples.Timing).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPredictionRequest(OrbitalPredictionUse.MeasuredAssociation, request.Snapshot, request.SelectionPolicy,
            request.Interval, request.Objects, request.Observer, request.Model, request.Bounds));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, request.Snapshot, request.SelectionPolicy,
            request.Interval, request.Objects, request.Observer, request.Model with { LeapTableCoverage = new(request.Interval.EndUtc, request.Interval.EndUtc) }, request.Bounds));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalExposureQuery(request, g,
            OrbitalContractExamples.Timing with { ReadoutBasis = OrbitalTimingBasis.Actual, ReadoutCompletedUtc = null }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (request.Bounds with { MaximumSampleStepSeconds = double.PositiveInfinity }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (request.Bounds with { MaximumChordErrorPixels = .251 }).Validate());
        Assert.AreEqual(AstrometricClockSynchronization.NotSupplied, OrbitalContractExamples.Timing.Clock.Synchronization);
    }

    [TestMethod]
    public void MeasuredEvidenceRequiresTheExactAcceptedCalibration()
    {
        var fixture = OrbitalContractExamples.MeasuredGeometry();
        Assert.IsTrue(fixture.Assessment.HasMeasuredMapping, fixture.Assessment.Reason);
        fixture.Geometry.ValidateMeasuredEvidence(fixture.Calibration, fixture.Assessment);
        Assert.ThrowsExactly<ArgumentException>(() => (fixture.Geometry with { CalibrationIdentitySha256 = OrbitalContractExamples.Sha('B') })
            .ValidateMeasuredEvidence(fixture.Calibration, fixture.Assessment));

    }

    [TestMethod]
    public void ResultBudgetsAndProductValidationAreMandatory()
    {
        var track = OrbitalContractExamples.Track(); var bounds = OrbitalContractExamples.Bounds;
        OrbitalPredictionResult<OrbitalExposureTrackV1> Result(OrbitalExecutionStatus status, OrbitalPredictionReason reason,
            IEnumerable<OrbitalExposureTrackV1> tracks, IEnumerable<OrbitalObjectFailure>? failures = null, OrbitalQueryBounds? limits = null)
            => new(new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, OrbitalContractExamples.Snapshot(), OrbitalContractExamples.Policy(),
                OrbitalContractExamples.Exposure, [new("norad", "25544")], OrbitalContractExamples.Observer, OrbitalContractExamples.Model, limits ?? bounds),
                status, reason, status == OrbitalExecutionStatus.Complete ? 1 : 0, 3, 1, tracks, failures ?? []);
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalExecutionStatus.Complete, OrbitalPredictionReason.Deadline, []));
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, [track with { ProductSha256 = OrbitalContractExamples.Sha('A') }]));
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, [track],
            [new(track.Lineage.ObjectIdentity, OrbitalPredictionReason.EvaluationBudget, "exhausted")]));
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, [track], limits: bounds with { MaximumSamples = 2 }));
        var samples = track.Samples.ToArray();
        var result = Result(OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, [track with { Samples = samples }]);
        samples[0] = samples[0] with { Pixel = new(1, 1) };
        Assert.AreEqual(track.Samples[0], result.Products[0].Samples[0]);
        var partial = Result(OrbitalExecutionStatus.Partial, OrbitalPredictionReason.Deadline, []);
        Assert.AreEqual(0, partial.ReturnedProducts); Assert.AreEqual(OrbitalExecutionStatus.Partial, partial.Status);
        Assert.IsTrue(partial.PopulationCoverage.KnownExclusions.Count > 0);
    }
    [TestMethod]
    public void ProductCanonicalNumbersGivePositiveAndNegativeZeroOneIdentity()
    {
        var original = OrbitalContractExamples.Track();
        var positive = OrbitalPredictionJson.CreateTrack(original with { Samples = [original.Samples[0] with { Horizontal = new(0, 0) }, .. original.Samples.Skip(1)] });
        var negative = OrbitalPredictionJson.CreateTrack(positive with { Samples = [positive.Samples[0] with { Horizontal = new(-0d, -0d) }, .. positive.Samples.Skip(1)] });
        Assert.AreEqual(positive.ProductSha256, negative.ProductSha256);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(positive), OrbitalPredictionJson.SerializeTrack(negative));
    }

}
