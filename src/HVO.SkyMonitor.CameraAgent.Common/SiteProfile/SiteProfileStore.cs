using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.SiteProfile;

/// <summary>Reads and replaces the operator-maintained site profile.</summary>
public interface ISiteProfileStore
{
    /// <summary>The current profile. Version zero means no operator has recorded one yet.</summary>
    ValueTask<SiteProfileState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the whole profile when <see cref="SiteProfileRequest.ExpectedVersion"/> is current.</summary>
    ValueTask<SiteProfileResult> ApplyAsync(SiteProfileRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the site profile as one owner-only JSON document under the CameraAgent data root. The profile is
/// descriptive, so unlike deployment geometry it is not protected, but it is written with the same durable
/// replace-and-sync discipline so a crash leaves either the previous or the next version, never a torn one.
/// </summary>
public sealed class FileSiteProfileStore(
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<FileSiteProfileStore> logger) : ISiteProfileStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CameraAgentHostOptions _options = options.Value;
    private SiteProfileRecord? _record;
    private bool _loaded;

    /// <summary>Flushes the record directory after the replace; tests substitute it to fail the step after publication.</summary>
    internal Action<string> SyncPublishedDirectory { get; init; } = RawIngressFileStore.SyncDirectory;

    public async ValueTask<SiteProfileState> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Project(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SiteProfileResult> ApplyAsync(
        SiteProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var actor = request.Actor?.Trim() ?? string.Empty;
            var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (request.Profile is null ||
                actor.Length == 0 || !IsBoundedText(actor, SiteProfileLimits.MaximumActorLength) ||
                idempotencyKey.Length == 0 || !IsBoundedText(idempotencyKey, SiteProfileLimits.MaximumIdempotencyKeyLength) ||
                !IsBoundedText(reason, SiteProfileLimits.MaximumReasonLength))
            {
                return Outcome(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidCommandReasonCode, "command", record);
            }

            var (profile, invalidField) = Normalize(request.Profile);
            if (invalidField is not null)
            {
                return Outcome(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidFieldReasonCode, invalidField, record);
            }

            var revisions = record?.Revisions ?? [];
            var replay = revisions.FirstOrDefault(item =>
                string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            if (replay is not null)
            {
                return replay.Profile == profile
                    ? Outcome(SiteProfileStatus.Replayed, null, null, record)
                    : Outcome(
                        SiteProfileStatus.Conflict,
                        SiteProfileLimits.IdempotencyKeyConflictReasonCode,
                        "idempotencyKey",
                        record);
            }

            var currentVersion = record?.Version ?? 0;
            if (request.ExpectedVersion != currentVersion)
            {
                return Outcome(
                    SiteProfileStatus.Conflict,
                    SiteProfileLimits.ExpectedVersionConflictReasonCode,
                    "expectedVersion",
                    record);
            }

            if ((record?.Profile ?? SiteProfileValues.Empty) == profile)
            {
                return Outcome(SiteProfileStatus.Unchanged, null, null, record);
            }

            var version = currentVersion + 1;
            var next = new SiteProfileRecord(
                CurrentSchemaVersion,
                version,
                profile,
                [
                    .. revisions.TakeLast(SiteProfileLimits.MaximumRetainedRevisions - 1),
                    new SiteProfileRevision(
                        version,
                        DateTimeOffset.FromUnixTimeMilliseconds(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
                        actor,
                        reason,
                        idempotencyKey,
                        profile)
                ]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, SerializerOptions);
            try
            {
                await WriteDurableAsync(EnsureStatePath(), bytes, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A step after the replace can fail once the new version is already published, so the file, not
                // this instance, decides what is current: the next read or write loads it again.
                _record = null;
                _loaded = false;
                throw;
            }
            _record = next;
            SiteProfileLog.Recorded(logger, version);
            return Outcome(SiteProfileStatus.Applied, null, null, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<SiteProfileRecord?> LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return _record;
        }

        var path = EnsureStatePath();
        SiteProfileRecord? record = null;
        if (File.Exists(path))
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    record = await JsonSerializer.DeserializeAsync<SiteProfileRecord>(
                        stream,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("The site profile record is not valid JSON.", exception);
                }
            }

            Validate(record);
            SiteProfileLog.Loaded(logger, record!.Version);
        }

        _record = record;
        _loaded = true;
        return record;
    }

    private SiteProfileState Project(SiteProfileRecord? record)
    {
        var profile = record?.Profile ?? SiteProfileValues.Empty;
        var configured = string.IsNullOrWhiteSpace(_options.DisplayName) ? null : _options.DisplayName.Trim();
        var latest = record?.Revisions.Count > 0 ? record.Revisions[^1] : null;
        return new SiteProfileState(
            record?.Version ?? 0,
            profile,
            profile.CameraName ?? configured,
            configured,
            latest?.RecordedAtUtc,
            latest?.Actor,
            record is null
                ? []
                : [.. record.Revisions.Reverse().Take(SiteProfileLimits.MaximumProjectedRevisions)]);
    }

    private SiteProfileResult Outcome(
        SiteProfileStatus status,
        string? reasonCode,
        string? fieldPath,
        SiteProfileRecord? record)
        => new(status, reasonCode, fieldPath, Project(record));

    /// <summary>Trims each field, turns blanks into "not set", and names the first field that is out of bounds.</summary>
    internal static (SiteProfileValues Profile, string? InvalidField) Normalize(SiteProfileValues profile)
    {
        var observatory = Clean(profile.ObservatoryName);
        var camera = Clean(profile.CameraName);
        var owner = Clean(profile.OwnerName);
        var contact = Clean(profile.OwnerContact);
        var invalid = !IsBoundedText(observatory, SiteProfileLimits.MaximumNameLength) ? "observatoryName"
            : !IsBoundedText(camera, SiteProfileLimits.MaximumNameLength) ? "cameraName"
            : !IsBoundedText(owner, SiteProfileLimits.MaximumNameLength) ? "ownerName"
            : !IsBoundedText(contact, SiteProfileLimits.MaximumContactLength) ? "ownerContact"
            : null;
        return (new SiteProfileValues(observatory, camera, owner, contact), invalid);

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Absent, or within the length bound and free of control characters.</summary>
    private static bool IsBoundedText(string? value, int maximumLength)
        => value is null || (value.Length <= maximumLength && !value.Any(char.IsControl));

    private static void Validate(SiteProfileRecord? record)
    {
        // Every retained revision is checked, not only the latest, because each one is shown in the history.
        if (record is null || record.SchemaVersion != CurrentSchemaVersion || record.Version < 1 ||
            record.Profile is null || record.Revisions is null || record.Revisions.Count == 0 ||
            record.Revisions.Count > SiteProfileLimits.MaximumRetainedRevisions ||
            record.Revisions[^1] is null ||
            record.Revisions[^1].Version != record.Version ||
            record.Revisions[^1].Profile != record.Profile ||
            record.Revisions.Any(static item => !IsValidRevision(item)))
        {
            throw new InvalidDataException("The site profile record has an unsupported or inconsistent schema.");
        }

        for (var index = 1; index < record.Revisions.Count; index++)
        {
            if (record.Revisions[index].Version <= record.Revisions[index - 1].Version)
            {
                throw new InvalidDataException("The site profile revisions are not in version order.");
            }
        }
    }

    private static bool IsValidRevision(SiteProfileRevision? revision)
        => revision is { Version: >= 1, Profile: not null } &&
            Normalize(revision.Profile) is (_, null) &&
            !string.IsNullOrWhiteSpace(revision.Actor) &&
            IsBoundedText(revision.Actor, SiteProfileLimits.MaximumActorLength) &&
            !string.IsNullOrWhiteSpace(revision.IdempotencyKey) &&
            IsBoundedText(revision.IdempotencyKey, SiteProfileLimits.MaximumIdempotencyKeyLength) &&
            IsBoundedText(revision.Reason, SiteProfileLimits.MaximumReasonLength);

    private string EnsureStatePath()
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("CameraAgent data root has no parent directory.");
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        var rootExisted = Directory.Exists(root);
        Directory.CreateDirectory(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(parent, root);
        if (!rootExisted)
        {
            RestrictDirectory(root);
            RawIngressFileStore.SyncDirectory(parent);
        }

        var directory = Path.GetFullPath(Path.Combine(root, ".site"));
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        RestrictDirectory(directory);
        if (!directoryExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(root, directory);
        }

        var path = Path.GetFullPath(Path.Combine(directory, "site-profile.v1.json"));
        var rootPrefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException("Site profile path escapes the CameraAgent data root.");
        }

        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        return path;
    }

    private async ValueTask WriteDurableAsync(string path, byte[] payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporaryPath = Path.Combine(
            directory,
            string.Concat('.', Path.GetFileName(path), '.', Guid.NewGuid().ToString("N"), ".tmp"));
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
            SyncPublishedDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

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

    private sealed record SiteProfileRecord(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] long Version,
        [property: JsonRequired] SiteProfileValues Profile,
        [property: JsonRequired] IReadOnlyList<SiteProfileRevision> Revisions);
}

internal static partial class SiteProfileLog
{
    [LoggerMessage(7601, LogLevel.Information, "Site profile version {Version} recorded")]
    internal static partial void Recorded(ILogger logger, long version);

    [LoggerMessage(7602, LogLevel.Debug, "Site profile version {Version} loaded")]
    internal static partial void Loaded(ILogger logger, long version);
}
