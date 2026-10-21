using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// Where the active time pair comes from. Any configured override setting selects the override. Otherwise the adopted
/// managed pair and the bundled pair compete by planning coverage (see <see cref="CameraAgentPinnedOrbitalTimeSource"/>).
/// </summary>
public enum CameraAgentOrbitalTimeDataSource
{
    Bundled,
    Override,
    Managed
}

/// <summary>Why no pair is active. Fixed codes; they never carry a path or exception text.</summary>
public enum CameraAgentOrbitalTimeDataProblem
{
    None,
    Disabled,
    OverrideIncomplete,
    OverrideInvalid,
    BundledInvalid
}

/// <summary>One verified bundled or managed file as its provenance record describes it.</summary>
public sealed record CameraAgentTimeFileProvenance(string File, Uri SourceUrl, DateTimeOffset RetrievedUtc, string Sha256, string Terms);

/// <summary>Whether the adopted managed pair exists and verified, whichever source is active.</summary>
public enum CameraAgentManagedTimeDataState
{
    None,
    Verified,
    Invalid
}

/// <summary>
/// The active source and pair. Data is present exactly when Problem is None. The provenance fields describe the active
/// bundled or managed pair. Managed and ManagedPair describe the adopted managed pair even when another source wins, and
/// ManagedCoverageEndUtc is its last supported planning instant when it verified.
/// </summary>
public sealed record CameraAgentOrbitalTimeResolution(
    CameraAgentOrbitalTimeDataSource Source,
    CameraAgentOrbitalTimeDataProblem Problem,
    OrbitalTimeData? Data,
    CameraAgentTimeFileProvenance? EarthOrientationProvenance = null,
    CameraAgentTimeFileProvenance? LeapSecondsProvenance = null,
    CameraAgentManagedTimeDataState Managed = CameraAgentManagedTimeDataState.None,
    CameraAgentManagedTimePointer? ManagedPair = null,
    DateTimeOffset? ManagedCoverageEndUtc = null);

/// <summary>Locally resolved immutable actual time bytes.</summary>
public interface ICameraAgentOrbitalTimeSource
{
    ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken);
}

public static class CameraAgentOrbitalTimeSourceExtensions
{
    /// <summary>The active pair, or null when it is disabled or unavailable.</summary>
    public static async ValueTask<OrbitalTimeData?> ReadAsync(this ICameraAgentOrbitalTimeSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (await source.ResolveAsync(cancellationToken).ConfigureAwait(false)).Data;
    }
}

/// <summary>
/// Bounded pinned local-file loader. A configured override (any of the four path/hash settings) takes precedence and
/// must be complete and valid; an invalid override never falls back to another pair. Otherwise the pair bundled with
/// this release and the managed pair the refresh last adopted (when there is one) are each verified against their
/// provenance SHA-256 values and parsed, and the pair whose planning coverage ends later is active; on a tie, or when
/// only one verifies, the bundled pair wins over the managed one, so a newer release is never shadowed by an older
/// refresh. This loader never downloads, writes or replaces data with nominal-zero Earth orientation; a different pair
/// becomes active only by an options change, a new release or an adoption. Problems carry no paths.
/// </summary>
public sealed class CameraAgentPinnedOrbitalTimeSource : ICameraAgentOrbitalTimeSource, IDisposable
{
    public const int MaximumEarthOrientationBytes = 8 * 1024 * 1024;
    public const int MaximumLeapSecondsBytes = 64 * 1024;
    public const int MaximumProvenanceBytes = 64 * 1024;
    public const string ProvenanceFileName = "provenance.json";
    public const string ProvenanceSchema = "hvo-bundled-time-data-v1";

    /// <summary>The fixed directory the release payload carries the bundled pair in.</summary>
    public static string BundledDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "time-data");

    private readonly IOptionsMonitor<CameraAgentSatellitePlanningOptions> options;
    private readonly string bundledDirectory;
    private readonly CameraAgentManagedTimeDataStore? managed;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedConfiguration;
    private CameraAgentOrbitalTimeResolution? cached;

    public CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options,
        CameraAgentManagedTimeDataStore? managed = null)
        : this(options, BundledDirectory, managed)
    {
    }

    internal CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options, string bundledDirectory,
        CameraAgentManagedTimeDataStore? managed = null)
    {
        this.options = options;
        this.bundledDirectory = bundledDirectory;
        this.managed = managed;
    }

    /// <summary>True when any override setting is present, which selects the override over the bundled pair.</summary>
    public static bool HasOverride(CameraAgentSatellitePlanningOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration.EarthOrientationPath) || !string.IsNullOrWhiteSpace(configuration.EarthOrientationSha256) ||
            !string.IsNullOrWhiteSpace(configuration.LeapSecondsPath) || !string.IsNullOrWhiteSpace(configuration.LeapSecondsSha256);
    }

    /// <summary>The last instant a pair supports planning: the earlier of its last Earth-orientation row and the leap expiry.</summary>
    public static DateTimeOffset PlanningCoverageEnd(OrbitalTimeData time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return time.EarthOrientationCoverage is { } eop && eop.EndUtc < time.LeapTableCoverage.EndUtc ? eop.EndUtc : time.LeapTableCoverage.EndUtc;
    }

    public async ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = options.CurrentValue.Copy();
        var source = HasOverride(configuration) ? CameraAgentOrbitalTimeDataSource.Override : CameraAgentOrbitalTimeDataSource.Bundled;
        if (!configuration.Enabled) return new(source, CameraAgentOrbitalTimeDataProblem.Disabled, null);
        if (source == CameraAgentOrbitalTimeDataSource.Override &&
            (!Path.IsPathFullyQualified(configuration.EarthOrientationPath ?? "") || !Path.IsPathFullyQualified(configuration.LeapSecondsPath ?? "") ||
             !IsHash(configuration.EarthOrientationSha256) || !IsHash(configuration.LeapSecondsSha256)))
            return new(source, CameraAgentOrbitalTimeDataProblem.OverrideIncomplete, null);
        var pointer = await ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        // The adopted pair is part of the identity, so an adoption is picked up by the next resolution.
        var identity = Convert.ToHexString(SHA256.HashData(OrbitalContract.SerializeCanonical(configuration))) + "/" +
            (pointer.Invalid ? "invalid" : pointer.Value?.Pair ?? "none");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (identity == cachedConfiguration && cached is not null) return cached;
            // A failed explicit adoption also clears the previous inputs. Never silently use an older pair.
            cached = null;
            cachedConfiguration = null;
            var resolution = source == CameraAgentOrbitalTimeDataSource.Override
                ? await ReadOverrideAsync(configuration, cancellationToken).ConfigureAwait(false)
                : await ReadBundledOrManagedAsync(pointer, cancellationToken).ConfigureAwait(false);
            if (resolution.Data is not null)
            {
                cached = resolution;
                cachedConfiguration = identity;
            }
            return resolution;
        }
        finally { gate.Release(); }
    }

    private async Task<(CameraAgentManagedTimePointer? Value, bool Invalid)> ReadPointerAsync(CancellationToken cancellationToken)
    {
        if (managed is null) return (null, false);
        try
        {
            return (await managed.ReadPointerAsync(cancellationToken).ConfigureAwait(false), false);
        }
        catch (Exception exception) when (IsInputFailure(exception))
        {
            return (null, true);
        }
    }

    private static async Task<CameraAgentOrbitalTimeResolution> ReadOverrideAsync(CameraAgentSatellitePlanningOptions configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var eop = await ReadPinnedAsync(configuration.EarthOrientationPath!, configuration.EarthOrientationSha256!,
                MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
            var leaps = await ReadPinnedAsync(configuration.LeapSecondsPath!, configuration.LeapSecondsSha256!,
                MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
            return new(CameraAgentOrbitalTimeDataSource.Override, CameraAgentOrbitalTimeDataProblem.None, OrbitalTimeData.FromFinals2000A(eop, leaps));
        }
        catch (Exception exception) when (IsInputFailure(exception))
        {
            return new(CameraAgentOrbitalTimeDataSource.Override, CameraAgentOrbitalTimeDataProblem.OverrideInvalid, null);
        }
    }

    private async Task<CameraAgentOrbitalTimeResolution> ReadBundledOrManagedAsync(
        (CameraAgentManagedTimePointer? Value, bool Invalid) pointer, CancellationToken cancellationToken)
    {
        var bundled = await ReadProvenancedAsync(bundledDirectory, ProvenanceSchema, cancellationToken).ConfigureAwait(false);
        var state = pointer.Invalid ? CameraAgentManagedTimeDataState.Invalid : CameraAgentManagedTimeDataState.None;
        PinnedPair? adopted = null;
        if (pointer.Value is { } value)
        {
            adopted = await ReadProvenancedAsync(managed!.PairDirectory(value.Pair), CameraAgentManagedTimeDataStore.ProvenanceSchema,
                cancellationToken).ConfigureAwait(false);
            state = adopted is null ? CameraAgentManagedTimeDataState.Invalid : CameraAgentManagedTimeDataState.Verified;
        }
        var managedEnd = adopted is null ? (DateTimeOffset?)null : PlanningCoverageEnd(adopted.Data);
        // The later planning coverage wins; a tie, or only the bundled pair verifying, keeps the bundled pair.
        var useManaged = adopted is not null && (bundled is null || managedEnd > PlanningCoverageEnd(bundled.Data));
        var active = useManaged ? adopted : bundled;
        var source = useManaged ? CameraAgentOrbitalTimeDataSource.Managed : CameraAgentOrbitalTimeDataSource.Bundled;
        return active is null
            ? new(source, CameraAgentOrbitalTimeDataProblem.BundledInvalid, null, Managed: state, ManagedPair: pointer.Value)
            : new(source, CameraAgentOrbitalTimeDataProblem.None, active.Data, active.EarthOrientation, active.LeapSeconds,
                state, pointer.Value, managedEnd);
    }

    /// <summary>A provenance-described pair directory, verified and parsed; null when anything fails.</summary>
    internal static async Task<PinnedPair?> ReadProvenancedAsync(string directory, string schema, CancellationToken cancellationToken)
    {
        try
        {
            var provenance = await ReadBoundedAsync(Path.Combine(directory, ProvenanceFileName), MaximumProvenanceBytes,
                cancellationToken).ConfigureAwait(false);
            var (earthOrientation, leapSeconds) = ParseProvenance(provenance, schema);
            var eop = await ReadPinnedAsync(Path.Combine(directory, earthOrientation.File), earthOrientation.Sha256,
                MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
            var leaps = await ReadPinnedAsync(Path.Combine(directory, leapSeconds.File), leapSeconds.Sha256,
                MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
            var data = OrbitalTimeData.FromFinals2000A(eop, leaps);
            return data.NominalZeroEarthOrientation ? null : new(data, earthOrientation, leapSeconds);
        }
        catch (Exception exception) when (IsInputFailure(exception) || exception is JsonException)
        {
            return null;
        }
    }

    internal sealed record PinnedPair(OrbitalTimeData Data, CameraAgentTimeFileProvenance EarthOrientation, CameraAgentTimeFileProvenance LeapSeconds);

    internal static (CameraAgentTimeFileProvenance EarthOrientation, CameraAgentTimeFileProvenance LeapSeconds) ParseProvenance(byte[] bytes,
        string schema)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != schema) throw new InvalidDataException();
        return (Entry(root, "earthOrientation"), Entry(root, "leapSeconds"));

        static CameraAgentTimeFileProvenance Entry(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var entry) || entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var file = Text(entry, "file");
            var url = Text(entry, "sourceUrl");
            var sha256 = Text(entry, "sha256");
            // A simple file name inside the pair directory, an approved HTTPS origin and an exact UTC retrieval time.
            if (file is null || file != Path.GetFileName(file) || file is "." or ".." || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsHash(sha256) ||
                Text(entry, "terms") is not { Length: > 0 } terms ||
                !DateTimeOffset.TryParse(Text(entry, "retrievedUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var retrieved) || retrieved.Offset != TimeSpan.Zero)
                throw new InvalidDataException();
            return new(file, uri, retrieved, sha256!.ToUpperInvariant(), terms);
        }

        static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool IsInputFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public void Dispose() => gate.Dispose();

    private static async Task<byte[]> ReadPinnedAsync(string path, string expectedHash, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return bytes;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length is <= 0 || length > maximumBytes) throw new InvalidDataException();
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0) throw new InvalidDataException();
        return bytes;
    }
}
