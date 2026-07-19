using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

internal static class TransientTestData
{
    internal static readonly DateTimeOffset Epoch = DateTimeOffset.Parse(
        "2026-01-15T06:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    internal static TransientEventV1 CreateEvent()
    {
        var first = CreateObservation(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            0,
            Epoch,
            Epoch.AddSeconds(1),
            10);
        var second = CreateObservation(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            1,
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2),
            20);
        var firstAssessmentId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var secondAssessmentId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var firstReviewId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var firstNotificationId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        return new TransientEventV1(
            TransientEventV1.CurrentSchemaVersion,
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000002"),
            2,
            Guid.Parse("70000000-0000-0000-0000-000000000003"),
            Epoch.AddSeconds(3),
            "agent-1",
            TransientEventState.NeedsReview,
            Epoch.AddSeconds(2),
            Epoch.AddSeconds(6),
            Epoch,
            Epoch.AddSeconds(2),
            [first, second],
            [
                new TransientAssessmentV1(
                    firstAssessmentId,
                    Epoch.AddSeconds(2),
                    TransientAssessmentAuthority.Provisional,
                    TransientClassification.Meteor,
                    TransientMeteorSeverity.Meteor,
                    750_000,
                    [new TransientReasonV1("transient.linear-track", TransientReasonKind.Supporting, [first.ObservationId])],
                    Producer("detector-v1"),
                    new string('a', 64),
                    [first.ObservationId],
                    null),
                new TransientAssessmentV1(
                    secondAssessmentId,
                    Epoch.AddSeconds(3),
                    TransientAssessmentAuthority.Authoritative,
                    TransientClassification.Meteor,
                    TransientMeteorSeverity.Fireball,
                    900_000,
                    [new TransientReasonV1(
                        "transient.saturated-brightness",
                        TransientReasonKind.Supporting,
                        [first.ObservationId, second.ObservationId])],
                    Producer("detector-v2"),
                    new string('b', 64),
                    [first.ObservationId, second.ObservationId],
                    firstAssessmentId)
            ],
            [
                new TransientReviewV1(
                    firstReviewId,
                    Epoch.AddSeconds(4),
                    "reviewer-1",
                    TransientReviewDisposition.NeedsReview,
                    secondAssessmentId,
                    null,
                    ["review.saturation"],
                    null),
                new TransientReviewV1(
                    Guid.Parse("50000000-0000-0000-0000-000000000002"),
                    Epoch.AddSeconds(5),
                    "reviewer-1",
                    TransientReviewDisposition.Overridden,
                    secondAssessmentId,
                    new TransientReviewOverrideV1(
                        TransientClassification.Meteor,
                        TransientMeteorSeverity.Fireball,
                        950_000),
                    ["review.confirmed-fireball"],
                    firstReviewId)
            ],
            [
                new TransientNotificationV1(
                    firstNotificationId,
                    Epoch.AddSeconds(4),
                    "operators",
                    TransientNotificationState.Pending,
                    secondAssessmentId,
                    null,
                    null),
                new TransientNotificationV1(
                    Guid.Parse("60000000-0000-0000-0000-000000000002"),
                    Epoch.AddSeconds(6),
                    "operators",
                    TransientNotificationState.Sent,
                    secondAssessmentId,
                    null,
                    firstNotificationId)
            ],
            [
                new TransientDerivativeV1(
                    Guid.Parse("80000000-0000-0000-0000-000000000001"),
                    Epoch.AddSeconds(3),
                    TransientDerivativeKind.Overlay,
                    Artifact(
                        Guid.Parse("90000000-0000-0000-0000-000000000001"),
                        FrameArtifactRole.AnnotatedPreview,
                        'd',
                        'c'),
                    new string('d', 64),
                    [first.Source.EvidenceId],
                    []),
                new TransientDerivativeV1(
                    Guid.Parse("80000000-0000-0000-0000-000000000002"),
                    Epoch.AddSeconds(3),
                    TransientDerivativeKind.Reconstruction,
                    Artifact(
                        Guid.Parse("90000000-0000-0000-0000-000000000002"),
                        FrameArtifactRole.Combined,
                        'f',
                        'e'),
                    new string('f', 64),
                    [first.Source.EvidenceId, second.Source.EvidenceId],
                    [
                        TransientDerivativeLimitation.IntraExposureTimingUnavailable,
                        TransientDerivativeLimitation.SaturatedPhotometryUnrecoverable
                    ])
            ]);
    }

    internal static TransientCandidateV1 CreateCandidate()
    {
        var center = CreateSource(
            Guid.Parse("20000000-0000-0000-0000-000000000011"),
            Guid.Parse("30000000-0000-0000-0000-000000000011"),
            Epoch,
            Epoch.AddSeconds(1));
        var context = CreateSource(
            Guid.Parse("20000000-0000-0000-0000-000000000012"),
            Guid.Parse("30000000-0000-0000-0000-000000000012"),
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2));
        return new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "agent-1",
            TransientCandidateState.Provisional,
            Epoch.AddSeconds(2),
            center.EvidenceId,
            [center, context],
            Provenance(),
            Extraction("extractor-v1", '1'),
            Geometry(center.EvidenceId, 4),
            Features(center.EvidenceId, 10),
            [new TransientReasonV1("transient.pending-later-context", TransientReasonKind.Limitation, [])]);
    }

    internal static (ProcessingArtifact Artifact, TransientSourceEvidenceReferenceV1 Source, TransientLinearLevelsV1 Levels)
        CreateDetectorSource(CameraPixelFormat format)
    {
        var pixels = format == CameraPixelFormat.Mono16
            ? new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }
            : new byte[] { 100, 0, 200, 0, 44, 1, 144, 1 };
        var artifactId = format == CameraPixelFormat.Mono16
            ? Guid.Parse("b0000000-0000-0000-0000-000000000001")
            : Guid.Parse("b0000000-0000-0000-0000-000000000002");
        var layout = new FrameLayoutDescriptor(
            2,
            2,
            4,
            format,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            0,
            4095,
            pixels.Length);
        var artifact = new ProcessingArtifact(
            artifactId,
            FrameArtifactRole.Raw,
            "linear-v1",
            new string('a', 64),
            "application/x-hvo-frame",
            layout,
            pixels,
            Epoch,
            TimeSpan.FromSeconds(1),
            new ProcessingCompatibilityIdentity(
                "rig-v1", "north-up-v1", "calibration-v1", "mask-v1", "sensor-v1", "night-v1", "processing-v1"),
            ObservationStartedUtc: Epoch,
            ObservationEndedUtc: Epoch.AddSeconds(1));
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            format == CameraPixelFormat.Mono16
                ? Guid.Parse("c0000000-0000-0000-0000-000000000001")
                : Guid.Parse("c0000000-0000-0000-0000-000000000002"),
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifactId,
                    artifact.Role,
                    artifact.Variant,
                    artifact.RecipeIdentitySha256,
                    Convert.ToHexString(SHA256.HashData(pixels)))),
            Epoch,
            Epoch.AddSeconds(1),
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("camera-module", "v1"));
        return (artifact, source, new TransientLinearLevelsV1(0, 4095, 4000));
    }

    internal static TransientDetectorInputDescriptorV1 CreateDetectorDescriptor(CameraPixelFormat format)
    {
        var source = CreateDetectorSource(format);
        var result = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels);
        if (!result.Validation.IsValid || result.Input is null)
        {
            throw new InvalidOperationException(result.Validation.ReasonCode);
        }
        return result.Input.Descriptor;
    }

    private static TransientObservationV1 CreateObservation(
        Guid observationId,
        Guid evidenceId,
        Guid artifactId,
        int ordinal,
        DateTimeOffset started,
        DateTimeOffset ended,
        ushort peak)
    {
        var source = CreateSource(evidenceId, artifactId, started, ended);
        return new TransientObservationV1(
            observationId,
            ordinal,
            source,
            [Artifact(
                ordinal == 0
                    ? Guid.Parse("31000000-0000-0000-0000-000000000001")
                    : Guid.Parse("31000000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Calibrated,
                'b')],
            Provenance(),
            Extraction(ordinal == 0 ? "extractor-v1" : "extractor-v2", ordinal == 0 ? '1' : '2'),
            Geometry(evidenceId, ordinal * 2),
            Features(evidenceId, peak));
    }

    private static TransientSourceEvidenceReferenceV1 CreateSource(
        Guid evidenceId,
        Guid artifactId,
        DateTimeOffset started,
        DateTimeOffset ended)
        => new(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                Artifact(artifactId, FrameArtifactRole.Raw, 'a')),
            started,
            ended,
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("camera-module", "v1"));

    private static TransientGeometryV1 Geometry(Guid evidenceId, double offset)
        => new(
            evidenceId,
            100,
            80,
            new TransientBoundingRegionV1(10 + offset, 20, 20, 5),
            [new TransientPointV1(10 + offset, 22), new TransientPointV1(30 + offset, 23)]);

    private static TransientFeaturesV1 Features(Guid evidenceId, ushort peak)
        => new(
            evidenceId,
            20,
            2,
            3,
            10_000,
            peak,
            peak > 15 ? 1 : 0,
            1,
            [new TransientProfileSampleV1(0, 2), new TransientProfileSampleV1(1_000_000, 3)],
            [new TransientProfileSampleV1(0, 5), new TransientProfileSampleV1(1_000_000, peak)]);

    private static TransientAssessmentProducerV1 Producer(string version)
        => new(
            TransientAssessmentProducerV1.CurrentSchemaVersion,
            TransientAssessmentProducerKind.DeterministicAlgorithm,
            "linear-transient-detector",
            version);

    private static TransientObservationProvenanceV1 Provenance()
        => new(new string('d', 64), "calibration-v1", "mask-v1", "processing-v1");

    private static TransientObservationExtractionV1 Extraction(string version, char recipeHash)
        => new(
            Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            new TransientExtractionProducerV1(
                TransientExtractionProducerV1.CurrentSchemaVersion,
                TransientExtractionProducerKind.DeterministicAlgorithm,
                "linear-component-extractor",
                version),
            new string(recipeHash, 64));

    private static TransientArtifactReferenceV1 Artifact(
        Guid id,
        FrameArtifactRole role,
        char recipeHash,
        char? checksumHash = null)
        => new(
            id,
            role,
            "linear-v1",
            new string(recipeHash, 64),
            new string(checksumHash ?? recipeHash, 64));
}
