using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalRefreshStatusTests
{
    private static OrbitalRefreshStatus Status => new(OrbitalRefreshStatus.CurrentVersion,
        new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), 1, false, new string('A', 64), new string('B', 64),
        null, null, null, null, null, null, null, null, "retained-offline", OrbitalRefreshRecoveryAction.None);

    [TestMethod]
    public void HaltedOrDisabledRefreshCanRetainTheReadableSnapshotIdentity()
    {
        var disabled = Status; disabled.Validate();
        var halted = disabled with { Enabled = true, ProviderHalt = new(disabled.ObservedUtc, 304, "non-200-response"), RecoveryAction = OrbitalRefreshRecoveryAction.ResumeProvider };
        halted.Validate();
        Assert.AreEqual(disabled.CurrentManifestSha256, halted.CurrentManifestSha256);
        Assert.AreEqual(disabled.CurrentPayloadSha256, halted.CurrentPayloadSha256);
    }

    [TestMethod]
    public void PartialIdentitiesNonUtcAndInvalidAutomationReferencesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => (Status with { CurrentPayloadSha256 = null }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Status with { ObservedUtc = Status.ObservedUtc.ToOffset(TimeSpan.FromHours(1)) }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Status with { Automation = new("refresh", new string('A', 64), " ") }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Status with { ProviderHalt = new(Status.ObservedUtc, 200, "success") }).Validate());
    }
}
