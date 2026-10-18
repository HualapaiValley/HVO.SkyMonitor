using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>
/// <c>create-release</c>: a release's files in one folder (install contract section 4, RoofController's
/// <c>release.json</c> schema 1), from the image archive the release image was built into and is pushed from, the
/// installers CI published, the approved catalog bundle and the upgrade notes:
/// <list type="bullet">
/// <item><c>hvo-sky-install-linux-x64</c> and <c>hvo-sky-install-linux-arm64</c>, the installers;</item>
/// <item><c>hvo-sky-linux-x64</c> and <c>hvo-sky-linux-arm64</c>, the runtime client (#1317), when given: both or neither;</item>
/// <item><c>skymonitor-cameraagent-&lt;version&gt;-linux-&lt;arch&gt;.oci.tar</c>, an offline image archive per platform;</item>
/// <item><c>&lt;catalog version&gt;.bundle.tar.gz</c>, the catalog bundle, under its own version;</item>
/// <item><c>install.sh</c>, when given, with the release's version written in (<see cref="StampInstallScript"/>);</item>
/// <item><c>release.json</c>, and <c>SHA256SUMS</c> over every file.</item>
/// </list>
/// release.json names the image by its key, <c>images.cameraAgent</c>, and repeats the image's state contract label as
/// <c>stateContracts.cameraAgent</c> (install contract sections 4 and 6).
/// Upgrade notes pass the installer's own terminal-safety rule (<see cref="UpgradeNoteText"/>) here, so a note that
/// would stop every upgrade on site stops the release instead.
/// </summary>
internal static partial class Program
{
    internal const string ReleaseProduct = "HVO SkyMonitor";
    internal const string ReleaseRepositoryUrl = "https://github.com/HualapaiValley/HVO.SkyMonitor";
    internal const string CameraAgentImageName = "skymonitor-cameraagent";

    /// <summary>The CameraAgent image's key under release.json's <c>images</c> and <c>stateContracts</c> (decision D6).</summary>
    internal const string CameraAgentImageKey = "cameraAgent";

    /// <summary>release.json's <c>created</c>, which is also the image's <c>org.opencontainers.image.created</c> label.</summary>
    internal const string CreatedFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";
    internal const string DefaultCameraAgentRepository = "ghcr.io/hualapaivalley/skymonitor-cameraagent";

    /// <summary>The longest upgrade note a release carries; the installer prints every note whole before it upgrades.</summary>
    internal const int MaximumUpgradeNoteLength = 16 * 1024;

    /// <summary>The longest that all the upgrade notes of a release may be together.</summary>
    internal const int MaximumUpgradeNotesLength = 128 * 1024;

    private static readonly string[] ReleaseImagePlatforms = ["linux/amd64", "linux/arm64"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static async Task CreateReleaseAsync(Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--version", "--commit", "--created-utc", "--image-repository", "--image-archive", "--platforms",
            "--installer-linux-x64", "--installer-linux-arm64", "--cli-linux-x64", "--cli-linux-arm64", "--catalog-bundle",
            "--install-script", "--upgrade-notes", "--output");
        var version = RequireReleaseVersion(options, "--version");
        var commit = RequireGitOid(options, "--commit");
        var created = RequireUtc(options, "--created-utc");
        created = new DateTimeOffset(created.Year, created.Month, created.Day, created.Hour, created.Minute, created.Second, TimeSpan.Zero);
        var repository = options.ContainsKey("--image-repository")
            ? RequireCameraAgentRepository(options, "--image-repository")
            : DefaultCameraAgentRepository;
        var platforms = ReleasePlatforms(options);
        var imageArchive = RequireExistingFile(options, "--image-archive");
        var x64 = RequireExistingFile(options, "--installer-linux-x64");
        var arm64 = RequireExistingFile(options, "--installer-linux-arm64");
        // The runtime client (install contract section 4, kind cli) is given for both platforms or for neither.
        if (options.ContainsKey("--cli-linux-x64") != options.ContainsKey("--cli-linux-arm64"))
        {
            throw new ReleaseToolException("--cli-linux-x64 and --cli-linux-arm64 are given together, or not at all.");
        }
        var clients = options.ContainsKey("--cli-linux-x64")
            ? new[] { ("linux-arm64", RequireExistingFile(options, "--cli-linux-arm64")), ("linux-x64", RequireExistingFile(options, "--cli-linux-x64")) }
            : [];
        var catalogBundle = options.ContainsKey("--catalog-bundle") ? RequireExistingDirectory(options, "--catalog-bundle") : null;
        var installScript = options.ContainsKey("--install-script")
            ? StampInstallScript(await ReadInstallScriptAsync(RequireExistingFile(options, "--install-script"), cancellationToken)
                .ConfigureAwait(false), version)
            : null;
        var notes = options.ContainsKey("--upgrade-notes")
            ? ReadUpgradeNotes(RequireExistingDirectory(options, "--upgrade-notes"), version)
            : [];
        ValidateElf(x64, expectedMachine: 0x3e, "linux-x64");
        ValidateElf(arm64, expectedMachine: 0xb7, "linux-arm64");
        foreach (var (runtime, client) in clients)
        {
            ValidateElf(client, expectedMachine: runtime == "linux-x64" ? (ushort)0x3e : (ushort)0xb7, runtime, "hvo-sky client");
        }

        // Every input has been read and checked; a failure from here on removes the half-written release.
        var output = PrepareOutput(options);
        try
        {
            var assets = new List<JsonObject>();
            foreach (var (runtime, installer) in new[] { ("linux-arm64", arm64), ("linux-x64", x64) })
            {
                var name = $"hvo-sky-install-{runtime}";
                CopyExecutable(installer, Path.Combine(output, name));
                assets.Add(await AssetAsync(output, name, "installer", runtime, null, cancellationToken).ConfigureAwait(false));
            }
            foreach (var (runtime, client) in clients)
            {
                var name = $"hvo-sky-{runtime}";
                CopyExecutable(client, Path.Combine(output, name));
                assets.Add(await AssetAsync(output, name, "cli", runtime, null, cancellationToken).ConfigureAwait(false));
            }
            var (digest, archives, stateContracts) = await OciImageArchive.SplitAsync(
                imageArchive, CameraAgentImageName, repository, version, commit, platforms, output, created, cancellationToken)
                .ConfigureAwait(false);
            foreach (var archive in archives)
            {
                assets.Add(await AssetAsync(
                    output, archive.Name, "image-archive", archive.Platform == "linux/amd64" ? "linux-x64" : "linux-arm64", null,
                    cancellationToken).ConfigureAwait(false));
            }
            if (catalogBundle is not null)
            {
                assets.Add(await CatalogAssetAsync(catalogBundle, output, created, cancellationToken).ConfigureAwait(false));
            }
            if (installScript is not null)
            {
                var path = Path.Combine(output, "install.sh");
                await File.WriteAllTextAsync(path, installScript, StrictUtf8, cancellationToken).ConfigureAwait(false);
                SetExecutable(path);
                assets.Add(await AssetAsync(output, "install.sh", "install-script", null, null, cancellationToken).ConfigureAwait(false));
            }

            var release = ReleaseDocument(
                version, commit, created, repository, digest, platforms, archives, stateContracts, notes, assets);
            await File.WriteAllTextAsync(
                Path.Combine(output, "release.json"),
                release.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                StrictUtf8,
                cancellationToken).ConfigureAwait(false);
            var sums = new StringBuilder();
            foreach (var name in assets.Select(static asset => (string)asset["name"]!).Append("release.json").Order(StringComparer.Ordinal))
            {
                sums.Append(CultureInfo.InvariantCulture, $"{await Sha256Async(Path.Combine(output, name), cancellationToken).ConfigureAwait(false)}  {name}\n");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "SHA256SUMS"), sums.ToString(), StrictUtf8, cancellationToken)
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(digest).ConfigureAwait(false);
        }
        catch
        {
            Directory.Delete(output, recursive: true);
            throw;
        }
    }

    private static JsonObject ReleaseDocument(
        string version,
        string commit,
        DateTimeOffset created,
        string repository,
        string digest,
        IReadOnlyList<string> platforms,
        IReadOnlyList<OfflineImageArchive> archives,
        JsonObject stateContracts,
        IReadOnlyList<(string Version, string Text)> notes,
        IReadOnlyList<JsonObject> assets)
        => new()
        {
            ["schemaVersion"] = 1,
            ["product"] = ReleaseProduct,
            ["version"] = version,
            ["tag"] = $"v{version}",
            ["prerelease"] = ProductVersion.IsPrerelease(version),
            ["commit"] = commit,
            ["created"] = created.ToString(CreatedFormat, CultureInfo.InvariantCulture),
            ["repository"] = ReleaseRepositoryUrl,
            ["images"] = new JsonObject
            {
                [CameraAgentImageKey] = new JsonObject
                {
                    ["repository"] = repository,
                    ["tag"] = version,
                    ["digest"] = digest,
                    ["reference"] = $"{repository}:{version}@{digest}",
                    ["platforms"] = new JsonArray([.. platforms.Select(static platform => (JsonNode)platform)]),
                    ["offlineArchives"] = new JsonArray([.. archives.Select(static archive => (JsonNode)new JsonObject
                    {
                        ["name"] = archive.Name,
                        ["platform"] = archive.Platform,
                        ["size"] = archive.Size,
                        ["sha256"] = archive.Sha256,
                        ["manifestDigest"] = archive.ManifestDigest,
                        ["configDigest"] = archive.ConfigDigest
                    })])
                }
            },
            ["stateContracts"] = new JsonObject { [CameraAgentImageKey] = stateContracts },
            ["upgradeNotes"] = new JsonArray([.. notes.Select(static note => (JsonNode)new JsonObject
            {
                ["version"] = note.Version,
                ["text"] = note.Text
            })]),
            ["assets"] = new JsonArray([.. assets.OrderBy(static asset => (string)asset["name"]!, StringComparer.Ordinal)])
        };

    private static async Task<JsonObject> AssetAsync(
        string output,
        string name,
        string kind,
        string? platform,
        JsonObject? files,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(output, name);
        var asset = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["platform"] = platform,
            ["size"] = new FileInfo(path).Length,
            ["sha256"] = await Sha256Async(path, cancellationToken).ConfigureAwait(false)
        };
        if (files is not null)
        {
            asset["files"] = files;
        }
        return asset;
    }

    /// <summary>
    /// The approved catalog bundle as its own versioned archive, <c>&lt;package version&gt;.bundle.tar.gz</c>: staged
    /// through the approved snapshot resolver exactly as a catalog release is, with the SHA-256 of each file in it.
    /// </summary>
    private static async Task<JsonObject> CatalogAssetAsync(
        string source,
        string output,
        DateTimeOffset created,
        CancellationToken cancellationToken)
    {
        var staging = Directory.CreateTempSubdirectory("hvo-release-catalog-");
        try
        {
            var bundle = StageValidatedCatalogBundle(source, staging.FullName);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(bundle, "manifest.json"), cancellationToken).ConfigureAwait(false));
            var packageVersion = manifest.RootElement.GetProperty("package").GetProperty("version").GetString()!;
            var specification = ReleaseCatalogs.Get(
                manifest.RootElement.GetProperty("catalog").GetProperty("id").GetString(), packageVersion);
            var name = $"{packageVersion}.bundle.tar.gz";
            await WriteCatalogArchiveAsync(bundle, Path.Combine(output, name), packageVersion, specification, created, cancellationToken)
                .ConfigureAwait(false);
            var files = new JsonObject();
            foreach (var file in ReleaseCatalogs.OrderedFiles(specification))
            {
                files[file] = await Sha256Async(Path.Combine(bundle, file), cancellationToken).ConfigureAwait(false);
            }
            return await AssetAsync(output, name, "catalog", null, files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Every release's upgrade notes in <paramref name="folder"/> (docs/upgrade-notes, X.Y.Z.md files alone) up to the
    /// release <paramref name="version"/> belongs to, oldest first: a release candidate or pre-release carries its final
    /// release's notes. Each must be UTF-8 text that, after CRLF becomes LF, holds no scalar the installer refuses to
    /// print to a terminal (<see cref="UpgradeNoteText"/>). Any violation fails the release, naming the file and the
    /// code point, because the installer would refuse the release.json, and every later one, on every host.
    /// </summary>
    internal static IReadOnlyList<(string Version, string Text)> ReadUpgradeNotes(string folder, string version)
    {
        var release = ProductVersion.Release(version);
        var notes = new List<(string Version, string Text)>();
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (!UpgradeNoteFileName().IsMatch(entry.Name) || entry is not FileInfo || entry.LinkTarget is not null)
            {
                throw new ReleaseToolException($"{folder} must hold X.Y.Z.md files alone; {entry.Name} is not one.");
            }
            var noteVersion = entry.Name[..^".md".Length];
            if (ProductVersion.IsOlder(release, noteVersion))
            {
                continue;
            }
            notes.Add((noteVersion, ReadUpgradeNote(entry.FullName, noteVersion)));
        }
        var total = notes.Sum(static note => note.Text.Length);
        if (total > MaximumUpgradeNotesLength)
        {
            throw new ReleaseToolException(
                $"The upgrade notes up to {version} hold {total} characters together; the installer prints them whole, so keep " +
                $"them under {MaximumUpgradeNotesLength}.");
        }
        return [.. notes.OrderBy(static note => note.Version, Comparer<string>.Create(ProductVersion.Compare))];
    }

    private static string ReadUpgradeNote(string path, string version)
    {
        if (new FileInfo(path).Length > 4 * MaximumUpgradeNoteLength)
        {
            throw new ReleaseToolException($"The upgrade notes for {version} are longer than {MaximumUpgradeNoteLength} characters.");
        }
        string text;
        try
        {
            // Decoded as it is, a byte-order mark included: the installer reads release.json's text, not the file's.
            text = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseToolException($"The upgrade notes for {version} are not UTF-8 text.", exception);
        }
        var lines = UpgradeNoteText.NormalizeLineEndings(text);
        if (UpgradeNoteText.FindUnsafeCodePoint(lines) is { } codePoint)
        {
            throw new ReleaseToolException(
                $"The upgrade notes for {version} contain {codePoint}, which the installer refuses to print to a terminal " +
                "(control, format, separator, private-use or noncharacter): remove it.");
        }
        lines = lines.Trim();
        if (lines.Length == 0)
        {
            throw new ReleaseToolException($"The upgrade notes for {version} are empty.");
        }
        if (lines.Length > MaximumUpgradeNoteLength)
        {
            throw new ReleaseToolException($"The upgrade notes for {version} are longer than {MaximumUpgradeNoteLength} characters.");
        }
        return lines;
    }

    private static async Task<string> ReadInstallScriptAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return StrictUtf8.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseToolException("--install-script is not UTF-8 text.", exception);
        }
    }

    /// <summary>
    /// <c>install.sh</c> as a release publishes it (install contract section 5): the repository's copy, whose one
    /// <c>  local release_version=''</c> line, with or without a trailing comment, becomes
    /// <c>  local release_version='&lt;version&gt;'</c>. The repository's copy names no release and refuses to run; a
    /// release's copy installs that release. A script without exactly one such line is refused, so a release never
    /// carries an <c>install.sh</c> that names no release, or two. The version is SemVer without build metadata, so it
    /// needs no quoting inside the single quotes.
    /// </summary>
    internal static string StampInstallScript(string text, string version)
    {
        var lines = InstallScriptVersionLine().Count(text);
        return lines == 1
            ? InstallScriptVersionLine().Replace(text, $"  local release_version='{version}'")
            : throw new ReleaseToolException(
                $"--install-script must name its release on one line, \"  local release_version=''\", and has {lines}.");
    }

    /// <summary>A release's version: SemVer without build metadata, which tags, the image tag and file names all carry.</summary>
    private static string RequireReleaseVersion(Dictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return ProductVersion.IsValid(value) && !value.Contains('+', StringComparison.Ordinal) && value.Length <= 128
            ? value
            : throw new ReleaseToolException($"{name} must be a release version such as 1.2.0 or 1.2.0-dev.3.");
    }

    private static string RequireCameraAgentRepository(Dictionary<string, string> options, string name)
    {
        var value = RequireImageRepository(options, name);
        var registry = value[..value.IndexOf('/', StringComparison.Ordinal)];
        return value.EndsWith("/" + CameraAgentImageName, StringComparison.Ordinal) &&
               (registry.Contains('.', StringComparison.Ordinal) || registry.Contains(':', StringComparison.Ordinal) ||
                registry == "localhost")
            ? value
            : throw new ReleaseToolException(
                $"{name} must name a registry and end in /{CameraAgentImageName}, such as {DefaultCameraAgentRepository}.");
    }

    private static string[] ReleasePlatforms(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--platforms", out var value))
        {
            return ReleaseImagePlatforms;
        }
        var platforms = value.Split(',');
        return platforms.Length > 0 && platforms.All(platform => ReleaseImagePlatforms.Contains(platform, StringComparer.Ordinal)) &&
               platforms.Distinct(StringComparer.Ordinal).Count() == platforms.Length
            ? [.. ReleaseImagePlatforms.Where(platforms.Contains)]
            : throw new ReleaseToolException("--platforms must list linux/amd64 and linux/arm64, or one of them, once each.");
    }

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static void CopyExecutable(string source, string destination)
    {
        File.Copy(source, destination);
        SetExecutable(destination);
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, ExecutableMode);
        }
    }

    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex UpgradeNoteFileName();

    [GeneratedRegex(@"^  local release_version=''(?:  #[^\n]*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InstallScriptVersionLine();
}
