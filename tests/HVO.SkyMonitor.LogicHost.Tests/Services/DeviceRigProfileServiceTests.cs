using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.LogicHost.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    [TestMethod]
    [DataRow("unsorted")]
    [DataRow("duplicate")]
    [DataRow("negative-azimuth")]
    [DataRow("azimuth-360")]
    [DataRow("low-elevation")]
    [DataRow("high-elevation")]
    [DataRow("null-sample")]
    [DataRow("null-samples")]
    [DataRow("too-many-samples")]
    [DataRow("invalid-schema")]
    [DataRow("invalid-revision")]
    [DataRow("invalid-gap")]
    [DataRow("invalid-identity")]
    [DataRow("null-identity")]
    public async Task UpsertAsync_WithInvalidHorizon_ReturnsSanitizedValidationProblemWithoutMutation(string invalidCase)
    {
        await using var context = CreateContext();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var registration = await SeedRegistrationAsync(context, bootstrapped: true).ConfigureAwait(false);
        registration.DeviceKeyHash = DeviceRegistrationService.ComputeSha256("device-key");
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = new DeviceRigProfileService(
            new DeviceCredentialValidator(context, timeProvider), context, timeProvider, NullLogger<DeviceRigProfileService>.Instance);
        var validJson = CreateHorizonRigJson();
        var accepted = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, "device-key", validJson.ToJsonString()), CancellationToken.None).ConfigureAwait(false);
        var updatedAt = registration.CurrentRigProfileUpdatedAtUtc;
        var lastSeen = registration.LastSeenUtc;
        var horizon = validJson["horizonObstruction"]!.AsObject();
        switch (invalidCase)
        {
            case "unsorted": horizon["samples"] = JsonNode.Parse("[{\"azimuthDegrees\":20,\"elevationDegrees\":0},{\"azimuthDegrees\":10,\"elevationDegrees\":0}]"); break;
            case "duplicate": horizon["samples"] = JsonNode.Parse("[{\"azimuthDegrees\":10,\"elevationDegrees\":0},{\"azimuthDegrees\":10,\"elevationDegrees\":1}]"); break;
            case "negative-azimuth": horizon["samples"]![0]!["azimuthDegrees"] = -1; break;
            case "azimuth-360": horizon["samples"]![0]!["azimuthDegrees"] = 360; break;
            case "low-elevation": horizon["samples"]![0]!["elevationDegrees"] = -91; break;
            case "high-elevation": horizon["samples"]![0]!["elevationDegrees"] = 91; break;
            case "null-sample": horizon["samples"] = new JsonArray((JsonNode?)null); break;
            case "null-samples": horizon["samples"] = null; break;
            case "too-many-samples": horizon["samples"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 721).Select(index => new HorizonObstructionSample(index / 3.0, 0)), RigSerializerOptions); break;
            case "invalid-schema": horizon["schemaVersion"] = "invalid"; break;
            case "invalid-revision": horizon["revision"] = 0; break;
            case "invalid-gap": horizon["maximumInterpolationGapDegrees"] = 91; break;
            case "invalid-identity": horizon["siteIdentitySha256"] = "invalid"; break;
            case "null-identity": horizon["geometryIdentitySha256"] = null; break;
            default: Assert.Fail("Unknown invalid horizon case."); break;
        }
        var invalidJson = validJson.ToJsonString();
        Func<Task> act = () => service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, "device-key", invalidJson), CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>()
            .WithMessage("RigConfigJson contains an invalid current camera rig configuration.").ConfigureAwait(false);

        var controller = new DeviceRigProfileController(service, NullLogger<DeviceRigProfileController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var response = await controller.UpsertAsync(new DeviceRigProfileController.DeviceRigProfileUpsertRequestDto(
            registration.DeviceId, "device-key", invalidJson, null), CancellationToken.None).ConfigureAwait(false);
        var result = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = result.Value.Should().BeOfType<ValidationProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Errors.Should().ContainSingle().Which.Key.Should().Be("RigConfigJson");
        problem.Errors["RigConfigJson"].Should().Equal("RigConfigJson must contain a valid camera rig configuration.");
        var responseJson = JsonSerializer.Serialize(problem);
        responseJson.Should().NotContain("ArgumentException").And.NotContain("JsonException")
            .And.NotContain("HorizonObstructionProfile").And.NotContain("samples")
            .And.NotContain("StackTrace").And.NotContain(".cs:").And.NotContain(invalidJson);

        (await context.DeviceRigProfiles.SingleAsync().ConfigureAwait(false)).ConfigHash.Should().Be(accepted.RigProfileHash);
        registration.CurrentRigProfileVersion.Should().Be(accepted.RigProfileVersion);
        registration.CurrentRigProfileHash.Should().Be(accepted.RigProfileHash);
        registration.CurrentRigProfileUpdatedAtUtc.Should().Be(updatedAt);
        registration.LastSeenUtc.Should().Be(lastSeen);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        var recovered = await service.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, "device-key", CreateHorizonRigJson().ToJsonString()), CancellationToken.None).ConfigureAwait(false);
        recovered.CreatedNewVersion.Should().BeFalse();
        recovered.RigProfileHash.Should().Be(accepted.RigProfileHash);
    }

    private static JsonObject CreateHorizonRigJson()
    {
        var rig = CreateRig();
        var horizon = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion,
            "test-horizon", 1, "test-installation", new string('A', 64),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Manual, 30,
            [new HorizonObstructionSample(0, 0), new HorizonObstructionSample(30, null)]);
        return JsonSerializer.SerializeToNode(rig with { HorizonObstruction = horizon }, RigSerializerOptions)!.AsObject();
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
