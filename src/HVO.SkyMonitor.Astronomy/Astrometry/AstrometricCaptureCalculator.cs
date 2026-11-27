using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

public enum AstrometricPixelSpace
{
    Readout,
    NativeSensor
}

/// <summary>Capture-bound angular calculations in continuous top-left pixel-edge coordinates.</summary>
public sealed class AstrometricCaptureCalculator
{
    public const int MaximumTrackSamples = 4096;
    public const string PointUncertaintyWithholdReason = "point-uncertainty-not-propagated";

    private readonly AstrometricMapping _mapping;
    private readonly int _width;
    private readonly int _height;
    private readonly FrameReadoutDescriptor? _readout;

    internal AstrometricCaptureCalculator(AstrometricMapping mapping, AstrometricFrameAssessment assessment,
        int width, int height, FrameReadoutDescriptor? readout)
    {
        if (readout is not null && (readout.RoiX < 0 || readout.RoiY < 0 || readout.BinX < 1 || readout.BinY < 1 ||
            readout.RoiWidth != (long)width * readout.BinX || readout.RoiHeight != (long)height * readout.BinY ||
            (long)readout.RoiX + readout.RoiWidth > readout.NativeWidth ||
            (long)readout.RoiY + readout.RoiHeight > readout.NativeHeight))
            throw new ArgumentException("Readout geometry differs from the measured image.", nameof(readout));
        _mapping = mapping;
        _width = width;
        _height = height;
        _readout = readout;
        Frame = assessment.Frame;
        AssessmentIdentitySha256 = assessment.IdentitySha256;
    }

    public AstrometricFrameContext Frame { get; }
    public string AssessmentIdentitySha256 { get; }
    public bool SupportsNativeCoordinates => _readout is not null;

    public AstrometricSkyPoint? PixelToSky(PixelPoint pixel, AstrometricPixelSpace space = AstrometricPixelSpace.Readout)
    {
        var readoutPixel = space switch
        {
            AstrometricPixelSpace.Readout => pixel,
            AstrometricPixelSpace.NativeSensor when _readout is { } readout =>
                new((pixel.X - readout.RoiX) / readout.BinX, (pixel.Y - readout.RoiY) / readout.BinY),
            AstrometricPixelSpace.NativeSensor => (PixelPoint?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(space))
        };
        if (readoutPixel is not { } point || !InsideReadout(point)) return null;
        return _mapping.PixelToSky(point) is { AboveGeometricHorizon: true } sky ? sky : null;
    }

    /// <summary>Converts equatorial mean-of-date coordinates at this capture's exposure midpoint.</summary>
    public PixelPoint? EquatorialToPixel(EquatorialPoint meanOfDate, AstrometricPixelSpace space = AstrometricPixelSpace.Readout)
    {
        if (!(meanOfDate.RightAscensionHours is >= 0 and < 24 && meanOfDate.DeclinationDegrees is >= -90 and <= 90)) return null;
        var horizontal = CoordinateTransforms.EquatorialToHorizontal(meanOfDate, Frame.MidpointUtc,
            Frame.Observer.LatitudeDegrees, Frame.Observer.LongitudeDegrees);
        return HorizontalToPixel(horizontal, space);
    }

    /// <summary>Converts geometric horizontal coordinates above or on the horizon, without refraction.</summary>
    public PixelPoint? HorizontalToPixel(AltAzPoint horizontal, AstrometricPixelSpace space = AstrometricPixelSpace.Readout)
    {
        if (!Enum.IsDefined(space)) throw new ArgumentOutOfRangeException(nameof(space));
        if (!(horizontal.AltitudeDegrees is >= 0 and <= 90 && horizontal.AzimuthDegrees is >= 0 and < 360) ||
            _mapping.ProjectHorizontal(horizontal) is not { } pixel || !InsideReadout(pixel)) return null;
        if (space == AstrometricPixelSpace.Readout) return pixel;
        return _readout is { } readout
            ? new(readout.RoiX + pixel.X * readout.BinX, readout.RoiY + pixel.Y * readout.BinY)
            : null;
    }

    /// <summary>Maps ordered samples at the exposure midpoint. Invalid samples retain their positions as null.</summary>
    public IReadOnlyList<AstrometricSkyPoint?> MapTrack(IReadOnlyList<PixelPoint> samples,
        AstrometricPixelSpace space = AstrometricPixelSpace.Readout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!Enum.IsDefined(space)) throw new ArgumentOutOfRangeException(nameof(space));
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.Count > MaximumTrackSamples) throw new ArgumentException("Too many angular track samples.", nameof(samples));
        var mapped = new AstrometricSkyPoint?[samples.Count];
        for (var i = 0; i < mapped.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mapped[i] = PixelToSky(samples[i], space);
        }
        return Array.AsReadOnly(mapped);
    }

    private bool InsideReadout(PixelPoint pixel) => pixel.X >= 0 && pixel.Y >= 0 && pixel.X < _width && pixel.Y < _height;
}
