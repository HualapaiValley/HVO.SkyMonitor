using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralTransientEventPersistenceIntegrationTests
{
    private static readonly JsonSerializerOptions Issue243PayloadJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task PayloadReleaseDelete_HasNoTransactionAndAllowsWriterDuringMinioIo()
    {
        await using var database = CreateDatabase("Issue243PayloadRelease");
        var publishedKeys = new List<string>();
        var fixtureMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            await database.Context.CentralDerivativeJobs.Where(item => item.Id == seeded.JobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Completed)
                    .SetProperty(item => item.CompletedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var principal = CreateOwnerPrincipal("issue-243-release", admin: true);
            var reviewed = await new CentralTransientReviewService(
                    database.Context,
                    new CentralTransientEventVersionAppender(database.Context),
                    TimeProvider.System)
                .ReviewAsync(
                    principal,
                    current.CentralTransientEventId,
                    current.RowVersion,
                    $"issue-243-review-{Guid.NewGuid():N}",
                    new CentralTransientReviewRequest(
                        current.ActiveAssessmentId,
                        TransientReviewDisposition.Confirmed,
                        null,
                        ["human.release-approved"]),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CentralTransientReviewMutationStatus.Applied, reviewed.Status);
            await database.Context.CentralDerivativeJobs.Where(item => item.RecipeName == CentralTransientDerivativeRuntime.RecipeName)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var reviewedCurrent = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            if (!await fixtureMinio.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-artifacts"))
                    .ConfigureAwait(false))
            {
                await fixtureMinio.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-artifacts"))
                    .ConfigureAwait(false);
            }
            foreach (var artifact in seeded.Artifacts)
            {
                var objectKey = artifact.StorageReference["s3://skymonitor-artifacts/".Length..];
                var payload = fixture.Payloads[artifact.ArtifactId];
                await using var stream = new MemoryStream(payload, writable: false);
                await fixtureMinio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey)
                    .WithStreamData(stream)
                    .WithObjectSize(payload.LongLength)
                    .WithContentType("application/octet-stream")).ConfigureAwait(false);
                publishedKeys.Add(objectKey);
            }

            var applicationName = $"HVO.SkyMonitor.Issue243.PayloadRelease.{Guid.NewGuid():N}";
            var subjectConnection = new SqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            await using var subjectDb = CreateContext(subjectConnection);
            using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
            var minio = new MinioClient()
                .WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
                .Build();
            var service = new CentralTransientPayloadReleaseService(
                subjectDb,
                new CentralArtifactRetentionReferences(subjectDb),
                ObjectStoreTestClient.Create(minio),
                Options.Create(new CentralTransientPayloadReleaseOptions
                {
                    Enabled = true,
                    InitialRetryDelay = TimeSpan.FromMilliseconds(1),
                    MaximumRetryDelay = TimeSpan.FromMilliseconds(10)
                }),
                TimeProvider.System);
            using var operationCancellation = new CancellationTokenSource();
            var started = Stopwatch.GetTimestamp();
            var releaseTask = service.ReleaseAsync(
                principal,
                current.CentralTransientEventId,
                reviewedCurrent.RowVersion,
                $"issue-243-release-{Guid.NewGuid():N}",
                operationCancellation.Token);
            SqlCriticalSectionSnapshot? snapshot = null;
            BlockedUpdateEvidence? blocker = null;
            CentralTransientPayloadReleaseResult? released = null;
            try
            {
                await handler.Entered.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                snapshot = await ReadIssue243PayloadSnapshotAsync(
                    database.ConnectionString, applicationName).ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(2, snapshot.Sessions);
                Assert.AreEqual(0, snapshot.OpenTransactionSessions);
                Assert.IsGreaterThanOrEqualTo(2, snapshot.SessionApplicationLocks);
                blocker = await ObserveIssue243PayloadBlockedUpdateAsync(
                    database.ConnectionString, seeded.Artifacts.Select(static artifact => artifact.Id).ToArray())
                    .ConfigureAwait(false);
                Assert.IsFalse(blocker.TimedOut);
            }
            finally
            {
                handler.Release();
                released = await EvidenceTaskCleanup.AwaitAsync(
                    releaseTask, operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.IsNotNull(released);
            Assert.AreEqual(CentralTransientPayloadReleaseStatus.Accepted, released.Status);
            Assert.AreEqual(CentralTransientPayloadReleaseState.Pending, released.Response!.State);
            await Task.Delay(20).ConfigureAwait(false);
            await using (var recoveryDb = CreateContext(database.ConnectionString))
            {
                var recovery = new CentralTransientPayloadReleaseService(
                    recoveryDb,
                    new CentralArtifactRetentionReferences(recoveryDb),
                    ObjectStoreTestClient.Create(fixtureMinio),
                    Options.Create(new CentralTransientPayloadReleaseOptions
                    {
                        Enabled = true,
                        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
                        MaximumRetryDelay = TimeSpan.FromMilliseconds(10),
                        MaximumRetryCount = 5
                    }),
                    TimeProvider.System);
                Assert.IsTrue(await recovery.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false));
            }
            subjectDb.ChangeTracker.Clear();
            var expectedArtifactIds = seeded.Artifacts.Select(static item => item.Id).Order().ToArray();
            var expiredArtifactIds = await subjectDb.CentralArtifacts
                .Where(item => expectedArtifactIds.Contains(item.Id)
                    && item.ObjectState == CentralArtifactObjectState.Expired)
                .Select(item => item.Id)
                .OrderBy(static id => id)
                .ToArrayAsync().ConfigureAwait(false);
            CollectionAssert.AreEquivalent(expectedArtifactIds, expiredArtifactIds);
            foreach (var objectKey in publishedKeys)
            {
                Func<Task> stat = () => fixtureMinio.StatObjectAsync(new StatObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey));
                await stat.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>().ConfigureAwait(false);
            }

            var repositoryRoot = FindIssue243PayloadRepositoryRoot();
            var source = await EvidenceSourceIdentity.CaptureAsync(
                repositoryRoot,
                typeof(CentralTransientEventPersistenceIntegrationTests),
                typeof(CentralTransientPayloadReleaseService)).ConfigureAwait(false);
            var output = Path.Combine(
                repositoryRoot, "TestResults", "issue-243", source.OutputDirectoryName, source.RunId);
            Directory.CreateDirectory(output);
            var evidence = new
            {
                Schema = "hvo-issue-243-transient-payload-release-v1",
                Source = source,
                Workload = new
                {
                    Artifacts = expectedArtifactIds.Length,
                    PublishedObjects = publishedKeys.Count,
                    CompetingWriterTimeoutSeconds = 1,
                    MinioBoundary = "First DELETE paused after reservation commit while parent/object application locks remain held"
                },
                Observation = new
                {
                    ElapsedMilliseconds = elapsed.TotalMilliseconds,
                    snapshot!.Sessions,
                    snapshot.OpenTransactionSessions,
                    snapshot.SessionApplicationLocks,
                    CompetingWriterCompleted = !blocker!.TimedOut,
                    CompetingWriterMilliseconds = blocker.ElapsedMilliseconds,
                    ExpiredArtifacts = expiredArtifactIds.Length,
                    RemovedObjects = publishedKeys.Count
                },
                Correctness = "No attributed SQL transaction remained open during DELETE, the competing writer completed, stale finalization scheduled a retry, and a fresh processor converged every target and object.",
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
            await EvidenceSourceIdentity.WriteJsonAsync(
                Path.Combine(output, "transient-payload-release-critical-section.json"),
                evidence,
                Issue243PayloadJsonOptions).ConfigureAwait(false);

            TestContext.WriteLine(
                "issue243 payload release: elapsed_ms={0:F3}, sessions={1}, open_transactions={2}, session_locks={3}, blocker_ms={4:F3}, expired={5}",
                elapsed.TotalMilliseconds,
                snapshot!.Sessions,
                snapshot.OpenTransactionSessions,
                snapshot.SessionApplicationLocks,
                blocker!.ElapsedMilliseconds,
                expiredArtifactIds.Length);
        }
        finally
        {
            foreach (var objectKey in publishedKeys)
            {
                await fixtureMinio.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey)).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static string FindIssue243PayloadRepositoryRoot()
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

    private static async Task<SqlCriticalSectionSnapshot> ReadIssue243PayloadSnapshotAsync(
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
                    AND resource.[request_status] = N'GRANT' THEN resource.[request_session_id] END)
            FROM [sys].[dm_exec_sessions] AS session
            LEFT JOIN [sys].[dm_tran_session_transactions] AS transaction_session
                ON transaction_session.[session_id] = session.[session_id]
            LEFT JOIN [sys].[dm_tran_locks] AS resource
                ON resource.[request_session_id] = session.[session_id]
            WHERE session.[program_name] = @application_name;
            """;
        command.Parameters.AddWithValue("@application_name", applicationName);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only generated parameter names are interpolated; every artifact identity remains parameterized.")]
    private static async Task<BlockedUpdateEvidence> ObserveIssue243PayloadBlockedUpdateAsync(
        string connectionString,
        Guid[] artifactIds)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        var parameterNames = artifactIds.Select((_, index) => $"@id{index}").ToArray();
        command.CommandText = $"UPDATE [CentralArtifacts] SET [StateReasonCode] = [StateReasonCode] WHERE [Id] IN ({string.Join(",", parameterNames)});";
        for (var index = 0; index < artifactIds.Length; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], artifactIds[index]);
        }
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

    private sealed class BlockingDeleteHandler : DelegatingHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record SqlCriticalSectionSnapshot(
        int Sessions,
        int OpenTransactionSessions,
        int SessionApplicationLocks);

    private sealed record BlockedUpdateEvidence(bool TimedOut, double ElapsedMilliseconds);
}
