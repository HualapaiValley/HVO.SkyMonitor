using System.Globalization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Deployment;

/// <summary>
/// Keeps the CameraAgent image's declared durable-state boundaries equal to the code that enforces them at
/// startup. The pre-<c>70ecdd3</c> upgrade failed because the image claimed unbounded backward compatibility
/// while the runtime rejected the persisted Identity lineage and raw-ingress schema it inherited.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStateCompatibilityLabelTests
{
    private const string MinimumCompatibleRevision = "70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f";

    [TestMethod]
    public void Dockerfile_DeclaresTheDurableStateContractInsteadOfUnboundedBackwardCompatibility()
    {
        var labels = ReadImageLabels();

        Assert.AreEqual("cameraagent-state-v2", labels["io.hvo.skymonitor.state-compatibility"]);
        Assert.AreEqual(MinimumCompatibleRevision, labels["io.hvo.skymonitor.minimum-compatible-revision"]);
        Assert.AreNotEqual(
            "backward-compatible",
            labels["io.hvo.skymonitor.state-compatibility"],
            "the image must not claim generic backward compatibility with pre-70ecdd3 CameraAgent state");
    }

    [TestMethod]
    public void Dockerfile_DeclaresTheRawIngressSchemaTheRuntimeRequires()
    {
        var labels = ReadImageLabels();

        Assert.AreEqual(
            SqliteRawCaptureJournal.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
            labels["io.hvo.skymonitor.raw-ingress-schema"]);
    }

    [TestMethod]
    public void Dockerfile_DeclaresTheCanonicalIdentityMigrationLineage()
    {
        var labels = ReadImageLabels();
        var migrationsRoot = Path.Combine(
            RepositoryRoot(), "src", "HVO.SkyMonitor.CameraAgent", "Data", "Migrations");
        var migrations = Directory.EnumerateFiles(migrationsRoot, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => name is not null && !name.EndsWith(".Designer", StringComparison.Ordinal) &&
                                  !name.EndsWith("ModelSnapshot", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The unreleased CameraAgent component keeps exactly one canonical initial migration; a replaced baseline
        // must move the declared boundary with it.
        Assert.AreEqual(1, migrations.Length, string.Join(", ", migrations));
        Assert.AreEqual(migrations[0], labels["io.hvo.skymonitor.identity-migration"]);
    }

    [TestMethod]
    public void Dockerfile_DeclaresTheCatalogManifestVersionTheProductionBundlePublishes()
    {
        var labels = ReadImageLabels();

        Assert.AreEqual(
            CatalogSnapshotResolver.SupportedManifestVersion.ToString(CultureInfo.InvariantCulture),
            labels["io.hvo.skymonitor.catalog-manifest-version"]);
        Assert.AreEqual("hyg-v42-production-p3-s2", labels["io.hvo.skymonitor.catalog-contract"]);
    }

    private static Dictionary<string, string> ReadImageLabels()
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile"));
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     dockerfile,
                     "^\\s*(?:LABEL\\s+)?(io\\.hvo\\.skymonitor\\.[a-z-]+)=\"([^\"]*)\"",
                     RegexOptions.Multiline | RegexOptions.CultureInvariant,
                     TimeSpan.FromSeconds(5)))
        {
            labels[match.Groups[1].Value] = match.Groups[2].Value;
        }
        Assert.IsTrue(labels.Count >= 8, $"expected the full CameraAgent label set; found {labels.Count}");
        return labels;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
