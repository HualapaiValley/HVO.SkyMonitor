using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceCredentialValidatorTests
{
    [TestMethod]
    public async Task ValidateAsync_WithValidCredentials_ReturnsRegistration()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 11, 25, 7, 0, 0, TimeSpan.Zero);
        var timeProvider = new DeviceCredentialValidatorTestsTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, status: DeviceRegistrationStatus.Active, expiresAt: now.AddHours(1)).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("secret-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        var result = await validator.ValidateAsync(registration.DeviceId, "secret-key", CancellationToken.None).ConfigureAwait(false);

        result.Should().BeSameAs(registration);
    }

    [TestMethod]
    public async Task ValidateAsync_WhenKeyDoesNotMatch_Throws()
    {
        await using var context = CreateContext();
        var timeProvider = new DeviceCredentialValidatorTestsTimeProvider(DateTimeOffset.UtcNow);
        var registration = await SeedRegistrationAsync(context, DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("real-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        Func<Task> act = () => validator.ValidateAsync(registration.DeviceId, "wrong-key", CancellationToken.None);

        await act.Should().ThrowAsync<DeviceRegistrationException>().WithMessage("*invalid*");
    }

    [TestMethod]
    public async Task ValidateAsync_WhenStoredHashIsMalformed_RejectsCredentialsWithoutThrowingFormatException()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(
            context,
            DeviceRegistrationStatus.Active,
            DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);
        registration.DeviceKeyHash = new string('Z', 64);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var validator = new DeviceCredentialValidator(
            context,
            new DeviceCredentialValidatorTestsTimeProvider(DateTimeOffset.UtcNow));

        Func<Task> act = () => validator.ValidateAsync(registration.DeviceId, "secret-key", CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false)).Which;
        exception.ReasonCode.Should().Be("invalid-credential");
    }

    [TestMethod]
    public async Task ValidateAsync_WhenKeyBelongsToAnotherAgent_ReportsBoundedAuditIdentity()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var claimed = await SeedRegistrationAsync(context, DeviceRegistrationStatus.Active, now.AddHours(1)).ConfigureAwait(false);
        var owner = await SeedRegistrationAsync(context, DeviceRegistrationStatus.Active, now.AddHours(1)).ConfigureAwait(false);
        claimed.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("claimed-key");
        owner.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("owner-key");
        await context.SaveChangesAsync().ConfigureAwait(false);
        var validator = new DeviceCredentialValidator(context, new DeviceCredentialValidatorTestsTimeProvider(now));

        Func<Task> act = () => validator.ValidateAsync(claimed.DeviceId, "owner-key", CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false)).Which;
        exception.ReasonCode.Should().Be("cross-agent-credential");
        exception.ClaimedRegistrationId.Should().Be(claimed.Id);
        exception.CredentialOwnerRegistrationId.Should().Be(owner.Id);
        exception.Message.Should().NotContain("owner-key");
    }

    [TestMethod]
    public async Task ValidateAsync_WhenRegistrationExpired_Throws()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 11, 25, 7, 5, 0, TimeSpan.Zero);
        var timeProvider = new DeviceCredentialValidatorTestsTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, DeviceRegistrationStatus.Active, now.AddMinutes(-1)).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("secret-key");
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        Func<Task> act = () => validator.ValidateAsync(registration.DeviceId, "secret-key", CancellationToken.None);

        await act.Should().ThrowAsync<DeviceRegistrationException>().WithMessage("*expired*");
    }

    [TestMethod]
    public async Task ValidateAsync_WhenRegistrationNotActive_Throws()
    {
        await using var context = CreateContext();
        var timeProvider = new DeviceCredentialValidatorTestsTimeProvider(DateTimeOffset.UtcNow);
        var registration = await SeedRegistrationAsync(context, DeviceRegistrationStatus.Pending, DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("secret-key");
        registration.Status = DeviceRegistrationStatus.Pending;
        await context.SaveChangesAsync().ConfigureAwait(false);

        var validator = new DeviceCredentialValidator(context, timeProvider);
        Func<Task> act = () => validator.ValidateAsync(registration.DeviceId, "secret-key", CancellationToken.None);

        await act.Should().ThrowAsync<DeviceRegistrationException>().WithMessage("*not active*");
    }

    private static async Task<DeviceRegistration> SeedRegistrationAsync(ApplicationDbContext context, DeviceRegistrationStatus status, DateTimeOffset? expiresAt)
    {
        var registration = new DeviceRegistration
        {
            DeviceId = $"device-{Guid.NewGuid():N}",
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Test",
            Status = status,
            IssuedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAtUtc = expiresAt,
            DevicePublicId = Guid.NewGuid(),
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

    private sealed class DeviceCredentialValidatorTestsTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
