using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Controllers;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceRegistrationsControllerTests
{
    [TestMethod]
    public async Task VerifyDeviceAsync_UsesAuthenticatedOwnerIdentity()
    {
        var registrationService = new CapturingRegistrationService();
        var controller = CreateController(registrationService, new CapturingEnvelopeService(), "owner-1");
        var observatoryId = Guid.NewGuid();

        var result = await controller.VerifyDeviceAsync(
            new DeviceRegistrationsController.DeviceRegistrationRequest(
                "camera-1",
                "VERIFY",
                observatoryId,
                "Camera One"),
            CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<OkObjectResult>();
        registrationService.Request.Should().NotBeNull();
        registrationService.Request!.OwnerUserId.Should().Be("owner-1");
        registrationService.Request.OwnerDisplayName.Should().Be("Owner One");
        registrationService.Request.OwnerEmail.Should().Be("owner-1@example.com");
    }

    [TestMethod]
    public async Task VerifyDeviceAsync_ForeignObservatoryDenialReturnsNotFound()
    {
        var registrationService = new CapturingRegistrationService
        {
            Exception = new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode)
        };
        var controller = CreateController(registrationService, new CapturingEnvelopeService(), "owner-1");

        var result = await controller.VerifyDeviceAsync(
            new DeviceRegistrationsController.DeviceRegistrationRequest(
                "camera-1",
                "VERIFY",
                Guid.NewGuid(),
                "Camera One"),
            CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<NotFoundResult>();
        registrationService.Request.Should().NotBeNull();
        registrationService.Request!.OwnerUserId.Should().Be("owner-1");
    }

    [TestMethod]
    public async Task CreateEnvelopeAsync_CrossOwnerDenialUsesAuthenticatedOwnerAndReturnsNotFound()
    {
        var envelopeService = new CapturingEnvelopeService
        {
            Exception = new DeviceRegistrationException(
                "Device registration not found or access denied.",
                DeviceRegistrationException.NotFoundReasonCode)
        };
        var controller = CreateController(new CapturingRegistrationService(), envelopeService, "owner-2");

        var result = await controller.CreateEnvelopeAsync(
            new DeviceRegistrationsController.DeviceRegistrationEnvelopeDtoRequest(
                Guid.NewGuid(),
                "camera-1",
                Guid.NewGuid()),
            CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<NotFoundResult>();
        envelopeService.Request.Should().NotBeNull();
        envelopeService.Request!.OwnerUserId.Should().Be("owner-2");
    }

    [TestMethod]
    public async Task GetContinuityAsync_ReturnsOwnedIdentityAndSequenceMaxima()
    {
        await using var dbContext = CreateDbContext();
        var registration = new DeviceRegistration
        {
            DeviceId = "camera-1",
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Camera One",
            ObservatoryName = "Test Observatory",
            ObservatoryTimeZoneId = "UTC",
            OwnerUserId = "owner-1",
            OwnerDisplayName = "Owner One",
            OwnerConfirmationMethod = "test",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = "HASH",
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
            CurrentRigProfileVersion = 3
        };
        dbContext.DeviceRegistrations.Add(registration);
        var firstCaptureId = Guid.NewGuid();
        var lastCaptureId = Guid.NewGuid();
        dbContext.CentralFrames.AddRange(
            new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                AgentId = registration.DeviceId,
                CaptureSequence = 8,
                FrameId = firstCaptureId
            },
            new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                AgentId = registration.DeviceId,
                CaptureSequence = 12,
                FrameId = lastCaptureId
            });
        dbContext.DeviceFleetStates.Add(new DeviceFleetState
        {
            RegistrationId = registration.Id,
            AgentInstanceId = Guid.NewGuid(),
            BootSessionId = Guid.NewGuid(),
            Sequence = 9
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(
            new CapturingRegistrationService(),
            new CapturingEnvelopeService(),
            "owner-1",
            dbContext);

        var result = await controller.GetContinuityAsync("camera-1", CancellationToken.None, 8, 12)
            .ConfigureAwait(false);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DeviceRegistrationsController.DeviceContinuityResponse>().Subject;
        response.DevicePublicId.Should().Be(registration.DevicePublicId);
        response.MaximumCaptureSequence.Should().Be(12);
        response.MaximumHeartbeatSequence.Should().Be(9);
        response.CurrentRigProfileVersion.Should().Be(3);
        response.CaptureWindow.Should().Equal(
            new DeviceRegistrationsController.DeviceCaptureContinuityResponse(8, firstCaptureId, []),
            new DeviceRegistrationsController.DeviceCaptureContinuityResponse(12, lastCaptureId, []));
    }

    [TestMethod]
    public async Task GetContinuityAsync_DoesNotRevealAnotherOwnersRegistration()
    {
        await using var dbContext = CreateDbContext();
        dbContext.DeviceRegistrations.Add(new DeviceRegistration
        {
            DeviceId = "camera-foreign",
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Foreign Camera",
            ObservatoryName = "Foreign Observatory",
            ObservatoryTimeZoneId = "UTC",
            OwnerUserId = "owner-2",
            OwnerDisplayName = "Owner Two",
            OwnerConfirmationMethod = "test",
            VerificationCodeHash = "HASH",
            IssuedAtUtc = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(
            new CapturingRegistrationService(),
            new CapturingEnvelopeService(),
            "owner-1",
            dbContext);

        var result = await controller.GetContinuityAsync("camera-foreign", CancellationToken.None)
            .ConfigureAwait(false);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [TestMethod]
    [DataRow((int)DeviceRegistrationStatus.Pending)]
    [DataRow((int)DeviceRegistrationStatus.Active)]
    public async Task GetContinuityAsync_ZeroFramesPreservesNullableMaximumAndStatus(int statusValue)
    {
        var status = (DeviceRegistrationStatus)statusValue;
        await using var dbContext = CreateDbContext();
        var registration = CreateRegistration("camera-empty", status, DateTimeOffset.UtcNow);
        dbContext.DeviceRegistrations.Add(registration);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.GetContinuityAsync("camera-empty", CancellationToken.None).ConfigureAwait(false);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DeviceRegistrationsController.DeviceContinuityResponse>().Subject;
        response.Status.Should().Be(status.ToString());
        response.MaximumCaptureSequence.Should().BeNull();
        response.CentralFrameCount.Should().Be(0);
        response.Registrations.Should().ContainSingle(item => item.IsAuthoritative && item.Status == status.ToString());
    }

    [TestMethod]
    public async Task GetContinuityAsync_ActiveRegistrationIsAuthoritativeAcrossRevokedHistory()
    {
        await using var dbContext = CreateDbContext();
        var active = CreateRegistration("camera-history", DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow.AddMinutes(-5));
        var revoked = CreateRegistration("camera-history", DeviceRegistrationStatus.Revoked, DateTimeOffset.UtcNow);
        dbContext.DeviceRegistrations.AddRange(active, revoked);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.GetContinuityAsync("camera-history", CancellationToken.None).ConfigureAwait(false);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DeviceRegistrationsController.DeviceContinuityResponse>().Subject;
        response.RegistrationId.Should().Be(active.Id);
        response.Registrations.Should().HaveCount(2);
        response.Registrations.Single(item => item.IsAuthoritative).RegistrationId.Should().Be(active.Id);
    }

    [TestMethod]
    public async Task GetContinuityAsync_MostRecentlyIssuedPendingRegistrationIsAuthoritative()
    {
        await using var dbContext = CreateDbContext();
        var older = CreateRegistration("camera-pending-history", DeviceRegistrationStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-2));
        var current = CreateRegistration("camera-pending-history", DeviceRegistrationStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));
        dbContext.DeviceRegistrations.AddRange(older, current);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.GetContinuityAsync("camera-pending-history", CancellationToken.None).ConfigureAwait(false);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DeviceRegistrationsController.DeviceContinuityResponse>().Subject;
        response.RegistrationId.Should().Be(current.Id);
        response.Registrations.Single(item => item.IsAuthoritative).RegistrationId.Should().Be(current.Id);
    }

    [TestMethod]
    public async Task GetContinuityAsync_MultipleActiveRegistrationsAreRejectedAsAmbiguous()
    {
        await using var dbContext = CreateDbContext();
        dbContext.DeviceRegistrations.AddRange(
            CreateRegistration("camera-ambiguous", DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow.AddMinutes(-1)),
            CreateRegistration("camera-ambiguous", DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.GetContinuityAsync("camera-ambiguous", CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [TestMethod]
    public async Task RecoverActiveRegistrationAsync_WithFleetEvidenceRequiresIncidentRecovery()
    {
        await using var dbContext = CreateDbContext();
        var registration = CreateRegistration("camera-recovery", DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow);
        dbContext.DeviceRegistrations.Add(registration);
        dbContext.DeviceFleetStates.Add(new DeviceFleetState
        {
            RegistrationId = registration.Id,
            AgentInstanceId = Guid.NewGuid(),
            BootSessionId = Guid.NewGuid(),
            Sequence = 1
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.RecoverActiveRegistrationAsync(
            new DeviceRegistrationsController.DeviceRegistrationRecoveryRequest(
                registration.Id, registration.DeviceId, "VERIFY", registration.ObservatoryId, registration.FriendlyName),
            CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        registration.Status.Should().Be(DeviceRegistrationStatus.Active);
    }

    [TestMethod]
    public async Task RecoverActiveRegistrationAsync_WithoutCentralEvidenceRotatesToPending()
    {
        await using var dbContext = CreateDbContext();
        var registration = CreateRegistration("camera-safe-recovery", DeviceRegistrationStatus.Active, DateTimeOffset.UtcNow);
        registration.DeviceKeyHash = "OLD-KEY";
        registration.RegistrationTokenHash = "OLD-TOKEN";
        dbContext.DeviceRegistrations.Add(registration);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var controller = CreateController(new CapturingRegistrationService(), new CapturingEnvelopeService(), "owner-1", dbContext);

        var result = await controller.RecoverActiveRegistrationAsync(
            new DeviceRegistrationsController.DeviceRegistrationRecoveryRequest(
                registration.Id, registration.DeviceId, "VERIFY-NEW", registration.ObservatoryId, "Recovered Camera"),
            CancellationToken.None).ConfigureAwait(false);

        result.Result.Should().BeOfType<OkObjectResult>();
        registration.Status.Should().Be(DeviceRegistrationStatus.Pending);
        registration.DevicePublicId.Should().BeNull();
        registration.DeviceKeyHash.Should().BeNull();
        registration.RegistrationTokenHash.Should().BeNull();
        registration.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    private static DeviceRegistration CreateRegistration(
        string deviceId,
        DeviceRegistrationStatus status,
        DateTimeOffset issuedAt)
        => new()
        {
            DeviceId = deviceId,
            DevicePublicId = status == DeviceRegistrationStatus.Pending ? null : Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            FriendlyName = "Camera",
            ObservatoryName = "Test Observatory",
            ObservatoryTimeZoneId = "UTC",
            OwnerUserId = "owner-1",
            OwnerDisplayName = "Owner One",
            OwnerConfirmationMethod = "test",
            Status = status,
            VerificationCodeHash = "HASH",
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = status == DeviceRegistrationStatus.Pending ? issuedAt.AddMinutes(15) : null,
            ActivatedAtUtc = status == DeviceRegistrationStatus.Active ? issuedAt : null
        };

    private static DeviceRegistrationsController CreateController(
        IDeviceRegistrationService registrationService,
        IDeviceRegistrationEnvelopeService envelopeService,
        string ownerUserId,
        ApplicationDbContext? dbContext = null)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerUserId),
            new Claim("account_type", "User"),
            new Claim("name", ownerUserId == "owner-1" ? "Owner One" : "Owner Two"),
            new Claim(ClaimTypes.Email, $"{ownerUserId}@example.com")
        ], IdentityConstants.ApplicationScheme);
        return new DeviceRegistrationsController(registrationService, envelopeService, dbContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity)
                }
            }
        };
    }

    private static ApplicationDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class CapturingRegistrationService : IDeviceRegistrationService
    {
        public DeviceRegistrationCreateRequest? Request { get; private set; }

        public DeviceRegistrationException? Exception { get; init; }

        public Task<DeviceRegistration> CreatePendingAsync(
            DeviceRegistrationCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(new DeviceRegistration
            {
                DeviceId = request.DeviceId,
                ObservatoryId = request.ObservatoryId,
                FriendlyName = request.FriendlyName,
                ObservatoryName = "Test Observatory",
                ObservatoryTimeZoneId = "UTC",
                OwnerUserId = request.OwnerUserId,
                OwnerDisplayName = request.OwnerDisplayName,
                OwnerEmail = request.OwnerEmail,
                OwnerConfirmationMethod = request.OwnerConfirmationMethod,
                Status = DeviceRegistrationStatus.Pending,
                VerificationCodeHash = "HASH",
                IssuedAtUtc = DateTimeOffset.UtcNow
            });
        }

        public Task<DeviceRegistration> RevokeAsync(
            DeviceRegistrationRevokeRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingEnvelopeService : IDeviceRegistrationEnvelopeService
    {
        public DeviceRegistrationEnvelopeRequest? Request { get; private set; }

        public DeviceRegistrationException? Exception { get; init; }

        public Task<DeviceRegistrationEnvelopeResponse> CreateEnvelopeAsync(
            DeviceRegistrationEnvelopeRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Exception is not null)
            {
                throw Exception;
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DeviceRegistrationEnvelopeResponse(
                request.RegistrationId,
                Guid.NewGuid(),
                now,
                now.AddMinutes(10),
                "envelope",
                "v1"));
        }
    }
}
