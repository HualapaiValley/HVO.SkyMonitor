namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>Explicit local diagnostic opt-in. No repository fixture defaults; the time-data refresh is separately opted in.</summary>
public sealed class CameraAgentSatellitePlanningOptions
{
    public const string SectionName = "CameraAgent:SatellitePlanning";
    public bool Enabled { get; set; }
    public bool AllowStaleDiagnostics { get; set; }
    public string? EarthOrientationPath { get; set; }
    public string? EarthOrientationSha256 { get; set; }
    public string? LeapSecondsPath { get; set; }
    public string? LeapSecondsSha256 { get; set; }

    /// <summary>Allows the managed, at-most-daily time-data refresh to download and adopt a newer pair. Off by default.</summary>
    public bool TimeDataRefreshEnabled { get; set; }

    internal CameraAgentSatellitePlanningOptions Copy() => (CameraAgentSatellitePlanningOptions)MemberwiseClone();
}
