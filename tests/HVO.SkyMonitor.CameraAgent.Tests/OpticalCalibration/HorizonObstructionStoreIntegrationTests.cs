using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.OpticalCalibration;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class HorizonObstructionStoreIntegrationTests
{
    private static HorizonObstructionProfile Profile(CameraRigConfig rig, long revision) => new(
        HorizonObstructionProfile.CurrentSchemaVersion, "site-horizon", revision, "virtual-installation",
        HorizonObstructionProfile.ComputeSiteIdentitySha256(35.347, -113.878, 1200,
            VirtualOpticalCalibrationReviewServiceTests.Configuration("VirtualSky").DeploymentLocation!.ToProvenance()),
        HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, 90,
        [new(0, 20), new(90, 25), new(180, 0), new(270, null)]);

    [TestMethod]
    public async Task PreviewSaveExplicitActivationAndRollbackPreserveArchivedRevisions()
    {
        using var harness = await VirtualOpticalCalibrationReviewServiceTests.Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var configuration = VirtualOpticalCalibrationReviewServiceTests.Configuration("VirtualSky");
        var named = harness.CreateRestartedNamedStore();
        var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var original = catalog.Revisions.Single(r => r.RevisionId == catalog.Selection.ActiveRevisionId);
        var candidate = await named.RetainHorizonObstructionAsync(original.RevisionId, catalog.Selection.Version,
            Profile(original.Rig, 2), CancellationToken.None).ConfigureAwait(false);
        var saved = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(original.RevisionId, saved.Selection.ActiveRevisionId);
        Assert.IsNull(saved.Selection.PendingRevisionId);
        Assert.IsNull(saved.Revisions.Single(r => r.RevisionId == original.RevisionId).Rig.HorizonObstruction);
        var preview = await named.PreviewAsync(candidate.RevisionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(preview.Valid, preview.Failure);
        Assert.IsNull((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
        await named.StageAsync(candidate.RevisionId, saved.Selection.Version, "activate-horizon", "owner", true,
            preview.ScheduleRevisionId, preview.ScheduleProfileSha256, CancellationToken.None).ConfigureAwait(false);
        var pending = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!;
        Assert.AreEqual(candidate.Rig.HorizonObstruction!.ComputeIdentitySha256(), pending.Profile.Rig.HorizonObstruction!.ComputeIdentitySha256());
        await named.CommitInitializedAsync(candidate.RevisionId, pending.Profile.ApplyTo(configuration), CancellationToken.None).ConfigureAwait(false);
        var archived = await harness.Schedule.GetRevisionAsync(pending.RevisionId, CancellationToken.None).ConfigureAwait(false);
        var activated = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(candidate.RevisionId, activated.Selection.ActiveRevisionId);
        var rollback = await named.PreviewAsync(original.RevisionId, CancellationToken.None).ConfigureAwait(false);
        await named.StageAsync(original.RevisionId, activated.Selection.Version, "rollback-horizon", "owner", true,
            rollback.ScheduleRevisionId, rollback.ScheduleProfileSha256, CancellationToken.None).ConfigureAwait(false);
        var restored = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!;
        await named.CommitInitializedAsync(original.RevisionId, restored.Profile.ApplyTo(configuration), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull((await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.Profile.Rig.HorizonObstruction);
        Assert.AreEqual(archived.ProfileSha256, (await harness.Schedule.GetRevisionAsync(archived.RevisionId, CancellationToken.None).ConfigureAwait(false)).ProfileSha256);
        Assert.AreEqual(candidate.Rig.HorizonObstruction.ComputeIdentitySha256(),
            (await harness.Schedule.GetRevisionAsync(archived.RevisionId, CancellationToken.None).ConfigureAwait(false)).Profile.Rig.HorizonObstruction!.ComputeIdentitySha256());
    }

    [TestMethod]
    public async Task DuplicateOrStalePreviewCannotReplaceRevision()
    {
        using var harness = await VirtualOpticalCalibrationReviewServiceTests.Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var named = harness.CreateRestartedNamedStore();
        var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var active = catalog.Revisions.Single(r => r.RevisionId == catalog.Selection.ActiveRevisionId);
        var profile = Profile(active.Rig, 2);
        await named.RetainHorizonObstructionAsync(active.RevisionId, catalog.Selection.Version, profile, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => named.RetainHorizonObstructionAsync(
            active.RevisionId, catalog.Selection.Version, profile, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => named.RetainHorizonObstructionAsync(
            active.RevisionId, catalog.Selection.Version - 1, Profile(active.Rig, 3), CancellationToken.None)).ConfigureAwait(false);
        Assert.HasCount(2, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Revisions);
    }

    [TestMethod]
    public async Task FailedRevisionPublicationLeavesActiveAndHistoryUnchanged()
    {
        using var harness = await VirtualOpticalCalibrationReviewServiceTests.Harness.CreateAsync("VirtualSky").ConfigureAwait(false);
        var named = harness.CreateRestartedNamedStore();
        var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var active = catalog.Revisions.Single(r => r.RevisionId == catalog.Selection.ActiveRevisionId);
        harness.Ingress.InjectRigFailure = true;
        await Assert.ThrowsExactlyAsync<SqliteException>(() => named.RetainHorizonObstructionAsync(active.RevisionId,
            catalog.Selection.Version, Profile(active.Rig, 2), CancellationToken.None)).ConfigureAwait(false);
        harness.Ingress.InjectRigFailure = false;
        var after = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(catalog.Selection, after.Selection);
        Assert.HasCount(1, after.Revisions);
    }
}
