using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalObservationServiceTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse(
        "2026-01-15T06:00:00Z",
        CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task IngestPersistsExactDuplicateAndRejectsConflictingIdentity()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var service = CreateIngest(context, telemetry);
        var observation = CreateObservation(target.Site.Id, target.AgentId);

        var accepted = await service.IngestAsync(observation).ConfigureAwait(false);
        var duplicate = await service.IngestAsync(observation).ConfigureAwait(false);
        Func<Task> conflict = () => service.IngestAsync(observation with
        {
            Value = observation.Value with { NumericValue = 46 }
        });
        Func<Task> sourceConflict = () => service.IngestAsync(observation with
        {
            ObservationId = Guid.NewGuid(),
            Source = observation.Source with { Kind = EnvironmentalObservationSourceKind.Simulated }
        });

        accepted.Disposition.Should().Be(EnvironmentalObservationIngestDisposition.Accepted);
        duplicate.Disposition.Should().Be(EnvironmentalObservationIngestDisposition.Duplicate);
        duplicate.RecordId.Should().Be(accepted.RecordId);
        await conflict.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        await sourceConflict.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        (await context.EnvironmentalObservationSources.CountAsync().ConfigureAwait(false)).Should().Be(1);
        var record = await context.EnvironmentalObservations.SingleAsync().ConfigureAwait(false);
        record.NumericValue.Should().Be(45);
        record.ReceivedAtUtc.Should().Be(Epoch);
        record.ClockDiagnostic.Should().Be(EnvironmentalClockDiagnostic.WithinTolerance);
    }

    [TestMethod]
    public async Task IngestRejectsUnknownSiteAndCrossSiteAgentBinding()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var service = CreateIngest(context, telemetry);

        Func<Task> unknownSite = () => service.IngestAsync(CreateObservation(Guid.NewGuid(), null));
        Func<Task> wrongAgent = () => service.IngestAsync(CreateObservation(target.Site.Id, Guid.NewGuid()));

        await unknownSite.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        await wrongAgent.Should().ThrowAsync<EnvironmentalObservationConflictException>().ConfigureAwait(false);
        (await context.EnvironmentalObservations.CountAsync().ConfigureAwait(false)).Should().Be(0);
    }

    [TestMethod]
    public async Task CorrelationIsDeterministicAcrossOverlapGapStalenessAndSourcePolicy()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var ingest = CreateIngest(context, telemetry);
        var older = CreateObservation(target.Site.Id, target.AgentId, observedAt: Epoch.AddMinutes(-2)) with
        {
            ObservationId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            ValidFromUtc = Epoch.AddMinutes(-3),
            ValidThroughUtc = Epoch.AddMinutes(3),
            StaleAfterUtc = Epoch.AddMinutes(1)
        };
        var newer = older with
        {
            ObservationId = Guid.Parse("10000000-0000-0000-0000-000000000002"),
            ObservedAtUtc = Epoch.AddMinutes(-1),
            Value = older.Value with { NumericValue = 50 }
        };
        var simulated = newer with
        {
            ObservationId = Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Source = newer.Source with
            {
                SourceId = "simulated-weather",
                Kind = EnvironmentalObservationSourceKind.Simulated
            },
            Value = newer.Value with { NumericValue = 75 }
        };
        await ingest.IngestAsync(older).ConfigureAwait(false);
        await ingest.IngestAsync(newer).ConfigureAwait(false);
        await ingest.IngestAsync(simulated).ConfigureAwait(false);
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);
        var selector = new EnvironmentalObservationSelector(
            EnvironmentalObservationKind.RelativeHumidity,
            [EnvironmentalObservationSourceKind.Measured, EnvironmentalObservationSourceKind.Simulated],
            [EnvironmentalObservationQuality.Good],
            TimeSpan.FromDays(1));

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            newer.Target,
            Epoch,
            Epoch,
            selector)).ConfigureAwait(false);
        var stale = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            newer.Target,
            Epoch.AddMinutes(4),
            Epoch.AddMinutes(4),
            selector)).ConfigureAwait(false);
        var staleBoundary = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            newer.Target,
            Epoch.AddMinutes(1),
            Epoch.AddMinutes(1),
            selector)).ConfigureAwait(false);
        var missing = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            newer.Target,
            Epoch.AddDays(2),
            Epoch.AddDays(2),
            selector)).ConfigureAwait(false);
        var history = await query.QueryAsync(new EnvironmentalObservationQuery(
            target.Site.Id,
            EnvironmentalObservationKind.RelativeHumidity,
            Epoch.AddHours(-1),
            Epoch.AddHours(1),
            AgentId: target.AgentId,
            RigId: "rig-1")).ConfigureAwait(false);

        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Fresh);
        match.HadOverlap.Should().BeTrue();
        match.Observation!.ObservationId.Should().Be(newer.ObservationId);
        match.Observation.Source.Kind.Should().Be(EnvironmentalObservationSourceKind.Measured);
        stale.Status.Should().Be(EnvironmentalObservationMatchStatus.Stale);
        stale.Observation!.ObservationId.Should().Be(newer.ObservationId);
        staleBoundary.Status.Should().Be(EnvironmentalObservationMatchStatus.Stale);
        missing.Status.Should().Be(EnvironmentalObservationMatchStatus.Missing);
        history.Should().HaveCount(3);
        history.Select(item => item.Observation.ObservationId).Should().Contain(newer.ObservationId);
        history.Should().OnlyContain(item => item.ContentSha256 ==
            EnvironmentalObservationJson.ComputeContentSha256(item.Observation));
    }

    [TestMethod]
    public async Task CorrelationIncludesClockAheadEvidenceOnlyWithinExplicitTolerance()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var ingest = CreateIngest(context, telemetry);
        var withinTolerance = CreateObservation(target.Site.Id, target.AgentId, Epoch.AddSeconds(60)) with
        {
            ValidFromUtc = Epoch.AddMinutes(-1),
            ValidThroughUtc = Epoch.AddMinutes(5),
            StaleAfterUtc = Epoch.AddMinutes(3)
        };
        var outsideTolerance = withinTolerance with
        {
            ObservationId = Guid.NewGuid(),
            ObservedAtUtc = Epoch.AddSeconds(121),
            Value = withinTolerance.Value with { NumericValue = 90 }
        };
        await ingest.IngestAsync(withinTolerance).ConfigureAwait(false);
        await ingest.IngestAsync(outsideTolerance).ConfigureAwait(false);
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions { ClockToleranceSeconds = 120 }),
            new FixedTimeProvider(Epoch),
            telemetry);

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            withinTolerance.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                EnvironmentalObservationKind.RelativeHumidity,
                [EnvironmentalObservationSourceKind.Measured],
                [EnvironmentalObservationQuality.Good],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);

        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Fresh);
        match.Observation!.ObservationId.Should().Be(withinTolerance.ObservationId);
        match.Age.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public async Task CorrelationBoundsStalenessAtExposureEndAndRejectsTimestampOverflow()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var observation = CreateObservation(target.Site.Id, target.AgentId) with
        {
            ValidThroughUtc = Epoch.AddHours(3),
            StaleAfterUtc = Epoch.AddMinutes(1)
        };
        await CreateIngest(context, telemetry).IngestAsync(observation).ConfigureAwait(false);
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);
        var selector = new EnvironmentalObservationSelector(
            EnvironmentalObservationKind.RelativeHumidity,
            [EnvironmentalObservationSourceKind.Measured],
            [EnvironmentalObservationQuality.Good],
            TimeSpan.FromHours(1));

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            observation.Target,
            Epoch.AddMinutes(30),
            Epoch.AddHours(2),
            selector)).ConfigureAwait(false);
        Func<Task> minimum = () => query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            observation.Target,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            selector));
        Func<Task> maximum = () => query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            observation.Target,
            DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue,
            selector));

        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Missing);
        await minimum.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        await maximum.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FreshLowerPriorityEvidenceBeatsStaleHigherPriorityEvidence()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var ingest = CreateIngest(context, telemetry);
        var measured = CreateObservation(target.Site.Id, target.AgentId, Epoch.AddMinutes(-2)) with
        {
            ValidFromUtc = Epoch.AddMinutes(-3),
            ValidThroughUtc = Epoch.AddMinutes(3),
            StaleAfterUtc = Epoch.AddMinutes(-1)
        };
        var simulated = measured with
        {
            ObservationId = Guid.NewGuid(),
            Source = measured.Source with
            {
                SourceId = "fresh-simulation",
                Kind = EnvironmentalObservationSourceKind.Simulated
            },
            StaleAfterUtc = Epoch.AddMinutes(1)
        };
        await ingest.IngestAsync(measured).ConfigureAwait(false);
        await ingest.IngestAsync(simulated).ConfigureAwait(false);
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            measured.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                EnvironmentalObservationKind.RelativeHumidity,
                [EnvironmentalObservationSourceKind.Measured, EnvironmentalObservationSourceKind.Simulated],
                [EnvironmentalObservationQuality.Good],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);
        var measuredOnly = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            measured.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                EnvironmentalObservationKind.RelativeHumidity,
                [EnvironmentalObservationSourceKind.Measured],
                [EnvironmentalObservationQuality.Good],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);

        match.Status.Should().Be(EnvironmentalObservationMatchStatus.Fresh);
        match.Observation!.ObservationId.Should().Be(simulated.ObservationId);
        measuredOnly.Status.Should().Be(EnvironmentalObservationMatchStatus.Stale);
        measuredOnly.HadOverlap.Should().BeFalse();
    }

    [TestMethod]
    public async Task CorrelationPrefersExactRigScopeWithoutUsingCurrentRegistrationState()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var ingest = CreateIngest(context, telemetry);
        var site = CreateObservation(target.Site.Id, null) with
        {
            ObservationId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Target = new EnvironmentalObservationTarget(target.Site.Id),
            Source = CreateObservation(target.Site.Id, null).Source with { SourceId = "site-weather" },
            Value = CreateObservation(target.Site.Id, null).Value with { NumericValue = 10 }
        };
        var rig = CreateObservation(target.Site.Id, target.AgentId) with
        {
            ObservationId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Value = CreateObservation(target.Site.Id, target.AgentId).Value with { NumericValue = 20 }
        };
        await ingest.IngestAsync(site).ConfigureAwait(false);
        await ingest.IngestAsync(rig).ConfigureAwait(false);
        target.Registration.ObservatoryId = Guid.NewGuid();
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);

        var match = await query.CorrelateAsync(new EnvironmentalObservationCorrelationRequest(
            rig.Target,
            Epoch,
            Epoch,
            new EnvironmentalObservationSelector(
                EnvironmentalObservationKind.RelativeHumidity,
                [EnvironmentalObservationSourceKind.Measured],
                [EnvironmentalObservationQuality.Good],
                TimeSpan.FromHours(1)))).ConfigureAwait(false);

        match.Observation!.ObservationId.Should().Be(rig.ObservationId);
        match.Observation.Target.RigId.Should().Be("rig-1");
    }

    [TestMethod]
    public async Task BoundedHistoryUsesSourceIdentityToBreakCrossSourceTies()
    {
        await using var context = CreateContext();
        var target = await SeedTargetAsync(context).ConfigureAwait(false);
        using var telemetry = new EnvironmentalObservationTelemetry();
        var ingest = CreateIngest(context, telemetry);
        var sharedObservationId = Guid.NewGuid();
        var first = CreateObservation(target.Site.Id, target.AgentId) with { ObservationId = sharedObservationId };
        var second = first with { Source = first.Source with { SourceId = "tie-source-2" } };
        await ingest.IngestAsync(first).ConfigureAwait(false);
        await ingest.IngestAsync(second).ConfigureAwait(false);
        var firstIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(first);
        var secondIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(second);
        var expectedSourceId = string.CompareOrdinal(firstIdentity, secondIdentity) < 0
            ? first.Source.SourceId
            : second.Source.SourceId;
        var query = new EnvironmentalObservationQueryService(
            context,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(Epoch),
            telemetry);
        var request = new EnvironmentalObservationQuery(
            target.Site.Id,
            EnvironmentalObservationKind.RelativeHumidity,
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(1),
            Take: 1,
            AgentId: target.AgentId,
            RigId: "rig-1");

        var firstResult = await query.QueryAsync(request).ConfigureAwait(false);
        var repeated = await query.QueryAsync(request).ConfigureAwait(false);

        firstResult.Should().ContainSingle();
        firstResult[0].Observation.Source.SourceId.Should().Be(expectedSourceId);
        repeated[0].Observation.Source.SourceId.Should().Be(expectedSourceId);
    }

    private static EnvironmentalObservationIngestService CreateIngest(
        ApplicationDbContext context,
        EnvironmentalObservationTelemetry telemetry)
        => new(
            context,
            new FixedTimeProvider(Epoch),
            Options.Create(new EnvironmentalObservationOptions()),
            telemetry,
            NullLogger<EnvironmentalObservationIngestService>.Instance);

    private static async Task<(Observatory Site, DeviceRegistration Registration, Guid AgentId)> SeedTargetAsync(
        ApplicationDbContext context)
    {
        var site = new Observatory
        {
            OwnerUserId = $"owner-{Guid.NewGuid():N}",
            Name = "Environment test site",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        var agentId = Guid.NewGuid();
        var registration = new DeviceRegistration
        {
            DeviceId = $"environment-test-{Guid.NewGuid():N}",
            Observatory = site,
            ObservatoryId = site.Id,
            FriendlyName = "Environment test agent",
            ObservatoryName = site.Name,
            OwnerUserId = site.OwnerUserId,
            OwnerDisplayName = "Environment Owner",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = agentId,
            DeviceKeyHash = new string('B', 64),
            IssuedAtUtc = Epoch.AddDays(-1),
            ExpiresAtUtc = Epoch.AddDays(1)
        };
        context.DeviceRegistrations.Add(registration);
        context.CentralFrames.Add(new CentralFrame
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
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (site, registration, agentId);
    }

    private static EnvironmentalObservationV1 CreateObservation(
        Guid siteId,
        Guid? agentId,
        DateTimeOffset? observedAt = null)
    {
        var parameters = Json("""{"calibration":"factory-v1"}""");
        var time = observedAt ?? Epoch;
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            Guid.NewGuid(),
            new EnvironmentalObservationTarget(siteId, agentId, agentId is null ? null : "rig-1"),
            new EnvironmentalObservationSource(
                "environment-provider",
                "measured-weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("weather-normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            time,
            null,
            null,
            time.AddMinutes(-1),
            time.AddMinutes(5),
            time.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
