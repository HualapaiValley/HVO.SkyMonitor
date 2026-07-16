using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.TestSupport;

public static class FleetStatusTestData
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    public static FleetStatusReportV1 CreateReport(
        Guid agentInstanceId,
        long sequence = 1,
        Guid? bootSessionId = null,
        DateTimeOffset? observedAtUtc = null,
        FleetHealth health = FleetHealth.Healthy,
        bool pressure = false,
        long quarantineCount = 0)
    {
        var observed = observedAtUtc ?? new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var queue = new FleetQueueSummary(
            quarantineCount > 0 ? FleetAvailability.Degraded : FleetAvailability.Available,
            quarantineCount > 0 ? "quarantine" : "ready",
            0, 0, 0, 0, quarantineCount, null, observed);
        return new FleetStatusReportV1(
            FleetStatusReportV1.CurrentSchemaVersion,
            agentInstanceId,
            bootSessionId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            sequence,
            observed,
            observed.AddMinutes(-1),
            "1.0.0-test",
            new FleetConfigurationIdentity("1", Hash, "VirtualSky", "test-v1", Hash, Hash),
            new FleetCaptureSummary(FleetAvailability.Available, "capturing", observed, null, null),
            new FleetRuntimeSummary(10, 1_000_000, 5, observed),
            queue,
            queue,
            queue,
            queue,
            [new FleetLaneSummary("standard", true, 0, 0, 0, quarantineCount, pressure ? 2 : 0, null)],
            [new FleetStorageSummary("storage-1", 1_000_000, pressure ? 10_000 : 500_000, pressure, 7, observed, null)],
            [
                new FleetTimingSummary(FleetTimingSegment.ModuleRender, 10, 5, 8, 10),
                new FleetTimingSummary(FleetTimingSegment.Readout, 10, 2, 3, 4),
                new FleetTimingSummary(FleetTimingSegment.Ingress, 10, 1, 2, 3),
                new FleetTimingSummary(FleetTimingSegment.Processing, 10, 4, 6, 7),
                new FleetTimingSummary(FleetTimingSegment.Upload, 10, 3, 5, 6)
            ],
            health,
            [new FleetHealthCheckSummary("capture", health, health == FleetHealth.Healthy ? "ready" : "failure")]);
    }
}
