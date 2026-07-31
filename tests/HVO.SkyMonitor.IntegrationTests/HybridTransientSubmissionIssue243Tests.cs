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

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class HybridTransientSubmissionIntegrationTests
{
    private static readonly JsonSerializerOptions Issue243HybridJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task GenerationCheck_HoldsSerializableTransactionAndArtifactLocksAcrossObjectIo()
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
                Assert.IsGreaterThanOrEqualTo(1, snapshot.OpenTransactionSessions);
                Assert.IsGreaterThanOrEqualTo(1, snapshot.TransactionApplicationLocks);
                blocker = await ObserveIssue243HybridBlockedUpdateAsync(
                    AssemblyHooks.Fixture.SqlServerConnectionString, scenario.CentralArtifactIds[0]).ConfigureAwait(false);
                Assert.IsTrue(blocker.TimedOut);
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
                Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
                Assert.AreEqual(5, reader.VerifyCalls);
                Assert.AreEqual(5, reader.GenerationCalls);
                var repositoryRoot = FindIssue243HybridRepositoryRoot();
                var source = await EvidenceSourceIdentity.CaptureAsync(
                    repositoryRoot,
                    typeof(HybridTransientSubmissionIntegrationTests),
                    typeof(CentralTransientSubmissionService)).ConfigureAwait(false);
                var output = Path.Combine(
                    repositoryRoot, "TestResults", "issue-243", source.OutputDirectoryName, source.RunId);
                Directory.CreateDirectory(output);
                var evidence = new
                {
                    Schema = "hvo-issue-243-hybrid-generation-critical-section-v1",
                    Source = source,
                    Workload = new
                    {
                        Sources = scenario.CentralArtifactIds.Count,
                        GenerationChecks = reader.GenerationCalls,
                        Verifications = reader.VerifyCalls,
                        CompetingWriterTimeoutSeconds = 1,
                        ObjectBoundary = "First generation HEAD paused after SQL transaction and locks were acquired"
                    },
                    Observation = new
                    {
                        ElapsedMilliseconds = elapsed.TotalMilliseconds,
                        snapshot!.Sessions,
                        snapshot.OpenTransactionSessions,
                        snapshot.TransactionApplicationLocks,
                        CompetingWriterTimedOut = blocker!.TimedOut,
                        CompetingWriterMilliseconds = blocker.ElapsedMilliseconds,
                        ResponseStatus = response.StatusCode.ToString()
                    },
                    Correctness = "All five object verifications and all five generation checks completed before one submission was accepted.",
                    RecordedAtUtc = DateTimeOffset.UtcNow
                };
                await EvidenceSourceIdentity.WriteJsonAsync(
                    Path.Combine(output, "hybrid-generation-critical-section.json"),
                    evidence,
                    Issue243HybridJsonOptions).ConfigureAwait(false);
                TestContext.WriteLine(
                    "issue243 hybrid generation: elapsed_ms={0:F3}, sessions={1}, open_transactions={2}, transaction_application_locks={3}, blocker_ms={4:F3}",
                    elapsed.TotalMilliseconds,
                    snapshot!.Sessions,
                    snapshot.OpenTransactionSessions,
                    snapshot.TransactionApplicationLocks,
                    blocker!.ElapsedMilliseconds);
            }
        }
        finally
        {
            await QuiesceIssue243HybridJobsAsync(scenario.CentralArtifactIds).ConfigureAwait(false);
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
        return new(result.GetInt32(0), result.GetInt32(1), result.GetInt32(2));
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
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return new(false, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return new(true, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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

    private sealed record SqlCriticalSectionSnapshot(
        int Sessions,
        int OpenTransactionSessions,
        int TransactionApplicationLocks);

    private sealed record BlockedUpdateEvidence(bool TimedOut, double ElapsedMilliseconds);
}
