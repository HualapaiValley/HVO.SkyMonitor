using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

[TestClass]
[DoNotParallelize]
internal sealed class CatalogSnapshotResolverTests
{
    private const string FixtureCatalogId = "hyg-v42-fixture";
    private const string ProductionCatalogId = "hyg-v42-production";
    private const string SnapshotVersion = "hyg-v42-fixture-1";
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite");

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
    {
        var instance = new CatalogSnapshotResolverTests();

        Assert.IsNotNull(instance);
    }

    [TestMethod]
    public void ResolveLoadsExplicitFixtureAndReturnsValidatedIdentity()
    {
        using var installation = CreateInstallation();

        var result = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
            installation.Root, FixtureCatalogId)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });

        Assert.AreEqual(SnapshotVersion, result.SnapshotVersion);
        Assert.AreEqual(CatalogSnapshotPackageKind.Fixture, result.PackageKind);
        Assert.AreEqual(2, result.ManifestVersion);
        Assert.AreEqual("hyg-v42-fixture", result.CatalogId);
        Assert.IsFalse(result.CatalogIdDerivedFromLegacyManifest);
        Assert.AreEqual("4.2-fixture.1", result.CatalogVersion);
        Assert.AreEqual("2", result.SchemaVersion);
        Assert.AreEqual("3", result.PreprocessingVersion);
        Assert.AreEqual(9, result.RowCount);
        Assert.AreEqual(9, result.Catalog.ObjectCount);
        Assert.AreEqual(result.DatabaseSha256, result.Catalog.Metadata.Checksum);
        Assert.IsTrue(Path.IsPathFullyQualified(result.DatabasePath));
    }

    [TestMethod]
    public void ResolveDerivesCanonicalIdentityForExactLegacyFixture()
    {
        using var installation = CreateLegacyFixtureInstallation();

        var result = ResolveFixture(installation.Root);

        Assert.AreEqual(1, result.ManifestVersion);
        Assert.AreEqual("hyg-v42-fixture", result.CatalogId);
        Assert.IsTrue(result.CatalogIdDerivedFromLegacyManifest);
    }

    [TestMethod]
    public void ResolveRejectsLegacyFixtureWhosePinnedFactsDoNotMatch()
    {
        using var installation = CreateLegacyFixtureInstallation();
        File.WriteAllText(installation.ManifestPath, File.ReadAllText(installation.ManifestPath)
            .Replace("\"rowCount\": 9", "\"rowCount\": 10", StringComparison.Ordinal));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        StringAssert.Contains(exception.Message, "canonical legacy catalog identity", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsExplicitAndDerivedCatalogIdentityMismatch()
    {
        using var explicitInstallation = CreateInstallation();
        File.WriteAllText(explicitInstallation.ManifestPath, File.ReadAllText(explicitInstallation.ManifestPath)
            .Replace($"\"id\": \"{FixtureCatalogId}\"", "\"id\": \"alternate-fixture\"",
                StringComparison.Ordinal));

        var explicitMismatch = Assert.ThrowsExactly<InvalidDataException>(() =>
            ResolveFixture(explicitInstallation.Root));
        StringAssert.Contains(explicitMismatch.Message, "Catalog identity mismatch", StringComparison.Ordinal);

        using var legacyInstallation = CreateLegacyFixtureInstallation();
        var derivedMismatch = Assert.ThrowsExactly<InvalidDataException>(() =>
            ResolveFixture(legacyInstallation.Root, "alternate-fixture"));
        StringAssert.Contains(derivedMismatch.Message, "Catalog identity mismatch", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveLoadsActualLegacyProductionBundleWhenProvided()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_LEGACY_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle))
        {
            Assert.Inconclusive("HVO_LEGACY_CATALOG_BUNDLE was not provided.");
        }
        using var installation = CreateProductionInstallationFromBundle(bundle!);

        var result = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
            installation.Root, ProductionCatalogId));

        Assert.AreEqual(1, result.ManifestVersion);
        Assert.AreEqual("hyg-v42-production", result.CatalogId);
        Assert.IsTrue(result.CatalogIdDerivedFromLegacyManifest);
        Assert.AreEqual(119_625, result.RowCount);
    }

    [TestMethod]
    public void ResolveRequiresProductionUnlessFixtureIsExplicitlyAllowed()
    {
        using var installation = CreateInstallation();

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, FixtureCatalogId)));

        StringAssert.Contains(exception.Message, "package kind mismatch", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsMissingNonLinkAndTraversalPointers()
    {
        using var installation = CreateInstallation();
        File.Delete(installation.PointerPath);
        Assert.ThrowsExactly<FileNotFoundException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.PointerPath, SnapshotVersion);
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
        File.Delete(installation.PointerPath);

        foreach (var target in new[]
                 {
                     "../outside", "..\\outside", "versions/../outside", "versions/nested/version",
                     $"/versions/{SnapshotVersion}", $"versions\\{SnapshotVersion}"
                 })
        {
            Directory.CreateSymbolicLink(installation.PointerPath, target);
            Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root),
                $"Pointer target '{target}' should fail closed.");
            File.Delete(installation.PointerPath);
        }
    }

    [TestMethod]
    public void ResolveRejectsMalformedAndDuplicateJson()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, "{");
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath)
            .Replace("\"kind\": \"fixture\"", "\"kind\": \"fixture\", \"kind\": \"production\"",
                StringComparison.Ordinal));
        var duplicate = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
        StringAssert.Contains(duplicate.Message, "duplicate property", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsOversizedManifestBeforeParsing()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, new string(' ', 65_537));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        StringAssert.Contains(exception.Message, "65536-byte limit", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsUnknownAndMissingFixtureProperties()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath)
            .Replace("\"manifestVersion\": 2,", "\"manifestVersion\": 2,\n  \"unexpected\": true,",
                StringComparison.Ordinal));
        var unknown = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
        StringAssert.Contains(unknown.Message, "unknown property", StringComparison.Ordinal);

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath)
            .Replace("    \"name\": \"HYG bright-star test fixture\",\n", string.Empty, StringComparison.Ordinal));
        var missing = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
        StringAssert.Contains(missing.Message, "missing required property", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsUnknownProductionPropertiesAndMismatchedPinnedProvenance()
    {
        using var installation = CreateProductionInstallation();
        File.WriteAllText(installation.ManifestPath, CreateProductionManifest()
            .Replace("\"version\": \"3.45.1\"", "\"version\": \"3.45.1\", \"unexpected\": true",
                StringComparison.Ordinal));
        var unknown = Assert.ThrowsExactly<InvalidDataException>(() =>
            CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, ProductionCatalogId)));
        StringAssert.Contains(unknown.Message, "unknown property", StringComparison.Ordinal);

        File.WriteAllText(installation.ManifestPath,
            CreateProductionManifest(sourceProjectUrl: "https://example.test/wrong"));
        var mismatch = Assert.ThrowsExactly<InvalidDataException>(() =>
            CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, ProductionCatalogId)));
        StringAssert.Contains(mismatch.Message, "source.projectUrl mismatch", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsProductionSnapshotMissingRetainedFiles()
    {
        using var installation = CreateProductionInstallation();

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, ProductionCatalogId)));

        StringAssert.Contains(exception.Message, "exactly its four retained files", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsFixtureSnapshotWithExtraEntry()
    {
        using var installation = CreateInstallation();
        var versionDirectory = Path.GetDirectoryName(installation.ManifestPath)!;
        var unexpectedPath = Path.Combine(versionDirectory, "unexpected");
        File.WriteAllText(unexpectedPath, "unexpected");

        var extraFile = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
        StringAssert.Contains(extraFile.Message, "exactly its manifest and database files", StringComparison.Ordinal);

        File.Delete(unexpectedPath);
        Directory.CreateDirectory(unexpectedPath);
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsFixtureSnapshotWithExtraSymbolicLink()
    {
        using var installation = CreateInstallation();
        var versionDirectory = Path.GetDirectoryName(installation.ManifestPath)!;
        File.CreateSymbolicLink(Path.Combine(versionDirectory, "unexpected"), installation.DatabasePath);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        StringAssert.Contains(exception.Message, "exactly its manifest and database files", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ResolveRejectsHardLinkedManifestWithoutMutatingExternalFingerprint()
    {
        using var installation = CreateInstallation();

        AssertHardLinkedRetainedFileRejected(
            installation.ManifestPath,
            () => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsHardLinkedDatabaseWithoutMutatingExternalFingerprint()
    {
        using var installation = CreateInstallation();

        AssertHardLinkedRetainedFileRejected(
            installation.DatabasePath,
            () => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsHardLinkedLicenseWithoutMutatingExternalFingerprint()
    {
        using var installation = CreateProductionInstallationWithRetainedFiles();
        var licensePath = Path.Combine(Path.GetDirectoryName(installation.ManifestPath)!, "LICENSE-HYG.md");

        AssertHardLinkedRetainedFileRejected(
            licensePath,
            () => CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, ProductionCatalogId)));
    }

    [TestMethod]
    public void ResolveRejectsHardLinkedAttributionWithoutMutatingExternalFingerprint()
    {
        using var installation = CreateProductionInstallationWithRetainedFiles();
        var attributionPath = Path.Combine(Path.GetDirectoryName(installation.ManifestPath)!, "ATTRIBUTION-HYG.md");

        AssertHardLinkedRetainedFileRejected(
            attributionPath,
            () => CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
                installation.Root, ProductionCatalogId)));
    }

    [TestMethod]
    public void ResolveRejectsManifestReplacementAfterInitialAuthenticationWithoutMutatingOriginal()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows replacement blocking is covered by the manifest ABA test.");
        }
        using var installation = CreateInstallation();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-original-{Guid.NewGuid():N}");
        FileFingerprint? fingerprint = null;
        var invoked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (invoked || point != CatalogSnapshotValidationPoint.AfterInitialAuthentication ||
                !string.Equals(path, installation.ManifestPath, StringComparison.Ordinal))
            {
                return;
            }
            invoked = true;
            File.Move(path, externalPath);
            fingerprint = ReadFingerprint(externalPath);
            File.Copy(externalPath, path);
        };
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

            Assert.IsTrue(invoked);
            Assert.AreEqual(fingerprint, ReadFingerprint(externalPath));
            StringAssert.Contains(exception.Message, "identity changed", StringComparison.Ordinal);
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            File.Delete(externalPath);
        }
    }

    [TestMethod]
    public void ResolveRejectsDatabaseReplacementAfterInitialAuthenticationWithoutMutatingOriginal()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows replacement blocking is covered by the SQLite ABA test.");
        }
        using var installation = CreateInstallation();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-original-{Guid.NewGuid():N}");
        FileFingerprint? fingerprint = null;
        var invoked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (invoked || point != CatalogSnapshotValidationPoint.AfterInitialAuthentication ||
                !string.Equals(path, installation.DatabasePath, StringComparison.Ordinal))
            {
                return;
            }
            invoked = true;
            File.Move(path, externalPath);
            fingerprint = ReadFingerprint(externalPath);
            File.Copy(externalPath, path);
        };
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

            Assert.IsTrue(invoked);
            Assert.AreEqual(fingerprint, ReadFingerprint(externalPath));
            StringAssert.Contains(exception.Message, "identity changed", StringComparison.Ordinal);
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            File.Delete(externalPath);
        }
    }

    [TestMethod]
    public void ResolveRejectsHardLinkCreatedAfterInitialAuthenticationWithoutMutatingExternalFingerprint()
    {
        using var installation = CreateInstallation();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-external-{Guid.NewGuid():N}");
        FileFingerprint? fingerprint = null;
        var invoked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (invoked || point != CatalogSnapshotValidationPoint.AfterInitialAuthentication ||
                !string.Equals(path, installation.DatabasePath, StringComparison.Ordinal))
            {
                return;
            }
            invoked = true;
            CreateHardLink(path, externalPath);
            fingerprint = ReadFingerprint(externalPath);
        };
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

            Assert.IsTrue(invoked);
            Assert.AreEqual(fingerprint, ReadFingerprint(externalPath));
            StringAssert.Contains(exception.Message, "hard-link", StringComparison.Ordinal);
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            File.Delete(externalPath);
        }
    }

    [TestMethod]
    public void ResolveConsumesAuthenticatedManifestAcrossAbaPathReplacement()
    {
        using var installation = CreateInstallation();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-original-{Guid.NewGuid():N}");
        var originalFingerprint = ReadFingerprint(installation.ManifestPath);
        var swapped = false;
        var replacementBlocked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (!string.Equals(path, installation.ManifestPath, StringComparison.Ordinal))
            {
                return;
            }
            if (point == CatalogSnapshotValidationPoint.AfterInitialAuthentication)
            {
                try
                {
                    File.Move(path, externalPath);
                    swapped = true;
                    File.WriteAllText(path, "{");
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    replacementBlocked = true;
                }
            }
            else if (point == CatalogSnapshotValidationPoint.AfterManifestRead && swapped)
            {
                File.Delete(path);
                File.Move(externalPath, path);
                swapped = false;
            }
        };
        try
        {
            var result = ResolveFixture(installation.Root);

            Assert.AreEqual(9, result.RowCount);
            Assert.AreEqual(OperatingSystem.IsWindows(), replacementBlocked);
            Assert.AreEqual(originalFingerprint, ReadFingerprint(installation.ManifestPath));
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            RestoreAbaFile(installation.ManifestPath, externalPath, ref swapped);
        }
    }

    [TestMethod]
    public void ResolveLoadsAuthenticatedDatabaseAcrossAbaSqliteOpenReplacement()
    {
        using var installation = CreateInstallation();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-original-{Guid.NewGuid():N}");
        var originalFingerprint = ReadFingerprint(installation.DatabasePath);
        var swapped = false;
        var replacementBlocked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (!string.Equals(path, installation.DatabasePath, StringComparison.Ordinal))
            {
                return;
            }
            if (point == CatalogSnapshotValidationPoint.BeforeSqliteOpen)
            {
                try
                {
                    File.Move(path, externalPath);
                    swapped = true;
                    File.WriteAllBytes(path, new byte[checked((int)originalFingerprint.Length)]);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    replacementBlocked = true;
                }
            }
            else if (point == CatalogSnapshotValidationPoint.AfterSqliteLoad && swapped)
            {
                File.Delete(path);
                File.Move(externalPath, path);
                swapped = false;
            }
        };
        try
        {
            var result = ResolveFixture(installation.Root);

            Assert.AreEqual(9, result.Catalog.ObjectCount);
            Assert.AreEqual(OperatingSystem.IsWindows(), replacementBlocked);
            Assert.AreEqual(originalFingerprint, ReadFingerprint(installation.DatabasePath));
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            RestoreAbaFile(installation.DatabasePath, externalPath, ref swapped);
        }
    }

    [TestMethod]
    public void ResolveRejectsInPlaceDatabaseMutationAfterSqliteLoadAndPreservesOriginalRecovery()
    {
        using var installation = CreateInstallation();
        var originalFingerprint = ReadFingerprint(installation.DatabasePath);
        using var writer = new FileStream(
            installation.DatabasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        var offset = writer.Length - 1;
        writer.Position = offset;
        var originalByte = writer.ReadByte();
        var invoked = false;
        CatalogSnapshotResolver.ValidationTestHook = (path, point) =>
        {
            if (point != CatalogSnapshotValidationPoint.AfterSqliteLoad ||
                !string.Equals(path, installation.DatabasePath, StringComparison.Ordinal))
            {
                return;
            }
            invoked = true;
            writer.Position = offset;
            writer.WriteByte((byte)(originalByte ^ 0xFF));
            writer.Flush(flushToDisk: true);
        };
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

            Assert.AreEqual(!OperatingSystem.IsWindows(), invoked);
            if (invoked)
            {
                StringAssert.Contains(exception.Message, "SHA-256 mismatch", StringComparison.Ordinal);
            }
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            writer.Position = offset;
            writer.WriteByte((byte)originalByte);
            writer.Flush(flushToDisk: true);
        }

        writer.Dispose();
        Assert.AreEqual(originalFingerprint, ReadFingerprint(installation.DatabasePath));
    }

    [TestMethod]
    public void ResolveRejectsManifestAndPackageVersionMismatch()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, manifestVersion: 1));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, packageVersion: "other"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsSchemaPreprocessingAndCatalogVersionMismatch()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath)
            .Replace("\"id\": \"hyg-v42-fixture\"", "\"id\": \"HYG-invalid\"", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath)
            .Replace("\"id\": \"hyg-v42-fixture\"", $"\"id\": \"{new string('a', 33)}\"", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, schemaVersion: "1"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, preprocessingVersion: "2"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, catalogVersion: "other"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsMalformedAndMismatchedDatabaseEvidence()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, sha256: "bad"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, sha256: new string('0', 64)));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath,
            CreateManifest(installation.DatabasePath, length: new FileInfo(installation.DatabasePath).Length + 1));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath, CreateManifest(installation.DatabasePath, rowCount: 10));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveRejectsMissingAndTraversalDatabasePaths()
    {
        using var installation = CreateInstallation();
        File.WriteAllText(installation.ManifestPath,
            CreateManifest(installation.DatabasePath, relativePath: "../catalog.sqlite"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath,
            CreateManifest(installation.DatabasePath, relativePath: "..\\catalog.sqlite"));
        Assert.ThrowsExactly<InvalidDataException>(() => ResolveFixture(installation.Root));

        File.WriteAllText(installation.ManifestPath,
            CreateManifest(installation.DatabasePath, relativePath: "missing.sqlite"));
        Assert.ThrowsExactly<FileNotFoundException>(() => ResolveFixture(installation.Root));
    }

    [TestMethod]
    public void ResolveValidatesOptionsBeforeReadingTheFileSystem()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CatalogSnapshotResolver.Resolve(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions(null!, FixtureCatalogId)));
        Assert.ThrowsExactly<ArgumentException>(() => CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions(" ", FixtureCatalogId)));
        Assert.ThrowsExactly<ArgumentException>(() => CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions("missing", " ")));
        Assert.ThrowsExactly<InvalidDataException>(() => CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions("missing", "Invalid")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions("missing", FixtureCatalogId)
            {
                ExpectedManifestVersion = 0
            }));
    }

    private static CatalogSnapshotResult ResolveFixture(string root, string expectedCatalogId = FixtureCatalogId)
        => CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(root, expectedCatalogId)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });

    internal static Installation CreateInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", SnapshotVersion);
        Directory.CreateDirectory(versionDirectory);
        var pointerPath = Path.Combine(root, "current");
        var databasePath = Path.Combine(versionDirectory, "hyg_v42.sqlite");
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        Directory.CreateSymbolicLink(pointerPath, $"versions/{SnapshotVersion}");
        File.Copy(FixturePath, databasePath);
        File.WriteAllText(manifestPath, CreateManifest(databasePath));
        return new Installation(root, pointerPath, manifestPath, databasePath);
    }

    private static Installation CreateLegacyFixtureInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", SnapshotVersion);
        Directory.CreateDirectory(versionDirectory);
        var pointerPath = Path.Combine(root, "current");
        var databasePath = Path.Combine(versionDirectory, "hyg_v42.sqlite");
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        Directory.CreateSymbolicLink(pointerPath, $"versions/{SnapshotVersion}");
        File.Copy(FixturePath, databasePath);
        File.WriteAllText(manifestPath, CreateManifest(
            databasePath,
            manifestVersion: 1,
            relativePath: "hyg_v42.sqlite",
            includeCatalogId: false));
        return new Installation(root, pointerPath, manifestPath, databasePath);
    }

    private static Installation CreateProductionInstallationFromBundle(string bundle)
    {
        const string version = "hyg-v4.2-p3-s2-r1";
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(versionDirectory);
        foreach (var name in new[] { "manifest.json", "hyg_v42.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md" })
        {
            File.Copy(Path.Combine(bundle, name), Path.Combine(versionDirectory, name));
        }
        var pointerPath = Path.Combine(root, "current");
        Directory.CreateSymbolicLink(pointerPath, $"versions/{version}");
        return new Installation(
            root,
            pointerPath,
            Path.Combine(versionDirectory, "manifest.json"),
            Path.Combine(versionDirectory, "hyg_v42.sqlite"));
    }

    private static Installation CreateProductionInstallation()
    {
        const string version = "hyg-v4.2-p3-s2-r1";
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-install-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(versionDirectory);
        var pointerPath = Path.Combine(root, "current");
        var databasePath = Path.Combine(versionDirectory, "hyg_v42.sqlite");
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        Directory.CreateSymbolicLink(pointerPath, $"versions/{version}");
        File.Copy(FixturePath, databasePath);
        File.WriteAllText(manifestPath, CreateProductionManifest());
        return new Installation(root, pointerPath, manifestPath, databasePath);
    }

    private static Installation CreateProductionInstallationWithRetainedFiles()
    {
        var installation = CreateProductionInstallation();
        var versionDirectory = Path.GetDirectoryName(installation.ManifestPath)!;
        File.WriteAllBytes(Path.Combine(versionDirectory, "LICENSE-HYG.md"), new byte[423]);
        File.WriteAllBytes(Path.Combine(versionDirectory, "ATTRIBUTION-HYG.md"), new byte[1_361]);
        return installation;
    }

    private static string CreateManifest(
        string databasePath,
        int manifestVersion = 2,
        string packageVersion = SnapshotVersion,
        string catalogVersion = "4.2-fixture.1",
        string schemaVersion = "2",
        string preprocessingVersion = "3",
        string relativePath = "hyg_v42.sqlite",
        string? sha256 = null,
        long? length = null,
        long rowCount = 9,
        bool includeCatalogId = true)
        => $$"""
            {
              "manifestVersion": {{manifestVersion}},
              "package": {
                "kind": "fixture",
                "version": "{{packageVersion}}"
              },
              "catalog": {
                {{(includeCatalogId ? "\"id\": \"hyg-v42-fixture\"," : string.Empty)}}
                "name": "HYG bright-star test fixture",
                "version": "{{catalogVersion}}"
              },
              "schemaVersion": "{{schemaVersion}}",
              "preprocessingVersion": "{{preprocessingVersion}}",
              "database": {
                "relativePath": "{{relativePath}}",
                "sha256": "{{sha256 ?? Checksum(databasePath)}}",
                "length": {{length ?? new FileInfo(databasePath).Length}},
                "rowCount": {{rowCount}}
              }
            }
            """;

    private static string CreateProductionManifest(
        string sourceProjectUrl = "https://codeberg.org/astronexus/hyg")
        => $$"""
            {
              "manifestVersion": 2,
              "package": {
                "kind": "production",
                "version": "hyg-v4.2-p3-s2-r1"
              },
              "catalog": {
                "id": "hyg-v42-production",
                "name": "HYG 4.2",
                "version": "4.2"
              },
              "source": {
                "projectUrl": "{{sourceProjectUrl}}",
                "downloadUrl": "https://codeberg.org/astronexus/hyg.git/info/lfs/objects/5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601",
                "oid": "5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601",
                "compressed": {
                  "sha256": "5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601",
                  "length": 13636976
                },
                "decompressed": {
                  "sha256": "b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2",
                  "length": 33932800
                }
              },
              "schemaVersion": "2",
              "preprocessingVersion": "3",
              "serializer": {
                "name": "sqlite3",
                "version": "3.45.1"
              },
              "database": {
                "relativePath": "hyg_v42.sqlite",
                "sha256": "b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2",
                "length": 9302016,
                "rowCount": 119625,
                "solCount": 0,
                "requiredColumn": "hipparcos_id"
              },
              "license": {
                "identifier": "CC BY-SA 4.0",
                "url": "https://creativecommons.org/licenses/by-sa/4.0/",
                "file": {
                  "relativePath": "LICENSE-HYG.md",
                  "sha256": "9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05",
                  "length": 423
                },
                "attribution": {
                  "relativePath": "ATTRIBUTION-HYG.md",
                  "sha256": "e3addc3480a0d0f07129f332b0dea592fa315373f21111d54ac8e2aebd03b5f1",
                  "length": 1361
                }
              },
              "topology": {
                "identity": "d3-celestial-v0.7.32-hip-coordinate-map-v1",
                "sha256": "70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621",
                "constellationCount": 88,
                "segmentCount": 743
              }
            }
            """;

    private static string Checksum(string path)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static void AssertHardLinkedRetainedFileRejected(string retainedPath, Action resolve)
    {
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-external-{Guid.NewGuid():N}");
        CreateHardLink(retainedPath, externalPath);
        var fingerprint = ReadFingerprint(externalPath);
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(resolve);

            Assert.AreEqual(fingerprint, ReadFingerprint(externalPath));
            StringAssert.Contains(exception.Message, "hard-link", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    private static FileFingerprint ReadFingerprint(string path)
        => new(new FileInfo(path).Length, Checksum(path));

    private static void RestoreAbaFile(string path, string externalPath, ref bool swapped)
    {
        if (swapped)
        {
            File.Delete(path);
            File.Move(externalPath, path);
            swapped = false;
        }
        else
        {
            File.Delete(externalPath);
        }
    }

    internal static void CreateHardLink(string source, string destination)
    {
        var succeeded = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(destination, source, 0)
            : Link(source, destination) == 0;
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal sealed record Installation(
        string Root,
        string PointerPath,
        string ManifestPath,
        string DatabasePath) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record FileFingerprint(long Length, string Sha256);

#pragma warning disable SYSLIB1054 // Test-only Unix hardlink setup does not warrant enabling unsafe interop generation.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int Link(string source, string destination);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, nint securityAttributes);
#pragma warning restore SYSLIB1054
}
