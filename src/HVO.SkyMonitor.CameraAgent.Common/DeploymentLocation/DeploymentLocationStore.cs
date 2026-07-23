using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

/// <summary>Protects deployment-location history without coupling edge infrastructure to a protection framework.</summary>
public interface IDeploymentLocationProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}

/// <summary>Initial values supplied by validated local configuration.</summary>
public sealed record DeploymentLocationSeed(
    string LocationId,
    string Source,
    double? HorizontalAccuracyMeters,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    ObservatoryLocation Coordinates);

/// <summary>Initializes and resolves protected immutable deployment-location versions.</summary>
public interface IDeploymentLocationStore
{
    DeploymentLocationSnapshot? Active { get; }

    ValueTask<DeploymentLocationSnapshot> InitializeAsync(
        DeploymentLocationSeed seed,
        CancellationToken cancellationToken);

    DeploymentLocationSnapshot Resolve(
        CaptureLocationProvenance provenance,
        DateTimeOffset? effectiveUtc = null);
}

public sealed class ProtectedDeploymentLocationStore(
    IOptions<CameraAgentHostOptions> options,
    IDeploymentLocationProtector protector,
    TimeProvider timeProvider,
    ILogger<ProtectedDeploymentLocationStore> logger,
    DeploymentLocationTelemetry? telemetry = null) : IDeploymentLocationStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CameraAgentHostOptions _options = options.Value;
    private readonly IDeploymentLocationProtector _protector = protector;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ProtectedDeploymentLocationStore> _logger = logger;
    private DeploymentLocationHistory? _history;

    public DeploymentLocationSnapshot? Active => Volatile.Read(ref _history)?.Snapshots[^1];

    public async ValueTask<DeploymentLocationSnapshot> InitializeAsync(
        DeploymentLocationSeed seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var started = Stopwatch.GetTimestamp();
        var operation = "load";
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity("deployment-location.initialize");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            seed = NormalizeSeed(seed);
            var (path, markerPath) = EnsureStatePaths();
            var historyExists = File.Exists(path);
            if (!historyExists && (File.Exists(markerPath) || HasLocationBearingEvidence()))
            {
                throw new InvalidDataException(
                    "Protected deployment-location history is missing for existing capture evidence.");
            }
            var history = historyExists ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
            if (File.Exists(markerPath))
            {
                EnsurePhysicalStatePath(markerPath);
            }
            var marker = File.Exists(markerPath)
                ? await ReadMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false)
                : null;
            ValidateMarker(marker, history);
            if (history is not null)
            {
                ValidateRetainedEvidence(history);
            }
            var snapshot = Reconcile(history, seed);
            if (history is null || !ReferenceEquals(snapshot.History, history))
            {
                ValidateRetainedEvidence(snapshot.History);
            }
            operation = history is null ? "initialize" : snapshot.Appended ? "change" : "load";
            if (history is null || !ReferenceEquals(snapshot.History, history))
            {
                await WriteAsync(path, snapshot.History, cancellationToken).ConfigureAwait(false);
            }
            if (marker is null || marker.Version != snapshot.Active.Version)
            {
                await WriteMarkerAsync(markerPath, snapshot.Active, cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _history, snapshot.History);
            if (snapshot.Appended)
            {
                DeploymentLocationLog.VersionAppended(_logger, snapshot.Active.LocationId, snapshot.Active.Version);
            }
            else
            {
                DeploymentLocationLog.VersionLoaded(_logger, snapshot.Active.LocationId, snapshot.Active.Version);
            }
            telemetry?.Record(operation, snapshot.Appended ? "applied" : "existing", Stopwatch.GetElapsedTime(started));
            activity?.SetTag("operation", operation);
            activity?.SetTag("outcome", snapshot.Appended ? "applied" : "existing");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return snapshot.Active;
        }
        catch
        {
            telemetry?.Record(operation, "failed", Stopwatch.GetElapsedTime(started));
            activity?.SetTag("operation", operation);
            activity?.SetTag("outcome", "failed");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DeploymentLocationSnapshot Resolve(
        CaptureLocationProvenance provenance,
        DateTimeOffset? effectiveUtc = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var validation = provenance.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Capture deployment-location provenance is invalid.");
        }
        var history = Volatile.Read(ref _history)
            ?? throw new InvalidOperationException("Deployment-location history has not been initialized.");
        var snapshot = history.Snapshots.SingleOrDefault(item =>
            string.Equals(item.LocationId, provenance.LocationId, StringComparison.Ordinal) &&
            item.Version == provenance.Version);
        if (snapshot is null || snapshot.ToProvenance() != provenance)
        {
            throw new InvalidDataException("Capture deployment-location provenance does not match protected history.");
        }
        if (effectiveUtc.HasValue && !IsEffective(history, snapshot, ToMilliseconds(effectiveUtc.Value)))
        {
            throw new InvalidDataException("Capture time is outside the protected deployment-location interval.");
        }
        return snapshot;
    }

    private ReconciledLocation Reconcile(DeploymentLocationHistory? history, DeploymentLocationSeed seed)
    {
        if (history is not null && !string.Equals(history.LocationId, seed.LocationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configured deployment-location identity does not match protected history.");
        }

        var latest = history?.Snapshots[^1];
        if (latest is not null && SameConfiguredLocation(latest, seed))
        {
            EnsureEffectiveAtStartup(latest, ToMilliseconds(_timeProvider.GetUtcNow()));
            return new ReconciledLocation(history!, latest, Appended: false);
        }

        var now = ToMilliseconds(_timeProvider.GetUtcNow());
        var effectiveFrom = seed.EffectiveFromUtc.HasValue
            ? ToMilliseconds(seed.EffectiveFromUtc.Value)
            : now;
        if (!seed.EffectiveFromUtc.HasValue && latest is not null && effectiveFrom <= latest.EffectiveFromUtc)
        {
            effectiveFrom = latest.EffectiveFromUtc.AddMilliseconds(1);
        }
        DateTimeOffset? effectiveUntil = seed.EffectiveUntilUtc.HasValue
            ? ToMilliseconds(seed.EffectiveUntilUtc.Value)
            : null;
        var version = latest?.Version + 1 ?? 1;
        var snapshot = DeploymentLocationSnapshot.Create(
            seed.LocationId,
            version,
            seed.Source,
            seed.HorizontalAccuracyMeters,
            effectiveFrom,
            effectiveUntil,
            seed.Coordinates.LatitudeDegrees,
            seed.Coordinates.LongitudeDegrees,
            seed.Coordinates.ElevationMeters,
            seed.Coordinates.TimeZoneId);
        var validation = snapshot.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"Configured deployment location is invalid ({validation.ReasonCode}, {validation.FieldPath}).");
        }
        EnsureEffectiveAtStartup(snapshot, now);
        if (latest is not null && (effectiveFrom <= latest.EffectiveFromUtc ||
            latest.EffectiveUntilUtc is { } previousUntil && effectiveFrom < previousUntil))
        {
            throw new InvalidDataException("Configured deployment-location interval overlaps protected history.");
        }

        var updated = new DeploymentLocationHistory(
            CurrentSchemaVersion,
            seed.LocationId,
            history is null ? [snapshot] : [.. history.Snapshots, snapshot],
            history is null
                ? new Dictionary<long, DateTimeOffset>()
                : new Dictionary<long, DateTimeOffset>(history.SupersededAtUtc)
                {
                    [latest!.Version] = effectiveFrom
                });
        ValidateHistory(updated);
        return new ReconciledLocation(updated, snapshot, Appended: true);
    }

    private static bool SameConfiguredLocation(DeploymentLocationSnapshot snapshot, DeploymentLocationSeed seed)
        => string.Equals(snapshot.LocationId, seed.LocationId, StringComparison.Ordinal) &&
           string.Equals(snapshot.Source, seed.Source, StringComparison.Ordinal) &&
           snapshot.HorizontalAccuracyMeters == seed.HorizontalAccuracyMeters &&
           snapshot.LatitudeDegrees == seed.Coordinates.LatitudeDegrees &&
           snapshot.LongitudeDegrees == seed.Coordinates.LongitudeDegrees &&
           snapshot.ElevationMeters == seed.Coordinates.ElevationMeters &&
           string.Equals(snapshot.TimeZoneId, seed.Coordinates.TimeZoneId, StringComparison.Ordinal) &&
           (!seed.EffectiveFromUtc.HasValue || snapshot.EffectiveFromUtc == ToMilliseconds(seed.EffectiveFromUtc.Value)) &&
           (!seed.EffectiveUntilUtc.HasValue || snapshot.EffectiveUntilUtc == ToMilliseconds(seed.EffectiveUntilUtc.Value));

    private static DeploymentLocationSeed NormalizeSeed(DeploymentLocationSeed seed)
        => seed with
        {
            LocationId = seed.LocationId?.Trim() ?? string.Empty,
            Source = seed.Source?.Trim() ?? string.Empty,
            Coordinates = seed.Coordinates with
            {
                LatitudeDegrees = seed.Coordinates.LatitudeDegrees == 0 ? 0 : seed.Coordinates.LatitudeDegrees,
                LongitudeDegrees = seed.Coordinates.LongitudeDegrees == 0 ? 0 : seed.Coordinates.LongitudeDegrees,
                ElevationMeters = seed.Coordinates.ElevationMeters == 0 ? 0 : seed.Coordinates.ElevationMeters,
                TimeZoneId = seed.Coordinates.TimeZoneId?.Trim() ?? string.Empty
            }
        };

    private static void EnsureEffectiveAtStartup(DeploymentLocationSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.EffectiveFromUtc > now || snapshot.EffectiveUntilUtc is { } until && until <= now)
        {
            throw new InvalidDataException("Configured deployment location is not effective at startup.");
        }
    }

    private async ValueTask<DeploymentLocationHistory> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetDirectoryName(path)!, path);
            var protectedPayload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var plaintext = _protector.Unprotect(protectedPayload);
            var history = JsonSerializer.Deserialize<DeploymentLocationHistory>(plaintext, SerializerOptions)
                ?? throw new InvalidDataException("Protected deployment-location history is empty.");
            ValidateHistory(history);
            return history;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException("Protected deployment-location history is unreadable.", exception);
        }
    }

    private async ValueTask WriteAsync(
        string path,
        DeploymentLocationHistory history,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(history, SerializerOptions);
        var protectedPayload = _protector.Protect(plaintext);
        EnsureProtectionKeysDurable(path);
        await WriteDurableAsync(path, protectedPayload, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteMarkerAsync(
        string path,
        DeploymentLocationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var marker = new DeploymentLocationMarker(CurrentSchemaVersion, snapshot.LocationId, snapshot.Version);
        await WriteDurableAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(marker, SerializerOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteDurableAsync(string path, byte[] payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        RawIngressFileStore.EnsureNoSymbolicLinks(directory, path);
        var temporaryPath = Path.Combine(directory, string.Concat('.', Path.GetFileName(path), '.', Guid.NewGuid().ToString("N"), ".tmp"));
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide a flush-to-disk contract.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            RestrictFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            RestrictFile(path);
            RawIngressFileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureProtectionKeysDurable(string statePath)
    {
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        var keyDirectory = Path.Combine(stateDirectory, "keys");
        if (!Directory.Exists(keyDirectory))
        {
            return;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(stateDirectory, keyDirectory);
        foreach (var keyFile in Directory.EnumerateFiles(keyDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            RawIngressFileStore.SyncFile(stateDirectory, keyFile);
        }
        RawIngressFileStore.SyncDirectory(keyDirectory);
    }

    private (string StatePath, string MarkerPath) EnsureStatePaths()
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("CameraAgent data root has no parent directory.");
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        var rootExisted = Directory.Exists(root);
        Directory.CreateDirectory(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        RestrictDirectory(root);
        if (!rootExisted)
        {
            RawIngressFileStore.SyncDirectory(parent);
        }
        var directory = Path.GetFullPath(Path.Combine(root, ".location"));
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        RestrictDirectory(directory);
        if (!directoryExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(root, directory);
        }
        var path = Path.GetFullPath(Path.Combine(directory, "deployment-location.v1.protected"));
        var rootPrefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(rootPrefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Deployment-location state path escapes the CameraAgent data root.");
        }
        return (path, Path.Combine(root, ".deployment-location.v1.identity"));
    }

    private static void ValidateHistory(DeploymentLocationHistory history)
    {
        if (history.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(history.LocationId) ||
            history.Snapshots is null || history.Snapshots.Count == 0 || history.SupersededAtUtc is null)
        {
            throw new InvalidDataException("Protected deployment-location history has an unsupported schema.");
        }
        DeploymentLocationSnapshot? previous = null;
        for (var index = 0; index < history.Snapshots.Count; index++)
        {
            var snapshot = history.Snapshots[index];
            var validation = snapshot.Validate();
            if (!validation.IsValid || !string.Equals(snapshot.LocationId, history.LocationId, StringComparison.Ordinal) ||
                snapshot.Version != (previous?.Version + 1 ?? 1) ||
                previous is not null && snapshot.EffectiveFromUtc <= previous.EffectiveFromUtc)
            {
                throw new InvalidDataException("Protected deployment-location history failed integrity validation.");
            }
            var isLatest = index == history.Snapshots.Count - 1;
            if (isLatest && history.SupersededAtUtc.ContainsKey(snapshot.Version) ||
                !isLatest && (!history.SupersededAtUtc.TryGetValue(snapshot.Version, out var superseded) ||
                    superseded != history.Snapshots[index + 1].EffectiveFromUtc ||
                    snapshot.EffectiveUntilUtc is { } until && until > superseded))
            {
                throw new InvalidDataException("Protected deployment-location history contains overlapping intervals.");
            }
            previous = snapshot;
        }
        if (history.SupersededAtUtc.Keys.Any(version => version < 1 || version >= history.Snapshots.Count + 1))
        {
            throw new InvalidDataException("Protected deployment-location supersession history is invalid.");
        }
    }

    private static bool IsEffective(
        DeploymentLocationHistory history,
        DeploymentLocationSnapshot snapshot,
        DateTimeOffset utc)
    {
        var effectiveUntil = snapshot.EffectiveUntilUtc;
        if (history.SupersededAtUtc.TryGetValue(snapshot.Version, out var superseded) &&
            (!effectiveUntil.HasValue || superseded < effectiveUntil.Value))
        {
            effectiveUntil = superseded;
        }
        return utc >= snapshot.EffectiveFromUtc && (!effectiveUntil.HasValue || utc < effectiveUntil.Value);
    }

    private static async ValueTask<DeploymentLocationMarker> ReadMarkerAsync(
        string path,
        CancellationToken cancellationToken)
        => JsonSerializer.Deserialize<DeploymentLocationMarker>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), SerializerOptions)
           ?? throw new InvalidDataException("Deployment-location identity marker is invalid.");

    private static void ValidateMarker(
        DeploymentLocationMarker? marker,
        DeploymentLocationHistory? history)
    {
        if (marker is null)
        {
            return;
        }
        if (marker.SchemaVersion != CurrentSchemaVersion || history is null ||
            !string.Equals(marker.LocationId, history.LocationId, StringComparison.Ordinal) ||
            marker.Version < 1 || marker.Version > history.Snapshots[^1].Version)
        {
            throw new InvalidDataException("Deployment-location identity marker does not match protected history.");
        }
    }

    private void ValidateRetainedEvidence(DeploymentLocationHistory history)
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        if (!Directory.Exists(root))
        {
            return;
        }
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*.json", enumeration))
        {
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                ValidateRetainedManifest(history, File.ReadAllBytes(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "Existing capture evidence could not be inspected for location provenance.", exception);
            }
        }
        ValidateJournalEvidence(root, history);
    }

    private static void ValidateRetainedManifest(DeploymentLocationHistory history, byte[] manifestJson)
    {
        var manifest = CaptureContractJson.ParseManifest(manifestJson).Document?.Manifest;
        var provenance = manifest?.Descriptor.Location;
        if (manifest is null || provenance is null)
        {
            return;
        }
        var snapshot = history.Snapshots.SingleOrDefault(item =>
            string.Equals(item.LocationId, provenance.LocationId, StringComparison.Ordinal) &&
            item.Version == provenance.Version);
        if (snapshot is null || snapshot.ToProvenance() != provenance ||
            !IsEffective(history, snapshot, manifest.Descriptor.Timing.ExposureStartedUtc))
        {
            throw new InvalidDataException(
                "Retained capture location provenance does not match protected deployment-location history.");
        }
    }

    private static void ValidateJournalEvidence(string root, DeploymentLocationHistory history)
    {
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path))
        {
            return;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'raw_captures';";
        if (Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json FROM raw_captures WHERE CAST(manifest_json AS TEXT) LIKE '%\"location\"%';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ValidateRetainedManifest(history, (byte[])reader.GetValue(0));
        }
    }

    private bool HasLocationBearingEvidence()
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        if (!Directory.Exists(root))
        {
            return false;
        }
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*.json", enumeration))
        {
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
                if (parsed.Document?.Manifest?.Descriptor.Location is not null)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "Existing capture evidence could not be inspected for location provenance.", exception);
            }
        }
        return JournalContainsLocationEvidence(root);
    }

    private static bool JournalContainsLocationEvidence(string root)
    {
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path))
        {
            return false;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'raw_captures';";
        if (Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM raw_captures WHERE CAST(manifest_json AS TEXT) LIKE '%\"location\"%' LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private void EnsurePhysicalStatePath(string path)
        => RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetFullPath(_options.RawIngressRoot), path);

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record DeploymentLocationHistory(
        int SchemaVersion,
        string LocationId,
        IReadOnlyList<DeploymentLocationSnapshot> Snapshots,
        IReadOnlyDictionary<long, DateTimeOffset> SupersededAtUtc);

    private sealed record DeploymentLocationMarker(int SchemaVersion, string LocationId, long Version);

    private sealed record ReconciledLocation(
        DeploymentLocationHistory History,
        DeploymentLocationSnapshot Active,
        bool Appended);
}

internal static partial class DeploymentLocationLog
{
    [LoggerMessage(7301, LogLevel.Information,
        "Deployment location {LocationId} version {Version} appended to protected history")]
    internal static partial void VersionAppended(ILogger logger, string locationId, long version);

    [LoggerMessage(7302, LogLevel.Information,
        "Deployment location {LocationId} version {Version} loaded from protected history")]
    internal static partial void VersionLoaded(ILogger logger, string locationId, long version);
}
