using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceRegistrationServiceTests
{
    [TestMethod]
    public async Task CreatePendingAsync_WithValidRequest_PersistsSnapshot()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 11, 25, 6, 30, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var observatoryId = Guid.NewGuid();

        context.Observatories.Add(new Observatory
        {
            Id = observatoryId,
            OwnerUserId = "owner-1",
            Name = "Summit Ridge",
            LatitudeDegrees = 21.3,
            LongitudeDegrees = -157.8,
            ElevationMeters = 1500,
            TimeZoneId = "Pacific/Honolulu",
            IsActive = true
        });
        await context.SaveChangesAsync().ConfigureAwait(false);

        var service = new DeviceRegistrationService(context, timeProvider);
        var request = new DeviceRegistrationCreateRequest(
            "camera-alpha",
            "verify-code",
            observatoryId,
            "  Summit Cam  ",
            "owner-1",
            "  Summit Ops  ",
            "summit@example.com",
            "SelfAttested",
            "Ready to deploy",
            TimeSpan.FromMinutes(30));

        var result = await service.CreatePendingAsync(request, CancellationToken.None).ConfigureAwait(false);

        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Status.Should().Be(DeviceRegistrationStatus.Pending);
        result.FriendlyName.Should().Be("Summit Cam");
        result.OwnerDisplayName.Should().Be("Summit Ops");
        result.OwnerEmail.Should().Be("summit@example.com");
        result.OwnerConfirmedAtUtc.Should().Be(now);
        result.ObservatoryName.Should().Be("Summit Ridge");
        result.ObservatoryLatitudeDegrees.Should().Be(21.3);
        result.ObservatoryLongitudeDegrees.Should().Be(-157.8);
        result.ObservatoryTimeZoneId.Should().Be("Pacific/Honolulu");
        result.ExpiresAtUtc.Should().Be(now + TimeSpan.FromMinutes(30));
        result.VerificationCodeHash.Should().Be(DeviceRegistrationService.ComputeSha256("verify-code"));

        var stored = await context.DeviceRegistrations.SingleAsync().ConfigureAwait(false);
        stored.Id.Should().Be(result.Id);
        stored.OwnerConfirmationNotes.Should().Be("Ready to deploy");
    }

    [TestMethod]
    public async Task CreatePendingAsync_WithExistingPending_ReusesAndResetsRegistration()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 11, 25, 6, 45, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var observatoryId = Guid.NewGuid();

        context.Observatories.Add(new Observatory
        {
            Id = observatoryId,
            OwnerUserId = "owner-99",
            Name = "Dawn Ridge",
            LatitudeDegrees = 33.92,
            LongitudeDegrees = -118.4,
            ElevationMeters = 250,
            TimeZoneId = "America/Los_Angeles",
            IsActive = true
        });

        var existing = new DeviceRegistration
        {
            DeviceId = "camera-alpha",
            ObservatoryId = observatoryId,
            FriendlyName = "Old Name",
            OwnerUserId = "owner-99",
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = "OLD",
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = "key",
            RegistrationTokenHash = "token",
            ActivatedAtUtc = now.AddMinutes(-5),
            IssuedAtUtc = now.AddMinutes(-10),
            ExpiresAtUtc = now.AddMinutes(-1)
        };

        context.DeviceRegistrations.Add(existing);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var service = new DeviceRegistrationService(context, timeProvider);
        var request = new DeviceRegistrationCreateRequest(
            "camera-alpha",
            "new-code",
            observatoryId,
            "  New Cam Name ",
            "owner-99",
            "Owner Name",
            null,
            "Manual",
            null,
            TimeSpan.FromMinutes(10));

        var result = await service.CreatePendingAsync(request, CancellationToken.None).ConfigureAwait(false);

        result.Id.Should().Be(existing.Id);
        result.FriendlyName.Should().Be("New Cam Name");
        result.DevicePublicId.Should().BeNull();
        result.DeviceKeyHash.Should().BeNull();
        result.RegistrationTokenHash.Should().BeNull();
        result.ActivatedAtUtc.Should().BeNull();
        result.VerificationCodeHash.Should().Be(DeviceRegistrationService.ComputeSha256("new-code"));
        result.IssuedAtUtc.Should().Be(now);
        result.ExpiresAtUtc.Should().Be(now + TimeSpan.FromMinutes(10));

        (await context.DeviceRegistrations.CountAsync().ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task CreatePendingAsync_WithForeignOwnerPendingRegistration_RejectsWithoutChanges()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 11, 25, 7, 0, 0, TimeSpan.Zero);
        var firstObservatory = new Observatory
        {
            OwnerUserId = "owner-1",
            Name = "First Ridge",
            LatitudeDegrees = 19.7,
            LongitudeDegrees = -155.1,
            ElevationMeters = 1200,
            TimeZoneId = "Pacific/Honolulu",
            IsActive = true
        };
        var secondObservatory = new Observatory
        {
            OwnerUserId = "owner-2",
            Name = "Second Ridge",
            LatitudeDegrees = 20.7,
            LongitudeDegrees = -156.1,
            ElevationMeters = 1300,
            TimeZoneId = "Pacific/Honolulu",
            IsActive = true
        };
        var existing = new DeviceRegistration
        {
            DeviceId = "shared-camera",
            ObservatoryId = firstObservatory.Id,
            FriendlyName = "First Owner Camera",
            OwnerUserId = "owner-1",
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = "ORIGINAL",
            IssuedAtUtc = now.AddMinutes(-5),
            ExpiresAtUtc = now.AddMinutes(10)
        };
        context.Observatories.AddRange(firstObservatory, secondObservatory);
        context.DeviceRegistrations.Add(existing);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = new DeviceRegistrationService(context, new TestTimeProvider(now));

        Func<Task> act = () => service.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            existing.DeviceId,
            "attacker-code",
            secondObservatory.Id,
            "Second Owner Camera",
            "owner-2",
            "Second Owner",
            null,
            "SelfAttested",
            null));

        await act.Should().ThrowAsync<DeviceRegistrationException>()
            .WithMessage("*access denied*").ConfigureAwait(false);
        existing.OwnerUserId.Should().Be("owner-1");
        existing.ObservatoryId.Should().Be(firstObservatory.Id);
        existing.VerificationCodeHash.Should().Be("ORIGINAL");
        (await context.DeviceRegistrations.CountAsync().ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task CreatePendingAsync_WithMissingOrForeignObservatory_UsesNotFoundReason()
    {
        await using var context = CreateContext();
        var foreignObservatory = new Observatory
        {
            OwnerUserId = "owner-2",
            Name = "Foreign Ridge",
            LatitudeDegrees = 19.7,
            LongitudeDegrees = -155.1,
            ElevationMeters = 1200,
            TimeZoneId = "Pacific/Honolulu",
            IsActive = true
        };
        context.Observatories.Add(foreignObservatory);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = new DeviceRegistrationService(context, new TestTimeProvider(DateTimeOffset.UtcNow));

        Func<Task> foreign = () => service.CreatePendingAsync(CreateRequest(foreignObservatory.Id));
        Func<Task> missing = () => service.CreatePendingAsync(CreateRequest(Guid.NewGuid()));

        var foreignException = await foreign.Should().ThrowAsync<DeviceRegistrationException>()
            .ConfigureAwait(false);
        foreignException.Which.ReasonCode.Should().Be(DeviceRegistrationException.NotFoundReasonCode);
        var missingException = await missing.Should().ThrowAsync<DeviceRegistrationException>()
            .ConfigureAwait(false);
        missingException.Which.ReasonCode.Should().Be(DeviceRegistrationException.NotFoundReasonCode);
        context.DeviceRegistrations.Should().BeEmpty();
    }

    private static DeviceRegistrationCreateRequest CreateRequest(Guid observatoryId)
    {
        return new DeviceRegistrationCreateRequest(
            "camera-alpha",
            "verify-code",
            observatoryId,
            "Camera Alpha",
            "owner-1",
            "Owner One",
            null,
            "SelfAttested",
            null);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
