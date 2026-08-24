using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CatalogInstallerTests
{
    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{\"package\":[]}")]
    [DataRow("{\"package\":{}}")]
    [DataRow("{\"package\":{\"version\":1}}")]
    public void Install_InvalidPackageMetadata_ReportsStableRedactedError(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-installer-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(bundle, "hyg_v42.sqlite"), "not-used");
        File.WriteAllText(Path.Combine(bundle, "LICENSE-HYG.md"), "not-used");
        File.WriteAllText(Path.Combine(bundle, "ATTRIBUTION-HYG.md"), "not-used");

        try
        {
            var exception = Assert.ThrowsExactly<InstallerException>(
                () => CatalogInstaller.Install(bundle, Path.Combine(root, "installed"), Guid.NewGuid()));

            Assert.AreEqual("The catalog bundle package version is invalid.", exception.Message);
            Assert.IsNull(exception.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
