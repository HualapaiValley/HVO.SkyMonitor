using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Pins the installer's default, approved catalog identities, and current image contract.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ProductionCatalogRegistryTests
{
    private const string HygV44CatalogId = "hyg-v44-production";
    private const string HygV44PackageVersion = "hyg-v4.4-p4-s3-r1";
    private static readonly string[] HygV44RetainedFiles = ["manifest.json", "hyg_v44.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md"];

    [TestMethod]
    public void Registry_HygV44IsTheApprovedFreshInstallDefault()
    {
        var specification = ProductionCatalog.ForPackageVersion(HygV44PackageVersion);

        Assert.AreEqual(ProductionCatalog.DefaultCatalogId, specification.CatalogId);
        Assert.AreEqual(HygV44.CatalogId, specification.CatalogId);
        Assert.AreEqual("hyg-v44-production-p4-s3", specification.PackageLineage);
        Assert.AreEqual("3", specification.SchemaVersion);
        Assert.AreEqual("4", specification.PreprocessingVersion);
        Assert.AreEqual("hyg_v44.sqlite", specification.DatabaseRelativePath);
        Assert.AreEqual(HygV44.DatabaseSha256, specification.DatabaseSha256);
        Assert.AreEqual(HygV44.DatabaseLength, specification.DatabaseLength);
        Assert.AreEqual(HygV44.RowCount, specification.RowCount);
        Assert.AreEqual("CC BY-SA 4.0", specification.LicenseIdentifier);
        CollectionAssert.AreEqual(HygV44RetainedFiles, specification.RetainedFileNames.ToArray());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("hyg-v4.4-p4-s3-r")]
    [DataRow("hyg-v4.4-p4-s3-r0")]
    [DataRow("hyg-v4.4-p4-s3-r1a")]
    [DataRow("hyg-v4.4-p3-s2-r1")]
    [DataRow("hyg-v4.5-p1-s1-r1")]
    public void Registry_UnapprovedPackageVersionIsRefused(string? packageVersion)
    {
        Assert.IsFalse(ProductionCatalog.TryForPackageVersion(packageVersion, out _));
        Assert.ThrowsExactly<InstallerException>(() => ProductionCatalog.ForPackageVersion(packageVersion));
    }

    [TestMethod]
    [DataRow("hvo-approved-catalogs-v4", "openngc-production", true)]
    [DataRow("hvo-approved-catalogs-v4", HygV44.CatalogId, true)]
    [DataRow("hvo-approved-catalogs-v4", "hyg-v44-openngc-production", true)]
    [DataRow("hvo-approved-catalogs-v4", "hyg-v45-production", false)]
    [DataRow("hvo-approved-catalogs-v4", null, false)]
    [DataRow("hvo-approved-catalogs-v3", HygV44.CatalogId, false)]
    [DataRow("hvo-approved-catalogs-v2", HygV44.CatalogId, false)]
    [DataRow("hvo-approved-catalogs-v1", HygV44.CatalogId, false)]
    [DataRow("unknown", HygV44.CatalogId, false)]
    [DataRow(null, HygV44.CatalogId, false)]
    public void ImageContract_SupportsOnlyTheCurrentRegistry(string? catalogContract, string? catalogId, bool supported)
    {
        Assert.AreEqual(supported, CameraAgentImageContract.SupportsCatalog(catalogContract, catalogId));
        Assert.AreEqual(catalogContract == "hvo-approved-catalogs-v4",
            CameraAgentImageContract.IsKnownCatalogContract(catalogContract));
    }

    [TestMethod]
    [DataRow(HygV44.CatalogId, HygV44.PackageVersion)]
    [DataRow("hyg-v44-openngc-production", "hyg-v4.4-openngc-v20260501-p5-s4-r1")]
    [DataRow(HygV44CatalogId, "hyg-v4.4-p4-s3-r2")]
    public void ReadBundleSpecification_SelectsTheLineageTheManifestNames(string catalogId, string packageVersion)
    {
        using var bundle = new BundleDirectory(catalogId, packageVersion);

        Assert.AreEqual(catalogId, ProductionCatalog.ReadBundleSpecification(bundle.Path).CatalogId);
    }

    [TestMethod]
    [DataRow("hyg-v44-openngc-production", HygV44PackageVersion, "The catalog bundle names a catalog other than its package lineage.")]
    [DataRow(HygV44CatalogId, "openngc-v20260501-p6-s5-r1", "The catalog bundle names a catalog other than its package lineage.")]
    [DataRow(null, HygV44PackageVersion, "The catalog bundle names a catalog other than its package lineage.")]
    [DataRow(HygV44CatalogId, "hyg-v4.5-p1-s1-r1", "The catalog bundle package version is invalid.")]
    public void ReadBundleSpecification_WrongLineageIsRefused(string? catalogId, string packageVersion, string message)
    {
        using var bundle = new BundleDirectory(catalogId, packageVersion);

        var exception = Assert.ThrowsExactly<InstallerException>(() => ProductionCatalog.ReadBundleSpecification(bundle.Path));
        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void InstallationPaths_EveryApprovedCatalogHasItsOwnRootAndReferences()
    {
        var instanceId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-registry-{Guid.NewGuid():N}");
        var hygV44 = InstallationPaths.Create(root, instanceId, HygV44.CatalogId);
        var composed = hygV44.WithCatalog("hyg-v44-openngc-production");

        Assert.AreEqual(Path.Combine(hygV44.ProductRoot, "catalogs", HygV44CatalogId), hygV44.CatalogRoot);
        Assert.AreEqual(Path.Combine(hygV44.OperationsRoot, "catalog-references", HygV44CatalogId), hygV44.CatalogReferencesRoot);
        Assert.AreEqual(hygV44.InstanceRoot, composed.InstanceRoot);
        Assert.AreEqual(hygV44, composed.WithCatalog(HygV44.CatalogId));
        CollectionAssert.AreEquivalent(
            new[] { hygV44.CatalogRoot, composed.CatalogRoot, hygV44.WithCatalog("openngc-production").CatalogRoot },
            hygV44.ForEveryApprovedCatalog().Select(static item => item.CatalogRoot).ToArray());
    }

    private sealed class BundleDirectory : IDisposable
    {
        public BundleDirectory(string? catalogId, string packageVersion)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-registry-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            var catalog = catalogId is null ? "{}" : $"{{\"id\":\"{catalogId}\"}}";
            File.WriteAllText(
                System.IO.Path.Combine(Path, "manifest.json"),
                $"{{\"manifestVersion\":2,\"catalog\":{catalog},\"package\":{{\"version\":\"{packageVersion}\"}}}}");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
