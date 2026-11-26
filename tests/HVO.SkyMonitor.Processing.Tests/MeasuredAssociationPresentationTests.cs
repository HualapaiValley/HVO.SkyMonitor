using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.Processing.Tests.MeasuredStellarAssociationFixtures;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #526 presentation layers derived solely from analytical association metadata: pre-solve measured markers/labels
/// (including unclaimed centroids without catalog identity) and separately selectable Expected, not detected diagnostics.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class MeasuredAssociationPresentationTests
{
    [TestMethod]
    public async Task MeasuredAndExpectedNotDetectedLayersSeparateQualifiedDetectionsUnclaimedCentroidsAndUnmatchedPredictions()
    {
        var scene = await StarFieldAsync(
            ("star:eligible", 0, 0, 1.5),
            ("star:low-snr", 6, 4, 2.2),
            ("star:absent", -6, -4, 2.0)).ConfigureAwait(false);
        var baseline = AllEligible(scene);
        var eligible = baseline.Associations.Single(static a => a.CatalogId == "star:eligible") with { DetectionIndex = 0 };
        var lowSnr = baseline.Associations.Single(static a => a.CatalogId == "star:low-snr") with
        {
            DetectionIndex = 1,
            SignalToNoise = 4.0,
            LabelEligible = false,
            LabelRejectionReason = MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise
        };
        var absent = baseline.Associations.Single(static a => a.CatalogId == "star:absent");
        var evidence = baseline with
        {
            Measurement = baseline.Measurement with { CandidateCount = 4, DetectionCount = 3 },
            Associations = [eligible, lowSnr],
            UnmatchedPredictions =
            [
                new(absent.CatalogId, absent.Magnitude, absent.ExpectedX, absent.ExpectedY,
                    MeasuredStellarAssociationReasonCodes.NoMeasuredSource)
            ],
            UnassociatedDetections =
            [
                new(2, 20.5, 22.5, 1800, 28, MeasuredStellarAssociationReasonCodes.NoCatalogPrediction)
            ]
        };

        var measured = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene);
        var expectedNotDetected = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, scene);

        // Measured layer marks eligible, ineligible, and unclaimed centroid (3 markers), but labels ONLY eligible.
        Assert.HasCount(3, measured.Markers);
        Assert.IsTrue(measured.Markers.Any(static m => m.Crosshair && Math.Abs(m.Center.X - 20.5) < 1e-6));
        var measuredLabel = Assert.ContainsSingle(measured.TextBlocks);
        Assert.IsFalse(measuredLabel.Lines[0].Contains("(expected)", StringComparison.Ordinal));
        Assert.AreEqual(evidence.AssociationIdentitySha256, measured.SourceIdentitySha256);

        // Expected, not detected marks both absent and low-SNR (ineligible) predictions with "(expected)" labels.
        Assert.HasCount(2, expectedNotDetected.Markers);
        Assert.HasCount(2, expectedNotDetected.TextBlocks);
        Assert.IsTrue(expectedNotDetected.TextBlocks.All(static block =>
            block.Lines[0].EndsWith(AnnotationLabelFormatter.ExpectedSuffix, StringComparison.Ordinal)));
        Assert.IsFalse(expectedNotDetected.TextBlocks.Any(static block =>
            block.Lines[0].Contains("eligible", StringComparison.OrdinalIgnoreCase)));

        // Canonical JSON round-trip preserves exact content identity for both layers.
        foreach (var payload in new[] { measured, expectedNotDetected })
        {
            var bytes = PresentationLayerPayloadJson.Serialize(payload);
            var reparsed = PresentationLayerPayloadJson.Parse(bytes);
            Assert.IsTrue(reparsed.IsValid, reparsed.ErrorPath);
            Assert.AreEqual(payload.ContentIdentitySha256, reparsed.Payload!.ContentIdentitySha256);
        }
    }

    [TestMethod]
    public async Task SceneOnlyExpectedNotDetectedLabelsAllClearPredictionsAsExpectedAndExcludesOutsideAperture()
    {
        var scene = await StarFieldAsync(("star:vega", 0, 0, 1.0), ("star:deneb", 6, -4, 1.5)).ConfigureAwait(false);

        var withoutMeasured = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(scene, associations: null);
        Assert.HasCount(2, withoutMeasured.Markers);
        Assert.HasCount(2, withoutMeasured.TextBlocks);
        Assert.IsTrue(withoutMeasured.TextBlocks.All(static b =>
            b.Lines[0].EndsWith(AnnotationLabelFormatter.ExpectedSuffix, StringComparison.Ordinal)));

        var allMatched = AllEligible(scene);
        var withAllMatched = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(scene, allMatched);
        Assert.IsEmpty(withAllMatched.Markers);
        Assert.IsEmpty(withAllMatched.TextBlocks);

        var outsideOnly = allMatched with
        {
            Measurement = allMatched.Measurement with { DetectionCount = 1 },
            Associations = [allMatched.Associations[0]],
            UnmatchedPredictions =
            [
                new(allMatched.Associations[1].CatalogId, allMatched.Associations[1].Magnitude,
                    allMatched.Associations[1].ExpectedX, allMatched.Associations[1].ExpectedY,
                    MeasuredStellarAssociationReasonCodes.OutsideAperture)
            ],
            UnassociatedDetections = []
        };
        var outsideLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(outsideOnly, scene);
        Assert.IsEmpty(outsideLayer.Markers);
        Assert.IsEmpty(outsideLayer.TextBlocks);
    }

    [TestMethod]
    public async Task ClassifyEmptyExpectedLayerReasonDistinguishesAllMatchedFilteredAndNoPredictions()
    {
        var scene = await StarFieldAsync(("star:bright", 0, 0, 1.5), ("star:faint", 8, -6, 7.8)).ConfigureAwait(false);
        var allMatched = AllEligible(scene);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedAllMatchedReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(allMatched));

        // Faint unmatched prediction (> 6.5 mag) produces zero drawable markers, yet must NOT claim all matched.
        var faintUnmatched = allMatched with
        {
            Measurement = allMatched.Measurement with { DetectionCount = 1 },
            Associations = [allMatched.Associations.Single(static a => a.CatalogId == "star:bright")],
            UnmatchedPredictions =
            [
                new("star:faint", 7.8,
                    allMatched.Associations.Single(static a => a.CatalogId == "star:faint").ExpectedX,
                    allMatched.Associations.Single(static a => a.CatalogId == "star:faint").ExpectedY,
                    MeasuredStellarAssociationReasonCodes.NoMeasuredSource)
            ],
            UnassociatedDetections = []
        };
        var filteredLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(faintUnmatched, scene);
        Assert.IsEmpty(filteredLayer.Markers);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedFilteredReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(faintUnmatched));

        // Outside-aperture unmatched prediction also produces zero drawable markers and must NOT claim all matched.
        var outsideAperture = faintUnmatched with
        {
            UnmatchedPredictions = [faintUnmatched.UnmatchedPredictions[0] with { Magnitude = 2.0, ReasonCode = MeasuredStellarAssociationReasonCodes.OutsideAperture }]
        };
        Assert.IsEmpty(MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(outsideAperture, scene).Markers);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedFilteredReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(outsideAperture));

        // Empty scene with zero predictions reports no-catalog-predictions.
        var emptyScene = await StarFieldAsync().ConfigureAwait(false);
        var emptyAssoc = AllEligible(emptyScene);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedNoPredictionsReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(emptyAssoc));
    }
}
