using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

/// <summary>
/// How a logical (bucket, key) maps onto the filesystem, and what the committed descriptor
/// says. Every key, whatever characters it contains, becomes an opaque hex name derived from
/// its SHA-256, fanned out two levels deep so no directory grows without bound and no key
/// segment ever becomes a physical path segment. Case, separators, dots, and reserved names
/// in the key therefore cannot alias or escape: the physical name is always 64 lowercase hex
/// characters. The exact key is recorded in the descriptor and verified on every read, so a
/// (vanishingly unlikely) hash collision is detected as a mismatch rather than served.
/// </summary>
internal static class FilesystemObjectLayout
{
    internal const string DescriptorSuffix = ".desc.json";
    internal const string DataSuffix = ".data";
    internal const string SchemaVersion = "hvo-fs-object-v1";

    /// <summary>Bucket names are already validated by options (^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$), so they are safe path segments.</summary>
    internal static string BucketRelativePath(string bucket) => bucket;

    /// <summary>The opaque physical name for a key: SHA-256 as lowercase hex.</summary>
    internal static string KeyHash(string key)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Relative path of the directory holding a key's descriptor and data files: bucket/ab/cd/.</summary>
    internal static string KeyDirectoryRelativePath(string bucket, string keyHash)
        => Path.Combine(bucket, keyHash[..2], keyHash[2..4]);

    internal static string DescriptorRelativePath(string bucket, string keyHash)
        => Path.Combine(KeyDirectoryRelativePath(bucket, keyHash), keyHash + DescriptorSuffix);

    internal static string DataRelativePath(string bucket, string keyHash, string generation)
        => Path.Combine(KeyDirectoryRelativePath(bucket, keyHash), keyHash + "." + generation + DataSuffix);

    /// <summary>
    /// A generation is 128 random bits as lowercase hex: opaque, never reused, and never derived
    /// from content, so two identical writes still get distinct immutable data files.
    /// </summary>
    internal static string NewGeneration()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static bool IsGeneration(string? value)
        => value is { Length: 32 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static readonly JsonSerializerOptions DescriptorJson = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>
/// The commit point. A key exists iff its descriptor exists; the descriptor names the exact
/// key it stands for, the immutable data file (by generation) that holds the bytes, and the
/// facts a reader must be able to verify without trusting the filesystem: length and a
/// provider-internal SHA-256 computed during the same streaming write. That digest catches
/// local corruption; it does not replace the application's checksum as the cross-provider
/// integrity authority.
/// </summary>
internal sealed record FilesystemObjectDescriptor(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("generation")] string Generation,
    [property: JsonPropertyName("modifiedUtc")] DateTimeOffset ModifiedUtc)
{
    internal bool IsWellFormed(string expectedKey)
        => Schema == FilesystemObjectLayout.SchemaVersion
            && Key == expectedKey
            && !string.IsNullOrEmpty(ContentType)
            && Length >= 0
            && Sha256 is { Length: 64 }
            && FilesystemObjectLayout.IsGeneration(Generation);
}
