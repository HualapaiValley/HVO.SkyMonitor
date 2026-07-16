using FluentAssertions;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceHeartbeatServiceTests
{
    [TestMethod]
    public async Task RecordHeartbeatAsync_WithActiveDevicePersistsCurrentAndHistory()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);
        var report = FleetStatusTestData.CreateReport(registration.DevicePublicId!.Value, observedAtUtc: now);

        var result = await service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", report, CancellationToken.None).ConfigureAwait(false);

        result.Disposition.Should().Be(FleetHeartbeatDisposition.Advanced);
        registration.LastSeenUtc.Should().Be(now);
        var current = await context.DeviceFleetStates.SingleAsync().ConfigureAwait(false);
        current.Sequence.Should().Be(1);
        current.ReceivedAtUtc.Should().Be(now);
        current.ClockDiagnostic.Should().Be(FleetClockDiagnostic.WithinTolerance);
        (await context.DeviceHeartbeatRecords.SingleAsync().ConfigureAwait(false)).IsSignificantSnapshot.Should().BeTrue();
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_ExactDuplicateIsIdempotent()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);
        var report = FleetStatusTestData.CreateReport(registration.DevicePublicId!.Value, observedAtUtc: now);

        await service.RecordHeartbeatAsync(registration.DeviceId, "device-key", report).ConfigureAwait(false);
        var duplicate = await service.RecordHeartbeatAsync(registration.DeviceId, "device-key", report).ConfigureAwait(false);

        duplicate.Disposition.Should().Be(FleetHeartbeatDisposition.Duplicate);
        (await context.DeviceHeartbeatRecords.CountAsync().ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_OutOfOrderReportCannotRegressCurrent()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);
        var instance = registration.DevicePublicId!.Value;

        await service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(instance, 3, observedAtUtc: now)).ConfigureAwait(false);
        var historical = await service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(instance, 2, observedAtUtc: now.AddHours(1))).ConfigureAwait(false);

        historical.Disposition.Should().Be(FleetHeartbeatDisposition.Historical);
        var current = await context.DeviceFleetStates.SingleAsync().ConfigureAwait(false);
        current.Sequence.Should().Be(3);
        current.SequenceGapCount.Should().Be(1);
        (await context.DeviceHeartbeatRecords.CountAsync().ConfigureAwait(false)).Should().Be(2);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_SequenceGapAndBootChangeAreRetained()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);
        var instance = registration.DevicePublicId!.Value;

        await service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(instance, 1, observedAtUtc: now)).ConfigureAwait(false);
        await service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(
                instance, 4, Guid.NewGuid(), now)).ConfigureAwait(false);

        var current = await context.DeviceFleetStates.SingleAsync().ConfigureAwait(false);
        current.SequenceGapCount.Should().Be(1);
        current.BootSessionChangeCount.Should().Be(1);
        current.LastSequenceGapUtc.Should().Be(now);
        current.LastBootSessionChangeUtc.Should().Be(now);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_ClockSkewBoundaryIsVisibleButDoesNotReject()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);
        var instance = registration.DevicePublicId!.Value;

        await service.RecordHeartbeatAsync(
            registration.DeviceId,
            "device-key",
            FleetStatusTestData.CreateReport(instance, 1, observedAtUtc: now.AddSeconds(120))).ConfigureAwait(false);
        (await context.DeviceFleetStates.SingleAsync().ConfigureAwait(false)).ClockDiagnostic
            .Should().Be(FleetClockDiagnostic.WithinTolerance);
        await service.RecordHeartbeatAsync(
            registration.DeviceId,
            "device-key",
            FleetStatusTestData.CreateReport(instance, 2, observedAtUtc: now.AddSeconds(121))).ConfigureAwait(false);

        var current = await context.DeviceFleetStates.SingleAsync().ConfigureAwait(false);
        current.ClockDiagnostic.Should().Be(FleetClockDiagnostic.ClockAhead);
        current.ApparentClockOffsetSeconds.Should().Be(121);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_ReportForAnotherAgentIsRejected()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);

        Func<Task> act = () => service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(Guid.NewGuid()));

        await act.Should().ThrowAsync<FleetHeartbeatConflictException>().ConfigureAwait(false);
        (await context.DeviceHeartbeatRecords.CountAsync().ConfigureAwait(false)).Should().Be(0);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_WhenNotBootstrappedThrows()
    {
        await using var context = CreateContext();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero));
        var registration = await SeedRegistrationAsync(context, bootstrapped: false).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var service = CreateService(context, timeProvider, telemetry);

        Func<Task> act = () => service.RecordHeartbeatAsync(
            registration.DeviceId, "device-key", FleetStatusTestData.CreateReport(Guid.NewGuid()));

        await act.Should().ThrowAsync<DeviceRegistrationException>().WithMessage("*bootstrap*").ConfigureAwait(false);
    }

    [TestMethod]
    public void ClassifierUsesExactServerReceiptAndClockBoundaries()
    {
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var classifier = new FleetStatusClassifier(Options.Create(new FleetStatusOptions()));
        var state = new DeviceFleetState
        {
            RegistrationId = Guid.NewGuid(),
            ReceivedAtUtc = now.AddSeconds(-149),
            ReportedHealth = FleetHealth.Healthy,
            ClockDiagnostic = FleetClockDiagnostic.WithinTolerance
        };

        classifier.Classify(state, now).State.Should().Be(DerivedFleetState.Online);
        state.ReceivedAtUtc = now.AddSeconds(-150);
        classifier.Classify(state, now).State.Should().Be(DerivedFleetState.Degraded);
        state.ReceivedAtUtc = now.AddSeconds(-300);
        classifier.Classify(state, now).State.Should().Be(DerivedFleetState.Offline);
        state.ReceivedAtUtc = now;
        state.ClockDiagnostic = FleetClockDiagnostic.ClockAhead;
        classifier.Classify(state, now).State.Should().Be(DerivedFleetState.Degraded);
    }

    private static DeviceHeartbeatService CreateService(
        ApplicationDbContext context,
        TimeProvider timeProvider,
        FleetStatusTelemetry telemetry)
        => new(
            new DeviceCredentialValidator(context, timeProvider),
            context,
            timeProvider,
            Options.Create(new FleetStatusOptions()),
            telemetry,
            NullLogger<DeviceHeartbeatService>.Instance);

    private static async Task<DeviceRegistration> SeedRegistrationAsync(ApplicationDbContext context, bool bootstrapped)
    {
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var registration = new DeviceRegistration
        {
            DeviceId = $"device-{Guid.NewGuid():N}",
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Test",
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = now.AddHours(-2),
            ExpiresAtUtc = now.AddHours(2),
            DevicePublicId = bootstrapped ? Guid.NewGuid() : null,
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key"),
            ObservatoryName = "Test Observatory",
            OwnerUserId = "user-1",
            OwnerDisplayName = "Owner"
        };
        context.DeviceRegistrations.Add(registration);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return registration;
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
