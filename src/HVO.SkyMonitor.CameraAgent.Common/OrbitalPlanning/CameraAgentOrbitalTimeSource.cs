using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>Where the active time pair comes from. Any configured override setting selects the override.</summary>
public enum CameraAgentOrbitalTimeDataSource
{
    Bundled,
    Override
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

/// <summary>One verified bundled file as its provenance record describes it.</summary>
public sealed record CameraAgentBundledTimeFile(string File, Uri SourceUrl, DateTimeOffset RetrievedUtc, string Sha256, string Terms);

/// <summary>The active source and pair. Data is present exactly when Problem is None.</summary>
public sealed record CameraAgentOrbitalTimeResolution(
    CameraAgentOrbitalTimeDataSource Source,
    CameraAgentOrbitalTimeDataProblem Problem,
    OrbitalTimeData? Data,
    CameraAgentBundledTimeFile? BundledEarthOrientation = null,
    CameraAgentBundledTimeFile? BundledLeapSeconds = null);

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
/// must be complete and valid; an invalid override never falls back to the bundled pair. Otherwise the pair bundled
/// with this release is verified against its provenance SHA-256 values. Only an explicit options change can adopt
/// another pair; files are never downloaded, watched, refreshed, written or replaced with nominal-zero Earth
/// orientation. Problems carry no paths.
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
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedConfiguration;
    private CameraAgentOrbitalTimeResolution? cached;

    public CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options)
        : this(options, BundledDirectory)
    {
    }

    internal CameraAgentPinnedOrbitalTimeSource(IOptionsMonitor<CameraAgentSatellitePlanningOptions> options, string bundledDirectory)
    {
        this.options = options;
        this.bundledDirectory = bundledDirectory;
    }

    /// <summary>True when any override setting is present, which selects the override over the bundled pair.</summary>
    public static bool HasOverride(CameraAgentSatellitePlanningOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration.EarthOrientationPath) || !string.IsNullOrWhiteSpace(configuration.EarthOrientationSha256) ||
            !string.IsNullOrWhiteSpace(configuration.LeapSecondsPath) || !string.IsNullOrWhiteSpace(configuration.LeapSecondsSha256);
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
        var identity = Convert.ToHexString(SHA256.HashData(OrbitalContract.SerializeCanonical(configuration)));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (identity == cachedConfiguration && cached is not null) return cached;
            // A failed explicit adoption also clears the previous inputs. Never silently use an older pair.
            cached = null;
            cachedConfiguration = null;
            var resolution = source == CameraAgentOrbitalTimeDataSource.Override
                ? await ReadOverrideAsync(configuration, cancellationToken).ConfigureAwait(false)
                : await ReadBundledAsync(cancellationToken).ConfigureAwait(false);
            if (resolution.Data is not null)
            {
                cached = resolution;
                cachedConfiguration = identity;
            }
            return resolution;
        }
        finally { gate.Release(); }
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

    private async Task<CameraAgentOrbitalTimeResolution> ReadBundledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provenance = await ReadBoundedAsync(Path.Combine(bundledDirectory, ProvenanceFileName), MaximumProvenanceBytes,
                cancellationToken).ConfigureAwait(false);
            var (earthOrientation, leapSeconds) = ParseProvenance(provenance);
            var eop = await ReadPinnedAsync(Path.Combine(bundledDirectory, earthOrientation.File), earthOrientation.Sha256,
                MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
            var leaps = await ReadPinnedAsync(Path.Combine(bundledDirectory, leapSeconds.File), leapSeconds.Sha256,
                MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
            return new(CameraAgentOrbitalTimeDataSource.Bundled, CameraAgentOrbitalTimeDataProblem.None,
                OrbitalTimeData.FromFinals2000A(eop, leaps), earthOrientation, leapSeconds);
        }
        catch (Exception exception) when (IsInputFailure(exception) || exception is JsonException)
        {
            return new(CameraAgentOrbitalTimeDataSource.Bundled, CameraAgentOrbitalTimeDataProblem.BundledInvalid, null);
        }
    }

    private static (CameraAgentBundledTimeFile EarthOrientation, CameraAgentBundledTimeFile LeapSeconds) ParseProvenance(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != ProvenanceSchema) throw new InvalidDataException();
        return (Entry(root, "earthOrientation"), Entry(root, "leapSeconds"));

        static CameraAgentBundledTimeFile Entry(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var entry) || entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var file = Text(entry, "file");
            var url = Text(entry, "sourceUrl");
            var sha256 = Text(entry, "sha256");
            // A simple file name inside the bundled directory, an approved HTTPS origin and an exact UTC retrieval time.
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
