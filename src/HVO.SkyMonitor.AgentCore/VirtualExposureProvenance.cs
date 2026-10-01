namespace HVO.SkyMonitor.AgentCore;

/// <summary>Separates virtual celestial/scenario clocks from real acquisition callbacks.</summary>
public enum VirtualExposureTimeMapping
{
    /// <summary>Both virtual clocks start at the operational request time.</summary>
    RequestUtc,
    /// <summary>The celestial interval repeats while the scenario follows requests.</summary>
    FixedCelestialUtc,
    /// <summary>Both virtual clocks follow the configured sequence timeline.</summary>
    FixedScenarioUtc,
    /// <summary>The celestial interval repeats while the scenario follows a sequence.</summary>
    FixedCelestialAndScenarioUtc
}

/// <summary>
/// Time-only, reconstructable virtual exposure facts. These are simulated logical intervals,
/// not evidence of a sensor shutter or readout time. Midpoints round down to an integral UTC tick.
/// </summary>
public sealed record VirtualExposureProvenance(
    string SchemaVersion,
    VirtualExposureTimeMapping TimeMapping,
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset ScenarioStartUtc,
    DateTimeOffset ScenarioEndUtc,
    DateTimeOffset ScenarioMidpointUtc,
    DateTimeOffset CelestialStartUtc,
    DateTimeOffset CelestialEndUtc,
    DateTimeOffset CelestialMidpointUtc)
{
    /// <summary>Identifies the inclusive endpoints and floor-midpoint convention.</summary>
    public const string CurrentSchemaVersion = "virtual-exposure-utc-ticks-v1";

    /// <summary>Creates resolved logical facts without consulting mutable sequence state later.</summary>
    public static VirtualExposureProvenance Create(
        DateTimeOffset requestedStartUtc,
        DateTimeOffset scenarioStartUtc,
        DateTimeOffset celestialStartUtc,
        TimeSpan exposure,
        VirtualExposureTimeMapping timeMapping)
    {
        if (exposure < TimeSpan.Zero || exposure > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(exposure));
        var result = new VirtualExposureProvenance(CurrentSchemaVersion, timeMapping,
            requestedStartUtc, scenarioStartUtc, scenarioStartUtc + exposure,
            scenarioStartUtc.AddTicks(exposure.Ticks / 2), celestialStartUtc, celestialStartUtc + exposure,
            celestialStartUtc.AddTicks(exposure.Ticks / 2));
        if (!result.IsValid(exposure))
            throw new ArgumentException("Virtual exposure clocks, mapping and UTC intervals must agree.", nameof(timeMapping));
        return result;
    }

    /// <summary>Validates logical facts against the effective exposure; zero means an instantaneous zero-signal interval.</summary>
    public bool IsValid(TimeSpan effectiveExposure)
    {
        if (SchemaVersion != CurrentSchemaVersion || !Enum.IsDefined(TimeMapping) ||
            effectiveExposure < TimeSpan.Zero || effectiveExposure > TimeSpan.FromDays(1) ||
            !IsUtc(RequestedStartUtc) || !IsUtc(ScenarioStartUtc) || !IsUtc(ScenarioEndUtc) ||
            !IsUtc(ScenarioMidpointUtc) || !IsUtc(CelestialStartUtc) || !IsUtc(CelestialEndUtc) ||
            !IsUtc(CelestialMidpointUtc) || ScenarioEndUtc < ScenarioStartUtc || CelestialEndUtc < CelestialStartUtc ||
            ScenarioEndUtc - ScenarioStartUtc != effectiveExposure || CelestialEndUtc - CelestialStartUtc != effectiveExposure ||
            ScenarioMidpointUtc.Ticks - ScenarioStartUtc.Ticks != effectiveExposure.Ticks / 2 ||
            CelestialMidpointUtc.Ticks - CelestialStartUtc.Ticks != effectiveExposure.Ticks / 2)
            return false;

        return TimeMapping switch
        {
            VirtualExposureTimeMapping.RequestUtc => ScenarioStartUtc == RequestedStartUtc && CelestialStartUtc == ScenarioStartUtc,
            VirtualExposureTimeMapping.FixedCelestialUtc => ScenarioStartUtc == RequestedStartUtc,
            VirtualExposureTimeMapping.FixedScenarioUtc => CelestialStartUtc == ScenarioStartUtc,
            VirtualExposureTimeMapping.FixedCelestialAndScenarioUtc => true,
            _ => false
        };
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
