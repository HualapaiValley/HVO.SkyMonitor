using System.Collections.Immutable;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed record SwpcFreshnessThresholds(TimeSpan BaseInterval, TimeSpan OutdatedAfter);
/// <summary>Caller-supplied root and S2 freshness thresholds; validated when the store opens.</summary>
public sealed record SwpcStoreSettings(string RootDirectory,
    ImmutableDictionary<SwpcProduct, SwpcFreshnessThresholds> Freshness);
