using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceRegistrationOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task OwnerScopedReads_UseCurrentObservatoryMembershipNotRegistrationSnapshotOwner()
    {
        await using var context = CreateContext();
        var firstObservatory = CreateObservatory("owner-1", "Owner One Current");
        var secondObservatory = CreateObservatory("owner-2", "Owner Two Current");
        var firstRegistration = CreateRegistration("owner-1", firstObservatory.Id, "device-1", "Owner One Snapshot");
        var secondRegistration = CreateRegistration("owner-2", secondObservatory.Id, "device-2", "Owner Two Snapshot");
        var inconsistentRegistration = CreateRegistration(
            "owner-1", secondObservatory.Id, "inconsistent-device", "Inconsistent Snapshot");
        var missingObservatoryRegistration = CreateRegistration(
            "owner-1", Guid.NewGuid(), "missing-observatory-device", "Missing Snapshot");

        context.Observatories.AddRange(firstObservatory, secondObservatory);
        AddOwnerMembership(context, firstObservatory);
        AddOwnerMembership(context, secondObservatory);
        context.DeviceRegistrations.AddRange(
            firstRegistration,
            secondRegistration,
            inconsistentRegistration,
            missingObservatoryRegistration);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var clock = new TestTimeProvider();
        var observatoryService = new ObservatoryService(
            context,
            clock,
            new DeploymentLocationAuthorityService(context, clock));
        var registrationService = new DeviceRegistrationReadService(context);

        var firstObservatories = await observatoryService.GetObservatoriesAsync("owner-1").ConfigureAwait(false);
        var secondObservatories = await observatoryService.GetObservatoriesAsync("owner-2").ConfigureAwait(false);
        var firstRegistrations = await registrationService.GetRegistrationsAsync("owner-1").ConfigureAwait(false);
        var secondRegistrations = await registrationService.GetRegistrationsAsync("owner-2").ConfigureAwait(false);

        firstObservatories.Should().ContainSingle(item =>
            item.Id == firstObservatory.Id && item.Name == "Owner One Current");
        secondObservatories.Should().ContainSingle(item =>
            item.Id == secondObservatory.Id && item.Name == "Owner Two Current");
        firstRegistrations.Should().ContainSingle(item =>
            item.RegistrationId == firstRegistration.Id
            && item.ObservatoryName == "Owner One Snapshot"
            && item.OwnerUserId == "owner-1");
        secondRegistrations.Should().HaveCount(2);
        secondRegistrations.Should().Contain(item =>
            item.RegistrationId == secondRegistration.Id
            && item.ObservatoryName == "Owner Two Snapshot"
            && item.OwnerUserId == "owner-2");
        secondRegistrations.Should().Contain(item =>
            item.RegistrationId == inconsistentRegistration.Id
            && item.OwnerUserId == "owner-1");
        firstRegistrations.Should().NotContain(item =>
            item.RegistrationId == inconsistentRegistration.Id
            || item.RegistrationId == missingObservatoryRegistration.Id);
    }

    [TestMethod]
    public async Task CreateEnvelopeAsync_UsesCurrentObservatoryMembershipNotRegistrationSnapshotOwner()
    {
        await using var context = CreateContext();
        var firstObservatory = CreateObservatory("owner-1", "Owner One");
        var secondObservatory = CreateObservatory("owner-2", "Owner Two");
        var firstRegistration = CreateRegistration("owner-1", firstObservatory.Id, "device-1", "Owner One");
        var inconsistentRegistration = CreateRegistration(
            "owner-1", secondObservatory.Id, "inconsistent-device", "Inconsistent");

        context.Observatories.AddRange(firstObservatory, secondObservatory);
        AddOwnerMembership(context, firstObservatory);
        AddOwnerMembership(context, secondObservatory);
        context.DeviceRegistrations.AddRange(firstRegistration, inconsistentRegistration);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateEnvelopeService(context);

        Func<Task> crossOwner = () => service.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            firstRegistration.Id,
            firstRegistration.DeviceId,
            firstObservatory.Id,
            "owner-2"));
        Func<Task> registrationOwnerWithForeignObservatory = () => service.CreateEnvelopeAsync(
            new DeviceRegistrationEnvelopeRequest(
                inconsistentRegistration.Id,
                inconsistentRegistration.DeviceId,
                secondObservatory.Id,
                "owner-1"));
        Func<Task> ownedObservatoryWithForeignRegistration = () => service.CreateEnvelopeAsync(
            new DeviceRegistrationEnvelopeRequest(
                inconsistentRegistration.Id,
                inconsistentRegistration.DeviceId,
                firstObservatory.Id,
                "owner-1"));
        Func<Task> wrongDeviceId = () => service.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            firstRegistration.Id,
            "wrong-device",
            firstObservatory.Id,
            "owner-1"));
        await crossOwner.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*access denied*").ConfigureAwait(false);
        await registrationOwnerWithForeignObservatory.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*access denied*").ConfigureAwait(false);
        await ownedObservatoryWithForeignRegistration.Should().ThrowAsync<DeviceRegistrationException>()
            .Where(exception => exception.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
            .WithMessage("*access denied*").ConfigureAwait(false);
        await wrongDeviceId.Should().ThrowAsync<DeviceRegistrationException>()
            .Where(exception => exception.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
            .WithMessage("*access denied*").ConfigureAwait(false);

        firstRegistration.DevicePublicId.Should().BeNull();
        firstRegistration.DeviceKeyHash.Should().BeNull();
        firstRegistration.RegistrationTokenHash.Should().BeNull();
        inconsistentRegistration.DevicePublicId.Should().BeNull();
        inconsistentRegistration.DeviceKeyHash.Should().BeNull();
        inconsistentRegistration.RegistrationTokenHash.Should().BeNull();

        var inconsistentEnvelope = await service.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            inconsistentRegistration.Id,
            inconsistentRegistration.DeviceId,
            secondObservatory.Id,
            "owner-2")).ConfigureAwait(false);

        inconsistentEnvelope.RegistrationId.Should().Be(inconsistentRegistration.Id);
        inconsistentRegistration.DevicePublicId.Should().Be(inconsistentEnvelope.DevicePublicId);
        inconsistentRegistration.DeviceKeyHash.Should().NotBeNullOrWhiteSpace();
        inconsistentRegistration.RegistrationTokenHash.Should().NotBeNullOrWhiteSpace();

        var envelope = await service.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            firstRegistration.Id,
            firstRegistration.DeviceId,
            firstObservatory.Id,
            "owner-1")).ConfigureAwait(false);

        envelope.RegistrationId.Should().Be(firstRegistration.Id);
        firstRegistration.DevicePublicId.Should().Be(envelope.DevicePublicId);
        firstRegistration.DeviceKeyHash.Should().NotBeNullOrWhiteSpace();
        firstRegistration.RegistrationTokenHash.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task CreateEnvelopeAsync_OwnedInactiveObservatoryIsRejectedWithoutCredentialChanges()
    {
        await using var context = CreateContext();
        var observatory = CreateObservatory("owner-1", "Inactive Observatory", isActive: false);
        var registration = CreateRegistration("owner-1", observatory.Id, "device-1", "Inactive Snapshot");
        context.Observatories.Add(observatory);
        AddOwnerMembership(context, observatory);
        context.DeviceRegistrations.Add(registration);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateEnvelopeService(context);

        Func<Task> act = () => service.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            registration.Id,
            registration.DeviceId,
            observatory.Id,
            "owner-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Observatory must be active*").ConfigureAwait(false);
        registration.DevicePublicId.Should().BeNull();
        registration.DeviceKeyHash.Should().BeNull();
        registration.RegistrationTokenHash.Should().BeNull();
    }

    private static DeviceRegistrationEnvelopeService CreateEnvelopeService(ApplicationDbContext context)
    {
        return new DeviceRegistrationEnvelopeService(
            context,
            new TestTimeProvider(),
            new EphemeralDataProtectionProvider(),
            NullLogger<DeviceRegistrationEnvelopeService>.Instance);
    }

    private static Observatory CreateObservatory(string ownerUserId, string name, bool isActive = true)
    {
        return new Observatory
        {
            OwnerUserId = ownerUserId,
            Name = name,
            LatitudeDegrees = 19.7,
            LongitudeDegrees = -155.1,
            ElevationMeters = 1200,
            TimeZoneId = "Pacific/Honolulu",
            CreatedAtUtc = Now.AddDays(-1),
            IsActive = isActive
        };
    }

    private static DeviceRegistration CreateRegistration(
        string ownerUserId,
        Guid observatoryId,
        string deviceId,
        string observatorySnapshotName)
    {
        return new DeviceRegistration
        {
            DeviceId = deviceId,
            ObservatoryId = observatoryId,
            FriendlyName = deviceId,
            ObservatoryName = observatorySnapshotName,
            ObservatoryLatitudeDegrees = 19.6,
            ObservatoryLongitudeDegrees = -155.0,
            ObservatoryElevationMeters = 1100,
            ObservatoryTimeZoneId = "Pacific/Honolulu",
            OwnerUserId = ownerUserId,
            OwnerDisplayName = ownerUserId,
            OwnerConfirmationMethod = "SelfAttested",
            OwnerConfirmedAtUtc = Now.AddMinutes(-5),
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("verification-code"),
            IssuedAtUtc = Now.AddMinutes(-5),
            ExpiresAtUtc = Now.AddMinutes(10)
        };
    }

    private static void AddOwnerMembership(ApplicationDbContext context, Observatory observatory)
    {
        if (!context.Users.Local.Any(user => user.Id == observatory.OwnerUserId))
        {
            context.Users.Add(new ApplicationUser
            {
                Id = observatory.OwnerUserId,
                UserName = observatory.OwnerUserId,
                AccountType = AccountType.User
            });
        }
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = observatory.OwnerUserId,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = Now
        });
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
