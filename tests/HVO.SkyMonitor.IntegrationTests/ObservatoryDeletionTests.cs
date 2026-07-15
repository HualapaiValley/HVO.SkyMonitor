using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ObservatoryDeletionTests
{
    [TestMethod]
    public async Task Delete_WithRegisteredDeviceAndRigHistory_DeactivatesWithoutLosingEvidence()
    {
        var fixture = AssemblyHooks.Fixture;
        const string deviceKey = "observatory-deletion-key";
        var observatory = new Observatory
        {
            OwnerUserId = $"observatory-owner-{Guid.NewGuid():N}",
            Name = "Deletion history observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = $"observatory-device-{Guid.NewGuid():N}",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            FriendlyName = "Deletion history device",
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Deletion Test",
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            IssuedAtUtc = DateTimeOffset.UnixEpoch,
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey)
        };
        var profile = new DeviceRigProfile
        {
            Registration = registration,
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = observatory.Id,
            Version = 1,
            ConfigHash = new string('A', 64),
            ConfigJson = "{}",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch
        };
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Observatories.Add(observatory);
            db.DeviceRegistrations.Add(registration);
            db.DeviceRigProfiles.Add(profile);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = new ObservatoryService(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                TimeProvider.System);

            (await service.DeleteAsync(observatory.Id, observatory.OwnerUserId).ConfigureAwait(false)).Should().BeTrue();
        }

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.Observatories.SingleAsync(item => item.Id == observatory.Id).ConfigureAwait(false))
            .IsActive.Should().BeFalse();
        var revokedRegistration = await assertionDb.DeviceRegistrations.SingleAsync(item => item.Id == registration.Id).ConfigureAwait(false);
        revokedRegistration.Status.Should().Be(DeviceRegistrationStatus.Revoked);
        revokedRegistration.DeviceKeyHash.Should().BeNull();
        revokedRegistration.RegistrationTokenHash.Should().BeNull();
        revokedRegistration.ExpiresAtUtc.Should().NotBeNull();
        (await assertionDb.DeviceRigProfiles.AnyAsync(item => item.Id == profile.Id).ConfigureAwait(false)).Should().BeTrue();

        var heartbeat = new DeviceHeartbeatService(
            new DeviceCredentialValidator(assertionDb, TimeProvider.System),
            assertionDb,
            TimeProvider.System,
            NullLogger<DeviceHeartbeatService>.Instance);
        Func<Task> heartbeatAttempt = async () => await heartbeat.RecordHeartbeatAsync(new DeviceHeartbeatRequest(
            registration.DeviceId, deviceKey, null, null, null, null)).ConfigureAwait(false);
        await heartbeatAttempt.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false);

        var rigProfiles = new DeviceRigProfileService(
            new DeviceCredentialValidator(assertionDb, TimeProvider.System),
            assertionDb,
            TimeProvider.System,
            NullLogger<DeviceRigProfileService>.Instance);
        Func<Task> profileAttempt = async () => await rigProfiles.UpsertAsync(new DeviceRigProfileUpsertRequest(
            registration.DeviceId, deviceKey, "{}", null)).ConfigureAwait(false);
        await profileAttempt.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false);

        var ingest = assertionScope.ServiceProvider.GetRequiredService<IArtifactIngestService>();
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            registration.DeviceId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)),
            DateTimeOffset.UnixEpoch,
            "raw-v1",
            "frames/raw.bin");
        Func<Task> ingestAttempt = async () => await ingest.IngestAsync(
            ArtifactManifestDocument.FromLegacy(manifest),
            new MemoryStream(payload),
            CancellationToken.None).ConfigureAwait(false);
        await ingestAttempt.Should().ThrowAsync<DeviceRegistrationException>().ConfigureAwait(false);

        var raceObservatory = new Observatory
        {
            OwnerUserId = $"observatory-race-owner-{Guid.NewGuid():N}",
            Name = "Registration race observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        var lockInterceptor = new ObservatoryLockInterceptor();
        using var raceFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(lockInterceptor);
            });
        }));
        await using (var setupScope = raceFactory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            setupDb.Observatories.Add(raceObservatory);
            await setupDb.SaveChangesAsync().ConfigureAwait(false);
        }
        lockInterceptor.Arm(raceObservatory.Id);
        var registrationTask = CreatePendingRegistrationAsync();
        await lockInterceptor.WaitUntilLockedAsync().ConfigureAwait(false);
        var deletionTask = DeleteRaceObservatoryAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        deletionTask.IsCompleted.Should().BeFalse("deletion must wait for the registration transaction's observatory lock");
        lockInterceptor.Release();
        await registrationTask.ConfigureAwait(false);
        (await deletionTask.ConfigureAwait(false)).Should().BeTrue();

        await using var raceAssertionScope = raceFactory.Services.CreateAsyncScope();
        var raceDb = raceAssertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await raceDb.Observatories.SingleAsync(item => item.Id == raceObservatory.Id).ConfigureAwait(false))
            .IsActive.Should().BeFalse();
        var raceRegistrations = await raceDb.DeviceRegistrations
            .Where(item => item.ObservatoryId == raceObservatory.Id)
            .ToListAsync().ConfigureAwait(false);
        raceRegistrations.Should().ContainSingle();
        raceRegistrations.Should().OnlyContain(item => item.Status == DeviceRegistrationStatus.Revoked
            && item.DeviceKeyHash == null
            && item.RegistrationTokenHash == null);

        async Task CreatePendingRegistrationAsync()
        {
            await using var scope = raceFactory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDeviceRegistrationService>();
            await service.CreatePendingAsync(new DeviceRegistrationCreateRequest(
                $"race-device-{Guid.NewGuid():N}",
                "ABCDE",
                raceObservatory.Id,
                "Race device",
                raceObservatory.OwnerUserId,
                "Race Owner",
                null,
                "SelfAttested",
                null)).ConfigureAwait(false);
        }

        async Task<bool> DeleteRaceObservatoryAsync()
        {
            await using var scope = raceFactory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IObservatoryService>();
            return await service.DeleteAsync(raceObservatory.Id, raceObservatory.OwnerUserId).ConfigureAwait(false);
        }
    }

    private sealed class ObservatoryLockInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid observatoryId;
        private int armed;

        public void Arm(Guid targetObservatoryId)
        {
            observatoryId = targetObservatoryId;
            Volatile.Write(ref armed, 1);
        }

        public Task WaitUntilLockedAsync() => locked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => released.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref armed, 0, 1) == 1
                && command.CommandText.Contains("[Observatories] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter =>
                    parameter.Value is Guid value && value == observatoryId))
            {
                locked.TrySetResult();
                await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }
}
