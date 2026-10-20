using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>Locally resolved immutable actual time bytes. Null means unconfigured or unavailable.</summary>
public interface ICameraAgentOrbitalTimeSource
{
    ValueTask<OrbitalTimeData?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bounded pinned local-file loader. Only an explicit options change can adopt another input pair;
/// files are never downloaded, watched, refreshed or replaced with nominal-zero Earth orientation.
/// Invalid inputs produce no path-bearing diagnostics.
/// </summary>
public sealed class CameraAgentPinnedOrbitalTimeSource(
    IOptionsMonitor<CameraAgentSatellitePlanningOptions> options) : ICameraAgentOrbitalTimeSource, IDisposable
{
    public const int MaximumEarthOrientationBytes = 8 * 1024 * 1024;
    public const int MaximumLeapSecondsBytes = 64 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedConfiguration;
    private OrbitalTimeData? cached;

    public async ValueTask<OrbitalTimeData?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = options.CurrentValue.Copy();
        if (!configuration.Enabled || !Path.IsPathFullyQualified(configuration.EarthOrientationPath ?? "") ||
            !Path.IsPathFullyQualified(configuration.LeapSecondsPath ?? "") ||
            !IsHash(configuration.EarthOrientationSha256) || !IsHash(configuration.LeapSecondsSha256)) return null;
        var identity = Convert.ToHexString(SHA256.HashData(OrbitalContract.SerializeCanonical(configuration)));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (identity == cachedConfiguration) return cached;
            // A failed explicit adoption also clears the previous inputs. Never silently use an older pair.
            cached = null;
            cachedConfiguration = null;
            var eop = await ReadPinnedAsync(configuration.EarthOrientationPath!, configuration.EarthOrientationSha256!,
                MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
            var leaps = await ReadPinnedAsync(configuration.LeapSecondsPath!, configuration.LeapSecondsSha256!,
                MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
            cached = OrbitalTimeData.FromFinals2000A(eop, leaps);
            cachedConfiguration = identity;
            return cached;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally { gate.Release(); }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public void Dispose() => gate.Dispose();

    private static async Task<byte[]> ReadPinnedAsync(string path, string expectedHash, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length is <= 0 || length > maximumBytes) throw new InvalidDataException();
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return bytes;
    }
}
