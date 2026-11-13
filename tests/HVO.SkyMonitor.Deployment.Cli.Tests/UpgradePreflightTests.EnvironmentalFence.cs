using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class UpgradePreflightTests
{
    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    [DataRow(16)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void EnvironmentalFenceReaderRequiresCanonicalMigrationAndRejectsOlderReaders(int sourceVersion)
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();
        if (sourceVersion == 13) fixture.WriteCanonicalV13RawIngressDatabase();
        else
        {
            fixture.WriteCanonicalV14RawIngressDatabase();
            fixture.SetRawIngressVersion(sourceVersion);
        }
        var requirements = CurrentRequirements with
        {
            RawIngressSchema = 16,
            RawIngressMigration = RawIngressV13Schema.EnvironmentalFenceMigrationContract
        };
        var path = CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot);
        var before = File.ReadAllBytes(path);
        Assert.IsTrue(Evaluate(fixture, requirements).Compatible);
        if (sourceVersion != 16)
        {
            Assert.IsFalse(Evaluate(fixture, requirements with { RawIngressMigration = null }).Compatible);
            Assert.IsFalse(Evaluate(fixture, requirements with { RawIngressMigration = RawIngressV13Schema.RecoveryMigrationContract }).Compatible);
            Assert.IsFalse(Evaluate(fixture, requirements, CameraAgentStateContractPolicy.AllowLegacy).Compatible);
        }
        else
        {
            foreach (var older in new[] { 13, 14, 15 })
                Assert.IsFalse(Evaluate(fixture, CurrentRequirements with { RawIngressSchema = older },
                    CameraAgentStateContractPolicy.AllowLegacy).Compatible);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "Preflight cannot rewrite the reader boundary.");
        fixture.ChangeRawIngressSchema("CREATE TABLE environmental_fence_drift(id INTEGER);");
        Assert.IsFalse(Evaluate(fixture, requirements).Compatible);
    }
}
