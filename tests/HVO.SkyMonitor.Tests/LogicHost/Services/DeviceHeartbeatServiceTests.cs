using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
public sealed class DeviceHeartbeatServiceTests
{
    [TestMethod]
    public async Task RecordHeartbeatAsync_WithActiveBootstrappedDevice_UpdatesLastSeenUtc()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 12, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var service = new DeviceHeartbeatService(validator, context, timeProvider, NullLogger<DeviceHeartbeatService>.Instance);

        var result = await service.RecordHeartbeatAsync(new DeviceHeartbeatRequest(
            registration.DeviceId,
            "device-key",
            SoftwareVersion: "1.2.3",
            AgentState: "Running",
            TemperatureCelsius: 10.0,
            CpuPercent: 12.5), CancellationToken.None).ConfigureAwait(false);

        result.ServerTimeUtc.Should().Be(now);
        registration.LastSeenUtc.Should().Be(now);
        result.RecommendedHeartbeatSeconds.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task RecordHeartbeatAsync_WhenNotBootstrapped_Throws()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 12, 18, 12, 5, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var registration = await SeedRegistrationAsync(context, bootstrapped: false).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var service = new DeviceHeartbeatService(validator, context, timeProvider, NullLogger<DeviceHeartbeatService>.Instance);

        Func<Task> act = () => service.RecordHeartbeatAsync(new DeviceHeartbeatRequest(
            registration.DeviceId,
            "device-key",
            SoftwareVersion: null,
            AgentState: null,
            TemperatureCelsius: null,
            CpuPercent: null), CancellationToken.None);

        await act.Should().ThrowAsync<DeviceRegistrationException>()
            .WithMessage("*bootstrap*");
    }

    private static async Task<DeviceRegistration> SeedRegistrationAsync(ApplicationDbContext context, bool bootstrapped)
    {
        var registration = new DeviceRegistration
        {
            DeviceId = $"device-{Guid.NewGuid():N}",
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Test",
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2),
            DevicePublicId = bootstrapped ? Guid.NewGuid() : null,
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
