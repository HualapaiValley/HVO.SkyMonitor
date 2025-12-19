using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
public sealed class DeviceRigProfileServiceTests
{
    [TestMethod]
    public async Task UpsertAsync_WhenFirstProfile_CreatesVersion1_AndUpdatesRegistrationCurrent()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 12, 18, 13, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var service = new DeviceRigProfileService(validator, context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);

        var result = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            "{\"b\":2,\"a\":1}",
            SoftwareVersion: "1.0.0"), CancellationToken.None).ConfigureAwait(false);

        result.RigProfileVersion.Should().Be(1);
        result.CreatedNewVersion.Should().BeTrue();

        registration.CurrentRigProfileVersion.Should().Be(1);
        registration.CurrentRigProfileHash.Should().Be(result.RigProfileHash);
        registration.CurrentRigProfileUpdatedAtUtc.Should().Be(now);
        registration.LastSeenUtc.Should().Be(now);

        var profile = await context.Set<DeviceRigProfile>().SingleAsync().ConfigureAwait(false);
        profile.Version.Should().Be(1);
        profile.ConfigJson.Should().Be("{\"a\":1,\"b\":2}");
        profile.ConfigHash.Should().Be(result.RigProfileHash);
        profile.SoftwareVersion.Should().Be("1.0.0");
    }

    [TestMethod]
    public async Task UpsertAsync_WhenJsonEquivalent_DoesNotCreateNewVersion()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 12, 18, 13, 5, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var service = new DeviceRigProfileService(validator, context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);

        var first = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            "{\"b\":2,\"a\":1}",
            SoftwareVersion: null), CancellationToken.None).ConfigureAwait(false);

        var second = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            "{\"a\":1,\"b\":2}",
            SoftwareVersion: null), CancellationToken.None).ConfigureAwait(false);

        second.RigProfileVersion.Should().Be(1);
        second.RigProfileHash.Should().Be(first.RigProfileHash);
        second.CreatedNewVersion.Should().BeFalse();

        var count = await context.Set<DeviceRigProfile>().CountAsync().ConfigureAwait(false);
        count.Should().Be(1);
    }

    [TestMethod]
    public async Task UpsertAsync_WhenNotBootstrapped_Throws()
    {
        await using var context = CreateContext();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);

        var registration = await SeedRegistrationAsync(context, bootstrapped: false).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var service = new DeviceRigProfileService(validator, context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);

        Func<Task> act = () => service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            "{}"), CancellationToken.None);

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
