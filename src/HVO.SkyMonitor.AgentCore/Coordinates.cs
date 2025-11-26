using System;

namespace HVO.SkyMonitor.AgentCore;

public sealed record PixelPoint(double X, double Y);
public sealed record AltAzPoint(double AltitudeDegrees, double AzimuthDegrees);

public interface IImageProjector
{
    PixelPoint ProjectAltAz(double altitudeDegrees, double azimuthDegrees);

    PixelPoint? ProjectEquatorial(double rightAscensionHours, double declinationDegrees, DateTimeOffset whenUtc);

    AltAzPoint UnprojectPixel(double x, double y);
}
