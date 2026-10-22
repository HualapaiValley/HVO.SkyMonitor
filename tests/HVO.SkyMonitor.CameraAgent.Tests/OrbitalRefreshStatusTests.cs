using System.Text.Json;
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

    [TestMethod]
    public void AProviderHaltWithNoRecordedStatusIsValidAndSurvivesAJsonRoundTripAsNull()
    {
        var halted = Status with
        {
            Enabled = true,
            ProviderHalt = new(Status.ObservedUtc, null, "orbital.provider-outcome-unknown: outcome not recorded"),
            RecoveryAction = OrbitalRefreshRecoveryAction.ResumeProvider
        };
        halted.Validate();

        var json = JsonSerializer.Serialize(halted, JsonSerializerOptions.Web);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.AreEqual(JsonValueKind.Null,
                document.RootElement.GetProperty("providerHalt").GetProperty("httpStatusCode").ValueKind,
                "A missing status is carried as null, never as an invented code.");
        }
        var roundTripped = JsonSerializer.Deserialize<OrbitalRefreshStatus>(json, JsonSerializerOptions.Web);

        Assert.IsNotNull(roundTripped?.ProviderHalt);
        Assert.IsNull(roundTripped.ProviderHalt.HttpStatusCode);
        Assert.AreEqual(halted.ProviderHalt, roundTripped.ProviderHalt);
        roundTripped.Validate();
    }

    [TestMethod]
    [DataRow(99)]
    [DataRow(200)]
    [DataRow(600)]
    public void AProviderHaltWithAnOutOfRangeOrSuccessStatusIsRejected(int status)
        => Assert.ThrowsExactly<ArgumentException>(() => (Status with
        {
            ProviderHalt = new(Status.ObservedUtc, status, "orbital.provider-http")
        }).Validate());

    [TestMethod]
    [DataRow(100)]
    [DataRow(304)]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(599)]
    public void AProviderHaltWithARecordedNon200StatusRemainsValidAndRoundTrips(int status)
    {
        var halted = Status with { ProviderHalt = new(Status.ObservedUtc, status, "orbital.provider-http") };
        halted.Validate();

        var roundTripped = JsonSerializer.Deserialize<OrbitalRefreshStatus>(
            JsonSerializer.Serialize(halted, JsonSerializerOptions.Web), JsonSerializerOptions.Web);

        Assert.AreEqual(status, roundTripped?.ProviderHalt?.HttpStatusCode);
    }
}
