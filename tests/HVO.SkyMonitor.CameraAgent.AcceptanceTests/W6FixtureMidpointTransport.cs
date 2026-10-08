using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Tests;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Moves a W6 conformance-fixture pixel from the fixture scene instant to a capture's celestial midpoint through the
/// independent Cartesian reference linked from CameraAgent.Tests. The fixture stays the absolute anchor; the reference
/// supplies only the differential between the two instants (docs/astronomy/virtual-camera-astrometry-baseline.md).
/// </summary>
internal static class W6FixtureMidpointTransport
{
    internal static PixelPoint Transport(
        CameraRigConfig rig,
        ObserverLocation site,
        PixelPoint fixturePixel,
        DateTimeOffset fixtureUtc,
        DateTimeOffset midpointUtc)
    {
        var ray = VirtualAstrometryReference.Unproject(rig, fixturePixel)
            ?? throw new InvalidDataException($"The fixture pixel {fixturePixel} does not unproject through the W6 rig.");
        var moved = VirtualAstrometryReference.ToEnu(
            VirtualAstrometryReference.FromEnu(ray, fixtureUtc, site), midpointUtc, site);
        return VirtualAstrometryReference.Project(rig, moved)
            ?? throw new InvalidDataException($"The transported fixture pixel {fixturePixel} leaves the W6 readout.");
    }
}
