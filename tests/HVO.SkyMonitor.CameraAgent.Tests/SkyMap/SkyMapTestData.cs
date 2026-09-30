using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>A fixed sky map projection shared by the UI-service and page tests.</summary>
internal static class SkyMapTestData
{
    internal static CameraAgentSkyMapProjectionResult Result(DateTimeOffset atUtc) => new(
        atUtc,
        new CameraAgentSkyMapCatalogIdentity(
            "hvo-hyg-v3", "3.7.0", new string('A', 64), "CC-BY-4.0", "2", "3"),
        EphemerisModelVersion,
        new CameraAgentSkyMapObserver(
            "hvo-observatory", 1, new string('B', 64), "startup-seed",
            DeploymentLocationSourceKind.Manual, CameraAgentSkyMapLocationOrigin.StartupSeed,
            StagedAcknowledgementPending: false,
            35.2, -111.65, 2100, "America/Phoenix", 5,
            DateTimeOffset.UnixEpoch, null, EffectiveAtInstant: true),
        new CameraAgentSkyMapGeometry(
            "EquidistantFisheye", "Circular", 90, 0, 0, HorizontalFlip: false,
            170, null, 968, 608, 560, 560, 1936, 1216, 560,
            "rig-v1", new string('D', 64), "calibration-v1", "rig-projection-v2",
            [
                new CameraAgentSkyMapCardinal("North", 0, 968, 48),
                new CameraAgentSkyMapCardinal("East", 90, 1528, 608),
                new CameraAgentSkyMapCardinal("South", 180, 968, 1168),
                new CameraAgentSkyMapCardinal("West", 270, 408, 608)
            ],
            [
                new CameraAgentSkyMapAltitudeRing(0, []),
                new CameraAgentSkyMapAltitudeRing(30, [Line((968, 328), (1248, 608), (968, 888), (688, 608), (968, 328))]),
                new CameraAgentSkyMapAltitudeRing(60, [Line((968, 468), (1108, 608), (968, 748), (828, 608), (968, 468))])
            ]),
        [
            new CameraAgentSkyMapObject("sirius", "Sirius", "Star", -1.46, 41.2, 172.5, 970.1, 1090.4, "32349"),
            new CameraAgentSkyMapObject("vega", "Vega", "Star", 0.03, 68.9, 47.1, 1120.6, 430.2, "91262")
        ],
        [new CameraAgentSkyMapConstellation("Lyr", 5, [Line((1120.6, 430.2), (1150.2, 470.8), (1180.4, 480.1))])],
        ["Lyr", "Ori"],
        CameraAgentSkyMapProjection.DefaultMaximumObjects,
        ObjectsAtBound: false,
        CameraAgentSkyMapProjection.MaximumMagnitude,
        CameraAgentSkyMapProjection.AstronomyAlgorithmVersion,
        "2 of at most 200 catalog objects are visible.",
        null);

    /// <summary>The ephemeris model the fixed projection claims placed its solar-system bodies.</summary>
    internal const string EphemerisModelVersion = "astronomy-engine-2.1.19-eqj-v1";

    internal static CameraAgentSkyMapPolyline Line(params (double X, double Y)[] points)
        => new([.. points.Select(static point => new CameraAgentSkyMapPoint(point.X, point.Y))]);
}
