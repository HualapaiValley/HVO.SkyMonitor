using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceRigProfileServiceTests
{
    private static readonly JsonSerializerOptions RigSerializerOptions = CreateRigSerializerOptions();
    private static readonly JsonSerializerOptions IndentedRigSerializerOptions =
        new(RigSerializerOptions) { WriteIndented = true };

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
        var rig = CreateRig();

        var result = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            JsonSerializer.Serialize(rig, RigSerializerOptions),
            SoftwareVersion: "1.0.0"), CancellationToken.None).ConfigureAwait(false);

        result.RigProfileVersion.Should().Be(1);
        result.CreatedNewVersion.Should().BeTrue();

        registration.CurrentRigProfileVersion.Should().Be(1);
        registration.CurrentRigProfileHash.Should().Be(result.RigProfileHash);
        registration.CurrentRigProfileUpdatedAtUtc.Should().Be(now);
        registration.LastSeenUtc.Should().Be(now);

        var profile = await context.Set<DeviceRigProfile>().SingleAsync().ConfigureAwait(false);
        profile.Version.Should().Be(1);
        profile.ConfigHash.Should().Be(result.RigProfileHash);
        profile.ProfileName.Should().Be("rig");
        profile.ProfileVersion.Should().Be(rig.ProfileVersion);
        profile.ProfileSha256.Should().Be(CameraRigProfileIdentity.ComputeSha256(rig));
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
        var rig = CreateRig();
        var compact = JsonSerializer.Serialize(rig, RigSerializerOptions);

        var first = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            compact,
            SoftwareVersion: null), CancellationToken.None).ConfigureAwait(false);

        var second = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            JsonSerializer.Serialize(rig, IndentedRigSerializerOptions),
            SoftwareVersion: null), CancellationToken.None).ConfigureAwait(false);

        second.RigProfileVersion.Should().Be(1);
        second.RigProfileHash.Should().Be(first.RigProfileHash);
        second.CreatedNewVersion.Should().BeFalse();

        var count = await context.Set<DeviceRigProfile>().CountAsync().ConfigureAwait(false);
        count.Should().Be(1);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"sensor\":{},\"optics\":{},\"orientation\":{},\"pipeline\":{},\"profileVersion\":\"x\"}")]
    public async Task UpsertAsync_WithIncompleteTypedRig_FailsWithoutPersistingState(string rigConfigJson)
    {
        await using var context = CreateContext();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = new DeviceRigProfileService(
            new DeviceCredentialValidator(context, timeProvider), context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);

        Func<Task> act = () => service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, "device-key", rigConfigJson), CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>().ConfigureAwait(false);
        (await context.DeviceRigProfiles.CountAsync().ConfigureAwait(false)).Should().Be(0);
        registration.CurrentRigProfileVersion.Should().BeNull();
        registration.CurrentRigProfileHash.Should().BeNull();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UpsertAsync_WithInvalidTypedRig_FailsWithoutPersistingState(bool nullSensorName)
    {
        await using var context = CreateContext();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = new DeviceRigProfileService(
            new DeviceCredentialValidator(context, timeProvider), context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);
        var valid = CreateRig();
        var invalid = nullSensorName
            ? valid with { Sensor = valid.Sensor with { Name = null! } }
            : valid with { Pipeline = valid.Pipeline with { CaptureInterval = TimeSpan.Zero } };

        Func<Task> act = () => service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId,
            "device-key",
            JsonSerializer.Serialize(invalid, RigSerializerOptions)), CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>().ConfigureAwait(false);
        (await context.DeviceRigProfiles.CountAsync().ConfigureAwait(false)).Should().Be(0);
        registration.CurrentRigProfileVersion.Should().BeNull();
        registration.CurrentRigProfileHash.Should().BeNull();
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

    [TestMethod]
    public async Task UpsertAsync_WithTypedRig_PersistsCaptureContractIdentity()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2025, 12, 18, 13, 10, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);
        var rig = CreateRig();
        var service = new DeviceRigProfileService(
            new DeviceCredentialValidator(context, timeProvider), context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);

        await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, "device-key", JsonSerializer.Serialize(rig, RigSerializerOptions)), CancellationToken.None).ConfigureAwait(false);

        var profile = await context.DeviceRigProfiles.SingleAsync().ConfigureAwait(false);
        profile.ProfileName.Should().Be("rig");
        profile.ProfileVersion.Should().Be(rig.ProfileVersion);
        profile.ProfileSha256.Should().Be(CameraRigProfileIdentity.ComputeSha256(rig));
    }

    [TestMethod]
    public void CameraRigProfileIdentity_UsesCaptureContractEnumSerialization()
    {
        var rig = CreateRig();
        var contractJson = JsonSerializer.SerializeToElement(rig, RigSerializerOptions);

        CameraRigProfileIdentity.ComputeSha256(rig).Should().Be(
            CaptureContractJson.ComputeCanonicalJsonSha256(contractJson));
    }

    private static CameraRigConfig CreateRig() =>
        new(
            new SensorProfile("sensor", 2, 2, 4.8, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new OpticsProfile("EquidistantFisheye", 1.5, 180, 0),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 2),
            ProfileVersion: "rig-v7");

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

    private static JsonSerializerOptions CreateRigSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
