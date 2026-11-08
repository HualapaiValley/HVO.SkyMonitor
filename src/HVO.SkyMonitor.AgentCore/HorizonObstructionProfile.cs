using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>True-north, eastward azimuth and geometric altitude in degrees; null altitude means unknown terrain.</summary>
public sealed record HorizonObstructionSample(double AzimuthDegrees, double? ElevationDegrees);

[JsonConverter(typeof(JsonStringEnumConverter<HorizonObstructionSource>))]
public enum HorizonObstructionSource { Manual, Synthetic }

/// <summary>Immutable, bounded installation evidence. See docs/contracts/horizon-obstruction-v1.md.</summary>
public sealed class HorizonObstructionProfile
{
    public const string CurrentSchemaVersion = "horizon-obstruction-v1";
    public const int MaximumSamples = 720;

    [JsonConstructor]
    public HorizonObstructionProfile(string schemaVersion, string profileId, long revision, string installationId,
        string siteIdentitySha256, string geometryIdentitySha256, HorizonObstructionSource source,
        double maximumInterpolationGapDegrees, IReadOnlyList<HorizonObstructionSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(siteIdentitySha256);
        ArgumentNullException.ThrowIfNull(geometryIdentitySha256);
        if (schemaVersion != CurrentSchemaVersion || !Bounded(profileId) || !Bounded(installationId) || revision < 1 ||
            !Sha256(siteIdentitySha256) || !Sha256(geometryIdentitySha256) || !Enum.IsDefined(source) ||
            !double.IsFinite(maximumInterpolationGapDegrees) || maximumInterpolationGapDegrees is <= 0 or > 90 ||
            samples.Count > MaximumSamples)
            throw new ArgumentException("Invalid horizon profile identity or bounds.");
        var frozen = samples.ToArray();
        for (var i = 0; i < frozen.Length; i++)
        {
            var sample = frozen[i];
            if (sample is null || !double.IsFinite(sample.AzimuthDegrees) || sample.AzimuthDegrees is < 0 or >= 360 ||
                sample.ElevationDegrees is { } elevation && (!double.IsFinite(elevation) || elevation is < -90 or > 90) ||
                i > 0 && frozen[i - 1].AzimuthDegrees >= sample.AzimuthDegrees)
                throw new ArgumentException("Horizon samples must have finite, ordered, unique angles within bounds.", nameof(samples));
        }
        SchemaVersion = schemaVersion;
        ProfileId = profileId;
        Revision = revision;
        InstallationId = installationId;
        SiteIdentitySha256 = siteIdentitySha256.ToUpperInvariant();
        GeometryIdentitySha256 = geometryIdentitySha256.ToUpperInvariant();
        Source = source;
        MaximumInterpolationGapDegrees = maximumInterpolationGapDegrees;
        Samples = Array.AsReadOnly(frozen);
    }

    public string SchemaVersion { get; }
    public string ProfileId { get; }
    public long Revision { get; }
    public string InstallationId { get; }
    public string SiteIdentitySha256 { get; }
    public string GeometryIdentitySha256 { get; }
    public HorizonObstructionSource Source { get; }
    public double MaximumInterpolationGapDegrees { get; }
    public IReadOnlyList<HorizonObstructionSample> Samples { get; }

    public string ComputeIdentitySha256() => CaptureContractJson.ComputeCanonicalJsonSha256(this);

    /// <summary>Excludes obstruction evidence itself to avoid a recursive identity; includes calibration and readout.</summary>
    public static string ComputeGeometryIdentitySha256(CameraRigConfig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new { rig.Sensor, rig.Optics, rig.Orientation, rig.Readout });
    }

    public static string ComputeSiteIdentitySha256(double latitudeDegrees, double longitudeDegrees, double elevationMeters)
    {
        if (!double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180 || !double.IsFinite(elevationMeters))
            throw new ArgumentException("Invalid site coordinates.");
        return CaptureContractJson.ComputeCanonicalJsonSha256(new { latitudeDegrees, longitudeDegrees, elevationMeters });
    }

    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value == value.Trim() && !value.Any(char.IsControl);
    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
