using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Measured centroid in the calibrated input's continuous pixel-edge coordinates.</summary>
public sealed record AstrometricDetection(int Index, PixelPoint Pixel, double Flux);

/// <summary>Explicitly geometric, fixed-position J2000 catalog semantics supported by this solver version.</summary>
public static class AstrometricConventions
{
    public const string SolverVersion = "spherical-triangle-astrometry-v1";
    public const string CoordinateModel = "j2000-fixed-position-iau1976-mean-of-date-v1";
    public const string Refraction = "none-geometric";
    public const string PixelCoordinates = "continuous-top-left-pixel-edge";
}

/// <summary>One immutable optical model in the actual input image coordinates, retained once by identity.</summary>
public sealed class AstrometricCalibration
{
    public AstrometricCalibration(ProjectionContext projection, string calibrationVersion, string readoutIdentitySha256)
    {
        projection.Validate();
        if (!projection.EnforceSensorBounds || (long)projection.WidthPixels * projection.HeightPixels > 16_777_216)
            throw new ArgumentException("Astrometry requires sensor-bounded calibration of at most16M pixels.", nameof(projection));
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationVersion);
        if (calibrationVersion.Length > 256) throw new ArgumentException("Calibration version exceeds256 characters.", nameof(calibrationVersion));
        Projection = projection; CalibrationVersion = calibrationVersion;
        ReadoutIdentitySha256 = AstrometricIdentity.RequireSha256(readoutIdentitySha256);
        IdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-calibration-v1", projection, calibrationVersion, ReadoutIdentitySha256 });
    }
    public ProjectionContext Projection { get; }
    public string CalibrationVersion { get; }
    public string ReadoutIdentitySha256 { get; }
    public string IdentitySha256 { get; }
}

/// <summary>Capture-authoritative evidence. A good geometric fit cannot authenticate clock or location accuracy.</summary>
public sealed record AstrometricFrameContext(Guid CaptureId, Guid SourceArtifactId,
    string SourceDescriptorSha256, string SourcePayloadSha256, string ObserverIdentitySha256,
    ObserverLocation Observer, DateTimeOffset ExposureStartUtc, DateTimeOffset ExposureEndUtc,
    string DetectionAlgorithmVersion, string DetectionSettingsIdentitySha256)
{
    public DateTimeOffset MidpointUtc => ExposureStartUtc.ToUniversalTime().AddTicks((ExposureEndUtc - ExposureStartUtc).Ticks / 2);
    public void Validate()
    {
        Observer.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(DetectionAlgorithmVersion);
        if (DetectionAlgorithmVersion.Length > 128) throw new ArgumentException("Detection version exceeds128 characters.");
        AstrometricIdentity.RequireSha256(DetectionSettingsIdentitySha256);
        if (CaptureId == Guid.Empty || SourceArtifactId == Guid.Empty || ExposureStartUtc.Offset != TimeSpan.Zero || ExposureEndUtc.Offset != TimeSpan.Zero ||
            ExposureEndUtc < ExposureStartUtc || (ExposureEndUtc - ExposureStartUtc).TotalSeconds > 86400)
            throw new ArgumentException("Capture identity and UTC exposure interval are invalid.");
        AstrometricIdentity.RequireSha256(SourceDescriptorSha256); AstrometricIdentity.RequireSha256(SourcePayloadSha256); AstrometricIdentity.RequireSha256(ObserverIdentitySha256);
    }
}

/// <summary>A bounded complete-for-request catalog result. Incomplete results must never become accepted fits.</summary>
public sealed class AstrometricCatalogData
{
    public AstrometricCatalogData(CatalogMetadata metadata, IEnumerable<CelestialCatalogObject> stars,
        bool isCompleteForRequestedMagnitude, string coordinateModel = AstrometricConventions.CoordinateModel)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(stars);
        AstrometricIdentity.RequireSha256(metadata.Checksum);
        if (string.IsNullOrWhiteSpace(metadata.Name) || string.IsNullOrWhiteSpace(metadata.Version) || string.IsNullOrWhiteSpace(metadata.License) || string.IsNullOrWhiteSpace(metadata.SchemaVersion) || metadata.SourceUrl is null || !metadata.SourceUrl.IsAbsoluteUri || metadata.Name.Length > 256 || metadata.Version.Length > 256 || metadata.SourceUrl.AbsoluteUri.Length > 2048 || metadata.License.Length > 2048 || metadata.SchemaVersion.Length > 256)
            throw new ArgumentException("Catalog metadata must identify an immutable source.", nameof(metadata));
        var values = stars.Take(2501).ToArray();
        if (values.Length > 2500) throw new ArgumentException("Catalog materialization exceeds2500 entries.", nameof(stars));
        foreach (var star in values)
            if (star is null || string.IsNullOrWhiteSpace(star.Id) || star.Id.Length > 256 || star.DisplayName is null || star.DisplayName.Length > 512 || star.HipparcosId?.Length > 128 || !double.IsFinite(star.RightAscensionHours) || star.RightAscensionHours is < 0 or >= 24 ||
                !double.IsFinite(star.DeclinationDegrees) || star.DeclinationDegrees is < -90 or > 90 || !double.IsFinite(star.Magnitude)) throw new ArgumentException("Invalid catalog entry.", nameof(stars));
        if (values.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Duplicate catalog identities.", nameof(stars));
        Metadata = metadata; Stars = Array.AsReadOnly(values.OrderBy(s => s.Magnitude).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray());
        IsCompleteForRequestedMagnitude = isCompleteForRequestedMagnitude; CoordinateModel = coordinateModel;
        IdentitySha256 = AstrometricIdentity.Hash(new { metadata, coordinateModel });
        SelectionIdentitySha256 = AstrometricIdentity.Hash(Stars);
    }
    public CatalogMetadata Metadata { get; }
    public ReadOnlyCollection<CelestialCatalogObject> Stars { get; }
    public bool IsCompleteForRequestedMagnitude { get; }
    public string CoordinateModel { get; }
    public string IdentitySha256 { get; }
    public string SelectionIdentitySha256 { get; }
}

/// <summary>Injected local catalog adapter; it must honor cancellation and report rather than silently truncate limits.</summary>
public interface IAstrometricCatalogSource
{
    ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries, CancellationToken cancellationToken = default);
}

/// <summary>Versioned finite work bounds. Quality thresholds remain declared geometric engineering gates.</summary>
public sealed record AstrometricSolverOptions(
    double MinimumFocalScale = .90, double MaximumFocalScale = 1.10, double FocalScaleStep = .01,
    double MaximumCatalogMagnitude = 7, int TriangleDetectionCount = 28, int ImageTriangleLimit = 192,
    int HypothesisLimit = 200000, int CandidateLimit = 32, double ColdBudgetMilliseconds = 15000,
    double WarmBudgetMilliseconds = 500, double MaximumWarmAgeSeconds = 600)
{
    [JsonIgnore]
    public string IdentitySha256 => AstrometricIdentity.Hash(this with { });
    public void Validate()
    {
        if (!double.IsFinite(MinimumFocalScale) || MinimumFocalScale <= 0 || !double.IsFinite(MaximumFocalScale) || MaximumFocalScale < MinimumFocalScale ||
            !double.IsFinite(FocalScaleStep) || FocalScaleStep <= 0 || (MaximumFocalScale - MinimumFocalScale) / FocalScaleStep > 100 ||
            !double.IsFinite(MaximumCatalogMagnitude) || TriangleDetectionCount is < 3 or > 60 || ImageTriangleLimit is < 1 or > 5000 ||
            HypothesisLimit is < 1 or > 200000 || CandidateLimit is < 2 or > 100 ||
            !double.IsFinite(ColdBudgetMilliseconds) || ColdBudgetMilliseconds is <= 0 or > 60000 ||
            !double.IsFinite(WarmBudgetMilliseconds) || WarmBudgetMilliseconds is <= 0 or > 60000 ||
            !double.IsFinite(MaximumWarmAgeSeconds) || MaximumWarmAgeSeconds is < 0 or > 86400)
            throw new ArgumentException("Invalid astrometric search settings.");
    }
}

public enum AstrometricAssessmentStatus { Accepted, Rejected, Unavailable, BudgetExceeded }
public enum AstrometricSolveMode { Blind, Warm }

/// <summary>Frame-specific fit parameters; all unmodified optical data stay in the referenced calibration.</summary>
public sealed record AstrometricFitParameters(double BoresightAltitudeDegrees, double BoresightAzimuthDegrees,
    double RollDegrees, double FocalScale, double BoresightRightAscensionHours, double BoresightDeclinationDegrees);
public sealed record AstrometricFitQuality(double Score0To100, int InlierCount, int VerificationCount, int ExpectedIsolatedCount,
    double FittingRmsPixels, double VerificationRmsPixels, double WidthCoverageFraction, double HeightCoverageFraction,
    bool IsCalibratedProbability = false, double? OrientationUncertaintyDegrees = null,
    string UncertaintyStatus = "unavailable-no-validated-covariance");
public sealed record AstrometricAssociation(string CatalogId, int DetectionIndex, double ResidualPixels, bool Verification);
public sealed record AstrometricExecutionMetrics(double ElapsedMilliseconds, int IndexStars, int IndexTriangles, int ImageTriangles,
    int Hypotheses, int DistinctCandidates, bool BudgetExhausted);

/// <summary>Compact per-frame evidence. Rejected frames carry no measured mapping and cannot replace a calibration.</summary>
public sealed record AstrometricFrameAssessment(string SchemaVersion, string IdentitySha256,
    AstrometricFrameContext Frame, string CalibrationIdentitySha256, string CatalogIdentitySha256,
    string CatalogSelectionIdentitySha256, string SolverVersion, string SettingsIdentitySha256,
    string CoordinateModel, string RefractionModel, string PixelCoordinateConvention,
    AstrometricSolveMode Mode, AstrometricAssessmentStatus Status, string ReasonCode, string Reason,
    AstrometricFitParameters? Parameters, AstrometricFitQuality? Quality, string? AssociationIdentitySha256,
    string? PreviousAssessmentIdentitySha256)
{
    public const string CurrentSchemaVersion = "astrometric-frame-assessment-v1";
    [JsonIgnore]
    public bool HasMeasuredMapping => Status == AstrometricAssessmentStatus.Accepted && Parameters is not null;
}

/// <summary>Diagnostics are returned separately, so compact capture references need not duplicate source lists.</summary>
public sealed class AstrometricSolveResult
{
    internal AstrometricSolveResult(AstrometricFrameAssessment assessment, IEnumerable<AstrometricAssociation> associations, AstrometricExecutionMetrics metrics)
    { Assessment = assessment; Associations = Array.AsReadOnly(associations.ToArray()); Metrics = metrics; }
    public AstrometricFrameAssessment Assessment { get; }
    public ReadOnlyCollection<AstrometricAssociation> Associations { get; }
    public AstrometricExecutionMetrics Metrics { get; }
}

internal static class AstrometricIdentity
{
    internal static string RequireSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A64-character SHA256 identity is required.", nameof(value));
        return Convert.ToHexStringLower(Convert.FromHexString(value));
    }
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
