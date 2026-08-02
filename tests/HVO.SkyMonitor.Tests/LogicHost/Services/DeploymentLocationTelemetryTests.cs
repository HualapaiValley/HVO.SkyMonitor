using System.Diagnostics.Metrics;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class DeploymentLocationTelemetryTests
{
    [TestMethod]
    public void MetricsUseOnlyBoundedCategoricalLabels()
    {
        var observations = new List<(string Name, string[] Keys)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == DeploymentLocationTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.Start();
        using var telemetry = new DeploymentLocationTelemetry();

        telemetry.RecordOperation(
            "operator-resolve", "applied", "owner-acknowledged", "deployment", TimeSpan.FromMilliseconds(3));
        telemetry.RecordBackfill(2);
        telemetry.RecordReconciliation("state", "completed", 2, TimeSpan.FromMilliseconds(4));
        telemetry.RecordPendingSnapshot(4, 30);
        listener.RecordObservableInstruments();

        observations.Select(item => item.Name).Should().Contain([
            "skymonitor.deployment_location.operations",
            "skymonitor.deployment_location.duration",
            "skymonitor.deployment_location.backfill",
            "skymonitor.deployment_location.reconciliation.items",
            "skymonitor.deployment_location.reconciliation.duration",
            "skymonitor.deployment_location.pending",
            "skymonitor.deployment_location.oldest_age"
        ]);
        var allowed = new HashSet<string>(["operation", "outcome", "reason", "entity", "phase"], StringComparer.Ordinal);
        observations.SelectMany(item => item.Keys).Should().OnlyContain(key => allowed.Contains(key));
    }

    [TestMethod]
    public async Task BootstrapSpanUsesBoundedTagsWithoutLocationFacts()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeploymentLocationTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var observatory = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Signal observatory",
            LatitudeDegrees = 35.347,
            LongitudeDegrees = -113.878,
            ElevationMeters = 520,
            TimeZoneId = "America/Phoenix",
            AllowedDeploymentRadiusMeters = 1000,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            DeviceId = "signal-camera",
            FriendlyName = "Signal camera",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Owner",
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = Guid.NewGuid(),
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = DateTimeOffset.UnixEpoch
        };
        db.AddRange(observatory, registration);
        await db.SaveChangesAsync();
        var service = new DeploymentLocationAuthorityService(db, TimeProvider.System);

        _ = await service.ProposeAsync(
            registration,
            DeploymentLocationSnapshot.Create(
                "signal-location", 1, "gps", 2, DateTimeOffset.UnixEpoch, null,
                35.347, -113.878, 520, "America/Phoenix"),
            DeploymentLocationSourceKind.Gps,
            "bootstrap:signal-camera");

        var activity = stopped.Should().ContainSingle(item =>
            item.OperationName == "deployment-location.bootstrap.resolve").Subject;
        var tags = activity.TagObjects.ToDictionary(item => item.Key, item => item.Value?.ToString());
        tags.Keys.Should().BeEquivalentTo(
            "deployment.outcome",
            "deployment.reason",
            "deployment.source_kind",
            "deployment.status",
            "deployment.version_relation");
        tags["deployment.version_relation"].Should().Be("current");
        string.Join(';', tags.Values).Should().NotContain("35.347").And.NotContain("-113.878")
            .And.NotContain("America/Phoenix").And.NotContain("signal-location");
    }

    [TestMethod]
    public async Task ReconcileAsync_WhenPersistenceFails_LogsStableFailureEvent()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new RecordingLogger<DeploymentLocationAuthorityService>();
        var service = new DeploymentLocationAuthorityService(
            db, TimeProvider.System, logger: logger);
        var deployment = new DeviceDeploymentLocationVersion
        {
            LocationId = "signal-location",
            Version = 1,
            SourceKind = DeploymentLocationSourceKind.Gps,
            Status = DeploymentLocationResolutionStatus.Pending
        };
        await db.DisposeAsync();

        Func<Task> action = () => service.ReconcileAsync(deployment);

        await action.Should().ThrowAsync<ObjectDisposedException>();
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error
            && entry.EventId.Id == 7406
            && entry.Exception is ObjectDisposedException);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, EventId EventId, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, exception));
    }
}
