using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>An ended session's bounded history, written once as an immutable local record.</summary>
public sealed record ManualFocusSessionRecord(
    string Schema,
    string RecordId,
    DateTimeOffset SavedUtc,
    string SavedBy,
    string RetentionStatement,
    ManualFocusSessionSnapshot Session)
{
    public const string SchemaVersion = "hvo.cameraagent.manual-focus-session.v1";

    public const string Statement =
        "Saved on this CameraAgent's local disk only. Never uploaded, never applied to a rig profile, and pruned oldest-first beyond the retention bound.";

    public static ManualFocusSessionRecord Create(ManualFocusSessionSnapshot session, string savedBy, DateTimeOffset savedUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        var recordId = string.Concat(
            savedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), "-", session.SessionId);
        return new(SchemaVersion, recordId, savedUtc, savedBy, Statement,
            session with { Retention = ManualFocusRetentionState.Saved, SavedRecordId = recordId });
    }
}

/// <summary>Listing entry for a saved record. An unverified record failed its checksum or could not be read.</summary>
public sealed record ManualFocusSessionRecordSummary(
    string RecordId,
    bool Verified,
    string? Sha256,
    long Bytes,
    DateTimeOffset? SavedUtc,
    string? SavedBy,
    ManualFocusSessionState? State,
    long TotalSamples,
    double? BestHalfFluxDiameterPixels,
    string? ModuleType,
    string? FidelityKind,
    bool QualifiesPhysicalFocus,
    ManualFocusPreviewSettings? BestSettings = null,
    ManualFocusTargetSource? BestTargetSource = null,
    double? BestSimulatedFocusPosition = null);

/// <summary>A verified record and its exact stored bytes, for authorized export.</summary>
public sealed record ManualFocusSessionRecordContent(ManualFocusSessionRecordSummary Summary, ManualFocusSessionRecord Record, ReadOnlyMemory<byte> Utf8Json);

public interface IManualFocusSessionStore
{
    int MaximumRecords { get; }

    Task<ManualFocusSessionRecordSummary> SaveAsync(ManualFocusSessionRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<ManualFocusSessionRecordSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Reads and verifies one record; null when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The record failed checksum or schema verification.</exception>
    Task<ManualFocusSessionRecordContent?> ReadAsync(string recordId, CancellationToken cancellationToken);
}

/// <summary>
/// Writes saved focus sessions under <c>&lt;RawIngressRoot&gt;/focus-sessions/</c> through the shared
/// <see cref="AtomicPublisher"/>: each record is a create-only JSON file plus a <c>.sha256</c> sidecar, published only after
/// its content is durable, so a reader sees a complete verified record or none. A record without a sidecar is an
/// interrupted save and is pruned. The store holds at most <see cref="MaximumRecords"/> records of at most
/// <see cref="MaximumRecordBytes"/> each and prunes the oldest beyond that bound.
/// </summary>
public sealed class ManualFocusSessionStore : IManualFocusSessionStore, IDisposable
{
    public const string DirectoryName = "focus-sessions";
    public const int DefaultMaximumRecords = 50;
    public const int MaximumRecordBytes = 512 * 1024;
    private const string RecordExtension = ".json";
    private const string ChecksumExtension = ".sha256";
    private const int MaximumEnumeratedEntries = 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManualFocusSessionStore(IOptions<CameraAgentHostOptions> options)
        : this(options?.Value.RawIngressRoot ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public ManualFocusSessionStore(string rawIngressRoot, int maximumRecords = DefaultMaximumRecords)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawIngressRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecords);
        _rootPath = Path.GetFullPath(rawIngressRoot);
        MaximumRecords = maximumRecords;
    }

    public int MaximumRecords { get; }

    public async Task<ManualFocusSessionRecordSummary> SaveAsync(ManualFocusSessionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecordId(record.RecordId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        if (bytes.Length > MaximumRecordBytes)
        {
            throw new ManualFocusSessionStateException(
                $"The session record is {bytes.Length} bytes, above the {MaximumRecordBytes}-byte retention bound.");
        }
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = OpenRoot(create: true)!;
            var directory = root.Resolve(DirectoryName);
            AtomicPublisher.EnsureDirectory(root, directory);
            AtomicPublisher.CleanupTemporaries(root, directory, MaximumEnumeratedEntries);
            await AtomicPublisher.PublishAsync(root, RelativePath(record.RecordId, RecordExtension), PublishMode.CreateNew,
                (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
            var sidecar = Encoding.ASCII.GetBytes(string.Concat(sha256, "  ", record.RecordId, RecordExtension, "\n"));
            await AtomicPublisher.PublishAsync(root, RelativePath(record.RecordId, ChecksumExtension), PublishMode.CreateNew,
                (stream, token) => stream.WriteAsync(sidecar, token).AsTask(), cancellationToken).ConfigureAwait(false);
            Prune(root, directory);
            return Summarize(record, sha256, bytes.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ManualFocusSessionRecordSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OpenRoot(create: false) is not { } root || !Directory.Exists(Path.Combine(root.Path, DirectoryName)))
            {
                return [];
            }
            var directory = root.Resolve(DirectoryName);
            var summaries = new List<ManualFocusSessionRecordSummary>();
            foreach (var recordId in EnumerateRecordIds(directory).OrderDescending(StringComparer.Ordinal).Take(limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                summaries.Add(await ReadVerifiedAsync(root, recordId, cancellationToken).ConfigureAwait(false) is { } content
                    ? content.Summary
                    : new ManualFocusSessionRecordSummary(recordId, false, null, 0, null, null, null, 0, null, null, null, false));
            }
            return summaries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManualFocusSessionRecordContent?> ReadAsync(string recordId, CancellationToken cancellationToken)
    {
        ValidateRecordId(recordId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OpenRoot(create: false) is not { } root ||
                !File.Exists(Path.Combine(root.Path, DirectoryName, recordId + RecordExtension)))
            {
                return null;
            }
            return await ReadVerifiedAsync(root, recordId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Focus session record '{recordId}' failed checksum or schema verification.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ManualFocusSessionRecordContent?> ReadVerifiedAsync(
        PhysicalRoot root,
        string recordId,
        CancellationToken cancellationToken)
    {
        var recordPath = root.Resolve(RelativePath(recordId, RecordExtension));
        var checksumPath = root.Resolve(RelativePath(recordId, ChecksumExtension));
        if (!File.Exists(recordPath) || !File.Exists(checksumPath))
        {
            return null;
        }
        root.Verify(recordPath, "read-focus-session");
        root.Verify(checksumPath, "read-focus-session");
        var length = new FileInfo(recordPath).Length;
        if (length > MaximumRecordBytes)
        {
            return null;
        }
        var bytes = await File.ReadAllBytesAsync(recordPath, cancellationToken).ConfigureAwait(false);
        var sidecar = await File.ReadAllTextAsync(checksumPath, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        var expected = sidecar.Split(' ', 2)[0].Trim();
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return null;
        }
        ManualFocusSessionRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ManualFocusSessionRecord>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (record is null || record.Schema != ManualFocusSessionRecord.SchemaVersion ||
            !string.Equals(record.RecordId, recordId, StringComparison.Ordinal))
        {
            return null;
        }
        return new(Summarize(record, actual, bytes.Length), record, bytes);
    }

    private static ManualFocusSessionRecordSummary Summarize(ManualFocusSessionRecord record, string sha256, long bytes)
    {
        var session = record.Session;
        return new(record.RecordId, true, sha256, bytes, record.SavedUtc, record.SavedBy, session.State,
            session.TotalSamples, session.Best?.Measurement.HalfFluxDiameterPixels, session.ModuleType,
            session.Fidelity?.Kind, session.Fidelity?.QualifiesPhysicalFocus ?? false, session.Best?.Settings,
            session.Best?.TargetSource, session.Best?.SimulatedFocusPosition);
    }

    private void Prune(PhysicalRoot root, string directory)
    {
        var recordIds = EnumerateRecordIds(directory).Order(StringComparer.Ordinal).ToList();
        // A record whose sidecar never landed is an interrupted save; it is never listed as verified, so remove it.
        foreach (var recordId in recordIds.Where(id => !File.Exists(Path.Combine(directory, id + ChecksumExtension))).ToList())
        {
            Delete(root, recordId);
            recordIds.Remove(recordId);
        }
        for (var index = 0; index < recordIds.Count - MaximumRecords; index++)
        {
            Delete(root, recordIds[index]);
        }
        DurableSync.Directory(directory);
    }

    private static void Delete(PhysicalRoot root, string recordId)
    {
        foreach (var extension in new[] { ChecksumExtension, RecordExtension })
        {
            var path = root.Resolve(RelativePath(recordId, extension));
            root.Verify(path, "prune-focus-session");
            File.Delete(path);
        }
    }

    private static IEnumerable<string> EnumerateRecordIds(string directory)
        => Directory.EnumerateFiles(directory, "*" + RecordExtension, SearchOption.TopDirectoryOnly)
            .Take(MaximumEnumeratedEntries)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(IsValidRecordId);

    private PhysicalRoot? OpenRoot(bool create)
    {
        if (!Directory.Exists(_rootPath))
        {
            if (!create)
            {
                return null;
            }
            Directory.CreateDirectory(_rootPath);
        }
        return PhysicalRoot.Open(_rootPath);
    }

    private static string RelativePath(string recordId, string extension)
        => Path.Combine(DirectoryName, recordId + extension);

    private static void ValidateRecordId(string recordId)
    {
        if (!IsValidRecordId(recordId))
        {
            throw new ManualFocusSessionValidationException("The focus session record identifier is invalid.");
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsValidRecordId(string? recordId)
        => recordId is { Length: > 0 and <= 96 } &&
           recordId.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
