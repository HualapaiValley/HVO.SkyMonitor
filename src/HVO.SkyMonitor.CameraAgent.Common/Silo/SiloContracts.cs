using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// Explicit identities of every new-layout format (#1965 4.7, #1967 acceptance 5). A reader accepts additive fields
/// within one of these identities and refuses any other identity read-only, without mutating what it read.
/// </summary>
internal static class SiloFormats
{
    public const string GroupManifest = "hvo.cameraagent.silo.group-manifest/1";
    public const string JournalSegment = "hvo.cameraagent.silo.journal-segment/1";
    public const string JournalCheckpoint = "hvo.cameraagent.silo.journal-checkpoint/1";
    public const string CatalogIndex = "hvo.cameraagent.silo.catalog-index/1";
    public const string PublicationState = "hvo.cameraagent.silo.publication-state/1";
    public const string StorageBinding = "hvo.cameraagent.silo.storage-binding/1";

    /// <summary>The only physical-verification method this format accepts: exact-version GET with independent SHA-256.</summary>
    public const string ExactGetSha256 = "exact-get-sha256";
}

internal enum SiloRetentionClass { Full, Display, Metadata }

internal enum SiloStoreClass { Local, Archive, Cold }

internal enum SiloRetrieval { Online, RestoreRequired }

/// <summary>A role is either one artifact revision (a member) or one group revision's manifest.</summary>
internal enum SiloIdentityKind { Member, Manifest }

internal enum SiloDependencyKind { Reopen, Compute }

internal enum SiloExpiryScope { Copy, Role }

internal enum SiloExpiryOutcome { Deleted, AlreadyAbsent, NotDeleted, Refused }

internal enum SiloAvailabilityScope { Copy, Role }

/// <summary>Journaled availability states (#1965 4.5). Copy scope: evicted, missing, quarantined, restored. Role scope: missing, conflict, policy-expired.</summary>
internal enum SiloAvailabilityState { Evicted, Missing, Quarantined, Restored, Conflict, PolicyExpired }

/// <summary>
/// One exact Silo object version. The store identity names a qualified store, never an endpoint URL, so relocating an
/// endpoint preserves it (#1965 4.2).
/// </summary>
internal sealed record SiloExactLocator(string StoreId, string Bucket, string Key, string VersionId)
{
    public void Validate()
    {
        SiloText.RequireToken(StoreId, nameof(StoreId), 128);
        SiloText.RequireToken(Bucket, nameof(Bucket), 63);
        SiloText.RequireKey(Key);
        SiloText.RequireText(VersionId, nameof(VersionId), 1024);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{StoreId}:{Bucket}/{Key}@{VersionId}");
}

/// <summary>
/// The immutable identity of a role. An artifact revision is bound to exactly one length and SHA-256; a manifest role
/// is a group revision bound to its manifest bytes. A different digest under the same identity is a conflict.
/// </summary>
internal sealed record SiloObjectIdentity(SiloIdentityKind Kind, string Id, string Revision, string Sha256, long Length)
{
    public void Validate()
    {
        SiloText.RequireText(Id, nameof(Id), 256);
        SiloText.RequireText(Revision, nameof(Revision), 256);
        SiloHash.Require(Sha256, nameof(Sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(Length);
    }

    [JsonIgnore] public SiloRoleKey Role => new(Kind, Id, Revision);
}

internal readonly record struct SiloRoleKey(SiloIdentityKind Kind, string Id, string Revision);

/// <summary>Total journal order. Sequences are gapless within a generation.</summary>
internal readonly record struct SiloJournalPosition(long Generation, long Sequence) : IComparable<SiloJournalPosition>
{
    public int CompareTo(SiloJournalPosition other)
    {
        var generation = Generation.CompareTo(other.Generation);
        return generation != 0 ? generation : Sequence.CompareTo(other.Sequence);
    }

    public static bool operator <(SiloJournalPosition left, SiloJournalPosition right) => left.CompareTo(right) < 0;
    public static bool operator >(SiloJournalPosition left, SiloJournalPosition right) => left.CompareTo(right) > 0;
    public static bool operator <=(SiloJournalPosition left, SiloJournalPosition right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SiloJournalPosition left, SiloJournalPosition right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"g{Generation}/{Sequence}");
}

/// <summary>
/// The instance's storage binding (#1965 4.2): the installer-issued instance identity, the qualified local store and the
/// bucket. Every manifest and journal segment names it, and a mismatch refuses before mutation.
/// </summary>
internal sealed record SiloStorageBinding(string InstanceId, string StoreId, string Bucket)
{
    public string BindingId { get; } = Compute(InstanceId, StoreId, Bucket);

    private static string Compute(string instanceId, string storeId, string bucket)
    {
        SiloText.RequireText(instanceId, nameof(InstanceId), 256);
        SiloText.RequireToken(storeId, nameof(StoreId), 128);
        SiloText.RequireToken(bucket, nameof(Bucket), 63);
        // Length-prefixed fields under a domain label, so no two distinct bindings share an encoding.
        var builder = new StringBuilder(SiloFormats.StorageBinding);
        foreach (var field in new[] { instanceId, storeId, bucket })
        {
            builder.Append('\n').Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        }
        return SiloHash.Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}

internal static class SiloHash
{
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Require(string? value, string name)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("A lowercase hexadecimal SHA-256 is required.", name);
        }
    }

    public static string ToBase64(string sha256Hex) => Convert.ToBase64String(Convert.FromHexString(sha256Hex));
}

internal static class SiloText
{
    public static void RequireText(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException($"A nonblank value of at most {maximum} characters without control characters is required.", name);
        }
    }

    /// <summary>A key-safe token: ASCII letters, digits, '.', '_' and '-' only.</summary>
    public static bool IsToken(string? value, int maximum) =>
        !string.IsNullOrEmpty(value) && value.Length <= maximum && value[0] != '.' &&
        value.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    public static void RequireToken(string? value, string name, int maximum)
    {
        if (!IsToken(value, maximum))
        {
            throw new ArgumentException($"A key-safe token of at most {maximum} characters is required.", name);
        }
    }

    public static void RequireKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 1024 || key.StartsWith('/') ||
            key.Split('/').Any(static segment => !IsToken(segment, 255)))
        {
            throw new ArgumentException("An object key of key-safe segments is required.", nameof(key));
        }
    }
}

/// <summary>Bytes or a database that cannot be accepted as a supported new-layout format.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal abstract class SiloDataException(string message, Exception? inner = null) : FormatException(message, inner);

/// <summary>A clear, read-only refusal of a format this CameraAgent does not support. Nothing was mutated.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloFormatRefusedException : SiloDataException
{
    public SiloFormatRefusedException(string subject, string? found, string supported)
        : base(string.Create(CultureInfo.InvariantCulture,
            $"{subject} has format '{found ?? "(none)"}', which this CameraAgent does not support (supported: '{supported}'). It was refused read-only and left unchanged."))
    {
        Subject = subject;
        Found = found;
        Supported = supported;
    }

    public string Subject { get; }
    public string? Found { get; }
    public string Supported { get; }
}

/// <summary>
/// Contradictory exact bytes or identity: a create-only key already holds different bytes, or a logical identity is
/// claimed with a different digest. The writer stops for the affected work; nothing is replaced.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloConflictException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A store could not be reached or did not answer. When <see cref="WriteMayHaveLanded"/> is true the write is
/// uncertain and is retried with the same exact bytes.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloStoreUnavailableException(string storeId, string operation, bool writeMayHaveLanded, Exception? inner = null)
    : IOException(string.Create(CultureInfo.InvariantCulture, $"Silo store '{storeId}' did not complete {operation}."), inner)
{
    public string StoreId { get; } = storeId;
    public string Operation { get; } = operation;
    public bool WriteMayHaveLanded { get; } = writeMayHaveLanded;
}

/// <summary>The catalog disagrees with the durable journal it indexes; it must be rebuilt, not served as authoritative.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloCatalogDivergedException(string message) : InvalidOperationException(message);
