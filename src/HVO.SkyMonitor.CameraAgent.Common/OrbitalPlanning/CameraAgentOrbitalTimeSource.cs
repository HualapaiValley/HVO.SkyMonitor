using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// Where the active time pair comes from. Any configured override setting selects the override. Otherwise the bundled
/// and managed pairs compete by coverage of the requested time and then of planning (see
/// <see cref="CameraAgentPinnedOrbitalTimeSource"/>).
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
/// bundled or managed pair, and ActiveManagedPair names the active managed pair: the adopted one, or the one it replaced
/// while the adopted one does not yet cover the requested time. Managed and ManagedPair describe the adopted managed pair
/// even when another pair is active, and ManagedCoverageEndUtc is its last supported planning instant when it verified.
/// </summary>
public sealed record CameraAgentOrbitalTimeResolution(
    CameraAgentOrbitalTimeDataSource Source,
    CameraAgentOrbitalTimeDataProblem Problem,
    OrbitalTimeData? Data,
    CameraAgentTimeFileProvenance? EarthOrientationProvenance = null,
    CameraAgentTimeFileProvenance? LeapSecondsProvenance = null,
    CameraAgentManagedTimeDataState Managed = CameraAgentManagedTimeDataState.None,
    CameraAgentManagedTimePointer? ManagedPair = null,
    DateTimeOffset? ManagedCoverageEndUtc = null,
    string? ActiveManagedPair = null);

/// <summary>Locally resolved immutable actual time bytes.</summary>
public interface ICameraAgentOrbitalTimeSource
{
    /// <summary>The pair for the current instant.</summary>
    ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>The pair for a planning window, which the window's queries are then validated against.</summary>
    ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(OrbitalUtcInterval window, CancellationToken cancellationToken);
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
/// this release, the managed pair the refresh last adopted and, while that one verifies, the managed pair it replaced
/// are each verified against their provenance SHA-256 values and parsed. Only pairs that continuously cover the
/// requested window (the current instant when the caller has none) are eligible, so a refresh retrieved after the
/// provider's daily update, which cannot evaluate the rest of that UTC day, never takes the day's coverage away. Among
/// the eligible pairs the one whose planning coverage ends later is active; on a tie the bundled pair wins over the
/// managed ones, so a newer release is never shadowed by an older refresh. When none is eligible the same rule ranks
/// every verified pair, and the caller's coverage check reports why. This loader never downloads, writes or replaces
/// data with nominal-zero Earth orientation; a different pair becomes active only by an options change, a new release,
/// an adoption or a later requested time. Problems carry no paths.
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
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedConfiguration;
    private VerifiedPairs? cached;

    public CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options,
        CameraAgentManagedTimeDataStore? managed = null, TimeProvider? timeProvider = null)
        : this(options, BundledDirectory, managed, timeProvider)
    {
    }

    internal CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options, string bundledDirectory,
        CameraAgentManagedTimeDataStore? managed = null, TimeProvider? timeProvider = null)
    {
        this.options = options;
        this.bundledDirectory = bundledDirectory;
        this.managed = managed;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// True when the pair continuously supports planning queries over the window, as the planner validates them: daily
    /// Earth-orientation brackets throughout (rapid-only rows are a gap), inside the leap-second list and with no leap
    /// crossing.
    /// </summary>
    public static bool Covers(OrbitalTimeData time, OrbitalUtcInterval window)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            time.ValidateModel(OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true), window,
                OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return ResolveAsync(new OrbitalUtcInterval(now, now), cancellationToken);
    }

    public async ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(OrbitalUtcInterval window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
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
        VerifiedPairs verified;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (identity == cachedConfiguration && cached is not null)
            {
                verified = cached;
            }
            else
            {
                // A failed explicit adoption also clears the previous inputs. Never silently use an older pair.
                cached = null;
                cachedConfiguration = null;
                verified = source == CameraAgentOrbitalTimeDataSource.Override
                    ? await ReadOverrideAsync(configuration, cancellationToken).ConfigureAwait(false)
                    : await ReadBundledOrManagedAsync(pointer, cancellationToken).ConfigureAwait(false);
                if (verified.Pairs.Count > 0)
                {
                    cached = verified;
                    cachedConfiguration = identity;
                }
            }
        }
        finally { gate.Release(); }
        return Select(verified, window);
    }

    /// <summary>The eligible pair whose planning coverage ends later; a tie keeps the earlier-ranked pair, so bundled wins.</summary>
    private static CameraAgentOrbitalTimeResolution Select(VerifiedPairs verified, OrbitalUtcInterval window)
    {
        var eligible = verified.Pairs.Where(pair => Covers(pair.Data, window)).ToArray();
        VerifiedPair? active = null;
        foreach (var pair in eligible.Length > 0 ? eligible : verified.Pairs)
        {
            if (active is null || PlanningCoverageEnd(pair.Data) > PlanningCoverageEnd(active.Data)) active = pair;
        }
        return active is null
            ? new(verified.Source, verified.Problem, null, Managed: verified.Managed, ManagedPair: verified.Pointer)
            : new(active.Source, CameraAgentOrbitalTimeDataProblem.None, active.Data, active.EarthOrientation, active.LeapSeconds,
                verified.Managed, verified.Pointer, verified.ManagedCoverageEndUtc, active.Pair);
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

    private static async Task<VerifiedPairs> ReadOverrideAsync(CameraAgentSatellitePlanningOptions configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var eop = await ReadPinnedAsync(configuration.EarthOrientationPath!, configuration.EarthOrientationSha256!,
                MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
            var leaps = await ReadPinnedAsync(configuration.LeapSecondsPath!, configuration.LeapSecondsSha256!,
                MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
            return new(CameraAgentOrbitalTimeDataSource.Override, CameraAgentOrbitalTimeDataProblem.None,
                [new(CameraAgentOrbitalTimeDataSource.Override, null, OrbitalTimeData.FromFinals2000A(eop, leaps))]);
        }
        catch (Exception exception) when (IsInputFailure(exception))
        {
            return new(CameraAgentOrbitalTimeDataSource.Override, CameraAgentOrbitalTimeDataProblem.OverrideInvalid, []);
        }
    }

    private async Task<VerifiedPairs> ReadBundledOrManagedAsync(
        (CameraAgentManagedTimePointer? Value, bool Invalid) pointer, CancellationToken cancellationToken)
    {
        var pairs = new List<VerifiedPair>(3);
        if (await ReadProvenancedAsync(bundledDirectory, ProvenanceSchema, cancellationToken).ConfigureAwait(false) is { } bundled)
            pairs.Add(new(CameraAgentOrbitalTimeDataSource.Bundled, null, bundled.Data, bundled.EarthOrientation, bundled.LeapSeconds));
        var state = pointer.Invalid ? CameraAgentManagedTimeDataState.Invalid : CameraAgentManagedTimeDataState.None;
        DateTimeOffset? managedEnd = null;
        if (pointer.Value is { } value)
        {
            var adopted = await ReadManagedAsync(value.Pair, cancellationToken).ConfigureAwait(false);
            state = adopted is null ? CameraAgentManagedTimeDataState.Invalid : CameraAgentManagedTimeDataState.Verified;
            if (adopted is not null)
            {
                managedEnd = PlanningCoverageEnd(adopted.Data);
                pairs.Add(adopted);
            }
            // An intact predecessor stays eligible even when the current pair is damaged. The current adoption's
            // verification state and coverage above remain distinct from the active pair used for planning/status.
            if (value.Previous is { } previous && await ReadManagedAsync(previous, cancellationToken).ConfigureAwait(false) is { } replaced)
                pairs.Add(replaced);
        }
        return new(CameraAgentOrbitalTimeDataSource.Bundled,
            pairs.Count == 0 ? CameraAgentOrbitalTimeDataProblem.BundledInvalid : CameraAgentOrbitalTimeDataProblem.None,
            pairs, state, pointer.Value, managedEnd);
    }

    private async Task<VerifiedPair?> ReadManagedAsync(string pair, CancellationToken cancellationToken)
        => await ReadProvenancedAsync(managed!.PairDirectory(pair), CameraAgentManagedTimeDataStore.ProvenanceSchema,
            cancellationToken).ConfigureAwait(false) is { } read
            ? new(CameraAgentOrbitalTimeDataSource.Managed, pair, read.Data, read.EarthOrientation, read.LeapSeconds)
            : null;

    /// <summary>The verified pairs of one configuration and pointer, ranked for ties: bundled, adopted, then the one it replaced.</summary>
    private sealed record VerifiedPairs(CameraAgentOrbitalTimeDataSource Source, CameraAgentOrbitalTimeDataProblem Problem,
        IReadOnlyList<VerifiedPair> Pairs, CameraAgentManagedTimeDataState Managed = CameraAgentManagedTimeDataState.None,
        CameraAgentManagedTimePointer? Pointer = null, DateTimeOffset? ManagedCoverageEndUtc = null);

    private sealed record VerifiedPair(CameraAgentOrbitalTimeDataSource Source, string? Pair, OrbitalTimeData Data,
        CameraAgentTimeFileProvenance? EarthOrientation = null, CameraAgentTimeFileProvenance? LeapSeconds = null);

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
