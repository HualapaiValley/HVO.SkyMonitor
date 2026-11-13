using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class UpgradePreflightTests
{
    private const string DriftRemediation = "drift";
    private const string MigrationRemediation = "migration";
    private const string ResetRemediation = "reset";

    /// <summary>
    /// A blocking raw-ingress finding names the remediation that fits the journal (#1823): repair drift or restore a
    /// backup for a journal already at the image's schema; satisfy the migration preconditions for an older journal the
    /// image declares a migration for; and archive and reset only for a journal outside the supported range.
    /// </summary>
    [TestMethod]
    [DataRow(14, 14, true, DriftRemediation)]
    [DataRow(14, 13, true, MigrationRemediation)]
    [DataRow(15, 15, true, DriftRemediation)]
    [DataRow(15, 13, true, MigrationRemediation)]
    [DataRow(15, 14, true, MigrationRemediation)]
    [DataRow(16, 16, true, DriftRemediation)]
    [DataRow(16, 13, true, MigrationRemediation)]
    [DataRow(16, 14, true, MigrationRemediation)]
    [DataRow(16, 15, true, MigrationRemediation)]
    [DataRow(16, 15, false, MigrationRemediation)]
    [DataRow(16, 12, false, ResetRemediation)]
    [DataRow(14, 12, false, ResetRemediation)]
    [DataRow(13, 12, false, ResetRemediation)]
    [DataRow(13, 14, false, ResetRemediation)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void RawIngressRemediationFitsTheJournalAgainstTheImagesDeclaredMigration(
        int expected, int observed, bool declareMigration, string remediation)
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();
        if (observed == 13) fixture.WriteCanonicalV13RawIngressDatabase();
        else
        {
            fixture.WriteCanonicalV14RawIngressDatabase();
            fixture.SetRawIngressVersion(observed);
        }
        // Without a migration declaration the journal itself stays canonical, so the declaration is what blocks it;
        // with one, a drifted object is what blocks it.
        if (declareMigration) fixture.ChangeRawIngressSchema("CREATE TABLE remediation_drift (id INTEGER);");
        var requirements = CurrentRequirements with
        {
            RawIngressSchema = expected,
            RawIngressMigration = declareMigration ? DeclaredMigration(expected) : null
        };

        var report = Evaluate(fixture, requirements);

        Assert.IsFalse(report.Compatible, CameraAgentStatePreflight.Render(report));
        var finding = report.Findings.Single(static value => value.Blocking);
        Assert.AreEqual("raw-ingress-schema", finding.Code);
        Assert.AreEqual(observed.ToString(System.Globalization.CultureInfo.InvariantCulture), finding.Observed);
        AssertRemediation(remediation, finding.Remediation);
    }

    /// <summary>A journal newer than the image (a restore-only rollback) is never offered the migration guidance.</summary>
    [TestMethod]
    [DataRow(15, 16)]
    [DataRow(14, 15)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void RawIngressRemediationForANewerJournalIsReset(int expected, int observed)
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();
        fixture.WriteCanonicalV14RawIngressDatabase();
        fixture.SetRawIngressVersion(observed);

        var report = Evaluate(fixture, CurrentRequirements with { RawIngressSchema = expected },
            CameraAgentStateContractPolicy.AllowLegacy);

        var finding = report.Findings.Single(static value => value.Blocking);
        Assert.AreEqual("raw-ingress-schema", finding.Code);
        AssertRemediation(ResetRemediation, finding.Remediation);
    }

    /// <summary>
    /// The installer's raw-ingress migration table must describe the schema the image declares, so a schema bump that
    /// forgets the table fails here instead of silently sending every blocking finding to the reset remedy (#1823).
    /// </summary>
    [TestMethod]
    public void RawIngressMigrationTableDescribesTheImagesDeclaredRawSchema()
    {
        var labels = CameraAgentSnapshotRollbackExceptionTests.ReadDockerfileLabels();
        var schema = long.Parse(labels["io.hvo.skymonitor.raw-ingress-schema"], NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.IsTrue(CameraAgentStatePreflight.RawIngressMigrations.TryGetValue(schema, out var migration),
            $"No raw-ingress migration row for the image's declared schema {schema}.");
        Assert.AreEqual(schema, CameraAgentStatePreflight.RawIngressMigrations.Keys.Max(),
            "The image declares the newest raw-ingress schema the installer verifies.");
        Assert.AreEqual(labels["io.hvo.skymonitor.raw-ingress-migration"], migration.Contract);
        using var contracts = JsonDocument.Parse(labels["io.hvo.skymonitor.state-contracts"]);
        var rawIngress = contracts.RootElement.GetProperty("rawIngress");
        Assert.AreEqual(schema, rawIngress.GetProperty("writes").GetInt64());
        Assert.AreEqual(migration.MinimumSource, rawIngress.GetProperty("reads").GetProperty("min").GetInt64(),
            "The oldest journal the image reads is the oldest its declared migration converts.");
        Assert.AreEqual(schema, rawIngress.GetProperty("reads").GetProperty("max").GetInt64());
        foreach (var (target, row) in CameraAgentStatePreflight.RawIngressMigrations)
        {
            Assert.IsTrue(row.MinimumSource < target, $"Schema {target} must migrate from an older schema.");
            for (var version = row.MinimumSource; version <= target; version++)
                Assert.IsTrue(CameraAgentStatePreflight.RawIngressCanonicalShapes.ContainsKey(version),
                    $"Schema {version} can be verified for schema {target} but has no canonical-shape check.");
        }
    }

    private static string DeclaredMigration(int expected) => expected switch
    {
        14 => RawIngressV13Schema.MigrationContract,
        15 => RawIngressV13Schema.RecoveryMigrationContract,
        16 => RawIngressV13Schema.EnvironmentalFenceMigrationContract,
        _ => "undeclared"
    };

    private static void AssertRemediation(string expected, string actual)
    {
        var kind = actual.StartsWith("The raw journal must have the canonical schema", StringComparison.Ordinal) &&
                   actual.Contains("resolve drift or restore a healthy canonical backup", StringComparison.Ordinal)
            ? DriftRemediation
            : actual.StartsWith("The earlier raw journal", StringComparison.Ordinal) &&
              actual.Contains("explicit migration declaration", StringComparison.Ordinal)
                ? MigrationRemediation
                : actual.StartsWith("Archive the raw-ingress database", StringComparison.Ordinal) &&
                  actual.Contains("CameraAgent-only reset procedure", StringComparison.Ordinal)
                    ? ResetRemediation
                    : "unrecognized";
        Assert.AreEqual(expected, kind, actual);
    }
}
