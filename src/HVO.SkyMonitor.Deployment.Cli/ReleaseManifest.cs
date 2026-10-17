using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// A release's <c>release.json</c> (install contract section 4, RoofController's schema 1): the release's version, tag
/// and commit, the CameraAgent image pinned to its digest with an offline archive per platform, every other asset with
/// its size and SHA-256, and the cumulative upgrade notes of every release up to it. An upgrade reads the version and the
/// notes before it changes anything; the acquisition reads the rest to select the candidate image. Members this
/// installer does not use, such as <c>product</c> and <c>stateContracts</c>, are left alone.
/// </summary>
internal sealed partial record ReleaseManifest(string Version, IReadOnlyList<UpgradeNote> UpgradeNotes)
{
    public const string FileName = "release.json";

    /// <summary>The release.json schema this installer reads.</summary>
    public const int Schema = 1;

    /// <summary>
    /// The largest release.json read. A release's own is a few kilobytes; the bound keeps a hostile or corrupt source
    /// from being parsed into memory whole.
    /// </summary>
    public const int MaximumLength = 1024 * 1024;

    /// <summary>
    /// The most upgrade notes read. A release.json carries the notes of every release that needed any, cumulatively, so
    /// a thousand outlasts a release a week for nineteen years. The bound keeps what a hostile or corrupt source packs
    /// into the size limit from costing more to read, order and print than a real release could.
    /// </summary>
    public const int MaximumUpgradeNotes = 1000;

    /// <summary>The most assets read. A release has about ten; the bound has the same purpose as the notes'.</summary>
    public const int MaximumAssets = 256;

    /// <summary>The repository every SkyMonitor release names.</summary>
    public const string RepositoryUrl = "https://github.com/HualapaiValley/HVO.SkyMonitor";

    /// <summary>The CameraAgent image's name: its key under <c>images</c>, and its repository's last segment.</summary>
    public const string CameraAgentImageName = "skymonitor-cameraagent";

    /// <summary>The kind <c>assets</c> gives an offline image archive.</summary>
    public const string ImageArchiveKind = "image-archive";

    /// <summary>The platforms a CameraAgent image is published for.</summary>
    public static readonly IReadOnlyList<string> ImagePlatforms = ["linux/amd64", "linux/arm64"];

    /// <summary>The release's tag: <c>v</c> and its version.</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>Whether the release is a pre-release, which its version decides.</summary>
    public bool Prerelease { get; init; }

    /// <summary>The 40-character commit the release was built from.</summary>
    public string Commit { get; init; } = string.Empty;

    /// <summary>When the release was built, in UTC.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>
    /// The CameraAgent image (<c>images.skymonitor-cameraagent</c>). Every release.json read by <see cref="Parse"/> has
    /// one; a manifest constructed only for its notes does not.
    /// </summary>
    public ReleaseImage? CameraAgent { get; init; }

    /// <summary>The release's files besides release.json and SHA256SUMS, each with its size and SHA-256.</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>
    /// Reads a release.json. Throws <see cref="InstallerException"/> naming what is wrong when it is not a complete
    /// schema-1 release.json of a SkyMonitor release: an unknown schema, a missing or malformed member, an image that is
    /// not pinned to a digest, or an offline archive that is not one of its assets. A note's lines end in LF, with TAB
    /// retained; <see cref="UpgradeNoteText"/> defines which other scalars are unsafe for a terminal. A message names
    /// only values this reader has already validated, never raw text from the document.
    /// </summary>
    public static ReleaseManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumLength)
        {
            throw Invalid($"it is larger than {MaximumLength} bytes");
        }
        // The JSON reader decodes only the strings it is asked for, so text that is not Unicode is refused here, wherever
        // it is in the document, rather than when, or whether, a member holding it is read: bytes that are not UTF-8,
        // and half of a surrogate pair escaped on its own (\uD800), which the tokenizer passes.
        if (!Utf8.IsValid(utf8Json.Span))
        {
            throw Invalid("it is not UTF-8 text");
        }
        try
        {
            // Duplicate property names, compared decoded, are refused anywhere in the document, so no member read below
            // can be shadowed by a second copy another reader would see instead.
            using var document = ReleaseJson.Parse(utf8Json, FileName);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("it is not a JSON object");
            }
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number)
            {
                throw Invalid("it has no schemaVersion");
            }
            if (!schema.TryGetInt32(out var number) || number != Schema)
            {
                throw Invalid($"it is schema {schema.GetRawText()}, and this installer reads schema {Schema}");
            }
            var version = Text(root, "version");
            if (!ProductVersion.IsValid(version))
            {
                throw Invalid("it names no release version");
            }
            // A release's version names its tag, its image tag and its files, none of which can carry build metadata.
            if (version.Contains('+', StringComparison.Ordinal))
            {
                throw Invalid($"its version {version} carries build metadata, which a release's version does not");
            }
            if (Text(root, "tag") != $"v{version}")
            {
                throw Invalid($"its tag is not v{version}");
            }
            if (!root.TryGetProperty("prerelease", out var prerelease) ||
                prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid("it does not say whether it is a pre-release");
            }
            if (prerelease.GetBoolean() != ProductVersion.IsPrerelease(version))
            {
                throw Invalid(prerelease.GetBoolean()
                    ? $"it is marked a pre-release, but {version} is a final release"
                    : $"{version} is a pre-release, but it is not marked one");
            }
            var commit = Text(root, "commit");
            if (commit is null || !CommitPattern().IsMatch(commit))
            {
                throw Invalid("its commit is not a 40-character lowercase commit ID");
            }
            if (Text(root, "created") is not { } createdText ||
                !DateTimeOffset.TryParseExact(
                    createdText, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            {
                throw Invalid("its created time is not a UTC time such as 2026-10-17T18:00:00Z");
            }
            if (Text(root, "repository") != RepositoryUrl)
            {
                throw Invalid($"it is not a release of {RepositoryUrl}");
            }
            var assets = ReadAssets(root);
            var image = ReadCameraAgentImage(root, version, assets);
            return new ReleaseManifest(version, ReadUpgradeNotes(root))
            {
                Tag = $"v{version}",
                Prerelease = prerelease.GetBoolean(),
                Commit = commit,
                Created = created,
                CameraAgent = image,
                Assets = assets
            };
        }
        catch (JsonException exception)
        {
            throw new InstallerException($"The installer cannot read {FileName}: it is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InstallerException(
                $"The installer will not use {FileName}: it contains a string that is not valid Unicode.", exception);
        }
    }

    private static ReleaseImage ReadCameraAgentImage(JsonElement root, string version, IReadOnlyList<ReleaseAsset> assets)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("it lists no images");
        }
        if (!images.TryGetProperty(CameraAgentImageName, out var image) || image.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"it has no {CameraAgentImageName} image");
        }
        var repository = Text(image, "repository");
        if (repository is null || !RepositoryPattern().IsMatch(repository) || !NamesRegistry(repository) ||
            !repository.EndsWith("/" + CameraAgentImageName, StringComparison.Ordinal))
        {
            throw Invalid($"the {CameraAgentImageName} image's repository is not a <registry>/<path>/{CameraAgentImageName} repository");
        }
        if (Text(image, "tag") != version || !DockerTagPattern().IsMatch(version))
        {
            throw Invalid($"the {CameraAgentImageName} image's tag is not {version}");
        }
        var digest = Text(image, "digest");
        if (digest is null || !DigestPattern().IsMatch(digest))
        {
            throw Invalid($"the {CameraAgentImageName} image's digest is not sha256:<64 lowercase hex digits>");
        }
        if (Text(image, "reference") != $"{repository}:{version}@{digest}")
        {
            throw Invalid($"the {CameraAgentImageName} image's reference is not its repository, its tag and its digest");
        }
        var platforms = ReadPlatforms(image);
        var archives = ReadOfflineArchives(image, version, platforms);
        foreach (var archive in archives)
        {
            // Every offline archive is also one of the release's files, so SHA256SUMS covers it and the asset list and
            // the image's own entry must describe the same bytes.
            var asset = assets.FirstOrDefault(candidate => candidate.Name == archive.Name);
            if (asset is null || asset.Kind != ImageArchiveKind || asset.Platform != RuntimeOf(archive.Platform) ||
                asset.Size != archive.Size || asset.Sha256 != archive.Sha256)
            {
                throw Invalid($"the offline archive {archive.Name} is not an {ImageArchiveKind} asset of the same size and SHA-256");
            }
        }
        return new ReleaseImage(repository, version, digest, platforms, archives);
    }

    private static string[] ReadPlatforms(JsonElement image)
    {
        if (!image.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array ||
            platforms.GetArrayLength() is 0 or > 2)
        {
            throw Invalid($"the {CameraAgentImageName} image's platforms are not a list of linux/amd64 and linux/arm64");
        }
        var read = new List<string>();
        foreach (var platform in platforms.EnumerateArray())
        {
            if (platform.ValueKind != JsonValueKind.String || platform.GetString() is not { } name ||
                !ImagePlatforms.Contains(name, StringComparer.Ordinal) || read.Contains(name, StringComparer.Ordinal))
            {
                throw Invalid($"the {CameraAgentImageName} image's platforms are not a list of linux/amd64 and linux/arm64");
            }
            read.Add(name);
        }
        return [.. read];
    }

    // images.<name>.offlineArchives, which a release that publishes no archives leaves out or null.
    private static OfflineArchive[] ReadOfflineArchives(JsonElement image, string version, string[] platforms)
    {
        if (!image.TryGetProperty("offlineArchives", out var archives) || archives.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (archives.ValueKind != JsonValueKind.Array || archives.GetArrayLength() > platforms.Length)
        {
            throw Invalid($"the {CameraAgentImageName} image's offline archives are not a list of at most one per platform");
        }
        var read = new List<OfflineArchive>();
        var index = 0;
        foreach (var archive in archives.EnumerateArray())
        {
            index++;
            if (archive.ValueKind != JsonValueKind.Object || Text(archive, "platform") is not { } platform ||
                !platforms.Contains(platform, StringComparer.Ordinal) || read.Any(value => value.Platform == platform))
            {
                throw Invalid($"offline archive {index} is not for one of the image's platforms, once");
            }
            var name = OfflineArchive.NameFor(version, platform);
            if (Text(archive, "name") != name)
            {
                throw Invalid($"offline archive {index} for {platform} is not named {name}");
            }
            if (!archive.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var length) || length <= 0)
            {
                throw Invalid($"{name}'s size is not a number of bytes");
            }
            if (Text(archive, "sha256") is not { } sha256 || !Sha256Pattern().IsMatch(sha256))
            {
                throw Invalid($"{name}'s sha256 is not 64 lowercase hex digits");
            }
            if (Text(archive, "manifestDigest") is not { } manifestDigest || !DigestPattern().IsMatch(manifestDigest) ||
                Text(archive, "configDigest") is not { } configDigest || !DigestPattern().IsMatch(configDigest))
            {
                throw Invalid($"{name}'s manifestDigest and configDigest are not each sha256:<64 lowercase hex digits>");
            }
            read.Add(new OfflineArchive(name, platform, length, sha256, manifestDigest, configDigest));
        }
        return [.. read];
    }

    private static ReleaseAsset[] ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("its assets are not a list");
        }
        if (assets.GetArrayLength() > MaximumAssets)
        {
            throw Invalid($"it has more than {MaximumAssets} assets");
        }
        var read = new List<ReleaseAsset>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            index++;
            if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") is not { } name ||
                !FileNamePattern().IsMatch(name) || name is FileName or ReleaseChecksums.FileName)
            {
                throw Invalid($"asset {index}'s name is not the file name of a release asset");
            }
            if (!names.Add(name))
            {
                throw Invalid($"it lists {name} more than once");
            }
            if (Text(asset, "kind") is not { } kind || !KindPattern().IsMatch(kind))
            {
                throw Invalid($"{name}'s kind is not a lowercase word");
            }
            if (!asset.TryGetProperty("platform", out var platformValue))
            {
                throw Invalid($"{name}'s platform is not null or a runtime such as linux-arm64");
            }
            string? platform = null;
            if (platformValue.ValueKind != JsonValueKind.Null)
            {
                platform = Text(asset, "platform");
                if (platform is null || !RuntimePattern().IsMatch(platform))
                {
                    throw Invalid($"{name}'s platform is not null or a runtime such as linux-arm64");
                }
            }
            if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var length) || length <= 0)
            {
                throw Invalid($"{name}'s size is not a number of bytes");
            }
            if (Text(asset, "sha256") is not { } sha256 || !Sha256Pattern().IsMatch(sha256))
            {
                throw Invalid($"{name}'s sha256 is not 64 lowercase hex digits");
            }
            if (asset.TryGetProperty("files", out var files) && !IsFileChecksums(files))
            {
                throw Invalid($"{name}'s files are not an object of file names and their SHA-256");
            }
            read.Add(new ReleaseAsset(name, kind, platform, length, sha256));
        }
        return [.. read];
    }

    private static bool IsFileChecksums(JsonElement files)
        => files.ValueKind == JsonValueKind.Object && files.EnumerateObject().All(static file =>
            FileNamePattern().IsMatch(file.Name) && file.Value.ValueKind == JsonValueKind.String &&
            Sha256Pattern().IsMatch(file.Value.GetString()!));

    private static UpgradeNote[] ReadUpgradeNotes(JsonElement root)
    {
        if (!root.TryGetProperty("upgradeNotes", out var notes) || notes.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (notes.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("its upgrade notes are not a list");
        }
        if (notes.GetArrayLength() > MaximumUpgradeNotes)
        {
            throw Invalid($"it has more than {MaximumUpgradeNotes} upgrade notes");
        }
        var read = new List<UpgradeNote>();
        // The releases read so far, each its version without build metadata, so a repeat is found as its note is read.
        var releases = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var note in notes.EnumerateArray())
        {
            index++;
            if (note.ValueKind != JsonValueKind.Object || Text(note, "version") is not { } version ||
                !ProductVersion.TryGetPrecedence(version, out var precedence) || StringValue(note, "text") is not { } text)
            {
                throw Invalid($"upgrade note {index} is not a release's version and its text");
            }
            // Admission and rendering share one scalar-based terminal-safety policy. Check it before refusing blank
            // text, so even a note made only of an unsafe whitespace character names its version and code point.
            var lines = UpgradeNoteText.NormalizeLineEndings(text);
            if (UpgradeNoteText.FindUnsafeCodePoint(lines) is { } codePoint)
            {
                throw Invalid($"upgrade note {index} ({version}) contains unsafe terminal character {codePoint}");
            }
            if (string.IsNullOrWhiteSpace(lines))
            {
                throw Invalid($"upgrade note {index} is not a release's version and its text");
            }
            if (!releases.Add(precedence))
            {
                throw Invalid($"it has more than one upgrade note for {version}");
            }
            read.Add(new UpgradeNote(version, lines));
        }
        return [.. read];
    }

    // The first segment names a registry the way Docker reads one: a host with a dot or a port, or localhost. Any other
    // first segment would be read as a Docker Hub namespace, and the image pulled from there.
    private static bool NamesRegistry(string repository)
    {
        var registry = repository[..repository.IndexOf('/', StringComparison.Ordinal)];
        return registry.Contains('.', StringComparison.Ordinal) || registry.Contains(':', StringComparison.Ordinal) ||
               registry == "localhost";
    }

    /// <summary>The .NET runtime of an image platform: <c>linux-arm64</c> for <c>linux/arm64</c>.</summary>
    internal static string RuntimeOf(string platform) => platform switch
    {
        "linux/amd64" => "linux-x64",
        "linux/arm64" => "linux-arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Not a CameraAgent image platform.")
    };

    private static string? Text(JsonElement element, string name)
        => StringValue(element, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? StringValue(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static InstallerException Invalid(string reason) => new($"The installer will not use {FileName}: {reason}.");

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    /// <summary>An image repository: a registry host, an optional port, and a lowercase path of at least one segment.</summary>
    [GeneratedRegex(@"\A[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]{1,5})?(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DockerTagPattern();

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex DigestPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex Sha256Pattern();

    // A name alone, never a path: the installer reads an asset by its name in the release's folder.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,254}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex FileNamePattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+){0,7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimePattern();
}

/// <summary>
/// The CameraAgent image a release names (<c>images.skymonitor-cameraagent</c>): its repository, the release's version
/// as its tag, and the digest of the image index over its platforms, which is what Docker pulls.
/// </summary>
internal sealed record ReleaseImage(
    string Repository,
    string Tag,
    string Digest,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<OfflineArchive> OfflineArchives)
{
    /// <summary>The repository, the tag and the digest, as release.json writes it.</summary>
    public string Reference => $"{Repository}:{Tag}@{Digest}";

    /// <summary>The repository and the digest: what Docker pulls, and what it lists among the image's repository digests.</summary>
    public string DigestReference => $"{Repository}@{Digest}";

    /// <summary>The offline archive for <paramref name="platform"/> (linux/arm64), or null when the release has none.</summary>
    public OfflineArchive? ArchiveFor(string platform) => OfflineArchives.FirstOrDefault(archive => archive.Platform == platform);
}

/// <summary>
/// A release's offline archive of the CameraAgent image for one platform: what <c>docker image load</c> reads in place
/// of a pull. It holds the release's image index, that platform's manifest, its config and its layers.
/// </summary>
internal sealed record OfflineArchive(
    string Name,
    string Platform,
    long Size,
    string Sha256,
    string ManifestDigest,
    string ConfigDigest)
{
    /// <summary>The archive's name for a version and platform: skymonitor-cameraagent-1.2.0-linux-arm64.oci.tar.</summary>
    public static string NameFor(string version, string platform)
        => $"{ReleaseManifest.CameraAgentImageName}-{version}-{platform.Replace('/', '-')}.oci.tar";
}

/// <summary>A file a release has besides release.json and SHA256SUMS, with its size and SHA-256.</summary>
internal sealed record ReleaseAsset(string Name, string Kind, string? Platform, long Size, string Sha256);
