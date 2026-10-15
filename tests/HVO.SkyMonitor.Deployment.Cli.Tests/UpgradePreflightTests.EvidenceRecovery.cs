using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class UpgradePreflightTests
{
    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void EvidenceRecoveryVersionRequiresDeclaredCanonicalUpgradeAndRefusesOlderRollback(int sourceVersion)
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();
        if (sourceVersion == 13) fixture.WriteCanonicalV13RawIngressDatabase();
        else fixture.WriteCanonicalV14RawIngressDatabase();
        var recovery = CurrentRequirements with
        {
            RawIngressSchema = 15,
            RawIngressMigration = RawIngressV13Schema.RecoveryMigrationContract
        };
        Assert.IsTrue(Evaluate(fixture, recovery).Compatible);
        Assert.IsFalse(Evaluate(fixture, recovery with { RawIngressMigration = null }).Compatible);
        Assert.IsFalse(Evaluate(fixture, recovery with { RawIngressMigration = RawIngressV13Schema.MigrationContract }).Compatible);
        Assert.IsFalse(Evaluate(fixture, recovery, CameraAgentStateContractPolicy.AllowLegacy).Compatible);
        fixture.ChangeRawIngressSchema("CREATE TABLE evidence_drift(id INTEGER);");
        Assert.IsFalse(Evaluate(fixture, recovery).Compatible);
        fixture.ChangeRawIngressSchema("DROP TABLE evidence_drift;");

        // v15 keeps v14 table definitions; its durable reader boundary must still reject v14/v13 images.
        using var current = new PreflightFixture();
        current.WriteCatalogManifest(manifestVersion: 2);
        current.CreateBindSources();
        current.WriteCanonicalV14RawIngressDatabase();
        current.SetRawIngressVersion(15);
        var path = CameraAgentStateLayout.RawIngressDatabasePath(current.Paths.StateRoot);
        var before = File.ReadAllBytes(path);
        Assert.IsTrue(Evaluate(current, recovery).Compatible);
        foreach (var older in new[] { 13, 14 })
        {
            var rejected = Evaluate(current, CurrentRequirements with { RawIngressSchema = older }, CameraAgentStateContractPolicy.AllowLegacy);
            Assert.IsFalse(rejected.Compatible);
            Assert.IsTrue(rejected.Findings.Any(finding => finding.Blocking && finding.Code == "raw-ingress-schema"));
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "Preflight must not rewrite evidence to permit rollback.");
    }
}
