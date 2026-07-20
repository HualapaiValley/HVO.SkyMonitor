using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientAssessmentTests
{
    private const string AssessmentReceiptSha256 = "3CB3D6CF847EC27FFCF1A31C16063DF5CD61574D8B69D1ECE5BC83BC7170BB12";
    private static readonly TransientDeterministicAssessmentOptionsV1 Options = new(
        MinimumMeteorLengthPixels: 5,
        MinimumMeteorElongation: 3,
        MaximumMeteorMeanWidthPixels: 4,
        CompactSensorMaximumLengthPixels: 3,
        SensorArtifactMaximumMeanWidthPixels: 1.8,
        StationaryMaximumDisplacementPixels: 0.5,
        EnvironmentalMinimumMeanWidthPixels: 5,
        FireballMinimumIntegratedSignalAdu: 1_000,
        FlareMinimumPeakToEndpointRatio: 3,
        PersistentTrackMinimumObservations: 3,
        AircraftMinimumBrightnessRatio: 3,
        AircraftMinimumIntegratedSignalAdu: 100,
        SmoothMotionMaximumTurnDegrees: 20,
        SmoothMotionMaximumStepRatio: 2);

    [TestMethod]
    public void OneObservationUsesGeometryNotFrameCountAndSaturationAloneIsNotFireball()
    {
        var meteor = Assess([Observation(0, 0, 10, 2, 500, [100, 150, 100])]);
        AssertClassification(TransientClassification.Meteor, TransientMeteorSeverity.Meteor, meteor);
        Assert.IsTrue(meteor.Descriptor!.Assessment.Reasons.Any(
            static reason => reason.Code == TransientAssessmentReasonCodes.SingleObservation));

        var compactSaturated = Assess([Observation(0, 0, 2, 1, 5_000, [100, 500, 100], saturated: 4)]);
        AssertClassification(TransientClassification.SensorArtifact, null, compactSaturated);
    }

    [TestMethod]
    public void FireballRequiresElongatedHighSignalWithMeasuredFlareFragmentOrClippingEvidence()
    {
        var outcome = Assess([Observation(0, 0, 12, 2, 2_000, [100, 500, 100], saturated: 2, fragments: 2)]);

        AssertClassification(TransientClassification.Meteor, TransientMeteorSeverity.Fireball, outcome);
        Assert.IsTrue(outcome.Descriptor!.Assessment.Reasons.Any(
            static reason => reason.Code == TransientAssessmentReasonCodes.FlareProfile));
        Assert.IsTrue(outcome.Descriptor.Assessment.Reasons.Any(
            static reason => reason.Code == TransientAssessmentReasonCodes.SaturatedBrightness &&
                reason.Kind == TransientReasonKind.Limitation));
    }

    [TestMethod]
    public void SmoothPersistenceAndMeasuredBrightnessVariationSeparateSatelliteAndAircraft()
    {
        var satellite = Assess(
        [
            Observation(0, 0, 10, 2, 500, [100, 150, 100]),
            Observation(1, 2, 10, 2, 550, [100, 150, 100]),
            Observation(2, 4, 10, 2, 500, [100, 150, 100])
        ]);
        AssertClassification(TransientClassification.Satellite, null, satellite);

        var aircraft = Assess(
        [
            Observation(0, 0, 10, 2, 100, [50, 0, 50]),
            Observation(1, 2, 10, 2, 500, [250, 0, 250]),
            Observation(2, 4, 10, 2, 100, [50, 0, 50])
        ]);
        AssertClassification(TransientClassification.Aircraft, null, aircraft);

        AssertClassification(
            TransientClassification.Meteor,
            TransientMeteorSeverity.Meteor,
            Assess([Observation(0, 0, 10, 2, 500, [250, 0, 250])]));

        var irregularMultiFrameMeteor = Assess(
        [
            Observation(0, 0, 10, 2, 500, [100, 150, 100]),
            Observation(1, 2, 10, 2, 500, [100, 150, 100]),
            Observation(2, 2, 10, 2, 500, [100, 150, 100])
        ]);
        AssertClassification(TransientClassification.Meteor, TransientMeteorSeverity.Meteor, irregularMultiFrameMeteor);
    }

    [TestMethod]
    public void CompactBroadAndAmbiguousEvidenceRemainConservative()
    {
        AssertClassification(
            TransientClassification.SensorArtifact,
            null,
            Assess([Observation(0, 0, 2, 1, 100, [20, 30, 20])]));
        AssertClassification(
            TransientClassification.SensorArtifact,
            null,
            Assess([Observation(0, 0, 30, 1.5, 100_000, [20_000, 60_000, 20_000])]));
        AssertClassification(
            TransientClassification.EnvironmentalArtifact,
            null,
            Assess([Observation(0, 0, 8, 6, 1_000, [300, 400, 300])]));
        AssertClassification(
            TransientClassification.Unknown,
            null,
            Assess([Observation(0, 0, 4, 2, 100, [20, 30, 20])]));
    }

    [TestMethod]
    public void ReprocessingPreservesHistoryAndOnlySupersedesExplicitSameProducerAssessment()
    {
        var observation = Observation(0, 0, 10, 2, 500, [100, 150, 100]);
        var initial = Assess([observation]);
        var prior = initial.Descriptor!.Assessment;
        var request = Request([observation]) with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000002"),
            CreatedUtc = prior.CreatedUtc.AddSeconds(1),
            PriorAssessments = [prior],
            SupersedesAssessmentId = prior.AssessmentId
        };

        var replacement = TransientAssessmentFactory.Create(request);

        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, replacement.Status);
        Assert.HasCount(2, replacement.AssessmentHistory);
        Assert.AreSame(prior, replacement.AssessmentHistory[0]);
        Assert.AreEqual(prior.AssessmentId, replacement.AssessmentHistory[1].SupersedesAssessmentId);

        var independentProducer = prior with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000003"),
            Producer = prior.Producer with { Name = "external-assessor" }
        };
        var invalid = TransientAssessmentFactory.Create(request with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000004"),
            PriorAssessments = [independentProducer],
            SupersedesAssessmentId = independentProducer.AssessmentId
        });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Invalid, invalid.Status);
        Assert.AreEqual(TransientAssessmentReasonCodes.InvalidHistory, invalid.ReasonCode);
        Assert.AreSame(independentProducer, invalid.AssessmentHistory[0]);

        var crossProducerRoot = prior with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000006"),
            Producer = prior.Producer with { Name = "external-assessor" }
        };
        var malformedReplacement = prior with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000007"),
            CreatedUtc = crossProducerRoot.CreatedUtc.AddMilliseconds(1),
            SupersedesAssessmentId = crossProducerRoot.AssessmentId
        };
        var malformedHistory = TransientAssessmentFactory.Create(request with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000008"),
            CreatedUtc = malformedReplacement.CreatedUtc.AddSeconds(1),
            PriorAssessments = [crossProducerRoot, malformedReplacement],
            SupersedesAssessmentId = null
        });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Invalid, malformedHistory.Status);
        Assert.AreEqual(TransientAssessmentReasonCodes.InvalidHistory, malformedHistory.ReasonCode);
    }

    [TestMethod]
    public void AssessmentReceiptIsStrictCanonicalAndOptionsChangeIdentity()
    {
        var request = Request([Observation(0, 0, 10, 2, 500, [100, 150, 100])]);
        var first = TransientAssessmentFactory.Create(request);
        var second = TransientAssessmentFactory.Create(request with
        {
            AssessmentId = Guid.Parse("91000000-0000-0000-0000-000000000005"),
            Options = request.Options with { MinimumMeteorLengthPixels = 6 }
        });
        Assert.AreNotEqual(first.Descriptor!.OptionsIdentitySha256, second.Descriptor!.OptionsIdentitySha256);
        Assert.AreNotEqual(first.Descriptor.ExecutionIdentitySha256, second.Descriptor.ExecutionIdentitySha256);

        var json = TransientAssessmentJson.Serialize(first.Descriptor);
        var receiptSha256 = Convert.ToHexString(SHA256.HashData(json));
        TestContext.WriteLine($"assessment-receipt-sha256={receiptSha256}");
        Assert.AreEqual(AssessmentReceiptSha256, receiptSha256);
        var parsed = TransientAssessmentJson.Parse(json);
        CollectionAssert.AreEqual(json, TransientAssessmentJson.Serialize(parsed));
        var text = Encoding.UTF8.GetString(json);
        foreach (var invalid in new[]
        {
            text.Insert(1, "\"unknown\":true,"),
            text.Insert(1, "\"eventId\":\"90000000-0000-0000-0000-000000000001\","),
            text.Replace("\"meteor\"", "1", StringComparison.Ordinal)
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                TransientAssessmentJson.Parse(Encoding.UTF8.GetBytes(invalid)));
        }

        var malformedObservation = request.Observations[0] with
        {
            Observation = request.Observations[0].Observation with
            {
                Features = request.Observations[0].Observation.Features with { BrightnessProfile = [] }
            }
        };
        var malformed = TransientAssessmentFactory.Create(request with { Observations = [malformedObservation] });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Invalid, malformed.Status);
        Assert.AreEqual(TransientAssessmentReasonCodes.InvalidRequest, malformed.ReasonCode);

        var wrongEvent = TransientAssessmentFactory.Create(request with
        {
            Observations = [request.Observations[0] with
            {
                EventId = Guid.Parse("90000000-0000-0000-0000-000000000099")
            }]
        });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Invalid, wrongEvent.Status);

        var oversizedProfile = request.Observations[0] with
        {
            Observation = request.Observations[0].Observation with
            {
                Features = request.Observations[0].Observation.Features with
                {
                    BrightnessProfile = Enumerable.Range(0, 65).Select(index =>
                        new TransientProfileSampleV1(index * 1_000_000 / 64, index)).ToArray()
                }
            }
        };
        var oversized = TransientAssessmentFactory.Create(request with { Observations = [oversizedProfile] });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Invalid, oversized.Status);

        var negativeZero = request.Observations[0] with
        {
            Observation = request.Observations[0].Observation with
            {
                Geometry = request.Observations[0].Observation.Geometry with
                {
                    Bounds = request.Observations[0].Observation.Geometry.Bounds with { X = -0d }
                }
            }
        };
        Assert.AreEqual(
            TransientAssessmentExecutionStatus.Invalid,
            TransientAssessmentFactory.Create(request with { Observations = [negativeZero] }).Status);

        var lowerCaseHash = request.Observations[0] with
        {
            Observation = request.Observations[0].Observation with
            {
                Provenance = request.Observations[0].Observation.Provenance with
                {
                    DetectorInputIdentitySha256 = new string('c', 64)
                }
            }
        };
        var canonicalCase = TransientAssessmentFactory.Create(request with { Observations = [lowerCaseHash] });
        Assert.AreEqual(
            first.Descriptor.OrderedObservationIdentitySha256s[0],
            canonicalCase.Descriptor!.OrderedObservationIdentitySha256s[0]);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            TransientAssessmentFactory.Create(request, cancellation.Token));
    }

    private static TransientAssessmentExecutionOutcome Assess(IReadOnlyList<TransientObservationV1> observations)
        => TransientAssessmentFactory.Create(Request(observations));

    private static TransientAssessmentExecutionRequest Request(IReadOnlyList<TransientObservationV1> observations)
    {
        var eventId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        return new(
            eventId,
            Guid.Parse("91000000-0000-0000-0000-000000000001"),
            TransientTestData.Epoch.AddMinutes(1),
            TransientAssessmentAuthority.Provisional,
            observations.Select(observation => new TransientAssessmentObservationV1(
                eventId,
                new string('E', 64),
                observation)).ToArray(),
            Options,
            []);
    }

    private static TransientObservationV1 Observation(
        int ordinal,
        double x,
        double length,
        double width,
        long integrated,
        IReadOnlyList<double> brightness,
        int saturated = 0,
        int fragments = 1)
    {
        var evidenceId = Guid.Parse($"92000000-0000-0000-0000-{ordinal + 1:D12}");
        var observationId = Guid.Parse($"93000000-0000-0000-0000-{ordinal + 1:D12}");
        var artifactId = Guid.Parse($"94000000-0000-0000-0000-{ordinal + 1:D12}");
        var started = TransientTestData.Epoch.AddSeconds(ordinal * 2);
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifactId,
                    FrameArtifactRole.Raw,
                    "linear-v1",
                    new string('A', 64),
                    new string('B', 64))),
            started,
            started.AddSeconds(1),
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("test", "v1"));
        var positions = brightness.Select((_, index) =>
            (int)((long)index * 1_000_000 / (brightness.Count - 1))).ToArray();
        return new TransientObservationV1(
            observationId,
            ordinal,
            source,
            [],
            new TransientObservationProvenanceV1(new string('C', 64), "cal-v1", "mask-v1", "profile-v1"),
            new TransientObservationExtractionV1(
                Guid.Parse($"95000000-0000-0000-0000-{ordinal + 1:D12}"),
                new TransientExtractionProducerV1(
                    TransientExtractionProducerV1.CurrentSchemaVersion,
                    TransientExtractionProducerKind.DeterministicAlgorithm,
                    TransientCandidateExtractionFactory.ProducerName,
                    TransientCandidateExtractionFactory.ProducerVersion),
                new string('D', 64)),
            new TransientGeometryV1(
                evidenceId,
                100,
                100,
                new TransientBoundingRegionV1(x, 10, Math.Max(length, width), width),
                [new TransientPointV1(x, 10.5), new TransientPointV1(x + length, 10.5)]),
            new TransientFeaturesV1(
                evidenceId,
                length,
                width,
                width,
                integrated,
                (ushort)Math.Min(ushort.MaxValue, integrated),
                saturated,
                fragments,
                positions.Select(position => new TransientProfileSampleV1(position, width)).ToArray(),
                positions.Zip(brightness, static (position, value) =>
                    new TransientProfileSampleV1(position, value)).ToArray()));
    }

    private static void AssertClassification(
        TransientClassification classification,
        TransientMeteorSeverity? severity,
        TransientAssessmentExecutionOutcome outcome)
    {
        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.AreEqual(classification, outcome.Descriptor!.Assessment.Classification);
        Assert.AreEqual(severity, outcome.Descriptor.Assessment.MeteorSeverity);
    }

    public TestContext TestContext { get; set; } = null!;
}
