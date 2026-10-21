using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

public sealed partial class SqliteOrbitalElementsStore : IOrbitalSnapshotManifestReader
{
    /// <summary>
    /// The strict contract options of <see cref="OrbitalContract"/>, which keeps its own instance internal to the
    /// Astronomy assembly. Canonical-byte and identity checks below make any drift between the two fail closed.
    /// </summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    /// <inheritdoc />
    /// <remarks>
    /// The snapshot is pinned for the whole read, so retention can neither tombstone it nor delete its manifest file
    /// mid-read. The file must be exactly its committed length, at most <see cref="OrbitalContract.MaximumManifestBytes"/>,
    /// hash to its committed file identity, be canonical contract JSON, and recompute to the requested manifest identity.
    /// </remarks>
    public async ValueTask<OrbitalSnapshotManifestV1?> ReadManifestAsync(string manifestSha256, CancellationToken cancellationToken)
    {
        if (!OrbitalContract.IsSha256(manifestSha256))
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            using var pin = await PinAsync(manifestSha256, cancellationToken).ConfigureAwait(false);
            var row = await FindSnapshotAsync(manifestSha256, cancellationToken).ConfigureAwait(false);
            if (row is null || row.IsRetired)
            {
                return null;
            }
            _faults.Inject(OrbitalStorageFaultPoint.BeforeManifestRead);
            var bytes = await ReadVerifiedFileAsync(ManifestPath(manifestSha256), row.ManifestLengthBytes,
                OrbitalContract.MaximumManifestBytes, row.ManifestFileSha256, cancellationToken).ConfigureAwait(false);
            return (bytes is null ? null : DeserializeManifest(bytes, manifestSha256))
                ?? throw new OrbitalStorageException(OrbitalElementsAvailability.IntegrityFailureReasonCode,
                    "A stored orbital manifest failed integrity verification.", null);
        }
        catch (OrbitalStorageException exception)
        {
            ManifestReadFailed(_logger, manifestSha256, exception.ReasonCode);
            throw;
        }
        catch (FileSystemFaultException fault) when (fault.Kind == FileSystemFaultKind.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The orbital manifest read was cancelled.", fault, cancellationToken);
        }
        catch (Exception exception) when (ClassifyManifestFault(exception, cancellationToken) is { } reasonCode)
        {
            ManifestReadFailed(_logger, manifestSha256, reasonCode);
            throw new OrbitalStorageException(reasonCode, "The orbital manifest could not be read.", exception);
        }
    }

    /// <summary>
    /// Reads a contained regular file and returns its bytes only when it is exactly <paramref name="expectedLength"/>
    /// bytes, within (0, <paramref name="maximumLength"/>], and hashes to <paramref name="expectedSha256"/>; otherwise
    /// null. Every verified read of stored snapshot bytes goes through this one proof.
    /// </summary>
    internal async ValueTask<byte[]?> ReadVerifiedFileAsync(
        string path,
        long expectedLength,
        long maximumLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        VerifyContained(path, "orbital-read");
        if (!File.Exists(path) || expectedLength <= 0 || expectedLength > maximumLength)
        {
            return null;
        }
        DurableSync.RequireRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
        {
            return null;
        }
        var buffer = new byte[expectedLength];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        var probe = new byte[1];
        if (await stream.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            return null;
        }
        return string.Equals(Convert.ToHexString(SHA256.HashData(buffer)), expectedSha256, StringComparison.Ordinal)
            ? buffer
            : null;
    }

    /// <summary>Null unless the bytes are the canonical contract form of a manifest that proves the requested identity.</summary>
    private static OrbitalSnapshotManifestV1? DeserializeManifest(byte[] bytes, string manifestSha256)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<OrbitalSnapshotManifestV1>(bytes, ManifestJsonOptions);
            return manifest is not null &&
                string.Equals(manifest.ManifestSha256, manifestSha256, StringComparison.Ordinal) &&
                string.Equals(OrbitalSnapshot.ComputeManifestIdentity(manifest), manifestSha256, StringComparison.Ordinal) &&
                bytes.AsSpan().SequenceEqual(OrbitalContract.SerializeCanonical(manifest))
                    ? manifest
                    : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException
            or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// A missing, linked or non-regular file the ledger says exists is an integrity failure, as it is for
    /// <see cref="OrbitalSnapshotReader"/>; other database and filesystem faults keep their stable storage classification; disposal, an uninitialized root and a foreign cancellation are store
    /// unavailability. Cancellation of the caller's token propagates.
    /// </summary>
    private static string? ClassifyManifestFault(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => null,
            FileSystemFaultException { Kind: FileSystemFaultKind.Containment } or FileNotFoundException
                or DirectoryNotFoundException or InvalidDataException => OrbitalElementsAvailability.IntegrityFailureReasonCode,
            IOException or UnauthorizedAccessException or SqliteException => ClassifyFault(exception),
            InvalidOperationException or OperationCanceledException => OrbitalElementsAvailability.StoreUnavailableReasonCode,
            _ => null
        };
}
