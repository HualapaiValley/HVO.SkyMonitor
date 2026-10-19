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
            => new(new OrbitalExposureQuery(new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, OrbitalContractExamples.Snapshot(), OrbitalContractExamples.Policy(),
                OrbitalContractExamples.Exposure, [new("norad", "25544")], OrbitalContractExamples.Observer, OrbitalContractExamples.Model, limits ?? bounds),
                OrbitalContractExamples.Geometry, OrbitalContractExamples.Timing),
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
    public void MeasuredQueryRejectsEvidenceFromADifferentExposureOrObserver()
    {
        var (query, calibration, assessment) = MeasuredQuery();
        query.Validate();
        var r = query.Request;
        OrbitalPredictionRequest Request(OrbitalUtcInterval interval, ObserverLocation observer) => new(r.Use, r.Snapshot,
            r.SelectionPolicy, interval, r.Objects, observer, r.Model, r.Bounds);
        var later = new OrbitalUtcInterval(r.Interval.StartUtc.AddHours(1), r.Interval.EndUtc.AddHours(1));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalExposureQuery(Request(later, r.Observer), query.Geometry,
            query.Timing with { Exposure = later, ReadoutCompletedUtc = later.EndUtc }, calibration, assessment));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalExposureQuery(Request(r.Interval, new(0, 0, 0)), query.Geometry,
            query.Timing, calibration, assessment));
    }

    [TestMethod]
    public void ResultRetainsValidatedMeasuredGeometryAndTiming()
    {
        var (query, _, _) = MeasuredQuery(); var r = query.Request; var original = OrbitalContractExamples.Track();
        var record = r.Snapshot.Records.Single();
        var samples = Enumerable.Range(0, 21).Select(i => original.Samples[0] with
        {
            Utc = r.Interval.StartUtc.AddSeconds(i),
            Pixel = new(250, 250)
        }).ToArray();
        var track = OrbitalPredictionJson.CreateTrack(original with
        {
            Lineage = original.Lineage with
            {
                SnapshotManifestSha256 = r.Snapshot.Manifest.ManifestSha256,
                NormalizedPayloadSha256 = r.Snapshot.Manifest.NormalizedPayloadSha256,
                RecordSha256 = record.RecordSha256,
                EpochUtc = record.EpochUtc,
                StartEpochAgeHours = 0,
                EndEpochAgeHours = (r.Interval.EndUtc - record.EpochUtc).TotalHours
            },
            RequestedInterval = r.Interval,
            CoveredInterval = r.Interval,
            Observer = r.Observer,
            Geometry = query.Geometry,
            Timing = query.Timing,
            Model = r.Model,
            Samples = samples,
            Segments = [new(0, samples.Length, OrbitalClipReason.WindowBoundary, OrbitalClipReason.WindowBoundary)]
        });
        OrbitalPredictionResult<OrbitalExposureTrackV1> Result(OrbitalExposureTrackV1 product) => new(query,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 21, 1, [product], []);
        Assert.AreEqual(OrbitalGeometryBasis.AcceptedMeasured, Result(track).Products[0].Geometry.Basis);
        var nominal = OrbitalPredictionJson.CreateTrack(track with
        {
            Geometry = track.Geometry with { Basis = OrbitalGeometryBasis.ConfiguredRig, AssessmentIdentitySha256 = null }
        });
        Assert.ThrowsExactly<ArgumentException>(() => Result(nominal));
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalPredictionJson.CreateTrack(track with
        {
            Geometry = track.Geometry with { RigIdentitySha256 = OrbitalContractExamples.Sha('A') }
        })));
        Assert.ThrowsExactly<ArgumentException>(() => Result(OrbitalPredictionJson.CreateTrack(track with
        {
            Timing = track.Timing with { EvidenceReference = "different-capture-timing" }
        })));
        var pass = OrbitalContractExamples.Pass();
        var passQuery = new OrbitalPassQuery(new(OrbitalPredictionUse.Diagnostic, OrbitalContractExamples.Snapshot(),
            OrbitalContractExamples.Policy(), pass.SearchInterval, [pass.Lineage.ObjectIdentity], pass.Observer,
            pass.Model, OrbitalContractExamples.Bounds), pass.MinimumElevationDegrees);
        OrbitalPredictionResult<OrbitalPassSummaryV1> PassResult(OrbitalPassSummaryV1 product) => new(passQuery,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 3, 1, [product], []);
        Assert.AreEqual(1, PassResult(pass).ReturnedProducts);
        Assert.ThrowsExactly<ArgumentException>(() => PassResult(OrbitalPredictionJson.CreatePass(pass with { MinimumElevationDegrees = 11 })));
    }

    [TestMethod]
    public void ResultCannotClaimCompleteBeyondRequestedSamplingOrDeadline()
    {
        var track = OrbitalContractExamples.Track();
        OrbitalPredictionResult<OrbitalExposureTrackV1> Result(OrbitalQueryBounds bounds, double elapsed,
            OrbitalExecutionStatus status = OrbitalExecutionStatus.Complete, OrbitalPredictionReason reason = OrbitalPredictionReason.None)
            => new(new OrbitalExposureQuery(new(OrbitalPredictionUse.Diagnostic, OrbitalContractExamples.Snapshot(), OrbitalContractExamples.Policy(),
                track.RequestedInterval, [track.Lineage.ObjectIdentity], track.Observer, track.Model, bounds), track.Geometry, track.Timing),
                status, reason, 1, 3, elapsed, status == OrbitalExecutionStatus.Complete ? [track] : [], []);
        var bounds = OrbitalContractExamples.Bounds;
        Assert.ThrowsExactly<ArgumentException>(() => Result(bounds, bounds.MaximumElapsedMilliseconds + 1));
        Assert.ThrowsExactly<ArgumentException>(() => Result(bounds with { MaximumSampleStepSeconds = .5 }, 1));
        Assert.ThrowsExactly<ArgumentException>(() => Result(bounds with { MaximumChordErrorPixels = .05 }, 1));
        Assert.AreEqual(OrbitalPredictionReason.Deadline, Result(bounds, bounds.MaximumElapsedMilliseconds + 1,
            OrbitalExecutionStatus.Partial, OrbitalPredictionReason.Deadline).Reason);
    }

    [TestMethod]
    public void SamplingBudgetClippingRequiresPartialBudgetStatus()
    {
        var clipped = OrbitalContractExamples.Track() with
        {
            Segments = [new(0, 1, OrbitalClipReason.WindowBoundary, OrbitalClipReason.SamplingBudget)]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(clipped));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPredictionJson.CreateTrack(clipped with
        {
            Status = OrbitalExecutionStatus.Partial,
            Reason = OrbitalPredictionReason.IncompleteInterval
        }));
        var partial = OrbitalPredictionJson.CreateTrack(clipped with { Status = OrbitalExecutionStatus.Partial, Reason = OrbitalPredictionReason.SampleBudget });
        Assert.AreEqual(OrbitalExecutionStatus.Partial, OrbitalPredictionJson.ParseTrack(OrbitalPredictionJson.SerializeTrack(partial)).Status);
    }

    [TestMethod]
    public void ReadoutRejectsInconsistentBinningAndCfaFacts()
    {
        var geometry = OrbitalContractExamples.Geometry;
        foreach (var readout in new[]
        {
            geometry.Readout with { BinningAlgorithm = HVO.SkyMonitor.AgentCore.FrameBinningAlgorithm.DigitalSumV1 },
            geometry.Readout with { CfaOriginX = 9 }, geometry.Readout with { CfaOriginX = 0 },
            geometry.Readout with { CfaOriginX = 1, CfaOriginY = 0 }
        }) Assert.ThrowsExactly<ArgumentException>(() => (geometry with { Readout = readout }).Validate());
        (geometry with { Readout = geometry.Readout with { CfaOriginX = 0, CfaOriginY = 0 } }).Validate();
        var measured = OrbitalContractExamples.MeasuredGeometry();
        Assert.ThrowsExactly<ArgumentException>(() => (measured.Geometry with
        {
            Readout = measured.Geometry.Readout with { BinningAlgorithm = HVO.SkyMonitor.AgentCore.FrameBinningAlgorithm.DigitalSumV1 }
        }).ValidateMeasuredEvidence(measured.Calibration, measured.Assessment));
    }

    private static (OrbitalExposureQuery Query, AstrometricCalibration Calibration, AstrometricFrameAssessment Assessment) MeasuredQuery()
    {
        var fixture = OrbitalContractExamples.MeasuredGeometry(); var frame = fixture.Assessment.Frame;
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
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.MeasuredAssociation, OrbitalContractExamples.Snapshot(record),
            OrbitalContractExamples.Policy(), interval, [record.ObjectIdentity], frame.Observer, model, OrbitalContractExamples.Bounds);
        var timing = OrbitalContractExamples.Timing with { Exposure = interval, ReadoutCompletedUtc = interval.EndUtc };
        return (new(request, fixture.Geometry, timing, fixture.Calibration, fixture.Assessment), fixture.Calibration, fixture.Assessment);
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
