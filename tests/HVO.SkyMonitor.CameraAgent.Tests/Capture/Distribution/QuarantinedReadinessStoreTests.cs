using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
    public async Task QuarantinedSqliteLane_WithQueuedWorkBlockedBehindQuarantine_IsRefused(string limit)
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
        Assert.IsNull(await fixture.ClaimAsync("standard").ConfigureAwait(false), "ordered work cannot pass the quarantined head");
        var beforeRestart = await fixture.Store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
        var restarted = fixture.RestartLaneStore();
        Assert.IsNull(await restarted.ClaimAsync(fixture.Policy.Definitions.Single(static lane => lane.Name == "standard"),
            "after-restart", fixture.Configuration, CancellationToken.None).ConfigureAwait(false));
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
        var afterRestart = await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var backlogs in new[] { beforeRestart, afterRestart })
        {
            var standard = backlogs.Single(static lane => lane.Lane == "standard");
            Assert.IsNotNull(standard.ActiveWork, "SQLite must supply active-work evidence");
            Assert.AreEqual(1L, standard.ActiveWork.Count);
            var state = new CaptureLaneState(fixture.Time, healthOptions);
            state.Update(backlogs);
            var lanes = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            Assert.AreEqual(1, lanes.Data["RequiredPressureLaneCount"]);
            using var health = await WriteReadinessAsync(fixture, lanes).ConfigureAwait(false);

            Assert.IsFalse(QuarantinedReadiness.TryAccept(health.RootElement, Catalog, out var reason));
            StringAssert.Contains(reason, "required-lane backlog pressure", StringComparison.Ordinal);
            StringAssert.Contains(reason, "queued behind a quarantined head", StringComparison.Ordinal);
            StringAssert.Contains(reason, "acknowledge", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task QuarantinedSqliteLane_UnrelatedRequiredLaneAgePressure_IsRefused()
    {
        using var fixture = DurableCaptureDistributionTests.CreateFixture(new CaptureDistributionOptions
        {
            SecondaryLanes = [new() { Name = "independent", Enabled = true, Required = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await fixture.Store.FailAsync(lease, CaptureLaneHandlerResult.Terminal("test-quarantine"), CancellationToken.None).ConfigureAwait(false);
        fixture.Time.Advance(TimeSpan.FromMinutes(10_081));
        var restarted = fixture.RestartLaneStore();
        var backlogs = await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, backlogs.Single(static lane => lane.Lane == "standard").ActiveWork!.Count);
        Assert.AreEqual(1L, backlogs.Single(static lane => lane.Lane == "independent").ActiveWork!.Count);
        var state = new CaptureLaneState(fixture.Time, fixture.Options);
        state.Update(backlogs);
        var lanes = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(1, lanes.Data["RequiredPressureLaneCount"]);
        using var health = await WriteReadinessAsync(fixture, lanes).ConfigureAwait(false);

        Assert.IsFalse(QuarantinedReadiness.TryAccept(health.RootElement, Catalog, out var reason));
        StringAssert.Contains(reason, "required-lane backlog pressure", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceInvalidSqliteQuarantine_OnlyDegradedRawIngressIsAccepted(bool unhealthyRaw)
    {
        using var fixture = DurableCaptureDistributionTests.CreateFixture(new CaptureDistributionOptions());
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await fixture.Store.FailAsync(lease, CaptureLaneHandlerResult.Terminal("evidence-invalid"), CancellationToken.None).ConfigureAwait(false);
        var restarted = fixture.RestartLaneStore();
        var state = new CaptureLaneState(fixture.Time, fixture.Options);
        state.Update(await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false));
        var lanes = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        var rawState = new RawIngressState(fixture.Time);
        rawState.Set(unhealthyRaw ? RawIngressAvailability.Unhealthy : RawIngressAvailability.Degraded,
            "evidence-invalid", quarantineCount: 1, quarantineBytes: 4);
        var raw = await new RawIngressHealthCheck(rawState).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        using var health = await WriteReadinessAsync(fixture, lanes, raw).ConfigureAwait(false);

        Assert.AreEqual(!unhealthyRaw, QuarantinedReadiness.TryAccept(health.RootElement, Catalog, out _));
        Assert.AreEqual(1L, (await restarted.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
            .Single(static lane => lane.Lane == "standard").QuarantineCount, "readiness must not mutate the row");
    }

    private static async Task<JsonDocument> WriteReadinessAsync(
        DurableCaptureDistributionTests.Fixture fixture, HealthCheckResult lanes, HealthCheckResult? raw = null)
    {
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var processingState = new CaptureProcessingState();
        using var refresh = new CaptureProcessingStateRefreshService(store, processingState, fixture.Time);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var processing = await new CaptureProcessingHealthCheck(processingState).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(1L, processing.Data["LaneQuarantineTerminalCount"]);
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
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
        };
        if (raw is { } rawResult) entries["raw-ingress"] = Entry(rawResult);
        var report = new HealthReport(entries, TimeSpan.Zero);
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
