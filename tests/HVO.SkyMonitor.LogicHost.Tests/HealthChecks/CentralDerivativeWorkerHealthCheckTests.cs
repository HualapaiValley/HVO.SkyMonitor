using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.HealthChecks;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeWorkerHealthCheckTests
{
    [TestMethod]
    public async Task ReportsDisabledWorkerAsHealthyAsync()
    {
        await using var context = CreateContext();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions { Enabled = false }),
            telemetry,
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().Contain("Status", "disabled");
    }

    [TestMethod]
    public async Task ReportsMissingHeartbeatAsUnhealthyAsync()
    {
        await using var context = CreateContext();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions()),
            telemetry,
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("Status", "stale");
    }

    [TestMethod]
    public async Task ReportsOldPendingWorkAsDegradedWithoutIdentifiersAsync()
    {
        var now = new DateTimeOffset(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);
        await using var context = CreateContext();
        context.CentralDerivativeJobs.AddRange(new CentralDerivativeJob
        {
            SourceCentralArtifactId = Guid.NewGuid(),
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "health-v1",
            TargetVariant = "preview",
            RecipeName = "encoded-preview",
            RequestedRecipeIdentitySha256 = new string('A', 64),
            RequestIdentitySha256 = new string('B', 64),
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = 3,
            AvailableAtUtc = now - TimeSpan.FromMinutes(11),
            CreatedAtUtc = now - TimeSpan.FromMinutes(11),
            UpdatedAtUtc = now - TimeSpan.FromMinutes(11)
        },
        new CentralDerivativeJob
        {
            SourceCentralArtifactId = Guid.NewGuid(),
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "health-v1",
            TargetVariant = "suspended",
            RecipeName = "encoded-preview",
            RequestedRecipeIdentitySha256 = new string('C', 64),
            RequestIdentitySha256 = new string('D', 64),
            Status = CentralDerivativeJobStatus.RetryableFailure,
            MaxAttempts = 3,
            AvailableAtUtc = null,
            CreatedAtUtc = now - TimeSpan.FromHours(1),
            UpdatedAtUtc = now
        },
        new CentralDerivativeJob
        {
            SourceCentralArtifactId = Guid.NewGuid(),
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "health-v1",
            TargetVariant = "future",
            RecipeName = "encoded-preview",
            RequestedRecipeIdentitySha256 = new string('E', 64),
            RequestIdentitySha256 = new string('F', 64),
            Status = CentralDerivativeJobStatus.RetryableFailure,
            MaxAttempts = 3,
            AvailableAtUtc = now + TimeSpan.FromMinutes(1),
            CreatedAtUtc = now - TimeSpan.FromHours(1),
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        telemetry.RecordPoll(now);
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions
            {
                BacklogDegradedAfter = TimeSpan.FromMinutes(10)
            }),
            telemetry,
            new FixedTimeProvider(now));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().Contain("Status", "backlog");
        result.Data.Should().Contain("PendingCount", 1L);
        result.Data.Keys.Should().BeEquivalentTo(
            "Status", "ActiveSlots", "PendingCount", "OldestAgeSeconds", "LastSuccessAgeSeconds",
            "WaitingCount", "OldestWaitAgeSeconds", "OverdueWaitingCount", "OldestPinAgeSeconds",
            "GraphExecutionCount", "GraphOldestConvergenceAgeSeconds", "GraphUnsealedCount",
            "GraphRecoveryAgeSeconds");
    }

    [TestMethod]
    public async Task ReportsRecentRenewalFailureAsDegradedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        telemetry.RecordPoll(now);
        telemetry.RecordRenewal("failed", now);
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions()),
            telemetry,
            new FixedTimeProvider(now));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().Contain("Status", "renewal-failure");
    }

    [TestMethod]
    public async Task ReportsRecentDependencyFailureAsDegradedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        telemetry.RecordPoll(now);
        telemetry.RecordDependencyFailure("database", now);
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions()),
            telemetry,
            new FixedTimeProvider(now));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().Contain("Status", "dependency-failure");
    }

    [TestMethod]
    public async Task ReportsOverdueWindowAsDegradedWithoutTreatingNormalWaitAsFailureAsync()
    {
        var now = new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);
        await using var context = CreateContext();
        context.CentralDerivativeJobs.AddRange(
            CreateWaitingJob(now - TimeSpan.FromMinutes(2), now + TimeSpan.FromMinutes(3), 'A'),
            CreateWaitingJob(now - TimeSpan.FromMinutes(10), now - TimeSpan.FromMinutes(1), 'C'));
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        telemetry.RecordPoll(now);
        var check = new CentralDerivativeWorkerHealthCheck(
            context,
            Options.Create(new CentralDerivativeWorkerOptions()),
            telemetry,
            new FixedTimeProvider(now));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().Contain("Status", "window-overdue");
        result.Data.Should().Contain("WaitingCount", 2L);
        result.Data.Should().Contain("OverdueWaitingCount", 1L);
    }

    private static CentralDerivativeJob CreateWaitingJob(
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc,
        char identitySeed) => new()
        {
            SourceCentralArtifactId = Guid.NewGuid(),
            TargetRole = FrameArtifactRole.Combined,
            TargetRecipeVersion = "window-v1",
            TargetVariant = "window",
            RecipeName = "rolling-mean",
            RequestedRecipeIdentitySha256 = new string(identitySeed, 64),
            RequestIdentitySha256 = new string((char)(identitySeed + 1), 64),
            Status = CentralDerivativeJobStatus.Waiting,
            MaxAttempts = 3,
            ResolutionDeadlineUtc = deadlineUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
