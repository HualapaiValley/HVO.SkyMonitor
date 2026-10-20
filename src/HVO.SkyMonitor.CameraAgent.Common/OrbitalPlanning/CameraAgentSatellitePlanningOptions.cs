namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>Explicit local diagnostic opt-in. No acquisition, refresh or repository fixture defaults.</summary>
public sealed class CameraAgentSatellitePlanningOptions
{
    public const string SectionName = "CameraAgent:SatellitePlanning";
    public bool Enabled { get; set; }
    public bool AllowStaleDiagnostics { get; set; }
    public string? EarthOrientationPath { get; set; }
    public string? EarthOrientationSha256 { get; set; }
    public string? LeapSecondsPath { get; set; }
    public string? LeapSecondsSha256 { get; set; }

    internal CameraAgentSatellitePlanningOptions Copy() => (CameraAgentSatellitePlanningOptions)MemberwiseClone();
}
