using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1812", Justification = "MSTest discovers and constructs this fixture through reflection.")]
internal sealed class IndependentOpenNgcCatalogTests
{
    [TestMethod]
    public void PublicOpenNgcArtifactMatchesTheApprovedIndependentSpecification()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_OPENNGC_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_OPENNGC_CATALOG_BUNDLE to verify a temporary public-source build.");
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var specification = ApprovedCatalogSpecifications.Get("openngc-production");
        var version = specification.PackageVersionPrefix + "1";
        var target = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(target);
        try
        {
            foreach (var name in specification.RetainedFileNames) File.Copy(Path.Combine(bundle, name), Path.Combine(target, name));
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), "versions/" + version);
            var resolved = CatalogSnapshotResolver.Resolve(new(root, specification.CatalogId));
            Assert.AreEqual(0, resolved.Catalog.ObjectCount);
            Assert.AreEqual(13371, resolved.Catalog.DeepSky!.Objects.Count);
            Assert.IsTrue(string.Equals(specification.Database.Sha256, resolved.DatabaseSha256, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(resolved.Catalog.DeepSky.Objects.Any(static item => item.Id.StartsWith("IC", StringComparison.Ordinal)));
            Assert.IsTrue(resolved.Catalog.DeepSky.Objects.Any(static item => item.Id.StartsWith("NGC", StringComparison.Ordinal)));
            Assert.IsTrue(resolved.Catalog.DeepSky.Objects.Any(static item => item.MessierNumber == 45));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void StandaloneSnapshotContainsNoStarsAndPreservesDeepSkyFactsAndIdentity()
    {
        using var fixture = new Snapshot();
        var result = fixture.Resolve();
        Assert.AreEqual(0, result.Catalog.ObjectCount);
        Assert.AreEqual(10, result.RowCount);
        Assert.IsNotNull(result.Catalog.DeepSky);
        Assert.AreEqual(10, result.Catalog.DeepSky.Objects.Count);
        Assert.IsTrue(result.Catalog.DeepSky.TryGetObject("Mel022", out var pleiades));
        Assert.AreEqual(45, pleiades.MessierNumber);
        Assert.AreEqual(Snapshot.CatalogId, result.Catalog.DeepSky.Semantics.SnapshotIdentity?.CatalogId);
        Assert.AreEqual(result.DatabaseSha256, result.Catalog.DeepSky.Semantics.SnapshotIdentity?.DatabaseSha256);
        Assert.IsNull(ApprovedCatalogSpecifications.Get("openngc-production").Topology);
        Assert.IsNull(ApprovedCatalogSpecifications.Get("openngc-production").Composition!.Stars);
    }

    [TestMethod]
    [DataRow("CREATE TABLE celestial_objects (id TEXT)")]
    [DataRow("DROP TABLE deep_sky_aliases")]
    [DataRow("UPDATE catalog_metadata SET value='4' WHERE key='schema_version'")]
    public void StandaloneLayoutRejectsMixedMissingOrMisidentifiedData(string mutation)
    {
        using var fixture = new Snapshot();
        fixture.Mutate(mutation);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Resolve());
    }

    [TestMethod]
    public void StandaloneResolutionRejectsChangedBytesAndWrongCatalogIdentity()
    {
        using var fixture = new Snapshot();
        Assert.ThrowsExactly<InvalidDataException>(() => CatalogSnapshotResolver.Resolve(new(fixture.Root, "hyg-v42-fixture")
        { ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture }));
        var bytes = File.ReadAllBytes(fixture.Database);
        bytes[^1] ^= 1;
        File.WriteAllBytes(fixture.Database, bytes);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Resolve());
    }

    [TestMethod]
    public void IndependentRuntimeSelectionKeepsTheStarCatalogAndSearchesDeepSky()
    {
        using var fixture = new Snapshot();
        using var stars = CatalogSnapshotResolverTests.CreateInstallation();
        var values = new Dictionary<string, string?>
        {
            ["Catalog:Root"] = stars.Root,
            ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
            ["Catalog:RequiredPackageKind"] = "Fixture",
            ["DeepSkyCatalog:Enabled"] = "true",
            ["DeepSkyCatalog:Root"] = fixture.Root,
            ["DeepSkyCatalog:RequiredCatalogId"] = Snapshot.CatalogId,
            ["DeepSkyCatalog:RequiredPackageKind"] = "Fixture"
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<SqliteCelestialCatalog>>(NullLogger<SqliteCelestialCatalog>.Instance);
        services.AddSingleton<IConfiguration>(new DictionaryConfiguration(values));
        services.AddInstalledCelestialCatalog();
        using var provider = services.BuildServiceProvider();
        var selected = provider.GetRequiredService<ICelestialCatalog>();
        var original = provider.GetRequiredService<SqliteCelestialCatalog>();
        CollectionAssert.AreEqual(original.Query(new(20, 100)).ToArray(), selected.Query(new(20, 100)).ToArray());
        Assert.AreSame(original.Metadata, ((ICelestialCatalogMetadataSource)selected).Metadata);
        Assert.IsNotNull(provider.GetRequiredService<IDeepSkyCatalogSource>().DeepSky);
        Assert.IsNull(original.DeepSky);
        Assert.IsTrue(provider.GetRequiredService<IDeepSkyCatalogSource>().DeepSky!.FindByAlias("M45").Count > 0);
    }

    [TestMethod]
    public void DesignationUnionIncludesStandaloneMessierAndIcAliasesWithoutDuplicates()
    {
        using var fixture = new Snapshot();
        var source = fixture.Resolve().Catalog.DeepSky!;
        var objects = source.Objects.ToArray();
        var aliases = objects.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))
            .Append(new("IC 1", "NGC0224", DeepSkyAliasKinds.CrossReference));
        var catalog = new DeepSkyCatalog(source.Semantics, objects, aliases, [], []);
        var messier = catalog.Query(new(100, Designations: DeepSkyDesignations.Messier));
        Assert.IsTrue(messier.Objects.Any(static item => item.Id == "Mel022"));
        var ic = catalog.Query(new(100, Designations: DeepSkyDesignations.Ic));
        Assert.HasCount(1, ic.Objects);
        Assert.AreEqual("NGC0224", ic.Objects[0].Id);
        var combined = catalog.Query(new(100, Designations: DeepSkyDesignations.All));
        Assert.AreEqual(combined.Objects.Count, combined.Objects.Select(static item => item.Id).Distinct().Count());
        Assert.AreEqual(0, catalog.Query(new(100, Designations: DeepSkyDesignations.None)).MatchCount);
    }

    [TestMethod]
    public void QueryBoundsPreserveUnknownValuesAndCountMatchesBeforeTruncation()
    {
        using var fixture = new Snapshot();
        var catalog = fixture.Resolve().Catalog.DeepSky!;
        var known = catalog.Objects.First(static item => item.MajorAxisArcminutes is not null && item.VMagnitude is not null);
        var exact = catalog.Query(new(100, MinimumSizeArcminutes: known.MajorAxisArcminutes,
            MaximumSizeArcminutes: known.MajorAxisArcminutes, IncludeUnknownSize: false,
            Brightness: new(DeepSkyMagnitudeBand.Visual, known.VMagnitude!.Value, false)));
        Assert.IsTrue(exact.Objects.Contains(known));
        Assert.IsTrue(exact.Objects.All(static item => item.MajorAxisArcminutes is not null && item.VMagnitude is not null));
        var bounded = catalog.Query(new(1));
        Assert.AreEqual(10, bounded.MatchCount);
        Assert.IsTrue(bounded.Truncated);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => catalog.Query(new(1, MinimumSizeArcminutes: double.NaN)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => catalog.Query(new(1, MinimumSizeArcminutes: 2, MaximumSizeArcminutes: 1)));
    }

    private sealed class DictionaryConfiguration(IReadOnlyDictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key] { get => values.GetValueOrDefault(key); set => throw new NotSupportedException(); }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }

    private sealed class Snapshot : IDisposable
    {
        public const string CatalogId = "openngc-fixture";
        private const string Version = "openngc-fixture-1";
        private static readonly CatalogDeepSkyCounts Counts = new(10, 69, 2, 4, 11, 14, 3357);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "versions", Version, "openngc.sqlite");

        public Snapshot()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v44-openngc-subset.sqlite"), Database);
            Mutate("""
                DROP TABLE celestial_objects;
                DROP TABLE celestial_object_aliases;
                DELETE FROM catalog_metadata WHERE key NOT LIKE 'deep_sky_%';
                INSERT INTO catalog_metadata VALUES ('name','OpenNGC-only test fixture'),('catalog_version','v20260501-fixture.1'),
                    ('schema_version','5'),('preprocessing_version','6'),('source_url','https://github.com/mattiaverga/OpenNGC'),('license','CC BY-SA 4.0');
                PRAGMA user_version=5;
                """);
            Directory.CreateSymbolicLink(Path.Combine(Root, "current"), $"versions/{Version}");
        }

        [SuppressMessage("Security", "CA2100", Justification = "Only fixed SQL literals from this disposable test fixture are executed.")]
        public void Mutate(string sql)
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            var manifest = new
            {
                manifestVersion = 4,
                package = new { kind = "fixture", version = Version },
                catalog = new { id = CatalogId, name = "OpenNGC-only test fixture", version = "v20260501-fixture.1" },
                schemaVersion = "5",
                preprocessingVersion = "6",
                database = new
                {
                    relativePath = "openngc.sqlite",
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Database))),
                    length = new FileInfo(Database).Length,
                    rowCount = 10,
                    deepSky = Counts
                }
            };
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Database)!, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
        }

        public CatalogSnapshotResult Resolve() => CatalogSnapshotResolver.Resolve(new(Root, CatalogId)
        { ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture });
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
