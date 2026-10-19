using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>
/// The release tool. Its one command, <c>create-release</c> (Program.Release.cs), writes a release's files as
/// scripts/release:assets builds them (install contract section 4). A release's trust is SHA256SUMS and the build
/// provenance attestations the release workflow makes; the key-signed manifests and indexes, and the commands that made
/// them, were retired by the owner on 2026-10-16 (#1310).
/// </summary>
internal static partial class Program
{
    // Matches the catalog snapshot resolver's manifest bound; the release tool reads at most one byte past it.
    internal const int MaximumCatalogManifestLength = 65_536;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ReleaseToolException("A release-tool command is required.");
            }
            var options = ParseOptions(args[1..]);
            switch (args[0])
            {
                case "create-release":
                    await CreateReleaseAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    throw new ReleaseToolException($"Unknown release-tool command '{args[0]}'.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is ReleaseToolException or IOException or UnauthorizedAccessException or
                                           JsonException)
        {
            await Console.Error.WriteLineAsync($"release error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Copies the bundle's exact registry file set into a private staging root and resolves it through the approved
    /// snapshot resolver, which checks the manifest, database bytes, schema, preprocessing, row count and retained
    /// notices against the registry. Every release artifact is then built from the staged copy, so the archive
    /// carries exactly the bytes that were validated and a refused bundle produces no release output. Each original
    /// input is read only through a handle that proved it is a regular, singly linked file, and the manifest is staged
    /// from the same bytes that were parsed.
    /// </summary>
    private static string StageValidatedCatalogBundle(string source, string stagingRoot)
    {
        byte[] manifestBytes;
        using (var manifestSource = AuthenticateCatalogBundleFile(source, "manifest.json"))
        {
            manifestBytes = ReadCatalogManifest(manifestSource.Stream);
        }
        using var inner = JsonDocument.Parse(manifestBytes);
        var root = inner.RootElement;
        var packageVersion = root.GetProperty("package").GetProperty("version").GetString()
            ?? throw new ReleaseToolException("The catalog manifest omits package.version.");
        ValidateSafeIdentifier(packageVersion, "catalog package version");
        var catalogId = root.GetProperty("catalog").GetProperty("id").GetString()
            ?? throw new ReleaseToolException("The catalog manifest omits catalog.id.");
        var specification = ReleaseCatalogs.Get(catalogId, packageVersion);
        var expected = ReleaseCatalogs.OrderedFiles(specification);
        var entries = new DirectoryInfo(source).EnumerateFileSystemInfos().ToArray();
        if (!entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal) ||
            entries.Any(static entry => entry is not FileInfo || entry.LinkTarget is not null))
        {
            throw new ReleaseToolException("The catalog bundle does not contain the exact release file set.");
        }
        var staged = Path.Combine(stagingRoot, "versions", packageVersion);
        Directory.CreateDirectory(staged);
        foreach (var name in expected.Where(static name => name != "manifest.json"))
        {
            using var input = AuthenticateCatalogBundleFile(source, name);
            using var destination = new FileStream(Path.Combine(staged, name), FileMode.CreateNew, FileAccess.Write);
            input.Stream.CopyTo(destination);
        }
        using (var destination = new FileStream(Path.Combine(staged, "manifest.json"), FileMode.CreateNew, FileAccess.Write))
        {
            destination.Write(manifestBytes);
        }
        try
        {
            _ = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(stagingRoot, specification.CatalogId)
            {
                ExpectedPackageKind = CatalogSnapshotPackageKind.Production,
                ExpectedPackageVersion = packageVersion
            });
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                           System.Data.Common.DbException)
        {
            throw new ReleaseToolException(
                $"The catalog bundle does not match its approved specification: {exception.Message}", exception);
        }
        return staged;
    }

    /// <summary>
    /// Reads the authenticated manifest handle once, never past one byte beyond the limit. Authentication fixes the
    /// file's identity, not its length, so the bound is enforced on the bytes actually read rather than on a length
    /// reported before the read.
    /// </summary>
    internal static byte[] ReadCatalogManifest(Stream manifest)
    {
        var buffer = new byte[MaximumCatalogManifestLength + 1];
        var length = manifest.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (length > MaximumCatalogManifestLength)
        {
            throw new ReleaseToolException("The catalog manifest exceeds its maximum length.");
        }
        return buffer[..length];
    }

    /// <summary>
    /// Opens one original bundle input through the catalog resolver's no-follow, non-blocking open and accepts it only
    /// when both its handle and its path identify the same regular file with exactly one link, so a FIFO, device,
    /// socket, symbolic link or hard link planted under a registry name is refused before any byte is read.
    /// </summary>
    private static CatalogSnapshotResolver.AuthenticatedFile AuthenticateCatalogBundleFile(string source, string name)
    {
        try
        {
            return CatalogSnapshotResolver.AuthenticateFile(Path.Combine(source, name), $"Catalog bundle file '{name}'");
        }
        catch (InvalidDataException exception)
        {
            throw new ReleaseToolException(exception.Message, exception);
        }
    }

    private static async Task WriteCatalogArchiveAsync(
        string bundle,
        string output,
        string packageVersion,
        ApprovedCatalogContract specification,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var expected = ReleaseCatalogs.OrderedFiles(specification);
        var actual = Directory.EnumerateFiles(bundle).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ReleaseToolException("The catalog bundle does not contain the exact release file set.");
        }
        RefuseExisting(output);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var name in expected)
        {
            await WriteTarFileAsync(
                tar,
                Path.Combine(bundle, name),
                $"{packageVersion}.bundle/{name}",
                0x124,
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTarFileAsync(
        TarWriter tar,
        string source,
        string name,
        int mode,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(source);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream,
            Gid = 0,
            GroupName = "root",
            Mode = (UnixFileMode)mode,
            ModificationTime = timestamp,
            Uid = 0,
            UserName = "root"
        };
        await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An image repository reference: a lowercase registry host and at least one path segment, without a tag or digest.
    /// </summary>
    private static string RequireImageRepository(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return ImageRepositoryRegex().IsMatch(value)
            ? value
            : throw new ReleaseToolException(
                $"{name} must be a lowercase OCI repository reference with a host and at least one path segment, " +
                "and without a tag or digest.");
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "^[a-z0-9][a-z0-9.-]{0,63}(:[0-9]{1,5})?(/[a-z0-9]+([._-][a-z0-9]+)*){1,6}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ImageRepositoryRegex();

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(args[index], args[index + 1]))
            {
                throw new ReleaseToolException("Release-tool options must be unique --name value pairs.");
            }
        }
        return options;
    }

    private static void RejectUnknown(IReadOnlyDictionary<string, string> options, params string[] allowed)
    {
        var known = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = options.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null)
        {
            throw new ReleaseToolException($"Unknown option '{unknown}'.");
        }
    }

    private static string PrepareOutput(IReadOnlyDictionary<string, string> options)
    {
        var output = Path.GetFullPath(Require(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new ReleaseToolException($"Release output '{output}' already exists.");
        }
        Directory.CreateDirectory(output);
        return output;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ReleaseToolException($"{name} is required.");

    private static string RequireExistingFile(IReadOnlyDictionary<string, string> options, string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        return File.Exists(path) ? path : throw new ReleaseToolException($"{name} must identify an existing file.");
    }

    private static string RequireExistingDirectory(IReadOnlyDictionary<string, string> options, string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        return Directory.Exists(path) ? path : throw new ReleaseToolException($"{name} must identify an existing directory.");
    }

    private static string RequireGitOid(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return value.Length == 40 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ReleaseToolException($"{name} must be a lowercase 40-character Git object ID.");
    }

    private static void ValidateSafeIdentifier(string value, string name)
    {
        if (value.Length is < 1 or > 128 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ReleaseToolException($"{name} must be a safe release identifier containing only ASCII letters, digits, periods, and hyphens.");
        }
    }

    private static DateTimeOffset RequireUtc(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return value.EndsWith('Z') && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new ReleaseToolException($"{name} must be an ISO-8601 UTC timestamp.");
    }

    private static void ValidateElf(string path, ushort expectedMachine, string architecture, string what = "installer")
    {
        Span<byte> header = stackalloc byte[20];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("\u007fELF"u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]) != expectedMachine)
        {
            throw new ReleaseToolException($"The {architecture} {what} does not have the expected ELF identity.");
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void RefuseExisting(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ReleaseToolException($"Release output '{path}' already exists.");
        }
    }
}

internal sealed class ReleaseToolException : Exception
{
    public ReleaseToolException()
    {
    }

    public ReleaseToolException(string message)
        : base(message)
    {
    }

    public ReleaseToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
