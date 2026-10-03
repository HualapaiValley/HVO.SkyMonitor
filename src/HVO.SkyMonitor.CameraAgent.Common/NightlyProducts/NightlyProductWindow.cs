namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>The exact span of one retained automation source window.</summary>
internal readonly record struct NightlyProductWindow(DateOnly ObservingDate, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
