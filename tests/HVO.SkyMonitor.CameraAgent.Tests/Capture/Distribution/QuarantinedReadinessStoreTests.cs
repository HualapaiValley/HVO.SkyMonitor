using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class QuarantinedReadinessStoreTests
{
    // Invoke the actual anonymous /health writer without starting a server or widening its production visibility.
    private static readonly Func<HttpContext, HealthReport, Task> WriteHealth =
        typeof(SkyMonitorObservabilityExtensions).GetMethod("WriteDetailedHealthResponse", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<HttpContext, HealthReport, Task>>();
    private static readonly ApprovedCatalogContract Catalog = new(
        "test-catalog", "test", "test-r", "Test", "1", 1, "1", "1", "catalog.sqlite", "", 0, 1, "", "", "", "", "");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QuarantinedSqliteLane_AfterAdmissionOrAgeAndRestart_IsAccepted(bool ageOnly)
    {
        using var fixture = DurableCaptureDistributionTests.CreateFixture(new CaptureDistributionOptions { MaximumAttempts = 1 });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await fixture.Store.FailAsync(lease, CaptureLaneHandlerResult.Retry("test-quarantine"), CancellationToken.None).ConfigureAwait(false);
        if (ageOnly)
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(fixture.Options.Value.CaptureDistribution.RequiredMaximumOldestAgeMinutes + 1));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
                await fixture.Store.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        var restarted = fixture.RestartLaneStore();
        var backlogs = await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
        var standard = backlogs.Single(static lane => lane.Lane == "standard");
        Assert.AreEqual(1L, standard.QuarantineCount);
        if (!ageOnly) Assert.AreEqual(2, standard.PressureLevel, "admission's quarantine latch must survive restart");
        var state = new CaptureLaneState(fixture.Time, fixture.Options);
        state.Update(backlogs);
        Assert.AreEqual(2, state.Snapshot.Lanes.Single(static lane => lane.Lane == "standard").PressureLevel);
        var lanes = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, lanes.Status, "capture admission must still be unhealthy for quarantine");
        Assert.AreEqual(0, lanes.Data["RequiredPressureLaneCount"], "quarantine itself is not active backlog pressure");
        using var health = await WriteReadinessAsync(fixture, lanes).ConfigureAwait(false);

        Assert.IsTrue(QuarantinedReadiness.TryAccept(health.RootElement, Catalog, out var reason), reason);
    }

    [TestMethod]
    [DataRow("count")]
    [DataRow("bytes")]
    [DataRow("age")]
    public async Task QuarantinedSqliteLane_WithRealActivePressure_IsRefused(string limit)
    {
        using var fixture = DurableCaptureDistributionTests.CreateFixture(new CaptureDistributionOptions
        {
            MaximumAttempts = 2
        });
        // Admit all rows before quarantine; ordinary admission must continue blocking afterwards.
        for (var index = 0; index < 2; index++) await fixture.AcceptAsync(index).ConfigureAwait(false);
        var quarantine = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(quarantine);
        await fixture.Store.FailAsync(quarantine, CaptureLaneHandlerResult.Terminal("test-quarantine"), CancellationToken.None).ConfigureAwait(false);
        if (limit == "age") fixture.Time.Advance(TimeSpan.FromMinutes(10_081));
        var restarted = fixture.RestartLaneStore();
        // A restart can apply a stricter policy to an existing queue; seed through normal admission first.
        var healthOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            CaptureDistribution = new CaptureDistributionOptions
            {
                RequiredMaximumPendingCount = limit == "count" ? 1 : 100,
                RequiredMaximumPendingBytes = limit == "bytes" ? 3 : 10_000,
                RequiredMaximumOldestAgeMinutes = 10_080
            }
        });
        var state = new CaptureLaneState(fixture.Time, healthOptions);
        state.Update(await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false));
        var lanes = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(1, lanes.Data["RequiredPressureLaneCount"]);
        using var health = await WriteReadinessAsync(fixture, lanes).ConfigureAwait(false);

        Assert.IsFalse(QuarantinedReadiness.TryAccept(health.RootElement, Catalog, out _));
    }

    private static async Task<JsonDocument> WriteReadinessAsync(DurableCaptureDistributionTests.Fixture fixture, HealthCheckResult lanes)
    {
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var processingState = new CaptureProcessingState();
        using var refresh = new CaptureProcessingStateRefreshService(store, processingState, fixture.Time);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var processing = await new CaptureProcessingHealthCheck(processingState).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(1L, processing.Data["LaneQuarantineTerminalCount"]);
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["capture-lanes"] = Entry(lanes),
            ["capture-processing"] = Entry(processing),
            ["catalog"] = Entry(HealthCheckResult.Healthy("Catalog matches.", new Dictionary<string, object>
            {
                ["CatalogId"] = Catalog.CatalogId,
                ["CatalogVersion"] = Catalog.CatalogVersion,
                ["SchemaVersion"] = Catalog.SchemaVersion,
                ["PreprocessingVersion"] = Catalog.PreprocessingVersion,
                ["RowCount"] = Catalog.RowCount,
                ["PackageVersion"] = Catalog.PackageVersionPrefix + "1"
            }))
        }, TimeSpan.Zero);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var output = new MemoryStream();
        context.Response.Body = output;
        await WriteHealth(context, report).ConfigureAwait(false);
        output.Position = 0;
        return await JsonDocument.ParseAsync(output).ConfigureAwait(false);
    }

    private static HealthReportEntry Entry(HealthCheckResult result)
        => new(result.Status, result.Description, TimeSpan.Zero, result.Exception, result.Data);
}
