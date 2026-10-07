using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Pins the installer's view of the approved catalog registry (issue #521): HYG 4.2 keeps its exact identity, HYG 4.4
/// is a separate side-by-side lineage, and images, bundles and paths can only name what the registry approves.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ProductionCatalogRegistryTests
{
    private const string HygV44CatalogId = "hyg-v44-production";
    private const string HygV44PackageVersion = "hyg-v4.4-p4-s3-r1";
    private static readonly string[] HygV44RetainedFiles = ["manifest.json", "hyg_v44.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md"];

    [TestMethod]
    public void Registry_HygV42KeepsItsPinnedIdentityAndRemainsTheDefault()
    {
        var specification = ProductionCatalog.Get(HygV42.CatalogId);

        Assert.AreSame(specification, ProductionCatalog.Get(ProductionCatalog.DefaultCatalogId));
        Assert.AreEqual("hyg-v42-production-p3-s2", specification.PackageLineage);
        Assert.IsTrue(specification.IsPackageVersion(HygV42.PackageVersion));
        Assert.AreEqual("2", specification.SchemaVersion);
        Assert.AreEqual("3", specification.PreprocessingVersion);
        Assert.AreEqual("hyg_v42.sqlite", specification.DatabaseRelativePath);
        Assert.AreEqual(HygV42.DatabaseSha256, specification.DatabaseSha256);
        Assert.AreEqual(HygV42.DatabaseLength, specification.DatabaseLength);
        Assert.AreEqual(HygV42.RowCount, specification.RowCount);
    }

    [TestMethod]
    public void Registry_HygV44IsASeparateApprovedLineage()
    {
        var specification = ProductionCatalog.ForPackageVersion(HygV44PackageVersion);

        Assert.AreEqual(HygV44CatalogId, specification.CatalogId);
        Assert.AreEqual("hyg-v44-production-p4-s3", specification.PackageLineage);
        Assert.AreEqual("3", specification.SchemaVersion);
        Assert.AreEqual("4", specification.PreprocessingVersion);
        Assert.AreEqual("hyg_v44.sqlite", specification.DatabaseRelativePath);
        Assert.AreEqual("2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321", specification.DatabaseSha256);
        Assert.AreEqual(16_166_912, specification.DatabaseLength);
        Assert.AreEqual(119_613, specification.RowCount);
        Assert.AreEqual(ProductionCatalog.Get(HygV42.CatalogId).LicenseIdentifier, specification.LicenseIdentifier);
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
    public void ImageContract_RegistryV2CatalogIdsEqualTheEmbeddedRegistry()
        => CollectionAssert.AreEquivalent(
            ProductionCatalog.Specifications.All.Select(static item => item.CatalogId).ToArray(),
            CameraAgentImageContract.RegistryV2CatalogIds.ToArray());

    [TestMethod]
    public void ImageContract_RegistryV2IsASupersetOfRegistryV1()
    {
        CollectionAssert.IsSubsetOf(
            CameraAgentImageContract.RegistryV1CatalogIds.ToArray(), CameraAgentImageContract.RegistryV2CatalogIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "hyg-v44-openngc-production" },
            CameraAgentImageContract.RegistryV2CatalogIds.Except(CameraAgentImageContract.RegistryV1CatalogIds).ToArray());
    }

    [TestMethod]
    [DataRow("hvo-approved-catalogs-v2", "hyg-v42-production", true)]
    [DataRow("hvo-approved-catalogs-v2", "hyg-v44-production", true)]
    [DataRow("hvo-approved-catalogs-v2", "hyg-v44-openngc-production", true)]
    [DataRow("hvo-approved-catalogs-v2", "hyg-v45-production", false)]
    [DataRow("hvo-approved-catalogs-v2", null, false)]
    [DataRow("hvo-approved-catalogs-v1", "hyg-v42-production", true)]
    [DataRow("hvo-approved-catalogs-v1", "hyg-v44-production", true)]
    [DataRow("hvo-approved-catalogs-v1", "hyg-v44-openngc-production", false)]
    [DataRow("hvo-approved-catalogs-v1", "hyg-v45-production", false)]
    [DataRow("hvo-approved-catalogs-v1", null, false)]
    [DataRow("hyg-v42-production-p3-s2", "hyg-v42-production", true)]
    [DataRow("hyg-v42-production-p3-s2", "hyg-v44-production", false)]
    [DataRow("hyg-v42-production-p3-s2", "hyg-v44-openngc-production", false)]
    [DataRow("hyg-v44-production-p4-s3", "hyg-v44-production", false)]
    [DataRow("hvo-approved-catalogs-v3", "hyg-v44-openngc-production", false)]
    [DataRow(null, "hyg-v42-production", false)]
    public void ImageContract_SupportsOnlyTheCatalogsItsRegistryApproves(string? catalogContract, string? catalogId, bool supported)
    {
        Assert.AreEqual(supported, CameraAgentImageContract.SupportsCatalog(catalogContract, catalogId));
        Assert.AreEqual(
            catalogContract is "hvo-approved-catalogs-v2" or "hvo-approved-catalogs-v1" or "hyg-v42-production-p3-s2",
            CameraAgentImageContract.IsKnownCatalogContract(catalogContract));
    }

    [TestMethod]
    [DataRow("hyg-v42-production", "hvo-approved-catalogs-v1")]
    [DataRow("hyg-v44-production", "hvo-approved-catalogs-v1")]
    [DataRow("hyg-v44-openngc-production", "hvo-approved-catalogs-v2")]
    public void ImageContract_RequiredCatalogContractIsTheEarliestRegistryApprovingTheCatalog(
        string catalogId, string expected)
        => Assert.AreEqual(expected, CameraAgentImageContract.RequiredCatalogContract(catalogId));

    [TestMethod]
    [DataRow(HygV42.CatalogId, HygV42.PackageVersion)]
    [DataRow(HygV44CatalogId, HygV44PackageVersion)]
    [DataRow(HygV44CatalogId, "hyg-v4.4-p4-s3-r2")]
    public void ReadBundleSpecification_SelectsTheLineageTheManifestNames(string catalogId, string packageVersion)
    {
        using var bundle = new BundleDirectory(catalogId, packageVersion);

        Assert.AreEqual(catalogId, ProductionCatalog.ReadBundleSpecification(bundle.Path).CatalogId);
    }

    [TestMethod]
    [DataRow(HygV42.CatalogId, HygV44PackageVersion, "The catalog bundle names a catalog other than its package lineage.")]
    [DataRow(HygV44CatalogId, HygV42.PackageVersion, "The catalog bundle names a catalog other than its package lineage.")]
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
        var hygV42 = InstallationPaths.Create(root, instanceId, HygV42.CatalogId);
        var hygV44 = hygV42.WithCatalog(HygV44CatalogId);

        Assert.AreEqual(Path.Combine(hygV42.ProductRoot, "catalogs", HygV44CatalogId), hygV44.CatalogRoot);
        Assert.AreEqual(Path.Combine(hygV42.OperationsRoot, "catalog-references", HygV44CatalogId), hygV44.CatalogReferencesRoot);
        Assert.AreEqual(hygV42.InstanceRoot, hygV44.InstanceRoot);
        Assert.AreEqual(hygV42, hygV44.WithCatalog(HygV42.CatalogId));
        CollectionAssert.AreEquivalent(
            new[] { hygV42.CatalogRoot, hygV44.CatalogRoot },
            hygV42.ForEveryApprovedCatalog().Select(static item => item.CatalogRoot).ToArray());
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
