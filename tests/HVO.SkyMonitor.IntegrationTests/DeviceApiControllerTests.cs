using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
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
    public async Task HeartbeatWithValidDeviceCredentialsReturnsAcceptedAndPersistsFleetState()
    {
        var (deviceId, deviceKey, registrationId, devicePublicId, _) = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var report = FleetStatusTestData.CreateReport(devicePublicId, observedAtUtc: DateTimeOffset.UtcNow);

        using var response = await PostHeartbeatAsync(deviceId, deviceKey, report).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var payload = await ReadAcknowledgementAsync(response).ConfigureAwait(false);
        payload.Should().NotBeNull();
        payload!.AgentInstanceId.Should().Be(devicePublicId);
        payload.Sequence.Should().Be(1);
        payload.Disposition.Should().Be(FleetHeartbeatDisposition.Advanced);
        payload.RecommendedHeartbeatSeconds.Should().BeGreaterThan(0);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(r => r.Id == registrationId).ConfigureAwait(false);
        registration.LastSeenUtc.Should().NotBeNull();
        (await db.DeviceFleetStates.SingleAsync(state => state.RegistrationId == registrationId).ConfigureAwait(false))
            .Sequence.Should().Be(1);
    }

    [TestMethod]
    public async Task HeartbeatDuplicateAndOutOfOrderReportsCannotRegressCurrent()
    {
        var (deviceId, deviceKey, registrationId, devicePublicId, _) = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var observed = DateTimeOffset.UtcNow;
        var sequence3 = FleetStatusTestData.CreateReport(devicePublicId, 3, observedAtUtc: observed);

        using var advanced = await PostHeartbeatAsync(deviceId, deviceKey, sequence3).ConfigureAwait(false);
        using var historical = await PostHeartbeatAsync(
            deviceId,
            deviceKey,
            FleetStatusTestData.CreateReport(devicePublicId, 1, observedAtUtc: observed.AddHours(1))).ConfigureAwait(false);
        using var duplicate = await PostHeartbeatAsync(deviceId, deviceKey, sequence3).ConfigureAwait(false);

        advanced.StatusCode.Should().Be(HttpStatusCode.Accepted);
        historical.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAcknowledgementAsync(duplicate).ConfigureAwait(false))!
            .Disposition.Should().Be(FleetHeartbeatDisposition.Duplicate);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.DeviceFleetStates.SingleAsync(state => state.RegistrationId == registrationId).ConfigureAwait(false))
            .Sequence.Should().Be(3);
        (await db.DeviceHeartbeatRecords.CountAsync(record => record.RegistrationId == registrationId).ConfigureAwait(false))
            .Should().Be(2);
    }

    [TestMethod]
    public async Task HeartbeatCrossAgentCredentialSpoofIsRejectedWithoutStateChange()
    {
        var first = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var second = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var report = FleetStatusTestData.CreateReport(second.DevicePublicId, observedAtUtc: DateTimeOffset.UtcNow);

        using var response = await PostHeartbeatAsync(second.DeviceId, first.DeviceKey, report).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.DeviceFleetStates.AnyAsync(state =>
            state.RegistrationId == first.RegistrationId || state.RegistrationId == second.RegistrationId).ConfigureAwait(false))
            .Should().BeFalse();
        (await db.DeviceHeartbeatRecords.AnyAsync(record =>
            record.RegistrationId == first.RegistrationId || record.RegistrationId == second.RegistrationId).ConfigureAwait(false))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task HeartbeatConcurrentAgentsPersistIndependentCurrentState()
    {
        var first = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var second = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);

        var firstRequest = PostHeartbeatAsync(
            first.DeviceId, first.DeviceKey, FleetStatusTestData.CreateReport(first.DevicePublicId, 1, observedAtUtc: DateTimeOffset.UtcNow));
        var secondRequest = PostHeartbeatAsync(
            second.DeviceId, second.DeviceKey, FleetStatusTestData.CreateReport(second.DevicePublicId, 1, observedAtUtc: DateTimeOffset.UtcNow));
        using var firstResponse = await firstRequest.ConfigureAwait(false);
        using var secondResponse = await secondRequest.ConfigureAwait(false);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var states = await db.DeviceFleetStates
            .Where(state => state.RegistrationId == first.RegistrationId || state.RegistrationId == second.RegistrationId)
            .ToArrayAsync()
            .ConfigureAwait(false);
        states.Should().HaveCount(2).And.OnlyContain(state => state.Sequence == 1);
    }

    [TestMethod]
    public async Task HeartbeatConcurrentDuplicateAndConflictConvergeToOnePayloadPerSequence()
    {
        var agent = await SeedBootstrappedActiveDeviceAsync().ConfigureAwait(false);
        var first = FleetStatusTestData.CreateReport(agent.DevicePublicId, 1, observedAtUtc: DateTimeOffset.UtcNow);
        var duplicateTasks = Enumerable.Range(0, 8)
            .Select(_ => PostHeartbeatAsync(agent.DeviceId, agent.DeviceKey, first))
            .ToArray();
        var duplicateResponses = await Task.WhenAll(duplicateTasks).ConfigureAwait(false);
        try
        {
            duplicateResponses.Count(response => response.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
            duplicateResponses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(7);
        }
        finally
        {
            foreach (var response in duplicateResponses)
            {
                response.Dispose();
            }
        }

        var sequence2 = FleetStatusTestData.CreateReport(agent.DevicePublicId, 2, observedAtUtc: DateTimeOffset.UtcNow);
        var conflictingSequence2 = sequence2 with
        {
            OverallHealth = FleetHealth.Degraded,
            HealthChecks = [new FleetHealthCheckSummary("capture", FleetHealth.Degraded, "failure")]
        };
        var conflicts = await Task.WhenAll(
            PostHeartbeatAsync(agent.DeviceId, agent.DeviceKey, sequence2),
            PostHeartbeatAsync(agent.DeviceId, agent.DeviceKey, conflictingSequence2)).ConfigureAwait(false);
        try
        {
            conflicts.Count(response => response.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
            conflicts.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        }
        finally
        {
            foreach (var response in conflicts)
            {
                response.Dispose();
            }
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.DeviceHeartbeatRecords.CountAsync(record => record.RegistrationId == agent.RegistrationId).ConfigureAwait(false))
            .Should().Be(2);
        (await db.DeviceFleetStates.SingleAsync(state => state.RegistrationId == agent.RegistrationId).ConfigureAwait(false))
            .Sequence.Should().Be(2);
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

    [TestMethod]
    public async Task BootstrapThenRevoke_InvalidatesEnvelopeAndDeviceKey()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var registrationService = services.GetRequiredService<IDeviceRegistrationService>();
        var envelopeService = services.GetRequiredService<IDeviceRegistrationEnvelopeService>();
        var bootstrapService = services.GetRequiredService<IDeviceBootstrapService>();
        var credentialValidator = services.GetRequiredService<IDeviceCredentialValidator>();

        const string ownerId = "identity-lifecycle-integration";
        var observatory = new Observatory
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            Name = "Identity Lifecycle Observatory",
            LatitudeDegrees = 0,
            LongitudeDegrees = 0,
            ElevationMeters = 0,
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        db.Users.Add(new ApplicationUser
        {
            Id = ownerId,
            UserName = ownerId,
            AccountType = AccountType.User
        });
        db.Observatories.Add(observatory);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = ownerId,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = observatory.CreatedAtUtc
        });
        await db.SaveChangesAsync().ConfigureAwait(false);

        var deviceId = $"device-{Guid.NewGuid():N}";
        var registration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            deviceId,
            "SELFATTEST",
            observatory.Id,
            "Lifecycle device",
            ownerId,
            "Integration test",
            null,
            "SelfAttested",
            null)).ConfigureAwait(false);
        var envelope = await envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            registration.Id,
            deviceId,
            observatory.Id,
            ownerId)).ConfigureAwait(false);

        var deployment = HVO.SkyMonitor.AgentCore.DeploymentLocationSnapshot.Create(
            "identity-lifecycle-inherited",
            1,
            "observatory-fallback",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        var bootstrap = await bootstrapService.BootstrapAsync(new DeviceBootstrapRequest(
            deviceId,
            envelope.Envelope,
            DeploymentLocation: deployment,
            DeploymentLocationSourceKind: HVO.SkyMonitor.AgentCore.DeploymentLocationSourceKind.Inherited))
            .ConfigureAwait(false);
        var active = await credentialValidator.ValidateAsync(
            deviceId,
            bootstrap.DeviceKey,
            CancellationToken.None).ConfigureAwait(false);
        active.Id.Should().Be(registration.Id);

        Func<Task> pairActiveAgain = () => registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            deviceId,
            "NEWCODE",
            observatory.Id,
            "Lifecycle device",
            ownerId,
            "Integration test",
            null,
            "SelfAttested",
            null));
        await pairActiveAgain.Should().ThrowAsync<DeviceRegistrationException>()
            .WithMessage("*already active*").ConfigureAwait(false);

        Func<Task> replay = () => bootstrapService.BootstrapAsync(new DeviceBootstrapRequest(
            deviceId,
            envelope.Envelope,
            DeploymentLocation: deployment,
            DeploymentLocationSourceKind: HVO.SkyMonitor.AgentCore.DeploymentLocationSourceKind.Inherited));
        await replay.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false);

        await registrationService.RevokeAsync(new DeviceRegistrationRevokeRequest(
            registration.Id,
            deviceId,
            ownerId,
            "Integration test",
            "TypedDeviceId",
            "Lifecycle test revocation")).ConfigureAwait(false);

        Func<Task> validateRevoked = () => credentialValidator.ValidateAsync(
            deviceId,
            bootstrap.DeviceKey,
            CancellationToken.None);
        await validateRevoked.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PreChangeV1Envelope_RedeemsWithoutFabricatingLocationEvidence()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var deviceKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var registrationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var observatory = new Observatory
        {
            OwnerUserId = $"legacy-owner-{Guid.NewGuid():N}",
            Name = "Legacy envelope observatory",
            LatitudeDegrees = 35.347,
            LongitudeDegrees = -113.878,
            ElevationMeters = 520,
            TimeZoneId = "America/Phoenix",
            CreatedAtUtc = now.AddDays(-1),
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = $"legacy-envelope-{Guid.NewGuid():N}",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            FriendlyName = "Legacy envelope camera",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Legacy owner",
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey),
            RegistrationTokenHash = DeviceRegistrationService.ComputeSha256(registrationToken),
            EnvelopeVersion = "v1",
            LocationEvidenceState = RegistrationLocationEvidenceState.LegacyIncomplete,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10)
        };
        db.AddRange(observatory, registration);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var legacyPayload = new DeviceRegistrationEnvelopePayload(
            registration.Id,
            registration.DeviceId,
            registration.DevicePublicId!.Value,
            observatory.Id,
            registration.FriendlyName,
            "v1",
            deviceKey,
            registrationToken,
            now,
            now.AddMinutes(10));
        var protector = services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("LogicHost", "DeviceRegistration", "Envelope", "v1");
        var protectedEnvelope = protector.Protect(
            JsonSerializer.Serialize(legacyPayload, DeviceRegistrationJson.Options));

        var clientShapedDeployment = HVO.SkyMonitor.AgentCore.DeploymentLocationSnapshot.Create(
            "new-client-local-location",
            1,
            "operator-local-configuration",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        var result = await services.GetRequiredService<IDeviceBootstrapService>().BootstrapAsync(
            new DeviceBootstrapRequest(
                registration.DeviceId,
                protectedEnvelope,
                DeploymentLocation: clientShapedDeployment,
                DeploymentLocationSourceKind: HVO.SkyMonitor.AgentCore.DeploymentLocationSourceKind.Manual))
            .ConfigureAwait(false);

        result.EnvelopeVersion.Should().Be("v1");
        var ciphertext = Convert.FromBase64String(result.Payload.Ciphertext);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(Convert.FromBase64String(result.DeviceKey), 16))
        {
            aes.Decrypt(
                Convert.FromBase64String(result.Payload.Nonce),
                ciphertext,
                Convert.FromBase64String(result.Payload.Tag),
                plaintext);
        }
        using var secrets = JsonDocument.Parse(plaintext);
        secrets.RootElement.GetProperty("deploymentLocationAcknowledgment").ValueKind
            .Should().Be(JsonValueKind.Null);
        db.ChangeTracker.Clear();
        var activated = await db.DeviceRegistrations.SingleAsync(item => item.Id == registration.Id)
            .ConfigureAwait(false);
        activated.Status.Should().Be(DeviceRegistrationStatus.Active);
        activated.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.LegacyIncomplete);
        (await db.DeviceDeploymentLocationVersions.AnyAsync(item => item.RegistrationId == registration.Id)
            .ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    public async Task OwnerScopedRegistrationServices_SeparateInventoriesAndRejectCrossOwnerEnvelope()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var observatoryService = services.GetRequiredService<IObservatoryService>();
        var registrationService = services.GetRequiredService<IDeviceRegistrationService>();
        var registrationReadService = services.GetRequiredService<IDeviceRegistrationReadService>();
        var envelopeService = services.GetRequiredService<IDeviceRegistrationEnvelopeService>();
        var marker = Guid.NewGuid().ToString("N");
        var firstOwnerId = $"owner-1-{marker}";
        var secondOwnerId = $"owner-2-{marker}";
        var firstObservatory = new Observatory
        {
            OwnerUserId = firstOwnerId,
            Name = "First Owner Observatory",
            LatitudeDegrees = 19.7,
            LongitudeDegrees = -155.1,
            ElevationMeters = 1200,
            TimeZoneId = "Pacific/Honolulu",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        var secondObservatory = new Observatory
        {
            OwnerUserId = secondOwnerId,
            Name = "Second Owner Observatory",
            LatitudeDegrees = 20.7,
            LongitudeDegrees = -156.1,
            ElevationMeters = 1300,
            TimeZoneId = "Pacific/Honolulu",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        db.Users.AddRange(
            new ApplicationUser
            {
                Id = firstOwnerId,
                UserName = firstOwnerId,
                AccountType = AccountType.User
            },
            new ApplicationUser
            {
                Id = secondOwnerId,
                UserName = secondOwnerId,
                AccountType = AccountType.User
            });
        db.Observatories.AddRange(firstObservatory, secondObservatory);
        db.ObservatoryMemberships.AddRange(
            new ObservatoryMembership
            {
                Observatory = firstObservatory,
                ObservatoryId = firstObservatory.Id,
                UserId = firstOwnerId,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = firstObservatory.CreatedAtUtc
            },
            new ObservatoryMembership
            {
                Observatory = secondObservatory,
                ObservatoryId = secondObservatory.Id,
                UserId = secondOwnerId,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = secondObservatory.CreatedAtUtc
            });
        await db.SaveChangesAsync().ConfigureAwait(false);

        var firstRegistration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            $"device-1-{marker}",
            "SELFATTEST1",
            firstObservatory.Id,
            "First owner device",
            firstOwnerId,
            "First owner",
            null,
            "SelfAttested",
            null)).ConfigureAwait(false);
        Func<Task> crossOwnerPendingTakeover = () => registrationService.CreatePendingAsync(
            new DeviceRegistrationCreateRequest(
                firstRegistration.DeviceId,
                "SELFATTEST2",
                secondObservatory.Id,
                "Attempted takeover",
                secondOwnerId,
                "Second owner",
                null,
                "SelfAttested",
                null));
        await crossOwnerPendingTakeover.Should().ThrowAsync<DeviceRegistrationException>()
            .WithMessage("*access denied*").ConfigureAwait(false);
        firstRegistration.OwnerUserId.Should().Be(firstOwnerId);
        firstRegistration.ObservatoryId.Should().Be(firstObservatory.Id);

        var secondRegistration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            $"device-2-{marker}",
            "SELFATTEST2",
            secondObservatory.Id,
            "Second owner device",
            secondOwnerId,
            "Second owner",
            null,
            "SelfAttested",
            null)).ConfigureAwait(false);

        (await observatoryService.GetObservatoriesAsync(firstOwnerId).ConfigureAwait(false))
            .Should().ContainSingle(item => item.Id == firstObservatory.Id);
        (await observatoryService.GetObservatoriesAsync(secondOwnerId).ConfigureAwait(false))
            .Should().ContainSingle(item => item.Id == secondObservatory.Id);
        (await registrationReadService.GetRegistrationsAsync(firstOwnerId).ConfigureAwait(false))
            .Should().ContainSingle(item =>
                item.RegistrationId == firstRegistration.Id
                && item.ObservatoryName == firstObservatory.Name);
        (await registrationReadService.GetRegistrationsAsync(secondOwnerId).ConfigureAwait(false))
            .Should().ContainSingle(item =>
                item.RegistrationId == secondRegistration.Id
                && item.ObservatoryName == secondObservatory.Name);

        Func<Task> crossOwner = () => envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            firstRegistration.Id,
            firstRegistration.DeviceId,
            firstObservatory.Id,
            secondOwnerId));

        await crossOwner.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*access denied*").ConfigureAwait(false);
        firstRegistration.DevicePublicId.Should().BeNull();
        firstRegistration.DeviceKeyHash.Should().BeNull();
        firstRegistration.RegistrationTokenHash.Should().BeNull();

        var envelope = await envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            firstRegistration.Id,
            firstRegistration.DeviceId,
            firstObservatory.Id,
            firstOwnerId)).ConfigureAwait(false);
        envelope.RegistrationId.Should().Be(firstRegistration.Id);
        firstRegistration.DeviceKeyHash.Should().NotBeNullOrWhiteSpace();
        secondRegistration.DeviceKeyHash.Should().BeNull();
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

    private Task<HttpResponseMessage> PostHeartbeatAsync(
        string deviceId,
        string deviceKey,
        FleetStatusReportV1 report)
    {
        var content = new ByteArrayContent(FleetContractJson.Serialize(new FleetHeartbeatEnvelope(deviceId, deviceKey, report)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return _client!.PostAsync(new Uri("/api/device/heartbeat", UriKind.Relative), content);
    }

    private static async Task<FleetHeartbeatAcknowledgement?> ReadAcknowledgementAsync(HttpResponseMessage response)
        => FleetContractJson.DeserializeAcknowledgement(
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

    private sealed record DeviceRigProfileUpsertResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId,
        int RigProfileVersion,
        string RigProfileHash,
        DateTimeOffset AcceptedAtUtc,
        bool CreatedNewVersion);
}
