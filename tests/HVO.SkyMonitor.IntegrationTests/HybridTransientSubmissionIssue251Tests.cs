using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class HybridTransientSubmissionIntegrationTests
{
    private static readonly JsonSerializerOptions Issue243HybridJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task GenerationCheck_HoldsObjectFencesWithoutSqlTransactionAndRejectsChangedSource()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        try
        {
            var applicationName = $"HVO.SkyMonitor.Issue243.Hybrid.{Guid.NewGuid():N}";
            var subjectConnection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            var reader = new BlockingGenerationReader();
            using var factory = CreateHybridFactory(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(subjectConnection));
                services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                {
                    reader.Inner = ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider);
                    return reader;
                }));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var operationCancellation = new CancellationTokenSource();
            var started = Stopwatch.GetTimestamp();
            var responseTask = SendAsync(
                client, scenario.DeviceId, DeviceKey, scenario.Envelope, operationCancellation.Token);
            SqlCriticalSectionSnapshot? snapshot = null;
            BlockedUpdateEvidence? blocker = null;
            HttpResponseMessage? response = null;
            try
            {
                await reader.Entered.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                snapshot = await ReadIssue243HybridSnapshotAsync(
                    AssemblyHooks.Fixture.SqlServerConnectionString, applicationName).ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(1, snapshot.Sessions);
                Assert.AreEqual(0, snapshot.OpenTransactionSessions);
                Assert.IsGreaterThanOrEqualTo(5, snapshot.SessionApplicationLocks);
                Assert.AreEqual(0, snapshot.TransactionApplicationLocks);
                blocker = await ObserveIssue243HybridBlockedUpdateAsync(
                    AssemblyHooks.Fixture.SqlServerConnectionString, scenario.CentralArtifactIds[0]).ConfigureAwait(false);
                Assert.IsFalse(blocker.TimedOut);
            }
            finally
            {
                reader.Release();
                response = await EvidenceTaskCleanup.AwaitAsync(
                    responseTask, operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            using (response)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                Assert.IsNotNull(response);
                Assert.AreEqual((HttpStatusCode)425, response.StatusCode);
                Assert.AreEqual(5, reader.VerifyCalls);
                Assert.AreEqual(5, reader.GenerationCalls);
                Assert.AreEqual(1, blocker!.RowsAffected);
                await using var assertionScope = factory.Services.CreateAsyncScope();
                var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var acceptedJobCount = await assertionDb.CentralTransientValidationJobs.AsNoTracking()
                    .CountAsync(validation =>
                        validation.SubmissionIdentitySha256 == scenario.Envelope.SubmissionIdentitySha256)
                    .ConfigureAwait(false);
                var rejectionAuditRecorded = await assertionDb.CentralTransientSubmissionAudits.AsNoTracking()
                    .AnyAsync(audit => audit.CandidateId == scenario.Envelope.CandidateId
                        && audit.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceUnavailable)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, acceptedJobCount);
                Assert.IsTrue(rejectionAuditRecorded);
                var repositoryRoot = FindIssue243HybridRepositoryRoot();
                var source = await EvidenceSourceIdentity.CaptureAsync(
                    repositoryRoot,
                    typeof(HybridTransientSubmissionIntegrationTests),
                    typeof(CentralTransientSubmissionService)).ConfigureAwait(false);
                var output = Path.Combine(
                    repositoryRoot, "TestResults", "issue-251", source.OutputDirectoryName, source.RunId);
                Directory.CreateDirectory(output);
                var evidence = new
                {
                    Schema = "hvo-issue-251-hybrid-generation-fence-v1",
                    Source = source,
                    Workload = new
                    {
                        Sources = scenario.CentralArtifactIds.Count,
                        GenerationChecks = reader.GenerationCalls,
                        Verifications = reader.VerifyCalls,
                        CompetingWriterTimeoutSeconds = 1,
                        ObjectBoundary = "First generation HEAD paused after all object fences were acquired and before SQL finalization"
                    },
                    Observation = new
                    {
                        ElapsedMilliseconds = elapsed.TotalMilliseconds,
                        snapshot!.Sessions,
                        snapshot.OpenTransactionSessions,
                        snapshot.SessionApplicationLocks,
                        snapshot.TransactionApplicationLocks,
                        CompetingWriterTimedOut = blocker!.TimedOut,
                        CompetingWriterMilliseconds = blocker.ElapsedMilliseconds,
                        blocker.RowsAffected,
                        AcceptedJobCount = acceptedJobCount,
                        RejectionAuditRecorded = rejectionAuditRecorded,
                        ResponseStatus = response.StatusCode.ToString()
                    },
                    Correctness = "The competing source update completed during generation HEAD I/O and exact finalization rejected the stale source without creating a job.",
                    RecordedAtUtc = DateTimeOffset.UtcNow
                };
                await EvidenceSourceIdentity.WriteJsonAsync(
                    Path.Combine(output, "hybrid-generation-fence.json"),
                    evidence,
                    Issue243HybridJsonOptions).ConfigureAwait(false);
                TestContext.WriteLine(
                    "issue251 hybrid generation: elapsed_ms={0:F3}, sessions={1}, open_transactions={2}, session_application_locks={3}, blocker_ms={4:F3}",
                    elapsed.TotalMilliseconds,
                    snapshot!.Sessions,
                    snapshot.OpenTransactionSessions,
                    snapshot.SessionApplicationLocks,
                    blocker!.ElapsedMilliseconds);
            }
        }
        finally
        {
            await QuiesceIssue243HybridJobsAsync(scenario.CentralArtifactIds).ConfigureAwait(false);
        }

        await AssertRealGenerationReplacementRejectedAsync().ConfigureAwait(false);
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            await AssertConcurrentFenceSessionsAsync(concurrency).ConfigureAwait(false);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task QuiesceIssue243HybridJobsAsync(IReadOnlyList<Guid> sourceArtifactIds)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobIds = db.CentralDerivativeJobInputs
            .Where(input => sourceArtifactIds.Contains(input.CentralArtifactId))
            .Select(input => input.CentralDerivativeJobId);
        await db.CentralDerivativeJobs
            .Where(job => jobIds.Contains(job.Id)
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.Leased
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure
                    || job.Status == CentralDerivativeJobStatus.CancelRequested))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.StateReasonCode, "issue-243.probe-complete"))
            .ConfigureAwait(false);
    }

    private static async Task AssertRealGenerationReplacementRejectedAsync()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        try
        {
            string storageReference;
            long byteLength;
            await using (var sourceScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = sourceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var source = await db.CentralArtifacts.AsNoTracking()
                    .SingleAsync(artifact => artifact.Id == scenario.CentralArtifactIds[0]).ConfigureAwait(false);
                storageReference = source.StorageReference;
                byteLength = source.ByteLength;
            }

            var reader = new BlockingGenerationReader();
            using var factory = CreateHybridFactory(services =>
                services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                {
                    reader.Inner = ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider);
                    return reader;
                })));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var operationCancellation = new CancellationTokenSource();
            var responseTask = SendAsync(
                client, scenario.DeviceId, DeviceKey, scenario.Envelope, operationCancellation.Token);
            HttpResponseMessage? response = null;
            try
            {
                await reader.Entered.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                var objectKey = storageReference["minio://skymonitor-artifacts/".Length..];
                var replacement = Enumerable.Repeat((byte)0xA5, checked((int)byteLength)).ToArray();
                await using var replacementStream = new MemoryStream(replacement, writable: false);
                await using var replacementScope = factory.Services.CreateAsyncScope();
                await replacementScope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(
                    new PutObjectArgs()
                        .WithBucket("skymonitor-artifacts")
                        .WithObject(objectKey)
                        .WithStreamData(replacementStream)
                        .WithObjectSize(byteLength)
                        .WithContentType("application/x-hvo-linear-frame"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                reader.Release();
                response = await EvidenceTaskCleanup.AwaitAsync(
                    responseTask, operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }

            using (response)
            {
                Assert.IsNotNull(response);
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
                await using var assertionScope = factory.Services.CreateAsyncScope();
                var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.IsFalse(await db.CentralTransientValidationJobs.AsNoTracking().AnyAsync(validation =>
                    validation.SubmissionIdentitySha256 == scenario.Envelope.SubmissionIdentitySha256)
                    .ConfigureAwait(false));
                Assert.IsTrue(await db.CentralTransientSubmissionAudits.AsNoTracking().AnyAsync(audit =>
                    audit.CandidateId == scenario.Envelope.CandidateId
                    && audit.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceConflict)
                    .ConfigureAwait(false));
            }
        }
        finally
        {
            await QuiesceIssue243HybridJobsAsync(scenario.CentralArtifactIds).ConfigureAwait(false);
        }
    }

    private static async Task AssertConcurrentFenceSessionsAsync(int concurrency)
    {
        var scenarios = new List<SubmissionScenario>(concurrency);
        try
        {
            for (var index = 0; index < concurrency; index++)
            {
                scenarios.Add(await CreateScenarioAsync().ConfigureAwait(false));
            }

            var applicationName = $"HVO.SkyMonitor.Issue251.C{concurrency}.{Guid.NewGuid():N}";
            var subjectConnection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            var probe = new ConcurrentGenerationProbe(concurrency);
            using var factory = CreateHybridFactory(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(subjectConnection));
                services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                    new ConcurrentGenerationReader(
                        ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider), probe)));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var operationCancellation = new CancellationTokenSource();
            var responseTasks = scenarios.Select(scenario => SendAsync(
                client, scenario.DeviceId, DeviceKey, scenario.Envelope, operationCancellation.Token)).ToArray();
            HttpResponseMessage[]? responses = null;
            try
            {
                await probe.AllEntered.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var snapshot = await ReadIssue243HybridSnapshotAsync(
                    AssemblyHooks.Fixture.SqlServerConnectionString, applicationName).ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(concurrency, snapshot.Sessions);
                Assert.AreEqual(0, snapshot.OpenTransactionSessions);
                Assert.AreEqual(concurrency, snapshot.SessionApplicationLockSessions);
                Assert.AreEqual(concurrency * 5, snapshot.SessionApplicationLocks);
                Assert.AreEqual(0, snapshot.TransactionApplicationLocks);
                probe.Release();
                responses = await Task.WhenAll(responseTasks).WaitAsync(TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);
            }
            catch
            {
                probe.Release();
                await operationCancellation.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(responseTasks).ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).ConfigureAwait(false);
                throw;
            }

            Assert.IsNotNull(responses);
            try
            {
                var responseEvidence = await Task.WhenAll(responses.Select(async response =>
                    $"{(int)response.StatusCode}:{response.StatusCode}:{await response.Content.ReadAsStringAsync().ConfigureAwait(false)}"))
                    .ConfigureAwait(false);
                Assert.IsTrue(
                    responses.All(response => response.StatusCode == HttpStatusCode.Accepted),
                    string.Join(Environment.NewLine, responseEvidence));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var assertionScope = factory.Services.CreateAsyncScope();
            var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var identities = scenarios.Select(scenario => scenario.Envelope.SubmissionIdentitySha256).ToArray();
            var jobs = await db.CentralTransientValidationJobs.AsNoTracking()
                .Where(validation => identities.Contains(validation.SubmissionIdentitySha256))
                .Select(validation => validation.CentralDerivativeJobId)
                .ToArrayAsync().ConfigureAwait(false);
            Assert.AreEqual(concurrency, jobs.Length);
            Assert.AreEqual(concurrency * 5, await db.CentralDerivativeJobInputs.AsNoTracking()
                .CountAsync(input => jobs.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false));
        }
        finally
        {
            foreach (var scenario in scenarios)
            {
                await QuiesceIssue243HybridJobsAsync(scenario.CentralArtifactIds).ConfigureAwait(false);
            }
        }
    }

    private static string FindIssue243HybridRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<SqlCriticalSectionSnapshot> ReadIssue243HybridSnapshotAsync(
        string connectionString,
        string applicationName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(DISTINCT session.[session_id]),
                COUNT(DISTINCT CASE WHEN transaction_session.[session_id] IS NOT NULL THEN session.[session_id] END),
                COUNT(DISTINCT CASE WHEN resource.[resource_type] = N'APPLICATION'
                    AND resource.[request_owner_type] = N'SESSION'
                    AND resource.[request_status] = N'GRANT' THEN resource.[request_session_id] END),
                COUNT(CASE WHEN resource.[resource_type] = N'APPLICATION'
                    AND resource.[request_owner_type] = N'SESSION'
                    AND resource.[request_status] = N'GRANT' THEN 1 END),
                COUNT(DISTINCT CASE WHEN resource.[resource_type] = N'APPLICATION'
                    AND resource.[request_owner_type] = N'TRANSACTION'
                    AND resource.[request_status] = N'GRANT' THEN resource.[request_session_id] END)
            FROM [sys].[dm_exec_sessions] AS session
            LEFT JOIN [sys].[dm_tran_session_transactions] AS transaction_session
                ON transaction_session.[session_id] = session.[session_id]
            LEFT JOIN [sys].[dm_tran_locks] AS resource
                ON resource.[request_session_id] = session.[session_id]
            WHERE session.[program_name] = @application_name;
            """;
        command.Parameters.AddWithValue("@application_name", applicationName);
        await using var result = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await result.ReadAsync().ConfigureAwait(false));
        return new(
            result.GetInt32(0),
            result.GetInt32(1),
            result.GetInt32(2),
            result.GetInt32(3),
            result.GetInt32(4));
    }

    private static async Task<BlockedUpdateEvidence> ObserveIssue243HybridBlockedUpdateAsync(
        string connectionString,
        Guid artifactId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "UPDATE [CentralArtifacts] SET [StateReasonCode] = [StateReasonCode] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", artifactId);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return new(false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rowsAffected);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return new(true, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0);
        }
    }

    private sealed class BlockingGenerationReader : ICentralArtifactObjectReader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generationCalls;
        private int _verifyCalls;

        internal CentralArtifactObjectReader Inner { private get; set; } = null!;

        internal Task Entered => _entered.Task;

        internal int GenerationCalls => Volatile.Read(ref _generationCalls);

        internal int VerifyCalls => Volatile.Read(ref _verifyCalls);

        internal void Release() => _release.TrySetResult();

        public async Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _verifyCalls);
            return await Inner.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _generationCalls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await Inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken).ConfigureAwait(false);
        }

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => Inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class ConcurrentGenerationProbe(int target)
    {
        private readonly TaskCompletionSource allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;

        internal Task AllEntered => allEntered.Task;

        internal async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref entered) == target)
            {
                allEntered.TrySetResult();
            }
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void Release() => release.TrySetResult();
    }

    private sealed class ConcurrentGenerationReader(
        ICentralArtifactObjectReader inner,
        ConcurrentGenerationProbe probe) : ICentralArtifactObjectReader
    {
        private int generationCalls;

        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public async Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref generationCalls) == 1)
            {
                await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            }
            return await inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken).ConfigureAwait(false);
        }

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed record SqlCriticalSectionSnapshot(
        int Sessions,
        int OpenTransactionSessions,
        int SessionApplicationLockSessions,
        int SessionApplicationLocks,
        int TransactionApplicationLocks);

    private sealed record BlockedUpdateEvidence(bool TimedOut, double ElapsedMilliseconds, int RowsAffected);
}
