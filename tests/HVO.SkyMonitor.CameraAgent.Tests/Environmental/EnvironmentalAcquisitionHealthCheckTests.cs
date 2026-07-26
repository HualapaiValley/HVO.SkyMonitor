using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalAcquisitionHealthCheckTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private string? _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"environment-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisabledAcquisitionIsHealthyWithoutReadingJournal()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var check = new EnvironmentalAcquisitionHealthCheck(
            store,
            store,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Path.Combine(_root!, "missing"),
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions { Enabled = false }
            }),
            new FixedTimeProvider(Epoch));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Healthy, result.Status);
        Assert.AreEqual("Disabled", result.Data["Availability"]);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root!, "missing")));
    }

    [TestMethod]
    public async Task RequiredSourceTransitionsFromInitializingToUnableWithoutLeakingReason()
    {
        using var document = JsonDocument.Parse("{}");
        var source = new EnvironmentalSourceConfiguration
        {
            Id = "temperature",
            Type = "VirtualEnvironment",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            ScheduleEpochUtc = Epoch,
            Options = document.RootElement.Clone()
        };
        var configured = new CameraAgentHostOptions
        {
            RawIngressRoot = _root!,
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                Sources = [source]
            }
        };
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch));
        var check = new EnvironmentalAcquisitionHealthCheck(
            store, store, Options.Create(configured), new FixedTimeProvider(Epoch));
        Assert.AreEqual(
            HealthStatus.Degraded,
            (await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status);
        await store.RecordAttemptAsync(
            _root!,
            source.ToDescriptor(),
            new EnvironmentalAcquisitionReceipt(
                source.Id,
                EnvironmentalAcquisitionTrigger.Periodic,
                EnvironmentalAcquisitionDisposition.Failed,
                "source-failure",
                null,
                Epoch,
                Epoch.AddMilliseconds(10)),
            null,
            null,
            CancellationToken.None).ConfigureAwait(false);

        var failed = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Unhealthy, failed.Status);
        Assert.AreEqual(1, failed.Data["FailingSourceCount"]);
        Assert.IsFalse(failed.Data.Values.Any(value => string.Equals(value?.ToString(), "source-failure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SuccessfulSourceTransitionsFromHealthyThroughStaleAndBackToHealthy()
    {
        using var document = JsonDocument.Parse("{}");
        var source = new EnvironmentalSourceConfiguration
        {
            Id = "temperature",
            Type = "VirtualEnvironment",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            ScheduleEpochUtc = Epoch,
            Options = document.RootElement.Clone()
        };
        var configured = new CameraAgentHostOptions
        {
            RawIngressRoot = _root!,
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                Sources = [source]
            }
        };
        var clock = new MutableTimeProvider(Epoch);
        using var store = new SqliteEnvironmentalObservationOutbox(clock);
        var check = new EnvironmentalAcquisitionHealthCheck(store, store, Options.Create(configured), clock);
        await RecordProducedAsync(store, source, Epoch, Epoch.AddSeconds(45), Epoch.AddSeconds(30)).ConfigureAwait(false);
        Assert.AreEqual(
            HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status);

        clock.Advance(TimeSpan.FromSeconds(46));
        var stale = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Degraded, stale.Status);
        Assert.AreEqual(1, stale.Data["StaleSourceCount"]);
        Assert.AreEqual(1, stale.Data["OverduePollCount"]);

        await RecordProducedAsync(
            store, source, clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(45), clock.GetUtcNow().AddSeconds(30))
            .ConfigureAwait(false);
        Assert.AreEqual(
            HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status);
    }

    private async Task RecordProducedAsync(
        SqliteEnvironmentalObservationOutbox store,
        EnvironmentalSourceConfiguration source,
        DateTimeOffset observedAtUtc,
        DateTimeOffset staleAfterUtc,
        DateTimeOffset nextPollUtc)
    {
        await store.RecordAttemptAsync(
            _root!,
            source.ToDescriptor(),
            new EnvironmentalAcquisitionReceipt(
                source.Id,
                EnvironmentalAcquisitionTrigger.Periodic,
                EnvironmentalAcquisitionDisposition.Produced,
                "produced",
                Guid.NewGuid(),
                observedAtUtc,
                observedAtUtc.AddMilliseconds(10),
                observedAtUtc,
                staleAfterUtc),
            null,
            null,
            CancellationToken.None).ConfigureAwait(false);
        await store.UpdateSourceScheduleAsync(
            _root!, source.ToDescriptor(), nextPollUtc, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
