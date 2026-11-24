using System.Text.Json;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The one pin of the stock Silo image a Silo-layout CameraAgent runs (#1966), shared by the installer and the release
/// tool so that <c>release.json</c>'s <c>images.silo</c> and the installer can never name different images.
/// </summary>
/// <remarks>
/// HVO does not convey the image: the release names Docker Hub as its supplier and pins the upstream index, and the
/// per-platform manifest and config digests an install accepts. Online installs pull it by the index digest; offline
/// sites stage the exact upstream platform image themselves and the installer verifies it by these digests before
/// loading it. The image carries AGPL-3.0-or-later Silo and Red Hat UBI content; shipping it as a release asset would
/// make HVO a distributor of that object code with its corresponding-source obligations, which this route avoids.
/// </remarks>
internal static class SiloImagePin
{
    /// <summary>The image's key under release.json's <c>images</c>.</summary>
    public const string ImageKey = "silo";

    public const string Repository = "docker.io/pgsty/silo";
    public const string Release = "RELEASE.2026-11-12T00-00-00Z";
    public const string IndexDigest = "sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329";
    public const string SourceRevision = "44e848fedf8cec3d8c0da4b1aa8422390a2b7a8b";
    public const string License = "AGPL-3.0-or-later";
    public const string Source = "https://github.com/pgsty/silo/tree/RELEASE.2026-11-12T00-00-00Z";
    public const string Supplier = "upstream";

    /// <summary>The reference an online install pulls and the manifest records.</summary>
    public static string Reference => $"{Repository}@{IndexDigest}";

    /// <summary>One platform of the pinned index: its manifest digest and its config digest (the classic store's image ID).</summary>
    internal sealed record PlatformImage(string Platform, string Architecture, string ManifestDigest, string ConfigDigest);

    public static IReadOnlyList<PlatformImage> Platforms { get; } =
    [
        new("linux/amd64", "amd64",
            "sha256:6f48a60830eb101e19f955124914554d73c4a4721396d3d13d61634e1396eadf",
            "sha256:f8cec3d6c63585d664c035acecfacb92dc974ab70c1f57a528d5baa3c224e082"),
        new("linux/arm64", "arm64",
            "sha256:dad2a4bc279f9095d9835398dda266abd4f2f7486d0cb66a071951f1e4a6791d",
            "sha256:66e09caa8c3e0e834de2202d9a4c45a9a0608f63ab2061a8f3953ae23fc135e7")
    ];

    public static PlatformImage? ForArchitecture(string? architecture)
        => Platforms.FirstOrDefault(platform => platform.Architecture == architecture);

    /// <summary>
    /// The image IDs Docker may give the pinned image on <paramref name="platform"/>: the index digest (containerd store,
    /// pulled), the platform manifest digest (containerd store, loaded from a staged archive) or the config digest
    /// (classic store).
    /// </summary>
    public static IReadOnlyList<string> AcceptedImageIds(PlatformImage platform)
        => [IndexDigest, platform.ManifestDigest, platform.ConfigDigest];

    /// <summary>
    /// Whether a container runs exactly <paramref name="platform"/>'s child of the pin (#1966 review). Under Docker's
    /// containerd store a container created from the shared pinned index reports the index as its image whichever child it
    /// runs, so the manifest it actually runs (its <c>ImageManifestDescriptor</c>) decides, and its image must be the index
    /// or that manifest. Under the classic store there is no descriptor, and the image ID is the config digest, which fixes
    /// the platform. Anything else, a foreign child of the same index included, is not the pinned image here.
    /// </summary>
    public static bool RunsPinnedChild(
        string? imageId, string? manifestDigest, string? manifestArchitecture, string? manifestOperatingSystem, PlatformImage platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return manifestDigest is not null
            ? manifestDigest == platform.ManifestDigest && manifestArchitecture == platform.Architecture &&
              manifestOperatingSystem is null or "linux" && (imageId == IndexDigest || imageId == platform.ManifestDigest)
            : imageId == platform.ConfigDigest;
    }

    /// <summary>release.json's <c>images.silo</c> entry, written by the release tool.</summary>
    public static JsonObject ReleaseEntry() => new()
    {
        ["repository"] = Repository,
        ["tag"] = Release,
        ["digest"] = IndexDigest,
        ["reference"] = $"{Repository}:{Release}@{IndexDigest}",
        ["platforms"] = new JsonArray([.. Platforms.Select(static platform => (JsonNode)new JsonObject
        {
            ["platform"] = platform.Platform,
            ["manifestDigest"] = platform.ManifestDigest,
            ["configDigest"] = platform.ConfigDigest
        })]),
        ["revision"] = SourceRevision,
        ["license"] = License,
        ["source"] = Source,
        ["supplier"] = Supplier
    };

    /// <summary>
    /// Whether a release's <c>images.silo</c> entry is exactly this pin, member for member. A release whose entry is
    /// absent predates the Silo layout; one whose entry differs names an image this installer has not qualified.
    /// </summary>
    public static bool Matches(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return false;
        var expected = JsonNode.Parse(ReleaseEntry().ToJsonString())!.AsObject();
        JsonObject actual;
        try
        {
            actual = JsonNode.Parse(entry.GetRawText())!.AsObject();
        }
        catch (JsonException)
        {
            return false;
        }
        return JsonNode.DeepEquals(expected, actual);
    }
}
