using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class DeviceApiControllerTests
{
    private HttpClient? _client;

    [TestInitialize]
    public void SetUp()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TearDown()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task HeartbeatWithValidDeviceCredentialsReturnsOkAndUpdatesLastSeen()
    {
        var (deviceId, deviceKey, registrationId, devicePublicId, observatoryId) = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);

        using var response = await _client!.PostAsJsonAsync(new Uri("/api/device/heartbeat", UriKind.Relative), new
        {
            deviceId,
            deviceKey,
            softwareVersion = "0.0.1",
            agentState = "Idle",
            temperatureCelsius = 12.3,
            cpuPercent = 4.2
        }).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<DeviceHeartbeatResponse>().ConfigureAwait(false);
        payload.Should().NotBeNull();
        payload!.RegistrationId.Should().Be(registrationId);
        payload.DevicePublicId.Should().Be(devicePublicId);
        payload.ObservatoryId.Should().Be(observatoryId);
        payload.RecommendedHeartbeatSeconds.Should().BeGreaterThan(0);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(r => r.Id == registrationId).ConfigureAwait(false);
        registration.LastSeenUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RigProfileUpsertCreatesV1ThenIsIdempotentForEquivalentJson()
    {
        var (deviceId, deviceKey, registrationId, devicePublicId, _) = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);

        using var first = await _client!.PostAsJsonAsync(new Uri("/api/device/profile/rig", UriKind.Relative), new
        {
            deviceId,
            deviceKey,
            rigConfigJson = "{\"b\":2,\"a\":1}",
            softwareVersion = "0.0.1"
        }).ConfigureAwait(false);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstPayload = await first.Content.ReadFromJsonAsync<DeviceRigProfileUpsertResponse>().ConfigureAwait(false);
        firstPayload.Should().NotBeNull();
        firstPayload!.RegistrationId.Should().Be(registrationId);
        firstPayload.DevicePublicId.Should().Be(devicePublicId);
        firstPayload.RigProfileVersion.Should().Be(1);
        firstPayload.CreatedNewVersion.Should().BeTrue();

        using var second = await _client!.PostAsJsonAsync(new Uri("/api/device/profile/rig", UriKind.Relative), new
        {
            deviceId,
            deviceKey,
            rigConfigJson = "{\"a\":1,\"b\":2}",
            softwareVersion = "0.0.1"
        }).ConfigureAwait(false);

        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var secondPayload = await second.Content.ReadFromJsonAsync<DeviceRigProfileUpsertResponse>().ConfigureAwait(false);
        secondPayload.Should().NotBeNull();
        secondPayload!.RigProfileVersion.Should().Be(1);
        secondPayload.RigProfileHash.Should().Be(firstPayload.RigProfileHash);
        secondPayload.CreatedNewVersion.Should().BeFalse();

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profiles = await db.Set<DeviceRigProfile>()
            .Where(p => p.DevicePublicId == devicePublicId)
            .ToListAsync()
            .ConfigureAwait(false);

        profiles.Count.Should().Be(1);
        var registration = await db.DeviceRegistrations.SingleAsync(r => r.Id == registrationId).ConfigureAwait(false);
        registration.CurrentRigProfileVersion.Should().Be(1);
        registration.CurrentRigProfileHash.Should().Be(firstPayload.RigProfileHash);
    }

    private static async Task<(string DeviceId, string DeviceKey, Guid RegistrationId, Guid DevicePublicId, Guid ObservatoryId)>
        SeedBootstrappedActiveDeviceAsync()
    {
        var deviceId = $"device-{Guid.NewGuid():N}";
        var deviceKey = $"key-{Guid.NewGuid():N}";

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var observatoryId = Guid.NewGuid();
        var observatory = new Observatory
        {
            Id = observatoryId,
            OwnerUserId = "integration-tests",
            Name = "Integration Test Observatory",
            LatitudeDegrees = 0,
            LongitudeDegrees = 0,
            ElevationMeters = 0,
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        db.Observatories.Add(observatory);

        var registrationId = Guid.NewGuid();
        var devicePublicId = Guid.NewGuid();

        var registration = new DeviceRegistration
        {
            Id = registrationId,
            DeviceId = deviceId,
            ObservatoryId = observatoryId,
            ObservatoryName = observatory.Name,
            ObservatoryLatitudeDegrees = observatory.LatitudeDegrees,
            ObservatoryLongitudeDegrees = observatory.LongitudeDegrees,
            ObservatoryElevationMeters = observatory.ElevationMeters,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            FriendlyName = "Integration Test Device",
            OwnerUserId = "integration-tests",
            OwnerDisplayName = "Integration Tests",
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            ActivatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            DevicePublicId = devicePublicId,
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey),
            RegistrationTokenHash = DeviceRegistrationService.ComputeSha256("reg-token")
        };

        db.DeviceRegistrations.Add(registration);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (deviceId, deviceKey, registrationId, devicePublicId, observatoryId);
    }

    private sealed record DeviceHeartbeatResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId,
        string ObservatoryName,
        string FriendlyName,
        DateTimeOffset ServerTimeUtc,
        int RecommendedHeartbeatSeconds);

    private sealed record DeviceRigProfileUpsertResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId,
        int RigProfileVersion,
        string RigProfileHash,
        DateTimeOffset AcceptedAtUtc,
        bool CreatedNewVersion);
}
