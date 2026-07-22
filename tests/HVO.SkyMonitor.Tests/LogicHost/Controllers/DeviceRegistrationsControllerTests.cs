using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    private static DeviceRegistrationsController CreateController(
        IDeviceRegistrationService registrationService,
        IDeviceRegistrationEnvelopeService envelopeService,
        string ownerUserId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerUserId),
            new Claim("name", ownerUserId == "owner-1" ? "Owner One" : "Owner Two"),
            new Claim(ClaimTypes.Email, $"{ownerUserId}@example.com")
        ], "test");
        return new DeviceRegistrationsController(registrationService, envelopeService)
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
