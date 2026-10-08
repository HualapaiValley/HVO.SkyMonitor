namespace HVO.SkyMonitor.CameraAgent.Services;

internal static class TimeLapseLinks
{
    internal static string Detail(Guid id) => FormattableString.Invariant($"/archive/products/time-lapse/{id:D}");
    internal static string Video(Guid id) => FormattableString.Invariant($"/api/v1/operations/time-lapses/{id:D}/video");
    internal static string Provenance(Guid id) => FormattableString.Invariant($"/api/v1/operations/time-lapses/{id:D}/provenance");
}
