#pragma warning disable CA5394 // Fixed seeds define reproducible synthetic centroids, never security material.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.OpticalCalibration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.OpticalCalibration;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "OpticalCalibration")]
public sealed class VirtualOpticalCalibrationReviewServiceTests
{
    private const int Size = 512;
    private static readonly DateTimeOffset Utc = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
    private static readonly ObserverLocation Observer = new(35.347, -113.878, 1200);
    private const double NominalFocal = 232 / (185 * Math.PI / 360);
    private static readonly OpticsProfile NominalOptics = new("EquidistantFisheye", 0, 185, 0, LensKind.Fisheye,
        PrincipalPointX: 256, PrincipalPointY: 256, ImageCircleRadiusPixels: 232, FocalLengthXPixels: NominalFocal,
        FocalLengthYPixels: NominalFocal, HorizontalFlip: true, CalibrationVersion: "virtual-nominal-v1");
    private static readonly FrameReadoutDescriptor FullFrame = new(Size, Size, 0, 0, Size, Size, 1, 1,
        FrameBinningAlgorithm.IdentityV1, null, null);
    private static readonly AstrometricCatalogData Catalog = CreateCatalog();
    private static readonly AstrometricSolverOptions Search = new(MinimumFocalScale: .97, MaximumFocalScale: 1.03);

    // Virtual-profile truth: +1.2% focal, a (3.5, -3) px principal-point offset and k1 = -0.006.
    internal static readonly Lazy<OpticalCalibrationResult> Accepted = new(() =>
    {
        var nominal = RigProjectionContextFactory.CreateNative(Configuration("VirtualSky").Rig);
        var truth = nominal with
        {
            FocalLengthXPixels = NominalFocal * 1.012,
            FocalLengthYPixels = NominalFocal * 1.012,
            PrincipalPointX = 259.5,
            PrincipalPointY = 253,
            RadialDistortionK1 = -.006,
            BoresightAltitudeDegrees = 72,
            BoresightAzimuthDegrees = 243,
            RollDegrees = 17
        };
        return OpticalCalibrationSession.Fit(nominal, [Frame(truth, 0), Frame(truth, 1.5), Frame(truth, 3)],
            [Frame(truth, 2.2)], Catalog, new(FitRadialDistortion: true), Search);
    });

    [TestMethod]
    public async Task AcceptedCalibration_IsReviewedRetainedAsDraftRevisionAndNeverActivated()
    {
        var result = Accepted.Value;
        Assert.IsTrue(result.IsAccepted, string.Join(",", result.Rejections));
        using var harness = await Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var before = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var service = new VirtualOpticalCalibrationReviewService(harness.Named);

        var review = await service.ReviewAsync(result, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(review.CanAccept, review.Blocker);
        Assert.AreEqual(before.Selection.ActiveRevisionId, review.RigRevisionId);
        Assert.AreEqual(NominalOptics, review.CurrentOptics);
        var proposed = review.ProposedOptics!;
        Assert.AreEqual(VirtualOpticalCalibrationReviewService.CalibrationVersion(result), proposed.CalibrationVersion);
        Assert.AreEqual(-.006, proposed.RadialDistortionK1, .0005);
        Assert.AreEqual(259.5, proposed.PrincipalPointX!.Value, .1);
        Assert.AreEqual(NominalOptics.ImageCircleRadiusPixels, proposed.ImageCircleRadiusPixels);
        Assert.AreEqual(NominalOptics.HorizontalFlip, proposed.HorizontalFlip);
        Assert.AreEqual(result.Diagnostics, review.Diagnostics);

        var rejected = VirtualOpticalCalibrationReviewService.Reject(review);
        Assert.AreEqual("rejected", rejected.Decision);
        Assert.IsNull(rejected.CandidateRigRevisionId);
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);

        var accepted = await service.AcceptAsync(review, result, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("accepted", accepted.Decision);
        var after = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.Selection, after.Selection, "Accepting never stages or activates the calibration.");
        Assert.HasCount(before.Revisions.Count + 1, after.Revisions);
        var original = after.Revisions.Single(revision => revision.RevisionId == review.RigRevisionId);
        Assert.AreEqual(NominalOptics, original.Rig.Optics, "The previously accepted optics revision is retained unchanged.");
        var candidate = after.Revisions.Single(revision => revision.RevisionId == accepted.CandidateRigRevisionId);
        Assert.AreEqual(original.ProfileId, candidate.ProfileId);
        Assert.AreEqual(original.CameraRevisionId, candidate.CameraRevisionId);
        Assert.AreEqual(original.MountRevisionId, candidate.MountRevisionId);
        Assert.AreEqual(accepted.CandidateOpticsRevisionId, candidate.OpticsRevisionId);
        Assert.AreNotEqual(original.OpticsRevisionId, candidate.OpticsRevisionId);
        Assert.AreEqual(proposed, candidate.Rig.Optics);
        Assert.AreEqual(result.CalibratedNative, RigProjectionContextFactory.CreateNative(candidate.Rig));
        var optics = await harness.Named.GetEquipmentAsync(candidate.OpticsRevisionId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("optics", optics.Kind);
        Assert.AreEqual(1, optics.RevisionNumber);

        var replay = await service.AcceptAsync(review, result, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(accepted, replay);
        await AssertUnchangedAsync(harness, after).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnacceptableCalibrationsAndStaleReviews_WriteNothing()
    {
        using var harness = await Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var before = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var service = new VirtualOpticalCalibrationReviewService(harness.Named);
        var nominal = RigProjectionContextFactory.CreateNative(Configuration("VirtualSky").Rig);
        var insufficient = OpticalCalibrationSession.Fit(nominal, [Frame(nominal, 0), Frame(nominal, 1.5)], [Frame(nominal, 3)],
            Catalog, new(FitRadialDistortion: true), Search);
        Assert.AreEqual("insufficient-frames", insufficient.ReasonCode);

        var review = await service.ReviewAsync(insufficient, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(review.CanAccept);
        Assert.AreEqual("calibration-not-accepted", review.Blocker);
        Assert.IsNull(review.ProposedOptics);
        Assert.AreEqual("insufficient-frames", review.ReasonCode);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.AcceptAsync(review, insufficient, CancellationToken.None))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.AcceptAsync(review, Accepted.Value, CancellationToken.None))
            .ConfigureAwait(false);
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);

        var acceptedReview = await service.ReviewAsync(Accepted.Value, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => service.AcceptAsync(
            acceptedReview with { RigRevisionId = "stale" }, Accepted.Value, CancellationToken.None)).ConfigureAwait(false);
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Accept_IsBoundToTheReviewedSelectionAndSerializesConcurrentAccepts()
    {
        var result = Accepted.Value;
        using var harness = await Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var service = new VirtualOpticalCalibrationReviewService(harness.Named);
        var review = await service.ReviewAsync(result, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(review.CanAccept, review.Blocker);
        var opticsBefore = await harness.ScalarAsync(CountOptics).ConfigureAwait(false);
        var initial = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var active = initial.Revisions.Single(revision => revision.RevisionId == review.RigRevisionId);

        // A rig change staged between review and accept is a conflict, and so is retaining against the new version.
        var draft = await harness.Named.ComposeAsync(active.ProfileId, active.CameraRevisionId, active.OpticsRevisionId,
            active.MountRevisionId, CancellationToken.None).ConfigureAwait(false);
        var preview = await harness.Named.PreviewAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
        _ = await harness.Named.StageAsync(draft.RevisionId, review.SelectionVersion, "calibration-race-stage", "owner", true,
            preview.ScheduleRevisionId, preview.ScheduleProfileSha256, CancellationToken.None).ConfigureAwait(false);
        var pending = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(draft.RevisionId, pending.Selection.PendingRevisionId);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => service.AcceptAsync(review, result,
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => harness.Named.RetainCalibratedOpticsAsync(
            review.RigRevisionId, pending.Selection.Version, "Calibrated optics", review.ProposedOptics!,
            CancellationToken.None)).ConfigureAwait(false);
        await AssertUnchangedAsync(harness, pending).ConfigureAwait(false);

        // Cancelling restores the reviewed active rig with nothing pending, but the review's selection version is spent.
        _ = await harness.Named.CancelPendingAsync(draft.RevisionId, pending.Selection.Version, "calibration-race-cancel",
            "owner", CancellationToken.None).ConfigureAwait(false);
        var cancelled = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(review.RigRevisionId, cancelled.Selection.ActiveRevisionId);
        Assert.IsNull(cancelled.Selection.PendingRevisionId);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => service.AcceptAsync(review, result,
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => harness.Named.RetainCalibratedOpticsAsync(
            review.RigRevisionId, review.SelectionVersion, "Calibrated optics", review.ProposedOptics!,
            CancellationToken.None)).ConfigureAwait(false);
        await AssertUnchangedAsync(harness, cancelled).ConfigureAwait(false);
        Assert.AreEqual(opticsBefore, await harness.ScalarAsync(CountOptics).ConfigureAwait(false));

        // Concurrent accepts of a fresh review retain exactly one optics revision and one draft rig revision.
        var fresh = await service.ReviewAsync(result, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(cancelled.Selection.Version, fresh.SelectionVersion);
        var decisions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            service.AcceptAsync(fresh, result, CancellationToken.None)))).ConfigureAwait(false);
        Assert.IsTrue(decisions.All(decision => decision == decisions[0]));
        var after = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(cancelled.Selection, after.Selection);
        Assert.HasCount(cancelled.Revisions.Count + 1, after.Revisions);
        Assert.AreEqual(opticsBefore + 1, await harness.ScalarAsync(CountOptics).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Accept_FailureOrCancellationBetweenWrites_WritesNothing()
    {
        var result = Accepted.Value;
        using var harness = await Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var service = new VirtualOpticalCalibrationReviewService(harness.Named);
        var review = await service.ReviewAsync(result, CancellationToken.None).ConfigureAwait(false);
        var before = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var opticsBefore = await harness.ScalarAsync(CountOptics).ConfigureAwait(false);

        // The draft rig insert fails after the optics revision was written inside the same transaction.
        harness.Ingress.InjectRigFailure = true;
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => service.AcceptAsync(review, result,
            CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "injected rig failure", StringComparison.Ordinal);
        harness.Ingress.InjectRigFailure = false;
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);
        Assert.AreEqual(opticsBefore, await harness.ScalarAsync(CountOptics).ConfigureAwait(false));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AcceptAsync(review, result, cancellation.Token))
            .ConfigureAwait(false);
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);
        Assert.AreEqual(opticsBefore, await harness.ScalarAsync(CountOptics).ConfigureAwait(false));

        var accepted = await service.AcceptAsync(review, result, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("accepted", accepted.Decision);
        Assert.AreEqual(opticsBefore + 1, await harness.ScalarAsync(CountOptics).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PhysicalRigsAndForeignNominalOpticsAreNeverRevised()
    {
        using (var physical = await Harness.CreateAsync("ZwoAsi").ConfigureAwait(false))
        {
            var before = await physical.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var service = new VirtualOpticalCalibrationReviewService(physical.Named);
            var review = await service.ReviewAsync(Accepted.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("physical-rig", review.Blocker);
            Assert.IsNotNull(review.ProposedOptics, "Physical observations can be compared in read-only review.");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.AcceptAsync(review, Accepted.Value, CancellationToken.None))
                .ConfigureAwait(false);
            await AssertUnchangedAsync(physical, before).ConfigureAwait(false);
        }

        using var foreign = await Harness.CreateAsync("VirtualSky", NominalOptics with { HorizontalFlip = false }).ConfigureAwait(false);
        var foreignReview = await new VirtualOpticalCalibrationReviewService(foreign.Named)
            .ReviewAsync(Accepted.Value, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("nominal-mismatch", foreignReview.Blocker);
        Assert.IsNull(foreignReview.ProposedOptics);
    }

    [TestMethod]
    public async Task MockedPhysicalQualificationBindsTheExactFitAndStillRequiresExplicitStaging()
    {
        using var harness = await Harness.CreateAsync("ZwoAsi").ConfigureAwait(false);
        var before = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var revision = before.Selection.ActiveRevisionId!;
        var fit = Accepted.Value;
        var qualification = new Mock<IOpticalAlignmentQualification>(MockBehavior.Strict);
        var authorized = qualification.Setup(policy => policy.IsQualifiedAsync(revision, fit.IdentitySha256, It.IsAny<CancellationToken>()));
        authorized.ReturnsAsync(true);
        var reviewService = new VirtualOpticalCalibrationReviewService(harness.Named, qualification.Object);
        var review = await reviewService.ReviewAsync(fit, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(review.CanAccept, review.Blocker);
        authorized.ReturnsAsync(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reviewService.AcceptAsync(review, fit, CancellationToken.None)).ConfigureAwait(false);
        await AssertUnchangedAsync(harness, before).ConfigureAwait(false);
        authorized.ReturnsAsync(true);
        var accepted = await reviewService.AcceptAsync(review, fit, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(accepted.CandidateRigRevisionId);
        var saved = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.Selection, saved.Selection);
        var preview = await harness.Named.PreviewAsync(accepted.CandidateRigRevisionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(preview.Valid, preview.Failure);
        qualification.Verify(policy => policy.IsQualifiedAsync(revision, fit.IdentitySha256, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task OpticalAlignmentFitsSelectedArchiveCapturesAndRechecksPublicationBeforeAccept()
    {
        using var harness = await Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var active = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var rig = active.Revisions.Single(r => r.RevisionId == active.Selection.ActiveRevisionId).Rig;
        var rigIdentity = CameraRigProfileIdentity.ComputeSha256(rig);
        var truth = RigProjectionContextFactory.CreateNative(rig) with
        {
            FocalLengthXPixels = NominalFocal * 1.012,
            FocalLengthYPixels = NominalFocal * 1.012,
            PrincipalPointX = 259.5,
            PrincipalPointY = 253,
            RadialDistortionK1 = -.006,
            BoresightAltitudeDegrees = 72,
            BoresightAzimuthDegrees = 243,
            RollDegrees = 17
        };
        var evidence = new[] { Frame(truth, 0), Frame(truth, 1.5), Frame(truth, 3), Frame(truth, 2.2) }
            .Select(frame => new OpticalAlignmentCaptureEvidence(new(frame.Frame.CaptureId, frame.Frame.SourceArtifactId,
                frame.Frame.SourcePayloadSha256), rigIdentity, frame, Catalog, AstrometricClockFacts.NotSupplied,
                Search, AstrometricAssessmentStatus.Accepted, .1)).ToArray();
        var archive = new Mock<ICameraAgentRetainedAstrometryService>(MockBehavior.Strict);
        foreach (var capture in evidence)
        {
            archive.Setup(service => service.GetOpticalAlignmentInputAsync(capture.Assessment.CaptureId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(capture);
            archive.Setup(service => service.ReadPublicationStateAsync(capture.Assessment, It.IsAny<CancellationToken>()))
                .ReturnsAsync(CameraAgentAstrometryPublicationState.Available);
        }
        using var workflow = new OpticalAlignmentWorkflow(harness.Named, archive.Object, new VirtualOpticalCalibrationReviewService(harness.Named));
        var training = evidence[..3].Select(capture => capture.Assessment.CaptureId).ToArray();
        var validation = evidence[3].Assessment.CaptureId;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => workflow.FitAsync(training, [training[0]], true, CancellationToken.None)).ConfigureAwait(false);
        var proposal = await workflow.FitAsync(training, [validation], true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(proposal.Review.CanAccept, proposal.Result.Reason);
        Assert.HasCount(4, proposal.Captures);
        Assert.HasCount(1, proposal.Result.Validations);
        Assert.AreEqual(validation, proposal.Result.Validations.Single().CaptureId);
        await AssertUnchangedAsync(harness, active).ConfigureAwait(false);

        archive.Setup(service => service.ReadPublicationStateAsync(evidence[3].Assessment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CameraAgentAstrometryPublicationState.Invalidated);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => workflow.AcceptAsync(proposal, CancellationToken.None)).ConfigureAwait(false);
        await AssertUnchangedAsync(harness, active).ConfigureAwait(false);
        archive.Setup(service => service.ReadPublicationStateAsync(evidence[3].Assessment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CameraAgentAstrometryPublicationState.Available);
        var decision = await workflow.AcceptAsync(proposal, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(decision.CandidateRigRevisionId);
        var after = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active.Selection, after.Selection);
        Assert.HasCount(active.Revisions.Count + 1, after.Revisions);
        var saved = after.Revisions.Single(revision => revision.RevisionId == decision.CandidateRigRevisionId).OpticalAlignment!;
        Assert.AreEqual(proposal.Result.IdentitySha256, saved.FitIdentitySha256);
        Assert.AreEqual(proposal.Result.InputIdentitySha256, saved.InputIdentitySha256);
        CollectionAssert.AreEqual(evidence.Select(capture => capture.Assessment).ToArray(),
            saved.Captures.Select(capture => capture.Assessment).ToArray());
        Assert.AreEqual(decision, await workflow.AcceptAsync(proposal, CancellationToken.None).ConfigureAwait(false));

        var candidateId = decision.CandidateRigRevisionId!;
        var previousId = after.Selection.ActiveRevisionId!;
        var preview = await harness.Named.PreviewAsync(candidateId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(preview.Valid, preview.Failure);
        _ = await harness.Named.StageAsync(candidateId, after.Selection.Version, "stage-alignment", "owner", true,
            preview.ScheduleRevisionId, preview.ScheduleProfileSha256, CancellationToken.None).ConfigureAwait(false);
        var pending = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(previousId, pending.Selection.ActiveRevisionId);
        Assert.AreEqual(candidateId, pending.Selection.PendingRevisionId);
        var configuration = Configuration("VirtualSky");
        var staged = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!;
        var restartedStore = harness.CreateRestartedNamedStore();
        await restartedStore.CommitInitializedAsync(candidateId, staged.Profile.ApplyTo(configuration), CancellationToken.None).ConfigureAwait(false);
        var activated = await restartedStore.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(candidateId, activated.Selection.ActiveRevisionId);
        var rollback = await restartedStore.PreviewAsync(previousId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(rollback.Valid, rollback.Failure);
        _ = await restartedStore.StageAsync(previousId, activated.Selection.Version, "rollback-alignment", "owner", true,
            rollback.ScheduleRevisionId, rollback.ScheduleProfileSha256, CancellationToken.None).ConfigureAwait(false);
        staged = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!;
        await restartedStore.CommitInitializedAsync(previousId, staged.Profile.ApplyTo(configuration), CancellationToken.None).ConfigureAwait(false);
        var restored = await restartedStore.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(previousId, restored.Selection.ActiveRevisionId);
        Assert.AreEqual(saved.FitIdentitySha256, restored.Revisions.Single(revision => revision.RevisionId == candidateId).OpticalAlignment!.FitIdentitySha256);
    }

    [TestMethod]
    public void CreateCalibratedOptics_RejectsCalibrationsOfAnotherFamilyApertureOrPose()
    {
        var rig = Configuration("VirtualSky").Rig;
        var nominal = RigProjectionContextFactory.CreateNative(rig);
        var calibrated = nominal with { FocalLengthXPixels = NominalFocal * 1.01, FocalLengthYPixels = NominalFocal * 1.01, RadialDistortionK1 = -.004 };
        var optics = RigProjectionContextFactory.CreateCalibratedOptics(rig, calibrated, "candidate-v1");
        Assert.AreEqual(calibrated, RigProjectionContextFactory.CreateNative(rig with { Optics = optics }));
        Assert.AreEqual("candidate-v1", optics.CalibrationVersion);
        foreach (var foreign in new[]
        {
            calibrated with { Model = ProjectionModel.EquisolidFisheye },
            calibrated with { ImageCircleRadiusPixels = 230 },
            calibrated with { HorizontalFlip = false },
            calibrated with { BoresightAltitudeDegrees = 45 },
            calibrated with { RollDegrees = 3 }
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => RigProjectionContextFactory.CreateCalibratedOptics(rig, foreign, "candidate-v1"));
        }
        Assert.ThrowsExactly<ArgumentException>(() => RigProjectionContextFactory.CreateCalibratedOptics(rig, calibrated, " "));
        Assert.ThrowsExactly<ArgumentException>(() => RigProjectionContextFactory.CreateCalibratedOptics(rig, calibrated, new string('v', 129)));
    }

    private static async Task AssertUnchangedAsync(Harness harness, NamedRigCatalog expected)
    {
        var actual = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expected.Selection, actual.Selection);
        CollectionAssert.AreEqual(expected.Revisions.Select(revision => revision.RevisionId).ToArray(),
            actual.Revisions.Select(revision => revision.RevisionId).ToArray());
    }

    internal static CameraModuleConfig Configuration(string moduleType, OpticsProfile? optics = null)
        => new(
            new ObservatoryLocation(Observer.LatitudeDegrees, Observer.LongitudeDegrees, Observer.ElevationMeters, "UTC"),
            new CameraModuleDescriptor(moduleType),
            new CameraRigConfig(
                new SensorProfile("virtual-512", Size, Size, 2.9, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                optics ?? NominalOptics,
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10)
                {
                    Envelope = new ExposureEnvelope(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 1), new ExposureDefaults(TimeSpan.FromSeconds(5), 10), 0.5)
                },
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty)
        {
            AgentId = "optical-calibration-agent",
            Schedule = new("capture-schedule-v1",
                [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 1, TimeSpan.FromSeconds(10))],
                [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(0, 0)),
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")]),
            DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, Observer.LatitudeDegrees, Observer.LongitudeDegrees, Observer.ElevationMeters, "UTC")
        };

    private static AstrometricCatalogData CreateCatalog()
    {
        var random = new Random(934817);
        var stars = Enumerable.Range(0, 560).Select(i => new CelestialCatalogObject($"SYN{i:0000}", $"Artificial {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1.8 + 3.4 * random.NextDouble())).ToArray();
        return new(new("Generated uniform test catalog", "1", new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))), "test-generated", "1"), stars, true, 7);
    }

    private const string CountOptics = "SELECT COUNT(*) FROM named_equipment_definitions WHERE kind = 'optics';";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static OpticalCalibrationFrame Frame(ProjectionContext truth, double hours)
    {
        var utc = Utc.AddHours(hours);
        var stamp = utc.ToString("O", CultureInfo.InvariantCulture);
        var frame = new AstrometricFrameContext(new Guid(Convert.FromHexString(Hash("capture" + stamp))[..16]),
            new Guid(Convert.FromHexString(Hash("artifact" + stamp))[..16]), Hash("descriptor" + stamp), Hash("pixels" + stamp),
            Hash("observer"), Observer, utc.AddSeconds(-10), utc.AddSeconds(10), "stellar-detector-v1", Hash("test-detector-settings"));
        var projector = ProjectorFactory.Create(truth);
        var random = new Random(812 + (int)Math.Round(hours * 100));
        var all = Catalog.Stars
            .Select(star => (Star: star, Horizontal: CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(
                new(star.RightAscensionHours, star.DeclinationDegrees), utc), utc, Observer.LatitudeDegrees, Observer.LongitudeDegrees)))
            .Where(star => star.Horizontal.AltitudeDegrees > 5)
            .Select(star => (star.Star, Pixel: projector.Project(star.Horizontal)))
            .Where(star => star.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < Size - 6 && p.Y < Size - 6).ToArray();
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var detections = all.Where(star => all.All(other => other.Star.Id == star.Star.Id || Distance(other.Pixel!.Value, star.Pixel!.Value) > 13))
            .Select((star, index) => new AstrometricDetection(index, new(star.Pixel!.Value.X + (random.NextDouble() - .5) * .04,
                star.Pixel.Value.Y + (random.NextDouble() - .5) * .04), 100000 * Math.Pow(10, -.4 * star.Star.Magnitude))).ToArray();
        return new(frame, FullFrame, Hash("readout:full"), detections);
    }

    internal sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly List<IDisposable> _owned = [];

        private Harness(string root) => _root = root;

        public SqliteCaptureScheduleStore Schedule { get; private set; } = default!;

        public SqliteNamedRigProfileStore CreateRestartedNamedStore() => new(Ingress,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }), TimeProvider.System, Schedule);

        public SqliteNamedRigProfileStore Named { get; private set; } = default!;

        public JournalInitializer Ingress { get; private set; } = default!;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Only fixed test SQL statements are passed.")]
        public async Task<long> ScalarAsync(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "journal", "raw-ingress.db"),
                Pooling = false
            }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture);
        }

        public static async Task<Harness> CreateAsync(string moduleType, OpticsProfile? optics = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-optical-calibration-review", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var harness = new Harness(root);
            try
            {
                var configuration = Configuration(moduleType, optics);
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var ingress = new JournalInitializer(root);
                harness.Ingress = ingress;
                var schedule = harness.Own(new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System));
                harness.Schedule = schedule;
                var active = await schedule.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var telemetry = harness.Own(new CaptureControlTelemetry());
                var admission = harness.Own(new CaptureAdmissionCoordinator(ingress, options, TimeProvider.System, telemetry));
                var runtime = harness.Own(new CaptureScheduleRuntimeCoordinator(schedule, admission,
                    new RawIngressState(TimeProvider.System), new CaptureLaneState(TimeProvider.System, options),
                    new EmptyPipelineFactory(), TimeProvider.System));
                _ = await runtime.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                harness.Named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule, runtime);
                _ = await harness.Named.ImportActiveAsync(active.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
                return harness;
            }
            catch
            {
                harness.Dispose();
                throw;
            }
        }

        private T Own<T>(T value) where T : IDisposable
        {
            _owned.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    internal sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        private readonly string _database = Path.Combine(root, "journal", "raw-ingress.db");

        /// <summary>Fails every named rig revision insert; installed only after the canonical schema is validated.</summary>
        public bool InjectRigFailure { get; set; }

        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            if (File.Exists(_database))
                await ExecuteAsync("DROP TRIGGER IF EXISTS inject_rig_failure;", cancellationToken).ConfigureAwait(false);
            await new SqliteRawCaptureJournal(_database, busyTimeoutSeconds: 5)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (InjectRigFailure)
                await ExecuteAsync("""
                    CREATE TRIGGER inject_rig_failure BEFORE INSERT ON named_rig_revisions
                    BEGIN SELECT RAISE(ABORT, 'injected rig failure'); END;
                    """, cancellationToken).ConfigureAwait(false);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Only fixed test SQL statements are passed.")]
        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _database,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
