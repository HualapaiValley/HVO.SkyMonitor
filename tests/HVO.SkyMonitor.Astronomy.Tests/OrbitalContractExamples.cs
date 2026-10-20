using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

internal static class OrbitalContractExamples
{
    internal static readonly DateTimeOffset Epoch = new(2026, 10, 19, 4, 0, 0, TimeSpan.Zero);
    internal static string Sha(char c) => new(c, 64);
    internal static OrbitalSoftwareIdentity Parser => new("synthetic-gp-parser", "1", Sha('A'));
    internal static OrbitalSoftwareIdentity Normalizer => new("synthetic-gp-normalizer", "1", Sha('B'));
    internal static OrbitalPopulationCoverage Coverage => new("active-group", OrbitalPopulationCompleteness.CompleteForRequestedPopulation,
        ["inactive objects", "objects absent from provider", "unqualified analyst identifiers"]);
    internal static OrbitalRecordV1 Record(string catalogNumber = "25544", double epochHours = 0, string dataset = "celestrak:gp:active", string? name = "Synthetic ISS-like fixture")
        => OrbitalContract.CreateRecord(new("", new("norad", catalogNumber), dataset, null, name, null, null, null,
            Epoch.AddHours(epochHours), null, null,
            new("EARTH", "TEME", "UTC", "SGP4", OrbitalContract.GpDerivativeConvention, OrbitalContract.CelesTrakGpProfile, Parser, Normalizer),
            new(15.5, .0002, 51.6, 125, 45, 270, .0001, .00002, -.0000003, 0, "U", 999, 50000)));
    internal static OrbitalSnapshotSource Source(string dataset = "celestrak:gp:active") => new(dataset, "celestrak", "USSF public GP",
        new("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON"), Epoch.AddHours(1), null, null, null,
        Sha('C'), 1234, Parser, Normalizer, new("https://celestrak.org/NORAD/documentation/gp-data-formats.php"),
        "Synthetic facts authored for #1402; no source payload redistributed.");
    internal static OrbitalSnapshot Snapshot(params OrbitalRecordV1[] records) => OrbitalSnapshot.Create(
        records.Length == 0 ? [Record(), Record("100001", name: null), Record("999999999"), Record(epochHours: -1)] : records,
        [Source()], Coverage);
    internal static OrbitalSelectionPolicy Policy(bool stale = false) => new([new("celestrak:gp:active", 0)], stale);
    internal static OrbitalUtcInterval Exposure => new(Epoch.AddHours(2), Epoch.AddHours(2).AddSeconds(2));
    internal static OrbitalGeometryFacts Geometry => new(OrbitalGeometryBasis.ConfiguredRig, Sha('D'), Sha('E'),
        new(640, 640, 0, 0, 640, 640, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null), Sha('F'), null, null,
        OrbitalProjectionCoordinates.NativeSensor,
        new(ProjectionModel.EquidistantFisheye, 320, 320, 200, 200, 640, 640, ProjectionAperture.Circular, 314),
        ProjectedSceneImageTransformV1.Identity(640, 640), HorizonPolicy.GeometricHorizon);
    internal static OrbitalTimingFacts Timing => new(Exposure, OrbitalTimingBasis.Estimated, Exposure.EndUtc.AddMilliseconds(20),
        OrbitalTimingBasis.Reported, "synthetic-sequence-clock; not sensor-actual evidence", AstrometricClockFacts.NotSupplied);
    internal static OrbitalPredictionModelFacts Model => new(new("qualified-later-sgp4-fixture", "1", Sha('A')), "WGS72", "a", "teme-gmst82-fixture-v1",
        "utc-assumed-ut1", "zero-assumed", null, null, OrbitalEarthOrientationStatus.ApproximateZero,
        "linear-dut1-polar-no-extrapolation-v1", true, Sha('B'), new(Epoch.AddDays(-1), Epoch.AddDays(8)), "reject-crossing-v1", "wgs84-ellipsoidal-assumed", "none-geometric", null);
    internal static OrbitalQueryBounds Bounds => new(32, 86400, 100000, 32, 100, 10000, 4000, 1, .25);
    internal static ObserverLocation Observer => new(35.1, -113.8, 1000);
    internal static OrbitalProductLineage Lineage(OrbitalSnapshot snapshot, OrbitalUtcInterval interval)
    {
        var record = snapshot.Records.Single(r => r.ObjectIdentity.CatalogNumber == "25544" && r.EpochUtc == Epoch);
        return new(snapshot.Manifest.ManifestSha256, snapshot.Manifest.NormalizedPayloadSha256, record.RecordSha256,
            record.ObjectIdentity, record.SourceDataset, Policy().IdentitySha256, record.EpochUtc,
            (interval.StartUtc - record.EpochUtc).TotalHours, (interval.EndUtc - record.EpochUtc).TotalHours,
            OrbitalElementFreshness.Fresh, OrbitalSourceValidity.Unknown, snapshot.Manifest.Coverage);
    }
    internal static OrbitalExposureTrackV1 Track(OrbitalSnapshot? snapshot = null)
    {
        var s = snapshot ?? Snapshot();
        return OrbitalPredictionJson.CreateTrack(new(OrbitalPredictionJson.TrackSchemaVersion, "", Lineage(s, Exposure),
            Exposure, Exposure, Observer, Geometry, Timing, Model,
            [new(Exposure.StartUtc, new(70, 0), new(320, 250), 500, -1, .2, OrbitalIllumination.Unknown),
             new(Exposure.StartUtc.AddSeconds(1), new(70, 1), null, 500, -1, .2, OrbitalIllumination.Unknown),
             new(Exposure.EndUtc, new(70, 2), new(322, 250), 500, -1, .2, OrbitalIllumination.Unknown)],
            [new(0, 1, OrbitalClipReason.WindowBoundary, OrbitalClipReason.ProjectionFailure),
             new(2, 1, OrbitalClipReason.ProjectionFailure, OrbitalClipReason.WindowBoundary)],
            new(new(null, "degrees", "physical-orbit-error-unqualified"), new(null, "seconds", "clock-not-supplied"),
                new(null, "pixels", "configured-rig-only"), new(.1, "pixels", "synthetic-declared-bound")),
            new(1, .25, 1, .1), OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));
    }
    internal static OrbitalPassSummaryV1 Pass(OrbitalSnapshot? snapshot = null)
    {
        var search = new OrbitalUtcInterval(Epoch.AddHours(2), Epoch.AddHours(3));
        return OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", Lineage(snapshot ?? Snapshot(), search), search,
            new(search.StartUtc.AddMinutes(10), search.StartUtc.AddMinutes(20)), search.StartUtc.AddMinutes(15), 70, 10,
            OrbitalPassBoundary.ResolvedCrossing, OrbitalPassBoundary.ResolvedCrossing, Observer, Model,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));
    }
    internal static (AstrometricCalibration Calibration, AstrometricFrameAssessment Assessment, OrbitalGeometryFacts Geometry) MeasuredGeometry()
    {
        var truth = AstrometricTestFixture.Truth();
        var calibration = AstrometricTestFixture.Calibration(truth);
        var context = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc);
        var catalog = AstrometricTestFixture.Catalog();
        var detections = AstrometricTestFixture.Centroids(catalog, truth, context.MidpointUtc);
        var assessment = AstrometricSolver.Solve(context, calibration, catalog, detections).Assessment;
        var geometry = Geometry with
        {
            Projection = AstrometricMapping.Projection(calibration, assessment),
            ReadoutIdentitySha256 = calibration.ReadoutIdentitySha256.ToUpperInvariant(),
            Readout = Geometry.Readout with { NativeWidth = 512, NativeHeight = 512, RoiWidth = 512, RoiHeight = 512 },
            ImageTransform = ProjectedSceneImageTransformV1.Identity(512, 512),
            Basis = OrbitalGeometryBasis.AcceptedMeasured,
            CalibrationIdentitySha256 = calibration.IdentitySha256.ToUpperInvariant(),
            AssessmentIdentitySha256 = assessment.IdentitySha256.ToUpperInvariant()
        };
        geometry.ValidateMeasuredEvidence(calibration, assessment);
        return (calibration, assessment, geometry);
    }

}
