using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.Tests.LogicHost.HealthChecks;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentLocationHealthCheckTests
{
    [TestMethod]
    public async Task CheckHealthAsync_PendingProposalIsDegradedWithAggregateData()
    {
        await using var context = CreateContext();
        context.DeviceDeploymentLocationVersions.Add(new DeviceDeploymentLocationVersion
        {
            Status = DeploymentLocationResolutionStatus.Pending,
            ProposedAtUtc = UtcNow.AddMinutes(-5)
        });
        await context.SaveChangesAsync();
        using var telemetry = new DeploymentLocationTelemetry();
        var healthCheck = new DeploymentLocationHealthCheck(context, new FixedTimeProvider(UtcNow), telemetry);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["PendingCount"].Should().Be(1L);
        result.Data["OldestAgeSeconds"].Should().Be(300d);
    }

    [TestMethod]
    public async Task CheckHealthAsync_ResolvedFrameWithoutProjectionIsUnhealthy()
    {
        await using var context = CreateContext();
        context.CentralFrames.Add(new CentralFrame
        {
            LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved
        });
        await context.SaveChangesAsync();
        using var telemetry = new DeploymentLocationTelemetry();
        var healthCheck = new DeploymentLocationHealthCheck(context, new FixedTimeProvider(UtcNow), telemetry);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("Guid").And.NotContain("locationId");
    }

    [TestMethod]
    public async Task CheckHealthAsync_CurrentObservatoryVersionWithoutBacklogIsHealthy()
    {
        await using var context = CreateContext();
        var observatory = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Healthy observatory",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            CreatedAtUtc = UtcNow,
            IsActive = true
        };
        context.Observatories.Add(observatory);
        await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            context, observatory, UtcNow, "test", CancellationToken.None);
        await context.SaveChangesAsync();
        using var telemetry = new DeploymentLocationTelemetry();

        var result = await new DeploymentLocationHealthCheck(
            context, new FixedTimeProvider(UtcNow), telemetry).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [TestMethod]
    public async Task CheckHealthAsync_MultipleCurrentObservatoryVersionsIsUnhealthy()
    {
        await using var context = CreateContext();
        var observatory = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Corrupt observatory",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            CreatedAtUtc = UtcNow,
            IsActive = true
        };
        context.Observatories.Add(observatory);
        var current = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            context, observatory, UtcNow, "test", CancellationToken.None);
        context.ObservatoryLocationVersions.Add(new ObservatoryLocationVersion
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Version = 2,
            CanonicalSha256 = new string('A', 64),
            EffectiveFromUtc = UtcNow.AddMinutes(1),
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            RecordedAtUtc = UtcNow.AddMinutes(1),
            RecordedBy = "corruption-test"
        });
        observatory.CurrentLocationVersion = current.Version;
        observatory.CurrentLocationCanonicalSha256 = current.CanonicalSha256;
        await context.SaveChangesAsync();
        using var telemetry = new DeploymentLocationTelemetry();

        var result = await new DeploymentLocationHealthCheck(
            context, new FixedTimeProvider(UtcNow), telemetry).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static readonly DateTimeOffset UtcNow = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
