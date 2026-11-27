using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualAstrometricUncertaintyQualificationTests
{
    [TestMethod]
    public Task TuningLongExposureRelease() => RunAsync(tuning: true, new QualificationCampaign(tuning: true));

    [TestMethod]
    public Task HeldOutLongExposureRelease() => RunAsync(tuning: false, new QualificationCampaign(tuning: false));

    private sealed class QualificationCampaign
    {
        public QualificationCampaign(bool tuning)
        {
            IsTuning = tuning;
            var cell = (VirtualAstrometryFixture.Family.Name, VirtualAstrometryFixture.ExposureSeconds);
            Assert.IsTrue(cell is ("equidistant", 20 or 60) or ("rectilinear", 20), $"Undeclared #1179 cell {cell}.");
            var historical = DeclaredFloorPixels[cell.Item2];
            Partitions = tuning ? [(2, 18, 117902), (8, 19, 117908)] : [(3, 20, 117903), (7, 22, 117907), (11, 18, 117911)];
            var floorFile = Environment.GetEnvironmentVariable("HVO_1179_FLOORS");
            if (tuning)
            {
                Assert.IsTrue(string.IsNullOrEmpty(floorFile), "Candidate floors cannot be supplied to tuning.");
                Floors = historical;
                return;
            }

            Assert.IsFalse(string.IsNullOrWhiteSpace(floorFile), "Held-out execution requires the frozen tuning output.");
            Floors = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(floorFile))!;
            var names = VirtualAstrometryFixture.Profiles(Partitions[0].Seed).Select(p => p.Name);
            CollectionAssert.AreEquivalent(names.ToArray(), Floors.Keys.ToArray());
            Assert.IsTrue(Floors.Values.All(sigma => double.IsFinite(sigma) && sigma is >= 0 and <= 10));
        }

        public bool IsTuning { get; }
        public IReadOnlyDictionary<string, double> Floors { get; }
        public (int Month, int Day, int Seed)[] Partitions { get; }
        public Dictionary<string, CaptureLineage> Lineage { get; } = new(StringComparer.Ordinal);

        public CaptureLineage Capture(string id, CameraModuleConfig config, VirtualAstrometryPixels input, DateTimeOffset sceneUtc)
        {
            var frame = VirtualAstrometryFixture.FrameContextV2(input);
            Assert.AreEqual(sceneUtc, frame.MidpointUtc, $"{id} must bind the exposure midpoint.");
            var lineage = new CaptureLineage(Convert.ToHexString(SHA256.HashData(input.Payload.Span)),
                CaptureContractJson.ComputeCanonicalJsonSha256(config), frame.ExposureStartUtc, frame.ExposureEndUtc, frame.MidpointUtc);
            Lineage.Add(id, lineage);
            return lineage;
        }
    }

    private sealed record CaptureLineage(string RawSha256, string ConfigurationSha256, DateTimeOffset StartUtc,
        DateTimeOffset EndUtc, DateTimeOffset MidpointUtc)
    {
        public AstrometricAssessmentStatus? Mapping { get; set; }
        public string? ProductionUncertainty { get; set; }
    }

    private static string SessionCaptureId(string session, string readout, DateTimeOffset start) =>
        $"{session}-{readout}-{start.ToString("HHmm", CultureInfo.InvariantCulture)}";

    private static AstrometricFrameUncertainty Estimate(AstrometricCalibration calibration, AstrometricCatalogData catalog,
        AstrometricSolveResult solved, StellarMeasurementResult measured, VirtualAstrometryPixels input,
        AstrometricUncertaintyOptions options, QualificationCampaign? campaign, AstrometricCalibrationCovariance? shared = null)
    {
        var detections = VirtualAstrometryFixture.Detections(measured);
        var covariances = Covariances(measured);
        return campaign is null
            ? AstrometricUncertaintyEstimator.Estimate(calibration, catalog, VirtualAstrometryFixture.SolverOptions, solved,
                detections, covariances, AstrometricClockFacts.NotSupplied, input.Layout.Readout, shared, options)
            : AstrometricUncertaintyEstimator.EstimateForLongExposureQualification(calibration, catalog, VirtualAstrometryFixture.SolverOptions,
                solved, detections, covariances, input.Layout.Readout, shared, options);
    }

    private static void CheckRelease(AstrometricCalibration calibration, AstrometricCatalogData catalog, AstrometricSolveResult solved,
        StellarMeasurementResult measured, VirtualAstrometryPixels input, AstrometricUncertaintyOptions options,
        AstrometricFrameUncertainty diagnostic, CaptureLineage lineage, AstrometricCalibrationCovariance? shared = null)
    {
        var production = Estimate(calibration, catalog, solved, measured, input, options, campaign: null, shared);
        Assert.AreEqual(lineage.MidpointUtc, solved.Assessment.Frame.MidpointUtc);
        Assert.AreEqual("exposure-not-qualified", production.ReasonCode);
        Assert.IsTrue(new[] { production.ConditionalFit, production.Systematic, production.SharedCalibration.Pose, production.Total }
            .All(component => component.Status == AstrometricUncertaintyEstimator.Withheld && component.Covariance is null));
        Assert.IsNull(production.SharedCalibration.Sensitivity);
        AstrometricUncertaintyEvidence.Validate(production, solved.Assessment);
        Assert.Throws<ArgumentException>(() => AstrometricUncertaintyEvidence.Validate(diagnostic, solved.Assessment));
        if (shared is not null)
            Assert.Throws<ArgumentException>(() => AstrometricUncertaintyEstimator.CrossCovariance(diagnostic, diagnostic, shared));
        lineage.ProductionUncertainty = production.ReasonCode;
    }
}
