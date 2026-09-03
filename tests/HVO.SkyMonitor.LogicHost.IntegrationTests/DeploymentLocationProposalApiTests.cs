using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DeploymentLocationProposalApiTests
{
    [TestMethod]
    public async Task ActiveDeviceProposalEndpointAuthenticatesAndPersistsIdempotently()
    {
        var fixture = AssemblyHooks.Fixture;
        const string deviceKey = "active-location-device-key";
        string deviceId;
        DeploymentLocationSnapshot deployment;
        Guid registrationId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var observatory = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = $"Active deployment {Guid.NewGuid():N}",
                LatitudeDegrees = 35.347,
                LongitudeDegrees = -113.878,
                ElevationMeters = 520,
                TimeZoneId = "America/Phoenix",
                AllowedDeploymentRadiusMeters = 1000,
                CreatedAtUtc = now,
                IsActive = true
            };
            deviceId = $"active-location-{Guid.NewGuid():N}";
            var registration = new DeviceRegistration
            {
                DeviceId = deviceId,
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = "Active Location Camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = owner.Id,
                OwnerDisplayName = TestUsers.Operator.FullName,
                OwnerConfirmationMethod = "SelfAttested",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('A', 64),
                DevicePublicId = Guid.NewGuid(),
                DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey),
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            registrationId = registration.Id;
            db.AddRange(observatory, registration);
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                UserId = owner.Id,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = now
            });
            await SeedObservatoryLocationAsync(db, observatory, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            deployment = DeploymentLocationSnapshot.Create(
                "active-device-location", 2, "gps-receiver", 3, now.AddHours(-1), null,
                35.3471, -113.878, 521, "America/Phoenix");
        }
        using var client = fixture.Factory.CreateClient();
        var request = new DeviceDeploymentLocationController.DeviceDeploymentLocationRequest(
            deviceId, deviceKey, deployment, DeploymentLocationSourceKind.Gps);

        using var rejected = await client.PostAsJsonAsync(
            new Uri("/api/device/deployment-location", UriKind.Relative), request with { DeviceKey = "wrong-key" })
            .ConfigureAwait(false);
        using var replayClient = fixture.Factory.CreateClient();
        var firstTask = client.PostAsJsonAsync(
            new Uri("/api/device/deployment-location", UriKind.Relative), request);
        var replayTask = replayClient.PostAsJsonAsync(
            new Uri("/api/device/deployment-location", UriKind.Relative), request);
        await Task.WhenAll(firstTask, replayTask).ConfigureAwait(false);
        using var first = await firstTask.ConfigureAwait(false);
        using var replay = await replayTask.ConfigureAwait(false);

        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var acknowledgment = await first.Content.ReadFromJsonAsync<DeploymentLocationAcknowledgment>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false);
        acknowledgment!.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.DeviceDeploymentLocationVersions.CountAsync(item => item.RegistrationId == registrationId)
            .ConfigureAwait(false)).Should().Be(1);
        (await assertionDb.DeploymentLocationResolutionAudits.CountAsync(item => item.RegistrationId == registrationId)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task ActiveReplayRacingOwnerResolution_UsesOneLockOrderWithoutDeadlock()
    {
        var fixture = AssemblyHooks.Fixture;
        Guid registrationId;
        Guid proposalId;
        Guid concurrencyToken;
        string ownerId;
        DeploymentLocationSnapshot deployment;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
            ownerId = owner.Id;
            var now = DateTimeOffset.UtcNow;
            var observatory = new Observatory
            {
                OwnerUserId = ownerId,
                Name = $"Lock-order observatory {Guid.NewGuid():N}",
                LatitudeDegrees = 35.347,
                LongitudeDegrees = -113.878,
                ElevationMeters = 520,
                TimeZoneId = "America/Phoenix",
                CreatedAtUtc = now,
                IsActive = true
            };
            var registration = new DeviceRegistration
            {
                DeviceId = $"lock-order-{Guid.NewGuid():N}",
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = "Lock order camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = ownerId,
                OwnerDisplayName = TestUsers.Operator.FullName,
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('A', 64),
                DevicePublicId = Guid.NewGuid(),
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            db.AddRange(observatory, registration);
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                UserId = ownerId,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = now
            });
            await SeedObservatoryLocationAsync(db, observatory, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            registrationId = registration.Id;
            deployment = DeploymentLocationSnapshot.Create(
                "lock-order-location", 1, "operator-survey", 2, now.AddMinutes(-1), null,
                observatory.LatitudeDegrees, observatory.LongitudeDegrees,
                observatory.ElevationMeters, observatory.TimeZoneId);
            var authority = setupScope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            _ = await authority.ProposeAsync(
                registration, deployment, DeploymentLocationSourceKind.Manual, "integration-test")
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var proposal = await db.DeviceDeploymentLocationVersions.SingleAsync(item =>
                item.RegistrationId == registrationId).ConfigureAwait(false);
            proposalId = proposal.Id;
            concurrencyToken = proposal.ConcurrencyToken;
        }

        async Task<DeploymentLocationAcknowledgment> ReplayAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
                .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            return await scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>()
                .ProposeAsync(
                    registration, deployment, DeploymentLocationSourceKind.Manual,
                    "active-device-reconciliation")
                .ConfigureAwait(false);
        }

        async Task<DeploymentLocationResolutionResult> ResolveAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>()
                .ResolveAsync(
                    proposalId, ownerId, DeploymentLocationResolutionStatus.Acknowledged,
                    "owner-approved", concurrencyToken)
                .ConfigureAwait(false);
        }

        var replayTask = ReplayAsync();
        var resolutionTask = ResolveAsync();
        await Task.WhenAll(replayTask, resolutionTask).ConfigureAwait(false);
        var replay = await replayTask.ConfigureAwait(false);
        var resolution = await resolutionTask.ConfigureAwait(false);

        replay.Status.Should().BeOneOf(
            DeploymentLocationResolutionStatus.Pending,
            DeploymentLocationResolutionStatus.Acknowledged);
        resolution.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
    }

    [TestMethod]
    public async Task OwnerResolutionApi_ListsHidesRequiresEtagAndAuditsMutation()
    {
        var fixture = AssemblyHooks.Fixture;
        Guid proposalId;
        Guid proposalRegistrationId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var observatory = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = $"Deployment API {Guid.NewGuid():N}",
                LatitudeDegrees = 35.347,
                LongitudeDegrees = -113.878,
                ElevationMeters = 520,
                TimeZoneId = "America/Phoenix",
                CreatedAtUtc = now,
                IsActive = true
            };
            var registration = new DeviceRegistration
            {
                DeviceId = $"deployment-api-{Guid.NewGuid():N}",
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = "Deployment API Camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = owner.Id,
                OwnerDisplayName = TestUsers.Operator.FullName,
                OwnerEmail = owner.Email,
                OwnerConfirmationMethod = "SelfAttested",
                OwnerConfirmedAtUtc = now,
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
                DevicePublicId = Guid.NewGuid(),
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            db.AddRange(observatory, registration);
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                UserId = owner.Id,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = now
            });
            await SeedObservatoryLocationAsync(db, observatory, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var deployment = DeploymentLocationSnapshot.Create(
                "deployment-api-location",
                1,
                "operator-survey",
                2,
                now.AddHours(-1),
                null,
                observatory.LatitudeDegrees,
                observatory.LongitudeDegrees,
                observatory.ElevationMeters,
                observatory.TimeZoneId);
            var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            var acknowledgment = await authority.ProposeAsync(
                registration, deployment, DeploymentLocationSourceKind.Manual, "integration-test")
                .ConfigureAwait(false);
            acknowledgment.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
            await db.SaveChangesAsync().ConfigureAwait(false);
            proposalId = await db.DeviceDeploymentLocationVersions
                .Where(item => item.RegistrationId == registration.Id)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            var secondRegistration = new DeviceRegistration
            {
                DeviceId = $"deployment-api-page-{Guid.NewGuid():N}",
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = "Deployment API Paging Camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = owner.Id,
                OwnerDisplayName = TestUsers.Operator.FullName,
                OwnerEmail = owner.Email,
                OwnerConfirmationMethod = "SelfAttested",
                OwnerConfirmedAtUtc = now,
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = DeviceRegistrationService.ComputeSha256("FGHIJ"),
                DevicePublicId = Guid.NewGuid(),
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            db.DeviceRegistrations.Add(secondRegistration);
            await db.SaveChangesAsync().ConfigureAwait(false);
            proposalRegistrationId = secondRegistration.Id;
            var secondDeployment = DeploymentLocationSnapshot.Create(
                "deployment-api-page-location",
                1,
                "operator-survey",
                2,
                now,
                null,
                observatory.LatitudeDegrees,
                observatory.LongitudeDegrees,
                observatory.ElevationMeters,
                observatory.TimeZoneId);
            _ = await authority.ProposeAsync(
                secondRegistration, secondDeployment, DeploymentLocationSourceKind.Manual, "integration-test")
                .ConfigureAwait(false);
        }

        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var foreignClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var readOnlyTokenClient = fixture.Factory.CreateClient();
        var readOnlyToken = await HttpHelpers.GetPasswordTokenAsync(
            readOnlyTokenClient,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            "openid profile api.viewer").ConfigureAwait(false);
        using var readOnlyOwnerClient = HttpHelpers.WithBearerToken(readOnlyTokenClient, readOnlyToken.AccessToken);
        using var writeOnlyTokenClient = fixture.Factory.CreateClient();
        var writeOnlyToken = await HttpHelpers.GetPasswordTokenAsync(
            writeOnlyTokenClient,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            "openid profile api.owner.write").ConfigureAwait(false);
        using var writeOnlyOwnerClient = HttpHelpers.WithBearerToken(writeOnlyTokenClient, writeOnlyToken.AccessToken);
        var path = $"/api/internal/deployment-location-proposals/{proposalId:D}";

        using var listResponse = await ownerClient.GetAsync(
            new Uri("/api/internal/deployment-location-proposals?status=Pending&take=25", UriKind.Relative))
            .ConfigureAwait(false);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<
            DeploymentLocationProposalsController.DeploymentLocationProposalResponse[]>(HttpHelpers.DefaultJsonOptions)
            .ConfigureAwait(false);
        list.Should().ContainSingle(item => item.Id == proposalId);

        using var firstPageResponse = await ownerClient.GetAsync(
            new Uri("/api/internal/deployment-location-proposals?status=Pending&take=1", UriKind.Relative))
            .ConfigureAwait(false);
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstPageResponse.Headers.TryGetValues("X-Next-Cursor", out var cursorValues).Should().BeTrue();
        var cursor = cursorValues!.Single();
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<
            DeploymentLocationProposalsController.DeploymentLocationProposalResponse[]>(HttpHelpers.DefaultJsonOptions)
            .ConfigureAwait(false);
        using var secondPageResponse = await ownerClient.GetAsync(new Uri(
            $"/api/internal/deployment-location-proposals?status=Pending&take=1&cursor={Uri.EscapeDataString(cursor)}",
            UriKind.Relative)).ConfigureAwait(false);
        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<
            DeploymentLocationProposalsController.DeploymentLocationProposalResponse[]>(HttpHelpers.DefaultJsonOptions)
            .ConfigureAwait(false);
        secondPage.Should().ContainSingle();
        secondPage![0].Id.Should().NotBe(firstPage![0].Id);

        using var foreignResponse = await foreignClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var getResponse = await ownerClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.ETag.Should().NotBeNull();
        var originalEtag = getResponse.Headers.ETag!.Tag;
        originalEtag.Should().NotBeNullOrWhiteSpace();

        var resolution = new DeploymentLocationProposalsController.DeploymentLocationResolutionRequest(
            DeploymentLocationResolutionStatus.Acknowledged,
            "owner-approved");
        using var missingPrecondition = await ownerClient.PostAsJsonAsync($"{path}/resolution", resolution)
            .ConfigureAwait(false);
        missingPrecondition.StatusCode.Should().Be((HttpStatusCode)428);
        using var readOnlyRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/resolution")
        {
            Content = JsonContent.Create(resolution)
        };
        readOnlyRequest.Headers.TryAddWithoutValidation("If-Match", originalEtag);
        using var readOnlyMutation = await readOnlyOwnerClient.SendAsync(readOnlyRequest).ConfigureAwait(false);
        readOnlyMutation.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var resolveRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/resolution")
        {
            Content = JsonContent.Create(resolution)
        };
        resolveRequest.Headers.TryAddWithoutValidation("If-Match", originalEtag);
        using var resolved = await writeOnlyOwnerClient.SendAsync(resolveRequest).ConfigureAwait(false);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
        resolved.Headers.ETag.Should().NotBeNull();
        resolved.Headers.ETag!.Tag.Should().NotBe(originalEtag);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/resolution")
        {
            Content = JsonContent.Create(resolution)
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", originalEtag);
        using var stale = await ownerClient.SendAsync(staleRequest).ConfigureAwait(false);
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var proposal = await assertionDb.DeviceDeploymentLocationVersions.SingleAsync(item => item.Id == proposalId)
            .ConfigureAwait(false);
        proposal.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        proposal.ReasonCode.Should().Be("owner-approved");
        (await assertionDb.DeploymentLocationResolutionAudits.CountAsync(item =>
            item.DeviceDeploymentLocationVersionId == proposalId).ConfigureAwait(false)).Should().Be(2);
        var remaining = await assertionDb.DeviceDeploymentLocationVersions
            .Include(item => item.Registration)
            .SingleAsync(item => item.RegistrationId == proposalRegistrationId
                && item.Status == DeploymentLocationResolutionStatus.Pending).ConfigureAwait(false);
        var cleanupAuthority = assertionScope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
        var cleanup = await cleanupAuthority.ResolveAsync(
            remaining.Id,
            remaining.Registration!.OwnerUserId,
            DeploymentLocationResolutionStatus.Acknowledged,
            "integration-test-cleanup",
            remaining.ConcurrencyToken).ConfigureAwait(false);
        cleanup.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
    }

    private static async Task SeedObservatoryLocationAsync(
        ApplicationDbContext db,
        Observatory observatory,
        DateTimeOffset effectiveFromUtc)
    {
        _ = await ObservatoryLocationAuthority.ApplyAsync(
            db,
            observatory,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters,
            effectiveFromUtc,
            "integration-test",
            CancellationToken.None).ConfigureAwait(false);
    }
}
