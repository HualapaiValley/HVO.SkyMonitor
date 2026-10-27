using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Typed, independently selectable measured geometry shared by SVG and raster export. Never reads pixels.</summary>
public static class AstrometricDiagnosticPresentation
{
    public const string AssociationsLayer = "MeasuredAstrometricAssociations";
    public const string ResidualsLayer = "MeasuredAstrometricResiduals";
    public const string Version = "measured-astrometric-diagnostics-v1";
    public const string CompositionSchema = "capture-astrometric-composition-v1";

    public static PresentationLayerPayloadV1 CreatePayload(string solutionIdentitySha256, AstrometricResidualDiagnostics diagnostics, int width, int height, bool residuals)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!diagnostics.HasMeasuredMapping || diagnostics.Residuals.Count is < 1 or > 2500 ||
            diagnostics.Residuals.Any(r => !Inside(r.Measured) || !Inside(r.Predicted)))
            throw new ArgumentException("Diagnostics require accepted, bounded source-pixel geometry.", nameof(diagnostics));
        var fitting = new PresentationColor(115, 232, 157);
        var heldOut = new PresentationColor(255, 201, 92);
        return PresentationLayerPayloadJson.Create(solutionIdentitySha256, width, height,
            // Rings keep subpixel residuals locatable without exaggerating their vector length.
            markers: diagnostics.Residuals.Select(r => new PresentationMarkerV1(residuals ? r.Predicted : r.Measured,
                residuals ? 2 : 3, r.Verification ? heldOut : fitting)).ToArray(),
            // Actual predicted-to-measured vectors, with no magnification or fabricated uncertainty band.
            segments: residuals ? diagnostics.Residuals.Select(r => new PresentationSegmentV1(r.Predicted, r.Measured, 1,
                r.Verification ? heldOut : fitting)).ToArray() : []);

        bool Inside(PixelPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height;
    }
}
