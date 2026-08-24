using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

internal static class CatalogInstaller
{
    private static readonly HashSet<string> ExpectedFiles = new(StringComparer.Ordinal)
    {
        "manifest.json", "hyg_v42.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md"
    };

    public static CatalogInstallationIdentity Install(string bundlePath, string installRoot, Guid installationId)
    {
        ValidateBundleEntries(bundlePath);
        var packageVersion = ReadPackageVersion(bundlePath);
        SafeFileSystem.EnsureSafeExistingAncestors(installRoot);
        SafeFileSystem.CreateOwnerDirectory(installRoot);
        var versionsRoot = Path.Combine(installRoot, "versions");
        SafeFileSystem.CreateOwnerDirectory(versionsRoot);
        using var catalogLock = OperationLock.Acquire(Path.Combine(installRoot, ".catalog.lock"));
        EnsureLineage(installRoot, versionsRoot);

        var candidateRoot = Path.Combine(installRoot, $".candidate-{installationId:N}");
        if (Directory.Exists(candidateRoot))
        {
            Directory.Delete(candidateRoot, recursive: true);
        }

        try
        {
            var candidateVersion = Path.Combine(candidateRoot, "versions", packageVersion);
            CopyBundle(bundlePath, candidateVersion);
            var candidate = CatalogSnapshotResolver.Resolve(ProductionCatalog.ResolverOptions(candidateRoot, packageVersion));

            var installedVersion = Path.Combine(versionsRoot, packageVersion);
            if (Directory.Exists(installedVersion))
            {
                ValidateInstalledVersion(installedVersion, candidateVersion);
                Directory.Delete(candidateVersion, recursive: true);
            }
            else
            {
                Directory.Move(candidateVersion, installedVersion);
                SetReadOnly(installedVersion);
                NativeLinux.FlushDirectory(versionsRoot);
            }

            EnsureLegacyCurrentPointer(installRoot, packageVersion);
            NativeLinux.FlushDirectory(installRoot);
            var installed = CatalogSnapshotResolver.Resolve(ProductionCatalog.ResolverOptions(installRoot, packageVersion));
            return ProductionCatalog.ToIdentity(installed, installRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InstallerException("The catalog bundle could not be installed or validated.", exception);
        }
        finally
        {
            if (Directory.Exists(candidateRoot))
            {
                Directory.Delete(candidateRoot, recursive: true);
            }
        }
    }

    public static CatalogInstallationIdentity ValidateExisting(string bundlePath, string installRoot)
    {
        ValidateBundleEntries(bundlePath);
        var packageVersion = ReadPackageVersion(bundlePath);
        var lineagePath = Path.Combine(installRoot, ".catalog-lineage.json");
        using (var stream = SafeFileSystem.OpenOwnerFileRead(lineagePath))
        {
            var lineage = JsonSerializer.Deserialize(stream, CatalogLineageJsonContext.Default.CatalogLineage)
                ?? throw new InstallerException("The catalog lineage binding is empty.");
            if (lineage != ExpectedLineage())
            {
                throw new InstallerException("The catalog lineage binding does not match the production catalog.");
            }
        }

        var installedVersion = Path.Combine(installRoot, "versions", packageVersion);
        ValidateInstalledVersion(installedVersion, bundlePath);
        var installed = CatalogSnapshotResolver.Resolve(ProductionCatalog.ResolverOptions(installRoot, packageVersion));
        return ProductionCatalog.ToIdentity(installed, installRoot);
    }

    private static void ValidateBundleEntries(string bundlePath)
    {
        var directory = new DirectoryInfo(bundlePath);
        directory.Refresh();
        if (!directory.Exists || directory.LinkTarget is not null)
        {
            throw new InstallerException("The catalog bundle must be an existing directory, not a link.");
        }

        var entries = directory.EnumerateFileSystemInfos().ToArray();
        if (entries.Length != ExpectedFiles.Count || entries.Any(entry => !ExpectedFiles.Contains(entry.Name)))
        {
            throw new InstallerException("The catalog bundle does not contain the exact production file set.");
        }

        foreach (var entry in entries)
        {
            entry.Refresh();
            if (entry is not FileInfo || entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstallerException($"Catalog bundle entry '{entry.Name}' must be a regular file.");
            }
        }
    }

    private static void CopyBundle(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        File.SetUnixFileMode(destinationRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var name in ExpectedFiles)
        {
            var destination = Path.Combine(destinationRoot, name);
            using var source = SafeFileSystem.OpenRegularFileRead(Path.Combine(sourceRoot, name));
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            source.CopyTo(output);
#pragma warning disable CA1849 // Catalog staging requires a flush-to-disk boundary.
            output.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
        NativeLinux.FlushDirectory(destinationRoot);
    }

    private static void ValidateInstalledVersion(string installedVersion, string candidateVersion)
    {
        ValidateBundleEntries(installedVersion);
        foreach (var name in ExpectedFiles)
        {
            var installedPath = Path.Combine(installedVersion, name);
            var candidatePath = Path.Combine(candidateVersion, name);
            if (new FileInfo(installedPath).Length != new FileInfo(candidatePath).Length ||
                !CryptographicOperations.FixedTimeEquals(HashFile(installedPath), HashFile(candidatePath)))
            {
                throw new InstallerException("The existing catalog package identity does not match the supplied bundle.");
            }
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = SafeFileSystem.OpenRegularFileRead(path);
        return SHA256.HashData(stream);
    }

    private static void EnsureLegacyCurrentPointer(string installRoot, string packageVersion)
    {
        var current = Path.Combine(installRoot, "current");
        if (File.Exists(current) || Directory.Exists(current))
        {
            return;
        }

        var pending = Path.Combine(installRoot, $".current-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(pending, $"versions/{packageVersion}");
        Directory.Move(pending, current);
        NativeLinux.FlushDirectory(installRoot);
    }

    private static void SetReadOnly(string versionRoot)
    {
        foreach (var file in Directory.EnumerateFiles(versionRoot))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.SetUnixFileMode(
            versionRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void EnsureLineage(string installRoot, string versionsRoot)
    {
        var path = Path.Combine(installRoot, ".catalog-lineage.json");
        if (File.Exists(path))
        {
            using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            var lineage = JsonSerializer.Deserialize(stream, CatalogLineageJsonContext.Default.CatalogLineage)
                ?? throw new InstallerException("The catalog lineage binding is empty.");
            if (lineage != ExpectedLineage())
            {
                throw new InstallerException("The catalog lineage binding does not match the production catalog.");
            }
            return;
        }

        if (Directory.EnumerateFileSystemEntries(versionsRoot).Any() ||
            File.Exists(Path.Combine(installRoot, "current")) || Directory.Exists(Path.Combine(installRoot, "current")))
        {
            throw new InstallerException("An existing catalog root without a lineage binding cannot be adopted.");
        }

        var temporary = Path.Combine(installRoot, $".catalog-lineage.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, ExpectedLineage(), CatalogLineageJsonContext.Default.CatalogLineage);
#pragma warning disable CA1849 // A lineage publication requires a flush-to-disk boundary.
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
        File.Move(temporary, path);
    }

    private static CatalogLineage ExpectedLineage()
        => new(1, ProductionCatalog.CatalogId, "production", "hyg-v42-production-p3-s2");

    private static string ReadPackageVersion(string bundlePath)
    {
        using var stream = SafeFileSystem.OpenRegularFileRead(Path.Combine(bundlePath, "manifest.json"));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException)
        {
            throw new InstallerException("The catalog bundle package version is invalid.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("package", out var package) ||
                package.ValueKind != JsonValueKind.Object ||
                !package.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                throw new InstallerException("The catalog bundle package version is invalid.");
            }

            var version = versionElement.GetString();
            if (version is null || !Regex.IsMatch(version, "^hyg-v4\\.2-p3-s2-r[1-9][0-9]*$", RegexOptions.CultureInvariant))
            {
                throw new InstallerException("The catalog bundle package version is invalid.");
            }
            return version;
        }
    }
}
