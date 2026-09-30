using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

public sealed record AstrometricSkyPoint(AltAzPoint Horizontal, EquatorialPoint EquatorialMeanOfDate,
    DateTimeOffset CoordinateEpochUtc, bool AboveGeometricHorizon);
public sealed record AstrometricLocalPixelScale(double? XArcsecondsPerPixel, double? YArcsecondsPerPixel);

/// <summary>Deterministic native-projection mapping from separately validated measured frame evidence.</summary>
public sealed class AstrometricMapping
{
    private readonly IImageProjector projector;
    private readonly AstrometricFrameAssessment assessment;
    internal readonly AstrometricRotation EnuToJ2000;
    public AstrometricMapping(AstrometricCalibration calibration, AstrometricFrameAssessment assessment)
    {
        this.assessment = assessment; projector = ProjectorFactory.Create(Projection(calibration, assessment));
        EnuVector EquatorialVector(AltAzPoint horizontal)
        {
            var eq = CoordinateTransforms.HorizontalToEquatorial(horizontal, assessment.Frame.MidpointUtc, assessment.Frame.Observer.LatitudeDegrees, assessment.Frame.Observer.LongitudeDegrees);
            var j2000 = EquatorialPrecession.PrecessToJ2000(eq, assessment.Frame.MidpointUtc);
            var ra = j2000.RightAscensionHours * Math.PI / 12; var dec = j2000.DeclinationDegrees * Math.PI / 180;
            return new(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }
        EnuToJ2000 = new(EquatorialVector(new(0, 90)), EquatorialVector(new(0, 0)), EquatorialVector(new(90, 0)));
    }
    public static ProjectionContext Projection(AstrometricCalibration calibration, AstrometricFrameAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(calibration); ArgumentNullException.ThrowIfNull(assessment);
        AstrometricEvidenceJson.Validate(assessment);
        if (!assessment.HasMeasuredMapping || assessment.CalibrationIdentitySha256 != calibration.IdentitySha256)
            throw new ArgumentException("Measured mapping requires an accepted assessment with the exact immutable calibration.", nameof(assessment));
        var fit = assessment.Parameters!;
        var projection = calibration.Projection with
        {
            BoresightAltitudeDegrees = fit.BoresightAltitudeDegrees,
            BoresightAzimuthDegrees = fit.BoresightAzimuthDegrees,
            RollDegrees = fit.RollDegrees,
            FocalLengthXPixels = calibration.Projection.FocalLengthXPixels * fit.FocalScale,
            FocalLengthYPixels = calibration.Projection.FocalLengthYPixels * fit.FocalScale
        };
        projection.Validate(); return projection;
    }
    public AstrometricSkyPoint? PixelToSky(PixelPoint pixel)
    {
        if (projector.Unproject(pixel) is not { } h) return null;
        return new(h, CoordinateTransforms.HorizontalToEquatorial(h, assessment.Frame.MidpointUtc,
            assessment.Frame.Observer.LatitudeDegrees, assessment.Frame.Observer.LongitudeDegrees), assessment.Frame.MidpointUtc, h.AltitudeDegrees >= 0);
    }
    public PixelPoint? SkyToPixel(EquatorialPoint meanOfDate) => projector.Project(CoordinateTransforms.EquatorialToHorizontal(meanOfDate,
        assessment.Frame.MidpointUtc, assessment.Frame.Observer.LatitudeDegrees, assessment.Frame.Observer.LongitudeDegrees));
    public AstrometricLocalPixelScale LocalPixelScale(PixelPoint pixel)
    {
        double? Along(PixelPoint a, PixelPoint b)
        {
            if (projector.Unproject(a) is not { } x || projector.Unproject(b) is not { } y) return null;
            return AstrometricSolverCore.Angle(CameraBasis.FromHorizontal(x), CameraBasis.FromHorizontal(y)) * 180 / Math.PI * 3600;
        }
        return new(Along(new(pixel.X - .5, pixel.Y), new(pixel.X + .5, pixel.Y)), Along(new(pixel.X, pixel.Y - .5), new(pixel.X, pixel.Y + .5)));
    }
    /// <summary>Map through fixed J2000 directions; temporal precession is included, and geometric ground is invalid.</summary>
    public PixelPoint? MapPixelTo(PixelPoint pixel, AstrometricMapping destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (projector.Unproject(pixel) is not { AltitudeDegrees: >= 0 } source) return null;
        var j2000 = EnuToJ2000.Apply(CameraBasis.FromHorizontal(source));
        var target = CameraBasis.ToHorizontal(destination.EnuToJ2000.Inverse(j2000));
        return target.AltitudeDegrees >= 0 ? destination.projector.Project(target) : null;
    }
}

/// <summary>Canonical compact assessment JSON, with content identity and bounded strict parsing.</summary>
public static class AstrometricEvidenceJson
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, AllowDuplicateProperties = false };
    public static byte[] Serialize(AstrometricFrameAssessment assessment) { Validate(assessment); return JsonSerializer.SerializeToUtf8Bytes(assessment, Options); }
    public static AstrometricFrameAssessment Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > 65536) throw new ArgumentException("Compact assessment exceeds64KiB.", nameof(utf8));
        using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
        if (document.RootElement.TryGetProperty(nameof(AstrometricFrameAssessment.HasMeasuredMapping), out _))
            throw new ArgumentException("Measured mapping is derived from validated status and is not a serialized input flag.", nameof(utf8));
        var value = JsonSerializer.Deserialize<AstrometricFrameAssessment>(utf8, Options) ?? throw new ArgumentException("Missing assessment.", nameof(utf8));
        if (document.RootElement.TryGetProperty(nameof(AstrometricFrameAssessment.Frame), out var frame) &&
            frame.TryGetProperty(nameof(AstrometricFrameContext.MidpointUtc), out var suppliedMidpoint) &&
            (suppliedMidpoint.ValueKind != JsonValueKind.String || !suppliedMidpoint.TryGetDateTimeOffset(out var midpoint) ||
             midpoint.Offset != TimeSpan.Zero || value.Frame is null || midpoint != value.Frame.MidpointUtc))
            throw new ArgumentException("Serialized midpoint must agree with the authoritative UTC exposure endpoints.", nameof(utf8));
        Validate(value); return value;
    }
    public static void Validate(AstrometricFrameAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment); ArgumentNullException.ThrowIfNull(assessment.Frame); assessment.Frame.Validate();
        if (assessment.SchemaVersion != AstrometricFrameAssessment.CurrentSchemaVersion || !Enum.IsDefined(assessment.Status) || !Enum.IsDefined(assessment.Mode) ||
            assessment.SolverVersion != AstrometricConventions.SolverVersion || assessment.CoordinateModel != AstrometricConventions.CoordinateModel ||
            assessment.RefractionModel != AstrometricConventions.Refraction || assessment.PixelCoordinateConvention != AstrometricConventions.PixelCoordinates ||
            assessment.Mode == AstrometricSolveMode.Warm && assessment.PreviousAssessmentIdentitySha256 is null ||
            assessment.Mode == AstrometricSolveMode.Blind && assessment.PreviousAssessmentIdentitySha256 is not null ||
            assessment.Status == AstrometricAssessmentStatus.Accepted && assessment.ReasonCode != "accepted" ||
            assessment.Status != AstrometricAssessmentStatus.Accepted && assessment.ReasonCode == "accepted" ||
            string.IsNullOrWhiteSpace(assessment.ReasonCode) || assessment.ReasonCode.Length > 128 || string.IsNullOrWhiteSpace(assessment.Reason) || assessment.Reason.Length > 1024 ||
            assessment.Status == AstrometricAssessmentStatus.Accepted && (assessment.Parameters is null || assessment.Quality is null) ||
            assessment.Status != AstrometricAssessmentStatus.Accepted && assessment.Parameters is not null)
            throw new ArgumentException("Invalid assessment status/conventions.", nameof(assessment));
        foreach (var id in new[] { assessment.IdentitySha256, assessment.CalibrationIdentitySha256, assessment.CatalogIdentitySha256, assessment.CatalogSelectionIdentitySha256, assessment.SettingsIdentitySha256 }) AstrometricIdentity.RequireSha256(id);
        if (assessment.PreviousAssessmentIdentitySha256 is { } previousHash) AstrometricIdentity.RequireSha256(previousHash);
        if (assessment.AssociationIdentitySha256 is { } associationHash) AstrometricIdentity.RequireSha256(associationHash);
        if (assessment.Parameters is { } p && (!double.IsFinite(p.BoresightAltitudeDegrees) || p.BoresightAltitudeDegrees is < -90 or > 90 || !double.IsFinite(p.BoresightAzimuthDegrees) ||
            !double.IsFinite(p.RollDegrees) || !double.IsFinite(p.FocalScale) || p.FocalScale <= 0 || !double.IsFinite(p.BoresightRightAscensionHours) || p.BoresightRightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(p.BoresightDeclinationDegrees) || p.BoresightDeclinationDegrees is < -90 or > 90)) throw new ArgumentException("Invalid fitted parameters.", nameof(assessment));
        if (assessment.Parameters is { } direction)
        {
            var expectedDirection = CoordinateTransforms.HorizontalToEquatorial(new(direction.BoresightAltitudeDegrees, direction.BoresightAzimuthDegrees), assessment.Frame.MidpointUtc,
                assessment.Frame.Observer.LatitudeDegrees, assessment.Frame.Observer.LongitudeDegrees);
            var raDifference = ((expectedDirection.RightAscensionHours - direction.BoresightRightAscensionHours + 36) % 24) - 12;
            if (Math.Abs(raDifference) > 1e-8 || Math.Abs(expectedDirection.DeclinationDegrees - direction.BoresightDeclinationDegrees) > 1e-8)
                throw new ArgumentException("Boresight RA/Dec must match horizontal direction at the capture midpoint.", nameof(assessment));
        }
        if (assessment.Quality is { } q && (q.IsCalibratedProbability || q.OrientationUncertaintyDegrees is not null || q.UncertaintyStatus != "unavailable-no-validated-covariance" || !double.IsFinite(q.Score0To100) || q.Score0To100 is < 0 or > 100 || q.InlierCount < 0 || q.VerificationCount < 0 || q.ExpectedIsolatedCount < 0 ||
            !double.IsFinite(q.FittingRmsPixels) || q.FittingRmsPixels < 0 || !double.IsFinite(q.VerificationRmsPixels) || q.VerificationRmsPixels < 0 ||
            !double.IsFinite(q.WidthCoverageFraction) || q.WidthCoverageFraction is < 0 or > 1 || !double.IsFinite(q.HeightCoverageFraction) || q.HeightCoverageFraction is < 0 or > 1)) throw new ArgumentException("Invalid fit quality.", nameof(assessment));
        if (assessment.Status == AstrometricAssessmentStatus.Accepted)
        {
            var quality = assessment.Quality!;
            if (assessment.AssociationIdentitySha256 is null || quality.InlierCount < 12 || quality.VerificationCount < 4 || quality.ExpectedIsolatedCount < quality.InlierCount ||
                quality.ExpectedIsolatedCount > 2500 || (long)quality.InlierCount + quality.VerificationCount > 2500 ||
                quality.InlierCount / (double)Math.Max(1, quality.ExpectedIsolatedCount) < .5 || quality.FittingRmsPixels > .4 || quality.VerificationRmsPixels > .5 ||
                quality.WidthCoverageFraction < .35 || quality.HeightCoverageFraction < .35)
                throw new ArgumentException("Accepted assessment must retain the solver's independent evidence and coverage gates.", nameof(assessment));
            var expectedScore = Math.Round(100 * (.3 * Math.Exp(-Math.Pow(quality.FittingRmsPixels / .3, 2)) + .3 * Math.Exp(-Math.Pow(quality.VerificationRmsPixels / .3, 2)) +
                .2 * Math.Min(1, quality.InlierCount / 40d) + .2 * Math.Min(1, Math.Min(quality.WidthCoverageFraction, quality.HeightCoverageFraction) / .7)), 1);
            if (Math.Abs(expectedScore - quality.Score0To100) > 1e-8) throw new ArgumentException("Accepted quality score disagrees with its declared components.", nameof(assessment));
        }
        var expected = AstrometricIdentity.Hash(assessment with { IdentitySha256 = string.Empty });
        if (!string.Equals(expected, assessment.IdentitySha256, StringComparison.Ordinal)) throw new ArgumentException("Assessment content identity mismatch.", nameof(assessment));
    }
}
