using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DeviceEnvironmentalObservationApiTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);
    private HttpClient? _client;

    [TestInitialize]
    public void Initialize() => _client = AssemblyHooks.Fixture.Factory.CreateClient();

    [TestCleanup]
    public void Cleanup() => _client?.Dispose();

    [TestMethod]
    public async Task AcceptedThenDuplicateReturnsOriginalReceiptAndOneDurableObservation()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        var observation = CreateObservation(device.ObservatoryId, device.DevicePublicId, Guid.NewGuid());

        using var accepted = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        using var duplicate = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstAcknowledgement = await ReadAcknowledgementAsync(accepted).ConfigureAwait(false);
        var duplicateAcknowledgement = await ReadAcknowledgementAsync(duplicate).ConfigureAwait(false);
        firstAcknowledgement.Disposition.Should().Be(EnvironmentalObservationDeliveryDisposition.Accepted);
        duplicateAcknowledgement.Disposition.Should().Be(EnvironmentalObservationDeliveryDisposition.Duplicate);
        duplicateAcknowledgement.ReceivedAtUtc.Should().Be(firstAcknowledgement.ReceivedAtUtc);
        duplicateAcknowledgement.ContentSha256.Should().Be(firstAcknowledgement.ContentSha256);
        duplicateAcknowledgement.SourceIdentitySha256.Should().Be(
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation));
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.EnvironmentalObservations.CountAsync(item => item.ObservationId == observation.ObservationId)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task Version2CameraSensorTemperatureRequiresRigAndPersistsWithoutDowngrade()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        await using (var bindingScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var bindingDb = bindingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            bindingDb.CentralFrames.Add(new CentralFrame
            {
                RegistrationId = device.RegistrationId,
                DevicePublicId = device.DevicePublicId,
                ObservatoryId = device.ObservatoryId,
                AgentId = device.DevicePublicId.ToString("D", CultureInfo.InvariantCulture),
                RigId = "rig-1",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = Epoch,
                FirstReceivedAtUtc = Epoch
            });
            await bindingDb.SaveChangesAsync().ConfigureAwait(false);
        }
        var observationId = Guid.NewGuid();
        var template = CreateObservation(device.ObservatoryId, device.DevicePublicId, observationId);
        var observation = template with
        {
            SchemaVersion = EnvironmentalObservationSchemaVersions.V2,
            Target = template.Target with { RigId = "rig-1" },
            Source = template.Source with { SourceId = "camera-sensor" },
            Value = new EnvironmentalObservationValue(
                EnvironmentalObservationKind.CameraSensorTemperature,
                EnvironmentalObservationUnit.DegreesCelsius,
                -12.5,
                null,
                EnvironmentalObservationQuality.Good,
                0.2)
        };

        using var accepted = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);
        using var duplicate = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);
        using var missingRig = await PostAsync(
            device.DeviceId,
            device.DeviceKey,
            observation with { ObservationId = Guid.NewGuid(), Target = observation.Target with { RigId = null } })
            .ConfigureAwait(false);
        using var wrongRig = await PostAsync(
            device.DeviceId,
            device.DeviceKey,
            observation with { ObservationId = Guid.NewGuid(), Target = observation.Target with { RigId = "rig-2" } })
            .ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAcknowledgementAsync(duplicate).ConfigureAwait(false)).Disposition.Should()
            .Be(EnvironmentalObservationDeliveryDisposition.Duplicate);
        missingRig.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongRig.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.EnvironmentalObservations.SingleAsync(item => item.ObservationId == observationId)
            .ConfigureAwait(false);
        persisted.SchemaVersion.Should().Be(EnvironmentalObservationSchemaVersions.V2);
        persisted.Kind.Should().Be(EnvironmentalObservationKind.CameraSensorTemperature);
        persisted.Unit.Should().Be(EnvironmentalObservationUnit.DegreesCelsius);
        persisted.RigId.Should().Be("rig-1");
        persisted.NumericValue.Should().Be(-12.5);
    }

    [TestMethod]
    public async Task AuthenticationTargetSpoofAndConflictingContentAreExplicit()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        var observation = CreateObservation(device.ObservatoryId, device.DevicePublicId, Guid.NewGuid());

        using var unauthorized = await PostAsync(device.DeviceId, "wrong-key", observation).ConfigureAwait(false);
        using var spoofed = await PostAsync(
            device.DeviceId,
            device.DeviceKey,
            observation with { Target = observation.Target with { AgentId = Guid.NewGuid() } }).ConfigureAwait(false);
        using var accepted = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);
        using var conflict = await PostAsync(
            device.DeviceId,
            device.DeviceKey,
            observation with { Value = observation.Value with { NumericValue = 55 } }).ConfigureAwait(false);

        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        spoofed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadReasonCodeAsync(spoofed).ConfigureAwait(false)).Should().Be("environment.target-agent-binding");
        (await ReadReasonCodeAsync(conflict).ConfigureAwait(false)).Should().Be("environment.content-conflict");
    }

    [TestMethod]
    public async Task StrictOuterAndObservationParsingRejectsUnknownDuplicateNumericAndOversizedJson()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        var observation = CreateObservation(device.ObservatoryId, device.DevicePublicId, Guid.NewGuid());
        var envelope = new EnvironmentalObservationDeliveryEnvelope(
            EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
            device.DeviceId,
            device.DeviceKey,
            observation);
        var json = Encoding.UTF8.GetString(EnvironmentalObservationDeliveryJson.Serialize(envelope));
        var invalidPayloads = new[]
        {
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":true,")),
            Encoding.UTF8.GetBytes(json.Insert(1, "\"deviceId\":\"duplicate\",")),
            Encoding.UTF8.GetBytes(json.Replace("\"Good\"", "1", StringComparison.Ordinal))
        };

        foreach (var payload in invalidPayloads)
        {
            using var response = await PostBytesAsync(payload).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using var oversized = await PostBytesAsync(
            Encoding.UTF8.GetBytes(json + new string(' ', EnvironmentalObservationDeliveryJson.MaximumEnvelopeBytes)))
            .ConfigureAwait(false);
        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [TestMethod]
    public async Task HistoricalSiteBindingRemainsValidAfterCurrentRegistrationMoves()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == device.RegistrationId)
                .ConfigureAwait(false);
            db.CentralFrames.Add(new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = device.DevicePublicId,
                ObservatoryId = device.ObservatoryId,
                AgentId = device.DevicePublicId.ToString("D", CultureInfo.InvariantCulture),
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = Epoch,
                FirstReceivedAtUtc = Epoch
            });
            var movedSite = new Observatory
            {
                OwnerUserId = "environment-api-tests",
                Name = "Moved site",
                TimeZoneId = "UTC",
                CreatedAtUtc = Epoch
            };
            db.Observatories.Add(movedSite);
            registration.Observatory = movedSite;
            registration.ObservatoryId = movedSite.Id;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var observation = CreateObservation(device.ObservatoryId, device.DevicePublicId, Guid.NewGuid());

        using var response = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [TestMethod]
    public async Task ReprovisionedDeviceCredentialsCanDrainHistoricallyBoundAgent()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        var historicalAgentId = Guid.NewGuid();
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var historicalRegistration = new DeviceRegistration
            {
                DeviceId = device.DeviceId,
                ObservatoryId = device.ObservatoryId,
                FriendlyName = "Historical environmental device",
                ObservatoryName = "Environmental API site",
                OwnerUserId = "environment-api-tests",
                OwnerDisplayName = "Integration Tests",
                Status = DeviceRegistrationStatus.Revoked,
                VerificationCodeHash = new string('B', 64),
                DevicePublicId = historicalAgentId,
                IssuedAtUtc = Epoch.AddDays(-2),
                ActivatedAtUtc = Epoch.AddDays(-2),
                RevokedReason = "reprovisioned"
            };
            db.DeviceRegistrations.Add(historicalRegistration);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var observation = CreateObservation(device.ObservatoryId, historicalAgentId, Guid.NewGuid());

        using var response = await PostAsync(device.DeviceId, device.DeviceKey, observation).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [TestMethod]
    public async Task ExpiredCredentialAndStaleCrossSiteTargetAreRejected()
    {
        var device = await SeedDeviceAsync().ConfigureAwait(false);
        var staleSiteId = Guid.NewGuid();
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Observatories.Add(new Observatory
            {
                Id = staleSiteId,
                OwnerUserId = "environment-api-tests",
                Name = "Unbound site",
                TimeZoneId = "UTC",
                CreatedAtUtc = Epoch
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var stale = CreateObservation(staleSiteId, device.DevicePublicId, Guid.NewGuid());
        using var staleResponse = await PostAsync(device.DeviceId, device.DeviceKey, stale).ConfigureAwait(false);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadReasonCodeAsync(staleResponse).ConfigureAwait(false)).Should().Be("environment.target-agent-binding");

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == device.RegistrationId)
                .ConfigureAwait(false);
            registration.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var current = CreateObservation(device.ObservatoryId, device.DevicePublicId, Guid.NewGuid());
        using var expiredResponse = await PostAsync(device.DeviceId, device.DeviceKey, current).ConfigureAwait(false);
        expiredResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> PostAsync(
        string deviceId,
        string deviceKey,
        EnvironmentalObservationV1 observation)
        => PostBytesAsync(EnvironmentalObservationDeliveryJson.Serialize(new EnvironmentalObservationDeliveryEnvelope(
            EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
            deviceId,
            deviceKey,
            observation)));

    private Task<HttpResponseMessage> PostBytesAsync(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return _client!.PostAsync(new Uri("/api/device/environmental-observations", UriKind.Relative), content);
    }

    private static async Task<EnvironmentalObservationAcknowledgement> ReadAcknowledgementAsync(HttpResponseMessage response)
    {
        var parsed = EnvironmentalObservationDeliveryJson.ParseAcknowledgement(
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        parsed.Validation.IsValid.Should().BeTrue();
        return parsed.Value!;
    }

    private static async Task<string?> ReadReasonCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("reasonCode").GetString();
    }

    private static async Task<TestDevice> SeedDeviceAsync()
    {
        var device = new TestDevice(
            $"environment-device-{Guid.NewGuid():N}",
            $"key-{Guid.NewGuid():N}",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var site = new Observatory
        {
            Id = device.ObservatoryId,
            OwnerUserId = "environment-api-tests",
            Name = "Environmental API site",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        db.Observatories.Add(site);
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            Id = device.RegistrationId,
            DeviceId = device.DeviceId,
            Observatory = site,
            ObservatoryId = site.Id,
            FriendlyName = "Environmental API device",
            ObservatoryName = site.Name,
            OwnerUserId = site.OwnerUserId,
            OwnerDisplayName = "Integration Tests",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = device.DevicePublicId,
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(device.DeviceKey),
            IssuedAtUtc = Epoch.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            ActivatedAtUtc = Epoch
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return device;
    }

    private static EnvironmentalObservationV1 CreateObservation(Guid siteId, Guid agentId, Guid observationId)
    {
        using var document = JsonDocument.Parse("{}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            observationId,
            new EnvironmentalObservationTarget(siteId, agentId),
            new EnvironmentalObservationSource(
                "api-provider",
                "weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch,
            null,
            null,
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(5),
            Epoch.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good),
            []);
    }

    private sealed record TestDevice(
        string DeviceId,
        string DeviceKey,
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId);

}
