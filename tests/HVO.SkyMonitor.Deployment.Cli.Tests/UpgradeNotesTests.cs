using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Which upgrade notes an upgrade shows, oldest first, and its refusal of an older target, as install contract section 6
/// and RoofController's installer define them.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class UpgradeNotesTests
{
    private static readonly Guid InstanceId = Guid.Parse("4f7a2c51-8a1e-4c3b-9d2e-0b6f5a1c7e90");

    [TestMethod]
    public void Plan_ShowsEveryNoteAfterTheInstalledVersionUpToTheTargetOldestFirst()
    {
        var target = ReleaseManifestTests.Release(
            "1.2.0", ("1.2.0", "twelve"), ("1.0.0", "ten"), ("1.3.0", "thirteen"), ("1.1.0", "eleven"));

        var plan = UpgradeNotes.Plan("1.0.0", target, InstanceId);

        Assert.AreEqual("1.0.0", plan.InstalledVersion);
        Assert.AreEqual("1.2.0", plan.TargetVersion);
        Assert.AreEqual("1.1.0 1.2.0", Versions(plan.Notes));
    }

    [TestMethod]
    public void Plan_CompleteReleaseFixture_ShowsTheNotesAfterTheInstalledRelease()
    {
        var plan = UpgradeNotes.Plan("1.0.0", ReleaseManifest.Parse(ReleaseManifestTests.Fixture()), InstanceId);

        Assert.AreEqual("1.1.0 1.2.0", Versions(plan.Notes));
    }

    [TestMethod]
    [DataRow("1.2.0-rc.1")]
    [DataRow("1.2.0-dev.3")]
    public void Plan_PreReleaseTarget_ShowsTheNotesOfTheReleaseItBelongsTo(string target)
    {
        var plan = UpgradeNotes.Plan(
            "1.1.0", ReleaseManifestTests.Release(target, ("1.1.0", "eleven"), ("1.2.0", "twelve")), InstanceId);

        Assert.AreEqual("1.2.0", Versions(plan.Notes));
    }

    [TestMethod]
    public void Plan_FromAReleaseCandidateToItsRelease_ShowsThatReleasesNotes()
    {
        var plan = UpgradeNotes.Plan("1.2.0-rc.1", ReleaseManifestTests.Release("1.2.0", ("1.2.0", "twelve")), InstanceId);

        Assert.AreEqual(1, plan.Notes.Count);
    }

    [TestMethod]
    public void Plan_UnrecordedInstalledVersion_ShowsEveryNoteUpToTheTarget()
    {
        var plan = UpgradeNotes.Plan(
            null, ReleaseManifestTests.Release("1.1.0", ("1.0.0", "ten"), ("1.1.0", "eleven"), ("1.2.0", "twelve")),
            InstanceId);

        Assert.IsNull(plan.InstalledVersion);
        Assert.AreEqual("1.0.0 1.1.0", Versions(plan.Notes));
    }

    [TestMethod]
    [DataRow("1.2.0", "1.2.0")]
    [DataRow("1.2.0+0123456", "1.2.0+abcdef0")]
    public void Plan_TargetThatIsTheInstalledRelease_IsNotRefusedAndShowsNothing(string installed, string target)
    {
        var plan = UpgradeNotes.Plan(installed, ReleaseManifestTests.Release(target, ("1.2.0", "twelve")), InstanceId);

        Assert.AreEqual(0, plan.Notes.Count);
    }

    [TestMethod]
    [DataRow("1.2.0", "1.1.0")]
    [DataRow("1.2.0", "1.2.0-rc.1")]
    [DataRow("1.2.0-dev.10", "1.2.0-dev.9")]
    public void Plan_TargetOlderThanTheInstalledVersion_IsRefusedAndPointsToRollback(string installed, string target)
    {
        var exception = Assert.ThrowsExactly<InstallerException>(
            () => UpgradeNotes.Plan(installed, ReleaseManifestTests.Release(target), InstanceId));

        Assert.AreEqual(
            $"{installed} is installed, which is newer than the upgrade target {target}; an upgrade never goes back. " +
            $"To return to the previous image, run 'hvo-skymonitor cameraagent rollback --instance-id {InstanceId:D}'.",
            exception.Message);
    }

    [TestMethod]
    public void Render_NamesBothVersionsAndIndentsEachNotesLines()
    {
        var plan = new LifecycleUpgradeNotes(
            "1.0.0", "1.2.0", [new UpgradeNote("1.1.0", "Nothing to do by hand.\r\nUpgrade."), new UpgradeNote("1.2.0", "One.\n\nTwo.")]);

        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                "Upgrading from 1.0.0 to 1.2.0.",
                string.Empty,
                "Upgrade notes for 1.1.0:",
                "  Nothing to do by hand.",
                "  Upgrade.",
                string.Empty,
                "Upgrade notes for 1.2.0:",
                "  One.",
                string.Empty,
                "  Two.",
                string.Empty),
            UpgradeNotes.Render(plan));
    }

    [TestMethod]
    public void Render_CarriageReturnInANote_StartsANewLineRatherThanOverwritingTheLine()
    {
        var plan = new LifecycleUpgradeNotes("1.0.0", "1.1.0", [new UpgradeNote("1.1.0", "DO NOT UPGRADE\rSafe to proceed.")]);

        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                "Upgrading from 1.0.0 to 1.1.0.",
                string.Empty,
                "Upgrade notes for 1.1.0:",
                "  DO NOT UPGRADE",
                "  Safe to proceed.",
                string.Empty),
            UpgradeNotes.Render(plan));
    }

    [TestMethod]
    public void Render_UnrecordedInstalledVersionAndNoNotes_SaySo()
    {
        Assert.AreEqual(
            "Upgrading to 1.2.0. The installed version is not recorded, so the upgrade notes of every release up to 1.2.0 are shown." +
            Environment.NewLine + "No upgrade notes apply." + Environment.NewLine,
            UpgradeNotes.Render(new LifecycleUpgradeNotes(null, "1.2.0", [])));
    }

    [TestMethod]
    public void Event_IsTheOneLineTheRunbookDocuments()
    {
        var plan = new LifecycleUpgradeNotes(
            "1.0.0", "1.2.0",
            [new UpgradeNote("1.1.0", "Nothing to do by hand."), new UpgradeNote("1.2.0", "Read the Camera & rig page after upgrading.")]);

        // docs/runbooks/deployment-installer.md, "Upgrade notes and older targets".
        Assert.AreEqual(
            """{"schemaVersion":1,"event":"upgrade-notes","upgradeNotes":{"installedVersion":"1.0.0","targetVersion":"1.2.0","notes":[{"version":"1.1.0","text":"Nothing to do by hand."},{"version":"1.2.0","text":"Read the Camera \u0026 rig page after upgrading."}]}}""",
            UpgradeNotes.Event(plan));
        Assert.AreEqual(
            """{"schemaVersion":1,"event":"upgrade-notes","upgradeNotes":{"installedVersion":null,"targetVersion":"1.2.0","notes":[{"version":"1.2.0","text":"One.\n\nTwo."}]}}""",
            UpgradeNotes.Event(new LifecycleUpgradeNotes(null, "1.2.0", [new UpgradeNote("1.2.0", "One.\n\nTwo.")])));
    }

    internal static string Versions(IEnumerable<UpgradeNote> notes) => string.Join(' ', notes.Select(static note => note.Version));
}
