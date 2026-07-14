using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>Creates an explicit installed catalog snapshot from the deterministic test fixture.</summary>
public sealed class CatalogFixtureInstallation : IDisposable
{
    private const string PackageVersion = "hyg-v42-fixture-1";
    private bool _disposed;

    private CatalogFixtureInstallation(string root)
    {
        Root = root;
    }

    /// <summary>Gets the temporary installation root.</summary>
    public string Root { get; }

    /// <summary>Creates a versioned fixture installation accepted only by explicit fixture-mode configuration.</summary>
    public static CatalogFixtureInstallation Create(string fixturePath, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        var installationRoot = root ?? Path.Combine(
            Path.GetTempPath(), $"hvo-catalog-fixture-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(installationRoot, "versions", PackageVersion);
        Directory.CreateDirectory(versionDirectory);

        var databasePath = Path.Combine(versionDirectory, "hyg_v42.sqlite");
        File.Copy(fixturePath, databasePath);
        using var source = File.OpenRead(databasePath);
        var checksum = Convert.ToHexString(SHA256.HashData(source));
        var manifest = new
        {
            manifestVersion = 1,
            package = new { kind = "fixture", version = PackageVersion },
            catalog = new { name = "HYG bright-star test fixture", version = "4.2-fixture.1" },
            schemaVersion = "2",
            preprocessingVersion = "3",
            database = new
            {
                relativePath = "hyg_v42.sqlite",
                sha256 = checksum,
                length = new FileInfo(databasePath).Length,
                rowCount = 9
            }
        };
        File.WriteAllText(
            Path.Combine(versionDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        Directory.CreateSymbolicLink(
            Path.Combine(installationRoot, "current"),
            $"versions/{PackageVersion}");
        return new CatalogFixtureInstallation(installationRoot);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
            _disposed = true;
        }
    }
}
