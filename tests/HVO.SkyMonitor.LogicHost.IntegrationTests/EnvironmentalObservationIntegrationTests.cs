using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EnvironmentalObservationIntegrationTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse(
        "2026-01-15T06:00:00Z",
        CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task ConcurrentDuplicateIngestConvergesAndConflictingIdentityIsRejected()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var observation = CreateObservation(target.SiteId, target.AgentId, Guid.NewGuid(), Epoch);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>()
                .IngestAsync(observation)
                .ConfigureAwait(false);
        })).ConfigureAwait(false);

        results.Count(result => result.Disposition == EnvironmentalObservationIngestDisposition.Accepted).Should().Be(1);
        results.Count(result => result.Disposition == EnvironmentalObservationIngestDisposition.Duplicate).Should().Be(7);
        results.Select(result => result.RecordId).Distinct().Should().ContainSingle();
        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
        var source = await db.EnvironmentalObservationSources
            .SingleAsync(item => item.IdentitySha256 == sourceIdentity)
            .ConfigureAwait(false);
        (await db.EnvironmentalObservations.CountAsync(item => item.SourceRecordId == source.Id).ConfigureAwait(false))
            .Should().Be(1);
        Func<Task> conflict = () => verificationScope.ServiceProvider
            .GetRequiredService<IEnvironmentalObservationIngestService>()
            .IngestAsync(observation with { Value = observation.Value with { NumericValue = 99 } });
        await conflict.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FrameCorrelationUsesHistoricalSiteRigAndExposureInterval()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var first = CreateObservation(
            target.SiteId,
            target.AgentId,
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Epoch.AddMinutes(-2)) with
        {
            ValidFromUtc = Epoch.AddMinutes(-5),
            ValidThroughUtc = Epoch.AddMinutes(5),
            StaleAfterUtc = Epoch.AddMinutes(4)
        };
        var second = first with
        {
            ObservationId = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            ObservedAtUtc = Epoch.AddMinutes(-1),
            Value = first.Value with { NumericValue = 55 }
        };
        await using var ingestScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var ingest = ingestScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>();
        await ingest.IngestAsync(first).ConfigureAwait(false);
        await ingest.IngestAsync(second).ConfigureAwait(false);
        var db = ingestScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = new CentralFrame
        {
            RegistrationId = target.RegistrationId,
            DevicePublicId = target.AgentId,
            ObservatoryId = target.SiteId,
            AgentId = target.AgentId.ToString("D", CultureInfo.InvariantCulture),
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Epoch,
            FirstReceivedAtUtc = Epoch.AddSeconds(3),
            RigId = "rig-1",
            CaptureSequence = 1,
            Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = Epoch.AddSeconds(-2),
                ExposureStartedUtc = Epoch.AddSeconds(-1),
                ExposureEndedUtc = Epoch.AddSeconds(1),
                ReadoutCompletedUtc = Epoch.AddSeconds(2),
                DurableIngressUtc = Epoch.AddSeconds(3)
            }
        };
        db.CentralFrames.Add(frame);
        var newSite = new Observatory
        {
            OwnerUserId = $"environment-moved-owner-{Guid.NewGuid():N}",
            Name = "Moved environment site",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        db.Observatories.Add(newSite);
        var registration = await db.DeviceRegistrations
            .SingleAsync(item => item.Id == target.RegistrationId)
            .ConfigureAwait(false);
        registration.ObservatoryId = newSite.Id;
        registration.Observatory = newSite;
        await db.SaveChangesAsync().ConfigureAwait(false);

        var match = await ingestScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationQueryService>()
            .CorrelateFrameAsync(
                frame.Id,
                new EnvironmentalObservationSelector(
                    EnvironmentalObservationKind.RelativeHumidity,
                    [EnvironmentalObservationSourceKind.Measured],
                    [EnvironmentalObservationQuality.Good],
                    TimeSpan.FromHours(1)))
            .ConfigureAwait(false);

        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Fresh);
        match.HadOverlap.Should().BeTrue();
        match.Observation!.ObservationId.Should().Be(second.ObservationId);
        match.Observation.Target.SiteId.Should().Be(target.SiteId);
        match.Observation.Target.RigId.Should().Be("rig-1");
    }

    [TestMethod]
    public async Task RigBindingAndCorrelationUseCaseSensitiveIdentity()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var lower = CreateObservation(target.SiteId, target.AgentId, Guid.NewGuid(), Epoch);
        var upper = lower with
        {
            ObservationId = Guid.NewGuid(),
            Target = lower.Target with { RigId = "RIG-1" },
            Value = lower.Value with { NumericValue = 90 }
        };
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>();
        Func<Task> unboundUpper = () => ingest.IngestAsync(upper);

        await unboundUpper.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.CentralFrames.Add(new CentralFrame
        {
            RegistrationId = target.RegistrationId,
            DevicePublicId = target.AgentId,
            ObservatoryId = target.SiteId,
            AgentId = target.AgentId.ToString("D", CultureInfo.InvariantCulture),
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Epoch,
            FirstReceivedAtUtc = Epoch,
            RigId = "RIG-1"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        await ingest.IngestAsync(lower).ConfigureAwait(false);
        await ingest.IngestAsync(upper).ConfigureAwait(false);

        var query = scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationQueryService>();
        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            lower.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                lower.Value.Kind,
                [lower.Source.Kind],
                [lower.Value.Quality],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);
        var history = await query.QueryAsync(new EnvironmentalObservationQuery(
            target.SiteId,
            lower.Value.Kind,
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(1),
            AgentId: target.AgentId,
            RigId: "rig-1")).ConfigureAwait(false);

        match.Observation!.ObservationId.Should().Be(lower.ObservationId);
        history.Should().ContainSingle(item => item.Observation.ObservationId == lower.ObservationId);
        history.Should().NotContain(item => item.Observation.ObservationId == upper.ObservationId);
    }

    [TestMethod]
    public async Task DerivedIngestWaitsForRetentionLockBeforePinningLineage()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var source = CreateObservation(target.SiteId, target.AgentId, Guid.NewGuid(), Epoch);
        await using (var sourceScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await sourceScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>()
                .IngestAsync(source)
                .ConfigureAwait(false);
        }
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(source);
        var derived = source with
        {
            ObservationId = Guid.NewGuid(),
            Source = source.Source with
            {
                SourceId = $"derived-lock-{Guid.NewGuid():N}",
                Kind = EnvironmentalObservationSourceKind.Derived
            },
            Lineage = [new EnvironmentalObservationReference(sourceIdentity, source.ObservationId)]
        };
        await using var blocker = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await blocker.OpenAsync().ConfigureAwait(false);
        await using var transaction = await blocker.BeginTransactionAsync().ConfigureAwait(false);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = (SqlTransaction)transaction;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 0;
                SELECT @result;
                """;
            command.Parameters.Add(new SqlParameter("@resource", EnvironmentalObservationLockNames.Retention));
            var lockResult = (int)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? -999);
            lockResult.Should().BeGreaterThanOrEqualTo(0);
        }
        await using var ingestScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var ingestTask = ingestScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>()
            .IngestAsync(derived);

        var first = await Task.WhenAny(ingestTask, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
        first.Should().NotBe(ingestTask);
        await transaction.CommitAsync().ConfigureAwait(false);
        var result = await ingestTask.ConfigureAwait(false);

        result.Disposition.Should().Be(EnvironmentalObservationIngestDisposition.Accepted);
    }

    [TestMethod]
    public async Task DerivedObservationPersistsQualifiedLineageAndRejectsMissingInputs()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var sourceObservation = CreateObservation(target.SiteId, target.AgentId, Guid.NewGuid(), Epoch);
        var secondSourceObservation = sourceObservation with
        {
            ObservationId = Guid.NewGuid(),
            ObservedAtUtc = Epoch.AddSeconds(1),
            ValidFromUtc = sourceObservation.ValidFromUtc.AddSeconds(1),
            ValidThroughUtc = sourceObservation.ValidThroughUtc.AddSeconds(1),
            StaleAfterUtc = sourceObservation.StaleAfterUtc.AddSeconds(1)
        };
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>();
        await ingest.IngestAsync(sourceObservation).ConfigureAwait(false);
        await ingest.IngestAsync(secondSourceObservation).ConfigureAwait(false);
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(sourceObservation);
        var derived = sourceObservation with
        {
            ObservationId = Guid.NewGuid(),
            Source = sourceObservation.Source with
            {
                SourceId = "derived-weather",
                Kind = EnvironmentalObservationSourceKind.Derived
            },
            Lineage =
            [
                new EnvironmentalObservationReference(LowerHex(sourceIdentity), secondSourceObservation.ObservationId),
                new EnvironmentalObservationReference(sourceIdentity, sourceObservation.ObservationId)
            ]
        };

        await ingest.IngestAsync(derived).ConfigureAwait(false);
        var rejectedId = Guid.NewGuid();
        Func<Task> missing = () => ingest.IngestAsync(derived with
        {
            ObservationId = rejectedId,
            Lineage = [new EnvironmentalObservationReference(sourceIdentity, Guid.NewGuid())]
        });

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var lineage = await db.EnvironmentalObservationLineage
            .Include(item => item.SourceObservation)
                .ThenInclude(item => item!.Source)
            .Where(item => item.DerivedObservationRecordId == db.EnvironmentalObservations
                .Where(observation => observation.ObservationId == derived.ObservationId)
                .Select(observation => observation.Id)
                .Single())
            .OrderBy(item => item.Ordinal)
            .ToArrayAsync()
            .ConfigureAwait(false);
        lineage.Select(item => item.SourceObservation!.ObservationId).Should().Equal(
            secondSourceObservation.ObservationId,
            sourceObservation.ObservationId);
        lineage.Should().OnlyContain(item => item.SourceObservation!.Source!.IdentitySha256 == sourceIdentity);
        var query = scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationQueryService>();
        var reconstructed = (await query.QueryAsync(new EnvironmentalObservationQuery(
            target.SiteId,
            EnvironmentalObservationKind.RelativeHumidity,
            Epoch.AddHours(-1),
            Epoch.AddHours(1),
            AgentId: target.AgentId,
            RigId: "rig-1")).ConfigureAwait(false))
            .Single(item => item.Observation.ObservationId == derived.ObservationId);
        reconstructed.Observation.Lineage.Select(item => item.ObservationId).Should().Equal(
            secondSourceObservation.ObservationId,
            sourceObservation.ObservationId);
        reconstructed.Observation.Lineage.Should().OnlyContain(item => item.SourceIdentitySha256 == sourceIdentity);
        reconstructed.ContentSha256.Should().Be(EnvironmentalObservationJson.ComputeContentSha256(reconstructed.Observation));
        await missing.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        (await db.EnvironmentalObservations.AnyAsync(item => item.ObservationId == rejectedId).ConfigureAwait(false))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task CorrelationReselectsWhenRetentionRemovesRowBeforeMaterialization()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var observation = CreateObservation(target.SiteId, target.AgentId, Guid.NewGuid(), Epoch);
        Guid recordId;
        await using (var ingestScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var result = await ingestScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>()
                .IngestAsync(observation)
                .ConfigureAwait(false);
            recordId = result.RecordId;
        }
        var interceptor = new DeleteBeforeMaterializationInterceptor(
            AssemblyHooks.Fixture.SqlServerConnectionString,
            recordId);
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var query = new EnvironmentalObservationQueryService(
            db,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            observation.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                observation.Value.Kind,
                [observation.Source.Kind],
                [observation.Value.Quality],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);

        interceptor.Fired.Should().BeTrue();
        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Missing);
    }

    [TestMethod]
    public async Task RetentionDeletesExpiredRowsInBoundedBatchesAndPreservesCurrentValidity()
    {
        var target = await SeedTargetAsync().ConfigureAwait(false);
        var now = Epoch.AddDays(10);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = CreateSource(target.SiteId, target.AgentId, now.AddDays(-10));
            var pinned = CreateRecord(source.Id, Guid.NewGuid(), now.AddDays(-5), now.AddDays(-4));
            var derived = CreateRecord(source.Id, Guid.NewGuid(), now.AddDays(-5), now.AddDays(1));
            db.EnvironmentalObservationSources.Add(source);
            db.EnvironmentalObservations.AddRange(
                pinned,
                CreateRecord(source.Id, Guid.NewGuid(), now.AddDays(-4), now.AddDays(-3)),
                CreateRecord(source.Id, Guid.NewGuid(), now.AddDays(-3), now.AddDays(-2)),
                derived,
                CreateRecord(source.Id, Guid.NewGuid(), now.AddHours(-12), now.AddHours(-1)));
            db.EnvironmentalObservationLineage.Add(new EnvironmentalObservationLineageRecord
            {
                DerivedObservationRecordId = derived.Id,
                Ordinal = 0,
                SourceObservationRecordId = pinned.Id
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var telemetry = new EnvironmentalObservationTelemetry();
        var worker = new EnvironmentalObservationRetentionWorker(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EnvironmentalObservationOptions
            {
                ReceiptRetentionDays = 1,
                RetentionBatchSize = 2
            }),
            new FixedTimeProvider(now),
            new EnvironmentalRetentionState(),
            telemetry,
            NullLogger<EnvironmentalObservationRetentionWorker>.Instance);

        var deleted = await worker.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        deleted.Should().Be(2);
        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var remaining = await verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .EnvironmentalObservations
            .Include(item => item.ReferencedBy)
            .Where(item => item.SourceRecordId == verificationScope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>().EnvironmentalObservationSources
                .Where(source => source.SiteId == target.SiteId)
                .Select(source => source.Id)
                .First())
            .OrderBy(item => item.ReceivedAtUtc)
            .ToArrayAsync()
            .ConfigureAwait(false);
        remaining.Should().HaveCount(3);
        remaining.Should().Contain(item => item.ValidThroughUtc > now);
        remaining.Should().Contain(item => item.ReceivedAtUtc > now.AddDays(-1));
        remaining.Should().Contain(item => item.ReferencedBy.Any());
    }

    [TestMethod]
    public async Task HealthTracksRetentionOperabilityWithoutRequiringObservations()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        var state = new EnvironmentalRetentionState();
        state.Succeeded(now);
        var check = new EnvironmentalObservationHealthCheck(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            state,
            new FixedTimeProvider(now),
            Options.Create(new EnvironmentalObservationOptions()));

        var healthy = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        state.Failed(new TimeoutException());
        var degraded = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        state.Failed(new InvalidOperationException());
        var terminal = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        healthy.Status.Should().Be(HealthStatus.Healthy);
        healthy.Data.Should().ContainKeys(
            "SourceCount",
            "ObservationCount",
            "NewestReceivedAgeSeconds",
            "RetentionEligibleCount",
            "RetentionOldestAgeSeconds",
            "RetentionLastSucceededUtc");
        degraded.Status.Should().Be(HealthStatus.Degraded);
        degraded.Data.Keys.Should().NotContain(key => key.Contains("Failure", StringComparison.Ordinal));
        terminal.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static async Task<(Guid SiteId, Guid RegistrationId, Guid AgentId)> SeedTargetAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var site = new Observatory
        {
            OwnerUserId = $"environment-owner-{Guid.NewGuid():N}",
            Name = "Environment integration site",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        var agentId = Guid.NewGuid();
        var registration = new DeviceRegistration
        {
            DeviceId = $"environment-integration-{Guid.NewGuid():N}",
            Observatory = site,
            ObservatoryId = site.Id,
            FriendlyName = "Environment integration agent",
            ObservatoryName = site.Name,
            OwnerUserId = site.OwnerUserId,
            OwnerDisplayName = "Environment Owner",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = agentId,
            DeviceKeyHash = new string('B', 64),
            IssuedAtUtc = Epoch.AddDays(-1),
            ExpiresAtUtc = Epoch.AddDays(30)
        };
        db.DeviceRegistrations.Add(registration);
        db.CentralFrames.Add(new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = agentId,
            ObservatoryId = site.Id,
            AgentId = agentId.ToString("D", CultureInfo.InvariantCulture),
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Epoch.AddMinutes(-1),
            FirstReceivedAtUtc = Epoch,
            RigId = "rig-1"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (site.Id, registration.Id, agentId);
    }

    private static EnvironmentalObservationV1 CreateObservation(
        Guid siteId,
        Guid agentId,
        Guid observationId,
        DateTimeOffset observedAt)
    {
        var parameters = Json("""{"calibration":"integration-v1"}""");
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            observationId,
            new EnvironmentalObservationTarget(siteId, agentId, "rig-1"),
            new EnvironmentalObservationSource(
                "integration-provider",
                "integration-weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("integration-normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            observedAt,
            null,
            null,
            observedAt.AddMinutes(-1),
            observedAt.AddMinutes(5),
            observedAt.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private static EnvironmentalObservationSourceRecord CreateSource(Guid siteId, Guid agentId, DateTimeOffset created)
        => new()
        {
            IdentitySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            SiteId = siteId,
            AgentId = agentId,
            RigId = "rig-1",
            Provider = "retention-provider",
            SourceId = $"retention-{Guid.NewGuid():N}",
            Version = "1.0.0",
            Kind = EnvironmentalObservationSourceKind.Measured,
            MethodName = "retention-test",
            MethodVersion = "1.0.0",
            ParametersJson = "{}",
            ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(Json("{}")),
            CreatedAtUtc = created
        };

    private static EnvironmentalObservationRecord CreateRecord(
        Guid sourceId,
        Guid observationId,
        DateTimeOffset received,
        DateTimeOffset validThrough)
        => new()
        {
            SourceRecordId = sourceId,
            SiteId = Guid.Empty,
            SourceKind = EnvironmentalObservationSourceKind.Measured,
            SourceIdentitySha256 = new string('C', 64),
            ObservationId = observationId,
            SchemaVersion = EnvironmentalObservationV1.CurrentSchemaVersion,
            Kind = EnvironmentalObservationKind.RelativeHumidity,
            Unit = EnvironmentalObservationUnit.Percent,
            NumericValue = 40,
            Quality = EnvironmentalObservationQuality.Good,
            ObservedAtUtc = received,
            ValidFromUtc = received.AddMinutes(-1),
            ValidThroughUtc = validThrough,
            StaleAfterUtc = validThrough,
            ReceivedAtUtc = received,
            ClockDiagnostic = EnvironmentalClockDiagnostic.WithinTolerance,
            PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(observationId.ToByteArray()))
        };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string LowerHex(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] is >= 'A' and <= 'F'
                    ? (char)(source[index] + ('a' - 'A'))
                    : source[index];
            }
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DeleteBeforeMaterializationInterceptor(string connectionString, Guid recordId)
        : DbCommandInterceptor
    {
        private int armed = 1;
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 1 &&
                command.CommandText.Contains("EnvironmentalObservations", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid value && value == recordId) &&
                Interlocked.Exchange(ref armed, 0) == 1)
            {
                await using var deletionConnection = new SqlConnection(connectionString);
                await deletionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var deletion = deletionConnection.CreateCommand();
                deletion.CommandText = "DELETE FROM [EnvironmentalObservations] WHERE [Id] = @id";
                deletion.Parameters.Add(new SqlParameter("@id", recordId));
                await deletion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                Fired = true;
            }
            return result;
        }
    }

}
