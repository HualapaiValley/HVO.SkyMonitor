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
    public const string SolverVersion = "spherical-triangle-astrometry-v2";
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

/// <summary>Immutable installed-package identity, separate from the catalog's coordinate epoch.</summary>
public sealed record AstrometricCatalogProvenance(
    string CatalogId, string PackageVersion, string PackageKind, string PreprocessingVersion)
{
    internal void Validate()
    {
        foreach (var value in new[] { CatalogId, PackageVersion, PreprocessingVersion })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw new ArgumentException("Catalog package identity must be nonblank and bounded.");
        if (PackageKind is not ("production" or "fixture"))
            throw new ArgumentException("Catalog package kind must be production or fixture.");
    }
}

/// <summary>A bounded complete-for-request catalog result. Incomplete results must never become accepted fits.</summary>
public sealed class AstrometricCatalogData
{
    /// <summary>
    /// Request bound of every public settings value: the solver reads and solves at most this many stars, exactly as solver
    /// v2 always has.
    /// </summary>
    public const int MaximumEntries = 2500;

    /// <summary>
    /// Capacity of this container and of catalog reads, independent of any selection profile. It promises no solve: the
    /// solver and calibration session refuse a selection above <see cref="MaximumEntries"/> with
    /// <c>catalog-selection-unsupported</c> for any settings public code can construct, because no deep selection is
    /// qualified (issue #1167).
    /// </summary>
    public const int MaterializationCeiling = 8192;

    public AstrometricCatalogData(CatalogMetadata metadata, IEnumerable<CelestialCatalogObject> stars,
        bool isCompleteForRequestedMagnitude, double completenessMagnitudeLimit, string coordinateModel = AstrometricConventions.CoordinateModel,
        AstrometricCatalogProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(stars);
        provenance?.Validate();
        AstrometricIdentity.RequireSha256(metadata.Checksum);
        if (!double.IsFinite(completenessMagnitudeLimit)) throw new ArgumentException("Catalog completeness magnitude limit must be finite.", nameof(completenessMagnitudeLimit));
        if (string.IsNullOrWhiteSpace(metadata.Name) || string.IsNullOrWhiteSpace(metadata.Version) || string.IsNullOrWhiteSpace(metadata.License) || string.IsNullOrWhiteSpace(metadata.SchemaVersion) || metadata.SourceUrl is null || !metadata.SourceUrl.IsAbsoluteUri || metadata.Name.Length > 256 || metadata.Version.Length > 256 || metadata.SourceUrl.AbsoluteUri.Length > 2048 || metadata.License.Length > 2048 || metadata.SchemaVersion.Length > 256)
            throw new ArgumentException("Catalog metadata must identify an immutable source.", nameof(metadata));
        var values = stars.Take(MaterializationCeiling + 1).ToArray();
        if (values.Length > MaterializationCeiling) throw new ArgumentException($"Catalog materialization exceeds {MaterializationCeiling} entries.", nameof(stars));
        foreach (var star in values)
            if (star is null || string.IsNullOrWhiteSpace(star.Id) || star.Id.Length > 256 || star.DisplayName is null || star.DisplayName.Length > 512 || star.HipparcosId?.Length > 128 || !double.IsFinite(star.RightAscensionHours) || star.RightAscensionHours is < 0 or >= 24 ||
                !double.IsFinite(star.DeclinationDegrees) || star.DeclinationDegrees is < -90 or > 90 || !double.IsFinite(star.Magnitude)) throw new ArgumentException("Invalid catalog entry.", nameof(stars));
        if (values.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Duplicate catalog identities.", nameof(stars));
        Metadata = metadata; Stars = Array.AsReadOnly(values.OrderBy(s => s.Magnitude).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray());
        IsCompleteForRequestedMagnitude = isCompleteForRequestedMagnitude; CompletenessMagnitudeLimit = completenessMagnitudeLimit; CoordinateModel = coordinateModel;
        Provenance = provenance;
        // Preserve the identity of retained standalone evidence that has no installed-package provenance.
        IdentitySha256 = provenance is null
            ? AstrometricIdentity.Hash(new { metadata, coordinateModel })
            : AstrometricIdentity.Hash(new { schema = "astrometric-catalog-v2", metadata, coordinateModel, provenance });
        SelectionIdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-catalog-selection-v2", Stars, IsCompleteForRequestedMagnitude, CompletenessMagnitudeLimit });
    }
    /// <summary>Exact installed package identity, or null for caller-supplied standalone evidence.</summary>
    public AstrometricCatalogProvenance? Provenance { get; }
    public CatalogMetadata Metadata { get; }
    public ReadOnlyCollection<CelestialCatalogObject> Stars { get; }
    public bool IsCompleteForRequestedMagnitude { get; }
    /// <summary>Faintest magnitude through which the adapter declares complete coverage; still unavailable when the completeness flag is false.</summary>
    public double CompletenessMagnitudeLimit { get; }
    public string CoordinateModel { get; }
    public string IdentitySha256 { get; }
    public string SelectionIdentitySha256 { get; }
}

/// <summary>Injected local catalog adapter; it must honor cancellation and report rather than silently truncate limits.</summary>
public interface IAstrometricCatalogSource
{
    ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries, CancellationToken cancellationToken = default);
}

/// <summary>
/// An immutable catalog selection envelope: the request bound a solve may materialize and the faintest magnitude its
/// measurement covered. A name never changes meaning; a different bound or magnitude needs a new name.
/// </summary>
internal sealed class AstrometricCatalogSelectionProfile
{
    /// <summary>
    /// The deep HYG selection measured under issue #1167. It is not qualified, so the name is burned: it is never declared,
    /// and any later attempt uses a new name.
    /// </summary>
    internal const string HygDeepSelectionV1Name = "hyg-deep-selection-v1";

    internal const int HygDeepSelectionV1MaximumEntries = 8192;

    private AstrometricCatalogSelectionProfile(string name, int maximumEntries, double qualifiedMaximumMagnitude) =>
        (Name, MaximumEntries, QualifiedMaximumMagnitude) = (name, maximumEntries, qualifiedMaximumMagnitude);

    internal static AstrometricCatalogSelectionProfile HygDeepSelectionV1 { get; } = new(HygDeepSelectionV1Name, HygDeepSelectionV1MaximumEntries, 6.0);

    /// <summary>Every qualified profile. None is: no deep selection is shown to meet the issue #1167 acceptance minimum.</summary>
    internal static IReadOnlyList<AstrometricCatalogSelectionProfile> Declared { get; } = [];

    /// <summary>Profiles kept only so their evaluation harnesses stay runnable; never declared, never configurable.</summary>
    internal static IReadOnlyList<AstrometricCatalogSelectionProfile> Measured { get; } = [HygDeepSelectionV1];

    internal string Name { get; }

    /// <summary>Request bound passed to the catalog source, and the largest selection a solve under this profile accepts.</summary>
    internal int MaximumEntries { get; }

    /// <summary>Faintest requested magnitude qualified, or for a measured profile measured; settings asking for more fail validation.</summary>
    internal double QualifiedMaximumMagnitude { get; }

    /// <summary>The declared or measured profile with this exact name, or null when there is none.</summary>
    internal static AstrometricCatalogSelectionProfile? Find(string? name) =>
        name is null ? null : Declared.Concat(Measured).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>Versioned finite work bounds. Quality thresholds remain declared geometric engineering gates.</summary>
public sealed record AstrometricSolverOptions(
    double MinimumFocalScale = .90, double MaximumFocalScale = 1.10, double FocalScaleStep = .01,
    double MaximumCatalogMagnitude = 7, int TriangleDetectionCount = 28, int ImageTriangleLimit = 192,
    int HypothesisLimit = 200000, int CandidateLimit = 32, double ColdBudgetMilliseconds = 15000,
    double WarmBudgetMilliseconds = 500, double MaximumWarmAgeSeconds = 600)
{
    private string? _catalogSelectionProfile;

    [JsonIgnore]
    public string IdentitySha256 => AstrometricIdentity.Hash(this with { });

    /// <summary>
    /// Evaluation-only selection envelope by name, set only through <see cref="WithCatalogSelectionProfile"/>. It has no
    /// setter, so neither configuration nor deserialized settings can select one. Null keeps the 2,500-entry bound and is
    /// omitted from the settings identity; a measured profile serializes last, as the issue #1167 evidence pins.
    /// </summary>
    [JsonInclude, JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? CatalogSelectionProfile => _catalogSelectionProfile;

    /// <summary>A copy of these settings under the named selection profile, or the legacy bound when null.</summary>
    internal AstrometricSolverOptions WithCatalogSelectionProfile(string? name)
    {
        var copy = this with { };
        copy._catalogSelectionProfile = name;
        return copy;
    }

    /// <summary>Largest catalog selection these settings read and solve. Derived from the profile, never part of the identity.</summary>
    [JsonIgnore]
    internal int CatalogEntryBound => AstrometricCatalogSelectionProfile.Find(CatalogSelectionProfile)?.MaximumEntries ?? AstrometricCatalogData.MaximumEntries;

    public void Validate()
    {
        if (CatalogSelectionProfile is not null && (AstrometricCatalogSelectionProfile.Find(CatalogSelectionProfile) is not { } profile ||
            MaximumCatalogMagnitude > profile.QualifiedMaximumMagnitude))
            throw new ArgumentException("Catalog selection profile is undeclared, or the requested magnitude exceeds its qualified maximum.");
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
