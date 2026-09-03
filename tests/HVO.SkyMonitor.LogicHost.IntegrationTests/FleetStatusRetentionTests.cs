using FluentAssertions;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class FleetStatusRetentionTests
{
    [TestMethod]
    public async Task HealthCheckAggregatesFleetStateInSql()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var check = new FleetStatusHealthCheck(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<FleetRetentionState>(),
            TimeProvider.System,
            Options.Create(new FleetStatusOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        result.Data.Should().ContainKeys("OnlineCount", "DegradedCount", "OfflineCount");
        Convert.ToInt32(result.Data["DegradedCount"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task SweepDeletesOnlyExpiredBoundedHistoryAndPreservesCurrentState()
    {
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var registration = await SeedAsync(now).ConfigureAwait(false);
        using var telemetry = new FleetStatusTelemetry();
        var worker = new FleetStatusRetentionWorker(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new FleetStatusOptions()),
            new FixedTimeProvider(now),
            new FleetRetentionState(),
            telemetry,
            NullLogger<FleetStatusRetentionWorker>.Instance);

        var deleted = await worker.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        deleted.Should().Be(2);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.DeviceHeartbeatRecords
            .Where(record => record.RegistrationId == registration.Id)
            .OrderBy(record => record.ReceivedAtUtc)
            .Select(record => record.IsSignificantSnapshot)
            .ToArrayAsync()
            .ConfigureAwait(false)).Should().Equal(true, false);
        (await db.DeviceFleetStates.SingleAsync(state => state.RegistrationId == registration.Id).ConfigureAwait(false))
            .Sequence.Should().Be(4);
    }

    private static async Task<DeviceRegistration> SeedAsync(DateTimeOffset now)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatory = new Observatory
        {
            OwnerUserId = $"fleet-retention-owner-{Guid.NewGuid():N}",
            Name = "Fleet retention observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = now,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = $"fleet-retention-{Guid.NewGuid():N}",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            FriendlyName = "Fleet retention device",
            ObservatoryName = observatory.Name,
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Fleet Retention",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = string.Concat(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")),
            IssuedAtUtc = now.AddDays(-1),
            ExpiresAtUtc = now.AddDays(1)
        };
        db.DeviceRegistrations.Add(registration);
        db.DeviceFleetStates.Add(new DeviceFleetState
        {
            RegistrationId = registration.Id,
            AgentInstanceId = registration.DevicePublicId.Value,
            BootSessionId = Guid.NewGuid(),
            Sequence = 4,
            ObservedAtUtc = now,
            ReceivedAtUtc = now,
            ClockDiagnostic = FleetClockDiagnostic.WithinTolerance,
            ReportedHealth = FleetHealth.Healthy,
            SoftwareVersion = "test",
            ConfigurationSha256 = new string('C', 64),
            StatusFingerprint = new string('D', 64),
            CurrentPayloadSha256 = new string('E', 64),
            SnapshotJson = "{}"
        });
        db.DeviceHeartbeatRecords.AddRange(
            Record(registration, 1, now.AddHours(-25), significant: false),
            Record(registration, 2, now.AddHours(-23), significant: false),
            Record(registration, 3, now.AddDays(-31), significant: true),
            Record(registration, 4, now.AddDays(-29), significant: true));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return registration;
    }

    private static DeviceHeartbeatRecord Record(
        DeviceRegistration registration,
        long sequence,
        DateTimeOffset received,
        bool significant)
        => new()
        {
            RegistrationId = registration.Id,
            AgentInstanceId = registration.DevicePublicId!.Value,
            BootSessionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Sequence = sequence,
            ObservedAtUtc = received,
            ReceivedAtUtc = received,
            PayloadSha256 = new string((char)('A' + sequence), 64),
            StatusFingerprint = new string('F', 64),
            ReportedHealth = FleetHealth.Healthy,
            ClockDiagnostic = FleetClockDiagnostic.WithinTolerance,
            AdvancedCurrent = true,
            IsSignificantSnapshot = significant,
            SnapshotJson = significant ? "{}" : null
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
