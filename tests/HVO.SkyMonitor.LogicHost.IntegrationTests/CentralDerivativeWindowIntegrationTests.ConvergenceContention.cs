using System.Data.Common;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Graph convergence racing derivative claims. Each test holds one side at a chosen statement, lets the other run into
/// it, and only then releases the first, so the interleaving that the hosted worker reaches by chance is reached every
/// time. Both run on a scratch database with no hosted services, so the only rows either side reads are the ones the
/// test seeds. Each test checks that the side it lets run blocks, if at all, on the lock the race is about, since a side
/// blocked anywhere else has not reached the race and its outcome would say nothing about it.
/// </summary>
public sealed partial class CentralDerivativeWindowIntegrationTests
{
    private static readonly TimeSpan ContentionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Convergence of one execution must not deadlock with a claim of another execution's job. Convergence holds its
    /// serializable reads until it commits, and after reading the execution's attempts it goes on to read rows that
    /// include the claimed job's: in service the terminal write's trigger reads every nonterminal job through a status
    /// index, and on this scratch database the later child-table reads already do. Before the fix the attempts were
    /// read under serializable range locks, which on an attempt key range with nothing above it cover every key the
    /// claim could insert, so the claim, holding its job row, waited to insert its attempt while convergence, holding
    /// the attempt range, waited for the job row. The convergence session is made the deadlock victim so the failure,
    /// when there is one, is always the same.
    /// </summary>
    [TestMethod]
    public async Task GraphConvergence_TerminalWriteDoesNotDeadlockWithAClaimOfAnotherExecution()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"convergence-claim-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        // The two executions come from different cameras, so neither reads the other's jobs as a dependency.
        var claimedSource = await SeedAndScheduleSourceAsync(
            $"{scenario}-claimed", Guid.NewGuid(), 1, capturedBase, CreatePayload(10), "compatible",
            graphNodeIds: ["Preview"], services: host.Services).ConfigureAwait(false);
        var convergedSource = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 2, capturedBase, CreatePayload(20), "compatible",
            graphNodeIds: ["Preview"], services: host.Services).ConfigureAwait(false);
        var claimedJobId = await ReadJobIdAsync(host.Services, claimedSource, BuiltInProcessingRecipes.EncodedPreview)
            .ConfigureAwait(false);
        var failedJobId = await ReadJobIdAsync(host.Services, convergedSource, BuiltInProcessingRecipes.EncodedPreview)
            .ConfigureAwait(false);
        var failedJob = await ReadJobAsync(host.Services, failedJobId).ConfigureAwait(false);
        var executionId = failedJob.GraphExecutionId!.Value;
        await SeedSettledFillerExecutionsAsync(host, $"{scenario}-filler", capturedBase, [claimedJobId, failedJobId])
            .ConfigureAwait(false);
        (await ReadJobAsync(host.Services, claimedJobId).ConfigureAwait(false)).Status
            .Should().Be(CentralDerivativeJobStatus.Pending);
        // Convergence of this execution now has to move it to Failed, which is the terminal write.
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(job => job.Id == failedJobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))).ConfigureAwait(false);

        // Convergence is held before it reads the jobs' inputs: it has not yet read the claimed job, and before the fix
        // it already holds range locks over the attempts it has read.
        var parkConvergence = new ParkCommandInterceptor(command =>
            command.CommandText.Contains("[CentralDerivativeJobInputs]", StringComparison.Ordinal));
        await using var convergenceDb = CreateContentionContext(host, parkConvergence, deadlockVictim: true);
        await using var convergenceScope = host.Services.CreateAsyncScope();
        var scheduler = ActivatorUtilities.CreateInstance<CentralProcessingGraphScheduler>(
            convergenceScope.ServiceProvider, convergenceDb);
        var convergence = Task.Run(() => scheduler.ConvergeAsync(executionId, DateTimeOffset.UtcNow, CancellationToken.None));
        await using var claimScope = host.Services.CreateAsyncScope();
        Task<CentralDerivativeJobLease?>? claim = null;
        try
        {
            await parkConvergence.Entered.WaitAsync(ContentionTimeout).ConfigureAwait(false);
            claim = Task.Run(() => claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("convergence-contention", TimeSpan.FromMinutes(2), CancellationToken.None));
            var claimWaitsOn = await WaitForClaimToFinishOrBlockAsync(host, claim).ConfigureAwait(false);
            if (claimWaitsOn is not null)
            {
                claimWaitsOn.Should().Be(
                    "CentralDerivativeJobAttempts.IX_CentralDerivativeJobAttempts_CentralDerivativeJobId_AttemptNumber");
            }
        }
        finally
        {
            parkConvergence.Release();
            await DrainAsync(convergence, claim).ConfigureAwait(false);
        }

        await convergence.ConfigureAwait(false);
        var lease = await claim!.ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(claimedJobId);
        await WithDbAsync(host.Services, async db =>
        {
            (await db.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync(item => item.Id == executionId)
                .ConfigureAwait(false)).Status.Should().Be(CentralProcessingGraphExecutionStatus.Failed);
            var claimed = await db.CentralDerivativeJobs.AsNoTracking().Include(job => job.Attempts)
                .SingleAsync(job => job.Id == claimedJobId).ConfigureAwait(false);
            claimed.Status.Should().Be(CentralDerivativeJobStatus.Leased);
            claimed.Attempts.Should().ContainSingle(attempt =>
                attempt.AttemptNumber == 1 && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Convergence canceling an execution must not deadlock with an expired-lease reclaim of that execution's own
    /// job. The reclaim selects the job while the execution is still running and holds an update lock on its row; the
    /// execution is then marked cancel-requested by a write to the execution row alone, and convergence loads the
    /// execution, the job and its leased attempt to cancel both. Before the fix convergence read the attempt under a
    /// serializable lock it held to commit, so the reclaim, holding the job, waited to expire the attempt while
    /// convergence, holding the attempt, waited to cancel the job. Cancellation through the execution service cannot
    /// reach this interleaving, because it also marks every nonterminal job and so waits for the reclaim's job lock;
    /// the direct write stands for any writer of the execution row that does not. The convergence session is made the
    /// deadlock victim so the failure, when there is one, is always the same.
    /// </summary>
    [TestMethod]
    public async Task GraphConvergence_CancellationDoesNotDeadlockWithAnExpiredLeaseReclaimOfItsOwnJob()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"convergence-reclaim-{Guid.NewGuid():N}";
        var source = await SeedAndScheduleSourceAsync(
            scenario, Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10), CreatePayload(10), "compatible",
            graphNodeIds: ["Preview"], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, source, BuiltInProcessingRecipes.EncodedPreview)
            .ConfigureAwait(false);
        var executionId = (await ReadJobAsync(host.Services, jobId).ConfigureAwait(false)).GraphExecutionId!.Value;
        await using (var leaseScope = host.Services.CreateAsyncScope())
        {
            (await leaseScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("convergence-reclaim-first", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!.JobId.Should().Be(jobId);
        }
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await WithDbAsync(host.Services, async db =>
        {
            await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LeaseAcquiredAtUtc, expiredAt.AddMinutes(-1))
                    .SetProperty(job => job.LeaseExpiresAtUtc, expiredAt))
                .ConfigureAwait(false);
            await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expiredAt))
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        var parkReclaim = new ParkCommandInterceptor(
            command => command.CommandText.Contains("UPDLOCK, READPAST", StringComparison.Ordinal),
            afterExecution: true);
        await using var reclaimDb = CreateContentionContext(host, parkReclaim, deadlockVictim: false);
        await using var reclaimScope = host.Services.CreateAsyncScope();
        var reclaimService = ActivatorUtilities.CreateInstance<CentralDerivativeJobService>(
            reclaimScope.ServiceProvider, reclaimDb);
        var reclaim = Task.Run(() => reclaimService.ClaimNextAsync(
            "convergence-reclaim-second", TimeSpan.FromMinutes(2), CancellationToken.None));
        var parkConvergence = new ParkCommandInterceptor(command =>
            command.CommandText.Contains("UPDATE [CentralDerivativeJob", StringComparison.Ordinal));
        await using var convergenceDb = CreateContentionContext(host, parkConvergence, deadlockVictim: true);
        await using var convergenceScope = host.Services.CreateAsyncScope();
        Task? convergence = null;
        try
        {
            await parkReclaim.Entered.WaitAsync(ContentionTimeout).ConfigureAwait(false);
            var canceledAt = DateTimeOffset.UtcNow;
            await WithDbAsync(host.Services, db => db.CentralProcessingGraphExecutions
                .Where(item => item.Id == executionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralProcessingGraphExecutionStatus.CancelRequested)
                    .SetProperty(item => item.CancellationRequestedAtUtc, (DateTimeOffset?)canceledAt)
                    .SetProperty(item => item.UpdatedAtUtc, canceledAt))).ConfigureAwait(false);

            var scheduler = ActivatorUtilities.CreateInstance<CentralProcessingGraphScheduler>(
                convergenceScope.ServiceProvider, convergenceDb);
            convergence = Task.Run(() => scheduler.ConvergeAsync(executionId, DateTimeOffset.UtcNow, CancellationToken.None));
            await parkConvergence.Entered.WaitAsync(ContentionTimeout).ConfigureAwait(false);

            parkReclaim.Release();
            var reclaimWaitsOn = await WaitForClaimToFinishOrBlockAsync(host, reclaim).ConfigureAwait(false);
            if (reclaimWaitsOn is not null)
            {
                reclaimWaitsOn.Should().Be("CentralDerivativeJobAttempts.PK_CentralDerivativeJobAttempts");
            }
        }
        finally
        {
            parkReclaim.Release();
            parkConvergence.Release();
            await DrainAsync(convergence, reclaim).ConfigureAwait(false);
        }

        await convergence!.ConfigureAwait(false);
        (await reclaim.ConfigureAwait(false)).Should().BeNull();
        await WithDbAsync(host.Services, async db =>
        {
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Canceled);
            job.Attempts.Should().ContainSingle().Which.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Canceled);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Convergence of a running execution must not deadlock with an expired-lease reclaim of that execution's own job
    /// when the reclaim expires the attempt between convergence's job and attempt reads. Convergence reads the jobs
    /// first and holds serializable shared locks on them, which the reclaim's update lock on its job row does not
    /// block. The reclaim then expires the attempt, taking an exclusive lock on it, and waits to convert its job lock to
    /// lease the job again, which convergence's shared lock blocks; if convergence's attempt read then waits on the
    /// expired attempt, each side waits on the other. Neither session is given a deadlock priority, so SQL Server
    /// chooses the victim, and both sides' waits are reported if either fails. Convergence, holding no writes, is the
    /// victim and reruns its transaction, so the test also requires that a rerun happened: a pass without one would
    /// mean the interleaving was missed, not survived.
    /// </summary>
    [TestMethod]
    public async Task GraphConvergence_DoesNotDeadlockWithAnExpiredLeaseReclaimBetweenItsJobAndAttemptReads()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"convergence-reclaim-reads-{Guid.NewGuid():N}";
        var source = await SeedAndScheduleSourceAsync(
            scenario, Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10), CreatePayload(10), "compatible",
            graphNodeIds: ["Preview"], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, source, BuiltInProcessingRecipes.EncodedPreview)
            .ConfigureAwait(false);
        var executionId = (await ReadJobAsync(host.Services, jobId).ConfigureAwait(false)).GraphExecutionId!.Value;
        await using (var leaseScope = host.Services.CreateAsyncScope())
        {
            (await leaseScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("convergence-reclaim-reads-first", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!.JobId.Should().Be(jobId);
        }
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await WithDbAsync(host.Services, async db =>
        {
            await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LeaseAcquiredAtUtc, expiredAt.AddMinutes(-1))
                    .SetProperty(job => job.LeaseExpiresAtUtc, expiredAt))
                .ConfigureAwait(false);
            await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expiredAt))
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Convergence is held after it has read the jobs and before it reads their attempts.
        var parkConvergence = new ParkCommandInterceptor(command =>
            command.CommandText.Contains("[CentralDerivativeJobAttempts]", StringComparison.Ordinal));
        var convergenceSession = new SessionIdInterceptor();
        await using var convergenceDb = CreateContentionContext(host, parkConvergence, convergenceSession);
        await using var convergenceScope = host.Services.CreateAsyncScope();
        var convergenceLog = new SchedulerLogRecorder();
        var scheduler = ActivatorUtilities.CreateInstance<CentralProcessingGraphScheduler>(
            convergenceScope.ServiceProvider, convergenceDb, convergenceLog);
        var convergence = Task.Run(() => scheduler.ConvergeAsync(executionId, DateTimeOffset.UtcNow, CancellationToken.None));
        var reclaimSession = new SessionIdInterceptor();
        await using var reclaimDb = CreateContentionContext(host, reclaimSession);
        await using var reclaimScope = host.Services.CreateAsyncScope();
        var reclaimService = ActivatorUtilities.CreateInstance<CentralDerivativeJobService>(
            reclaimScope.ServiceProvider, reclaimDb);
        Task<CentralDerivativeJobLease?>? reclaim = null;
        string? reclaimWaitsOn = null;
        string? convergenceWaitsOn = null;
        try
        {
            await parkConvergence.Entered.WaitAsync(ContentionTimeout).ConfigureAwait(false);
            reclaim = Task.Run(() => reclaimService.ClaimNextAsync(
                "convergence-reclaim-reads-second", TimeSpan.FromMinutes(2), CancellationToken.None));
            reclaimWaitsOn = await WaitForClaimToFinishOrBlockAsync(host, reclaim, reclaimSession).ConfigureAwait(false);
            parkConvergence.Release();
            convergenceWaitsOn = await WaitForClaimToFinishOrBlockAsync(host, convergence, convergenceSession)
                .ConfigureAwait(false);
        }
        finally
        {
            parkConvergence.Release();
            await DrainAsync(convergence, reclaim).ConfigureAwait(false);
        }

        var waits = $"the reclaim waited on {reclaimWaitsOn ?? "nothing"} and then convergence on "
            + $"{convergenceWaitsOn ?? "nothing"}";
        await FluentActions.Awaiting(() => convergence).Should().NotThrowAsync(waits).ConfigureAwait(false);
        var lease = (await FluentActions.Awaiting(() => reclaim!).Should().NotThrowAsync(waits).ConfigureAwait(false))
            .Subject;
        if (reclaimWaitsOn is not null)
        {
            reclaimWaitsOn.Should().Be("CentralDerivativeJobs.PK_CentralDerivativeJobs");
        }
        convergenceLog.DeadlockRetries.Should().NotBeEmpty(waits);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(jobId);
        await WithDbAsync(host.Services, async db =>
        {
            (await db.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync(item => item.Id == executionId)
                .ConfigureAwait(false)).Status.Should().Be(CentralProcessingGraphExecutionStatus.Running);
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Leased);
            job.AttemptCount.Should().Be(2);
            job.Attempts.OrderBy(attempt => attempt.AttemptNumber).Select(attempt => attempt.Outcome).Should().Equal(
                CentralDerivativeAttemptOutcome.LeaseExpired, CentralDerivativeAttemptOutcome.Leased);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Convergence reruns a deadlocked transaction a bounded number of times. Every attempt here reads the attempts
    /// into an injected deadlock, so all three attempts fail: the first two are absorbed and logged, without a failed
    /// convergence being recorded, and the third deadlock propagates and is recorded once as a failure.
    /// </summary>
    [TestMethod]
    public async Task GraphConvergence_RetriesADeadlockABoundedNumberOfTimesAndThenPropagatesIt()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var executionId = await SeedRunningExecutionAsync(host, $"convergence-exhaustion-{Guid.NewGuid():N}")
            .ConfigureAwait(false);
        var deadlocks = new DeadlockInjectingInterceptor();
        await using var convergenceDb = CreateContentionContext(host, deadlocks);
        await using var convergenceScope = host.Services.CreateAsyncScope();
        var convergenceLog = new SchedulerLogRecorder();
        using var outcomes = new ConvergenceOutcomeCollector();
        var scheduler = ActivatorUtilities.CreateInstance<CentralProcessingGraphScheduler>(
            convergenceScope.ServiceProvider, convergenceDb, convergenceLog, outcomes.Telemetry);

        var thrown = await FluentActions.Awaiting(() => scheduler.ConvergeAsync(
                executionId, DateTimeOffset.UtcNow, CancellationToken.None))
            .Should().ThrowAsync<Exception>().ConfigureAwait(false);

        CentralProcessingGraphScheduler.IsDeadlock(thrown.Which).Should().BeTrue();
        deadlocks.Injected.Should().Be(3);
        convergenceLog.DeadlockRetries.Should().Equal(1, 2);
        outcomes.Outcomes.Should().Equal("failed");
    }

    /// <summary>
    /// A deadlock that arrives once the caller has canceled is not rerun: it propagates from the first attempt and is
    /// recorded as a failure.
    /// </summary>
    [TestMethod]
    public async Task GraphConvergence_DoesNotRetryADeadlockOnceCanceled()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var executionId = await SeedRunningExecutionAsync(host, $"convergence-canceled-{Guid.NewGuid():N}")
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var deadlocks = new DeadlockInjectingInterceptor(cancellation.Cancel);
        await using var convergenceDb = CreateContentionContext(host, deadlocks);
        await using var convergenceScope = host.Services.CreateAsyncScope();
        var convergenceLog = new SchedulerLogRecorder();
        using var outcomes = new ConvergenceOutcomeCollector();
        var scheduler = ActivatorUtilities.CreateInstance<CentralProcessingGraphScheduler>(
            convergenceScope.ServiceProvider, convergenceDb, convergenceLog, outcomes.Telemetry);

        var thrown = await FluentActions.Awaiting(() => scheduler.ConvergeAsync(
                executionId, DateTimeOffset.UtcNow, cancellation.Token))
            .Should().ThrowAsync<Exception>().ConfigureAwait(false);

        CentralProcessingGraphScheduler.IsDeadlock(thrown.Which).Should().BeTrue();
        deadlocks.Injected.Should().Be(1);
        convergenceLog.DeadlockRetries.Should().BeEmpty();
        outcomes.Outcomes.Should().Equal("failed");
    }

    private static async Task<Guid> SeedRunningExecutionAsync(IsolatedWindowHost host, string scenario)
    {
        var source = await SeedAndScheduleSourceAsync(
            scenario, Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10), CreatePayload(10), "compatible",
            graphNodeIds: ["Preview"], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, source, BuiltInProcessingRecipes.EncodedPreview)
            .ConfigureAwait(false);
        return (await ReadJobAsync(host.Services, jobId).ConfigureAwait(false)).GraphExecutionId!.Value;
    }

    /// <summary>
    /// Gives the job table enough settled rows from other executions that convergence reads its own execution's jobs
    /// through the execution index, as it does in service. On a table of two jobs it scans the clustered index and,
    /// under serializable, locks the claimed job before the claim reaches the race. Statistics are refreshed and this
    /// database's cached plans cleared so both sides compile against those rows.
    /// </summary>
    private static async Task SeedSettledFillerExecutionsAsync(
        IsolatedWindowHost host,
        string scenario,
        DateTimeOffset capturedBase,
        IReadOnlyCollection<Guid> keepJobIds)
    {
        const int FillerExecutions = 64;
        var devicePublicId = Guid.NewGuid();
        for (var index = 0; index < FillerExecutions; index++)
        {
            _ = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, 100 + index, capturedBase, CreatePayload((ushort)(100 + index)), "compatible",
                services: host.Services).ConfigureAwait(false);
        }
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(job => !keepJobIds.Contains(job.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))).ConfigureAwait(false);
        await using var connection = new SqlConnection(host.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'UPDATE STATISTICS ' + QUOTENAME(SCHEMA_NAME([schema_id])) + N'.' + QUOTENAME([name])
                + N' WITH FULLSCAN; '
            FROM [sys].[tables] WHERE [is_ms_shipped] = 0;
            EXEC (@sql);
            ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
            """;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static ApplicationDbContext CreateContentionContext(
        IsolatedWindowHost host,
        ParkCommandInterceptor park,
        bool deadlockVictim)
        => deadlockVictim
            ? CreateContentionContext(host, park, new LowDeadlockPriorityInterceptor())
            : CreateContentionContext(host, park);

    private static ApplicationDbContext CreateContentionContext(
        IsolatedWindowHost host,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(host.ConnectionString);
        options.AddInterceptors(interceptors);
        return new ApplicationDbContext(options.Options);
    }

    /// <summary>
    /// Returns once the claim has finished, with null, or once a session in the scratch database is waiting on a key
    /// lock, with the table and index it waits on. Without a session that can only be the claim, because the other side
    /// is parked; with one, only that session's waits count. Polling the claim alone would not tell a blocked claim from
    /// a slow one.
    /// </summary>
    private static async Task<string?> WaitForClaimToFinishOrBlockAsync(
        IsolatedWindowHost host,
        Task claim,
        SessionIdInterceptor? session = null)
    {
        await using var connection = new SqlConnection(host.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + ContentionTimeout;
        while (!claim.IsCompleted)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1) CONCAT(OBJECT_NAME([p].[object_id]), N'.', [i].[name])
                FROM [sys].[dm_tran_locks] AS [l]
                JOIN [sys].[partitions] AS [p] ON [p].[hobt_id] = [l].[resource_associated_entity_id]
                JOIN [sys].[indexes] AS [i] ON [i].[object_id] = [p].[object_id] AND [i].[index_id] = [p].[index_id]
                WHERE [l].[resource_database_id] = DB_ID() AND [l].[request_status] IN (N'WAIT', N'CONVERT')
                    AND [l].[request_session_id] <> @@SPID
                    AND (@session IS NULL OR [l].[request_session_id] = @session);
                """;
            _ = command.Parameters.Add(new SqlParameter("@session", System.Data.SqlDbType.Int)
            {
                Value = session is null ? DBNull.Value : session.SessionId
            });
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is string waitingOn)
            {
                return waitingOn;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The watched side neither finished nor blocked.");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Lets every side of the race finish before its context is disposed, whatever failed first, so a failure reports
    /// the deadlock or error that caused it rather than an error from disposing a context mid-command.
    /// </summary>
    private static async Task DrainAsync(params Task?[] tasks)
    {
        // WhenAny does not throw; each side's own fault is rethrown where the test awaits it.
        _ = await Task.WhenAny(Task.WhenAll(tasks.OfType<Task>()), Task.Delay(ContentionTimeout)).ConfigureAwait(false);
    }

    /// <summary>Holds the first matching command, before or after it runs, until released.</summary>
    private sealed class ParkCommandInterceptor(Func<DbCommand, bool> matches, bool afterExecution = false)
        : DbCommandInterceptor
    {

        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int parked;

        public Task Entered => entered.Task;

        public void Release() => release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!afterExecution)
            {
                await ParkIfMatchedAsync(command, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!afterExecution)
            {
                await ParkIfMatchedAsync(command, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (afterExecution)
            {
                await ParkIfMatchedAsync(command, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        private async Task ParkIfMatchedAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (matches(command) && Interlocked.CompareExchange(ref parked, 1, 0) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fails every read of the attempts table with the error SQL Server raises for a deadlock victim, before the read
    /// reaches the server, and optionally runs an action first.
    /// </summary>
    private sealed class DeadlockInjectingInterceptor(Action? beforeEach = null) : DbCommandInterceptor
    {
        private int injected;

        public int Injected => Volatile.Read(ref injected);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[CentralDerivativeJobAttempts]", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref injected);
                beforeEach?.Invoke();
                throw CreateDeadlockVictimException();
            }
            return ValueTask.FromResult(result);
        }

        /// <summary>
        /// <see cref="SqlException"/> has no public constructor; build the deadlock-victim error through SqlClient's
        /// internal factory.
        /// </summary>
        private static SqlException CreateDeadlockVictimException()
        {
            const System.Reflection.BindingFlags nonPublic =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
            var errorType = typeof(SqlError);
            var errorConstructor = errorType.GetConstructors(nonPublic)
                .Where(candidate => candidate.GetParameters().Length > 0 &&
                    candidate.GetParameters()[0].ParameterType == typeof(int))
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .First();
            var arguments = errorConstructor.GetParameters().Select((parameter, index) => index == 0
                ? 1205
                : parameter.ParameterType == typeof(string)
                    ? "Injected deadlock victim."
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null).ToArray();
            var collectionType = typeof(SqlErrorCollection);
            var collection = Activator.CreateInstance(collectionType, nonPublic, null, null, null)!;
            _ = collectionType.GetMethod("Add", nonPublic, [errorType])!
                .Invoke(collection, [errorConstructor.Invoke(arguments)]);
            var factory = typeof(SqlException).GetMethod(
                "CreateException", nonPublic, [collectionType, typeof(string)])!;
            return (SqlException)factory.Invoke(null, [collection, "16.0"])!;
        }
    }

    /// <summary>Records the attempt numbers of the convergence deadlocks the scheduler logged as retried.</summary>
    private sealed class SchedulerLogRecorder : ILogger<CentralProcessingGraphScheduler>
    {
        private const int DeadlockRetriedEventId = 2155;
        private readonly List<int> deadlockRetries = [];

        public IReadOnlyList<int> DeadlockRetries
        {
            get
            {
                lock (deadlockRetries)
                {
                    return [.. deadlockRetries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != DeadlockRetriedEventId || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                return;
            }
            var attempt = values.Single(value => value.Key == "Attempt").Value;
            lock (deadlockRetries)
            {
                deadlockRetries.Add((int)attempt!);
            }
        }
    }

    /// <summary>
    /// Owns a telemetry instance and records the outcome of every graph convergence it counts. Only that instance's
    /// counter is listened to: it is recognized as the one published on this thread while the instance is constructed,
    /// so convergences counted by other hosts in the same process are not seen.
    /// </summary>
    private sealed class ConvergenceOutcomeCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<string> outcomes = [];
        private int constructingThread;

        public ConvergenceOutcomeCollector()
        {
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref constructingThread)
                    && instrument.Meter.Name == CentralDerivativeWorkerTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.processing_graph.convergences")
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome" && tag.Value is string outcome)
                    {
                        lock (outcomes)
                        {
                            outcomes.Add(outcome);
                        }
                    }
                }
            });
            listener.Start();
            Volatile.Write(ref constructingThread, Environment.CurrentManagedThreadId);
            Telemetry = new CentralDerivativeWorkerTelemetry();
            Volatile.Write(ref constructingThread, 0);
        }

        public CentralDerivativeWorkerTelemetry Telemetry { get; }

        public IReadOnlyList<string> Outcomes
        {
            get
            {
                lock (outcomes)
                {
                    return [.. outcomes];
                }
            }
        }

        public void Dispose()
        {
            listener.Dispose();
            Telemetry.Dispose();
        }
    }

    /// <summary>Records the server session of the connection the context most recently opened.</summary>
    private sealed class SessionIdInterceptor : DbConnectionInterceptor
    {
        private int sessionId;

        public int SessionId => Volatile.Read(ref sessionId);

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
            => Volatile.Write(ref sessionId, ((SqlConnection)connection).ServerProcessId);

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            ConnectionOpened(connection, eventData);
            return Task.CompletedTask;
        }
    }

    /// <summary>Makes every session the context opens the preferred deadlock victim.</summary>
    private sealed class LowDeadlockPriorityInterceptor : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET DEADLOCK_PRIORITY LOW;";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
