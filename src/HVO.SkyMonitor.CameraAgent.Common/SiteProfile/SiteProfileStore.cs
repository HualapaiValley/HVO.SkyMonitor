using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.SiteProfile;

/// <summary>Reads and replaces the operator-maintained site profile.</summary>
public interface ISiteProfileStore
{
    /// <summary>The current profile and the settings file version a save must name.</summary>
    ValueTask<SiteProfileState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the whole profile when <see cref="SiteProfileRequest.ExpectedVersion"/> is current.</summary>
    ValueTask<SiteProfileResult> ApplyAsync(SiteProfileRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the site profile in the <c>CameraAgent:Site</c> section of the operator settings file, the same
/// <c>appsettings.local.json</c> an operator edits by hand, so the profile has one persisted place whichever way it
/// is changed.
/// </summary>
public sealed class SettingsFileSiteProfileStore(
    IOptions<CameraAgentHostOptions> options,
    ILogger<SettingsFileSiteProfileStore> logger,
    OperatorSettingsFile? settingsFile = null) : ISiteProfileStore, IDisposable
{
    internal const string SectionKey = "CameraAgent:Site";
    internal const string ObservatoryNameKey = SectionKey + ":ObservatoryName";
    internal const string CameraNameKey = SectionKey + ":CameraName";
    internal const string OwnerNameKey = SectionKey + ":OwnerName";
    internal const string OwnerContactKey = SectionKey + ":OwnerContact";
    internal const string InstallerNameKey = "CameraAgent:DisplayName";
    internal const string MigrationActor = "site-profile-migration";

    private const string NoSettingsFileProblem =
        "This host loads no operator settings file, so the site profile cannot be saved here.";

    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private readonly CameraAgentHostOptions _options = options.Value;
    private volatile bool _migrated;

    public async ValueTask<SiteProfileState> GetAsync(CancellationToken cancellationToken)
    {
        if (settingsFile is null)
        {
            return Unavailable();
        }
        await EnsureMigratedAsync(settingsFile, cancellationToken).ConfigureAwait(false);
        return Project(await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<SiteProfileResult> ApplyAsync(
        SiteProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = request.Actor?.Trim() ?? string.Empty;
        if (request.Profile is null || request.ExpectedVersion is null ||
            actor.Length == 0 || !IsBoundedText(actor, SiteProfileLimits.MaximumActorLength))
        {
            return new(
                SiteProfileStatus.Invalid,
                SiteProfileLimits.InvalidCommandReasonCode,
                "command",
                await GetAsync(cancellationToken).ConfigureAwait(false));
        }

        var (profile, invalidField) = Normalize(request.Profile);
        if (invalidField is not null)
        {
            return new(
                SiteProfileStatus.Invalid,
                SiteProfileLimits.InvalidFieldReasonCode,
                invalidField,
                await GetAsync(cancellationToken).ConfigureAwait(false));
        }
        if (settingsFile is null)
        {
            return new(SiteProfileStatus.Invalid, SiteProfileLimits.UnavailableReasonCode, null, Unavailable());
        }

        await EnsureMigratedAsync(settingsFile, cancellationToken).ConfigureAwait(false);
        var snapshot = await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var current = Project(snapshot);
        if (snapshot.Problem is not null)
        {
            return new(SiteProfileStatus.Invalid, SiteProfileLimits.UnreadableReasonCode, null, current);
        }
        if (current.Profile == profile)
        {
            return new(SiteProfileStatus.Unchanged, null, null, current);
        }
        if (!string.Equals(request.ExpectedVersion, snapshot.Version, StringComparison.Ordinal))
        {
            return new(
                SiteProfileStatus.Conflict,
                SiteProfileLimits.ExpectedVersionConflictReasonCode,
                "expectedVersion",
                current);
        }

        var result = await settingsFile.WriteAsync(
            request.ExpectedVersion,
            ToSettings(profile),
            actor,
            cancellationToken).ConfigureAwait(false);
        var state = Project(result.Snapshot);
        return result.Status switch
        {
            OperatorSettingsWriteStatus.Applied => new(SiteProfileStatus.Applied, null, null, state),
            OperatorSettingsWriteStatus.Unchanged => new(SiteProfileStatus.Unchanged, null, null, state),
            OperatorSettingsWriteStatus.Conflict => new(
                SiteProfileStatus.Conflict,
                SiteProfileLimits.ExpectedVersionConflictReasonCode,
                "expectedVersion",
                state),
            _ => new(SiteProfileStatus.Invalid, SiteProfileLimits.UnreadableReasonCode, null, state)
        };
    }

    /// <summary>
    /// Moves a profile recorded by an earlier release, and an installer-configured camera name, into the settings
    /// file once, so the file is the only place either is kept from then on. The name the installer seeded into the
    /// file is removed in the same write, leaving the site section as the one place the camera name is set.
    /// </summary>
    private async ValueTask EnsureMigratedAsync(OperatorSettingsFile file, CancellationToken cancellationToken)
    {
        if (_migrated)
        {
            return;
        }
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated)
            {
                return;
            }
            var snapshot = await file.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Problem is not null)
            {
                // The operator has to correct the file first; the next read tries again.
                return;
            }

            var legacyPath = LegacyRecordPath();
            var legacy = ReadLegacyRecord(legacyPath);
            if (!snapshot.HasSection(SectionKey))
            {
                var configured = ConfiguredName();
                var seed = legacy is null
                    ? new SiteProfileValues(null, configured, null, null)
                    : legacy with { CameraName = legacy.CameraName ?? configured };
                if (seed != SiteProfileValues.Empty)
                {
                    var settings = new Dictionary<string, JsonNode?>(ToSettings(seed), StringComparer.Ordinal)
                    {
                        [InstallerNameKey] = null
                    };
                    var result = await file.WriteAsync(snapshot.Version, settings, MigrationActor, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Status is not (OperatorSettingsWriteStatus.Applied or OperatorSettingsWriteStatus.Unchanged))
                    {
                        return;
                    }
                    SiteProfileLog.Migrated(logger, file.FilePath);
                }
            }
            else if (snapshot.GetValue(InstallerNameKey) is not null)
            {
                // A site section written by hand into the installer's seed before this first read already settles
                // the profile; only the seeded name goes, so the file does not carry the camera name twice.
                var result = await file.WriteAsync(
                    snapshot.Version,
                    new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [InstallerNameKey] = null },
                    MigrationActor,
                    cancellationToken).ConfigureAwait(false);
                if (result.Status is not (OperatorSettingsWriteStatus.Applied or OperatorSettingsWriteStatus.Unchanged))
                {
                    return;
                }
                SiteProfileLog.SeededNameRemoved(logger, file.FilePath);
            }

            // A record already in the file, or superseded by a section the operator wrote, is removed; one that
            // cannot be read is left where it is rather than deleted unread.
            if (legacy is not null)
            {
                DeleteLegacyRecord(legacyPath);
            }
            _migrated = true;
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    private SiteProfileState Project(OperatorSettingsSnapshot snapshot)
    {
        var profile = Normalize(new SiteProfileValues(
            snapshot.GetValue(ObservatoryNameKey),
            snapshot.GetValue(CameraNameKey),
            snapshot.GetValue(OwnerNameKey),
            snapshot.GetValue(OwnerContactKey))).Profile;

        // Once the file has a site section, its camera name is the name, even when the operator has cleared it.
        var effective = profile.CameraName ?? (snapshot.HasSection(SectionKey) ? null : ConfiguredName());
        return new SiteProfileState(snapshot.Version, profile, effective, settingsFile?.FilePath, snapshot.Problem);
    }

    private SiteProfileState Unavailable()
        => new(OperatorSettingsFile.AbsentVersion, SiteProfileValues.Empty, ConfiguredName(), null, NoSettingsFileProblem);

    private string? ConfiguredName()
        => string.IsNullOrWhiteSpace(_options.DisplayName) ? null : _options.DisplayName.Trim();

    /// <summary>
    /// The four profile keys, a cleared field removing its key. A profile with every field cleared keeps an empty
    /// section, which records that the operator cleared it rather than never set it.
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonNode?> ToSettings(SiteProfileValues profile)
    {
        if (profile == SiteProfileValues.Empty)
        {
            return new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [SectionKey] = new JsonObject() };
        }
        return new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [ObservatoryNameKey] = Value(profile.ObservatoryName),
            [CameraNameKey] = Value(profile.CameraName),
            [OwnerNameKey] = Value(profile.OwnerName),
            [OwnerContactKey] = Value(profile.OwnerContact)
        };

        static JsonNode? Value(string? value) => value is null ? null : JsonValue.Create(value);
    }

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

    /// <summary>Where an earlier release kept the profile, with its revision history, under the data root.</summary>
    internal string LegacyRecordPath()
        => Path.Combine(Path.GetFullPath(_options.RawIngressRoot), ".site", "site-profile.v1.json");

    /// <summary>The current profile from an earlier release's record, or null when there is none it can read.</summary>
    private SiteProfileValues? ReadLegacyRecord(string path)
    {
        try
        {
            var root = Path.GetFullPath(_options.RawIngressRoot);
            if (!File.Exists(path))
            {
                return null;
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            if (new FileInfo(path).Length > OperatorSettingsFile.MaximumFileBytes)
            {
                throw new InvalidDataException("The retired site profile record is too large.");
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("profile", out var profile) ||
                profile.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The retired site profile record has no profile.");
            }
            var (values, invalid) = Normalize(new SiteProfileValues(
                Text(profile, "observatoryName"),
                Text(profile, "cameraName"),
                Text(profile, "ownerName"),
                Text(profile, "ownerContact")));
            return invalid is null ? values : throw new InvalidDataException("The retired site profile is out of bounds.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException)
        {
            SiteProfileLog.LegacyUnreadable(logger, exception);
            return null;
        }

        static string? Text(JsonElement profile, string name)
            => profile.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private void DeleteLegacyRecord(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            RawIngressFileStore.EnsureNoSymbolicLinks(Path.GetFullPath(_options.RawIngressRoot), directory);
            File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SiteProfileLog.LegacyNotRemoved(logger, exception);
        }
    }

    public void Dispose()
    {
        _migrationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static partial class SiteProfileLog
{
    [LoggerMessage(7601, LogLevel.Information, "Site profile moved into the operator settings file {Path}")]
    internal static partial void Migrated(ILogger logger, string path);

    [LoggerMessage(7602, LogLevel.Warning, "The site profile recorded by an earlier release could not be read and was left in place")]
    internal static partial void LegacyUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(7603, LogLevel.Warning, "The site profile recorded by an earlier release could not be removed after it was moved")]
    internal static partial void LegacyNotRemoved(ILogger logger, Exception exception);

    [LoggerMessage(7604, LogLevel.Information, "The installer's seeded camera name was removed from the operator settings file {Path}, which already has a site section")]
    internal static partial void SeededNameRemoved(ILogger logger, string path);
}
