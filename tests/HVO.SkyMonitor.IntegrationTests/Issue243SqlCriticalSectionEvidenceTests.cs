using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class Issue243SqlCriticalSectionEvidenceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task RetentionDelete_HoldsSerializableTransactionAndSessionLockAcrossMinioIo()
    {
        var fixture = AssemblyHooks.Fixture;
        var seeded = await ArtifactRetrievalTests.SeedArtifactAsync(
            TestUsers.Operator.Email,
            Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray()).ConfigureAwait(false);
        Guid centralArtifactId;
        await using (var seedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            centralArtifactId = await db.CentralArtifacts
                .Where(item => item.ArtifactId == seeded.ArtifactId && item.DevicePublicId == seeded.DevicePublicId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
        }

        var applicationName = $"HVO.SkyMonitor.Issue243.{Guid.NewGuid():N}";
        var subjectConnection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var factory = CreateFactory(fixture, subjectConnection, handler);
        _ = factory.Services;

        var started = Stopwatch.GetTimestamp();
        SqlCriticalSectionSnapshot? criticalSection = null;
        BlockedUpdateEvidence? blockedUpdate = null;
        CentralArtifactRetentionResult? releaseResult = null;
        using var operationCancellation = new CancellationTokenSource();
        var releaseTask = Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(centralArtifactId, operationCancellation.Token).ConfigureAwait(false);
        });

        try
        {
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            criticalSection = await ReadSnapshotAsync(
                fixture.SqlServerConnectionString,
                applicationName).ConfigureAwait(false);
            criticalSection.Sessions.Should().BeGreaterThanOrEqualTo(2);
            criticalSection.OpenTransactionSessions.Should().BeGreaterThanOrEqualTo(1);
            criticalSection.SessionApplicationLocks.Should().BeGreaterThanOrEqualTo(1);

            blockedUpdate = await ObserveBlockedUpdateAsync(
                fixture.SqlServerConnectionString,
                centralArtifactId).ConfigureAwait(false);
            blockedUpdate.TimedOut.Should().BeTrue();
            blockedUpdate.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(900);
        }
        finally
        {
            handler.Release();
            releaseResult = await EvidenceTaskCleanup.AwaitAsync(
                releaseTask, operationCancellation, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }

        releaseResult.Should().Be(CentralArtifactRetentionResult.Released);
        var elapsed = Stopwatch.GetElapsedTime(started);
        await using (var verificationScope = factory.Services.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == centralArtifactId)
                .ConfigureAwait(false);
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
            artifact.StateReasonCode.Should().Be("retention.expired");
        }

        var minio = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        var stat = () => minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(seeded.ObjectKey));
        await stat.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>().ConfigureAwait(false);

        var repositoryRoot = FindRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(Issue243SqlCriticalSectionEvidenceTests),
            typeof(CentralArtifactRetentionService)).ConfigureAwait(false);
        var output = Path.Combine(
            repositoryRoot, "TestResults", "issue-243", source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(output);
        var evidence = new
        {
            Schema = "hvo-issue-243-sql-critical-section-v1",
            Source = source,
            Workload = new
            {
                Operation = "CentralArtifactRetentionService.ReleaseAsync",
                PayloadBytes = 4096,
                MinioBoundary = "DELETE paused after serializable SQL mutation",
                CompetingWriters = 1,
                CompetingWriterCommandTimeoutSeconds = 1
            },
            Observation = new
            {
                TotalOperationMilliseconds = elapsed.TotalMilliseconds,
                criticalSection!.Sessions,
                criticalSection.OpenTransactionSessions,
                criticalSection.SessionApplicationLocks,
                CompetingWriterTimedOut = blockedUpdate!.TimedOut,
                CompetingWriterMilliseconds = blockedUpdate.ElapsedMilliseconds
            },
            Correctness = "The object was removed only after the barrier released, SQL converged to Expired/retention.expired, and the conflicting update made no mutation.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "sql-retention-critical-section.json"),
            evidence,
            EvidenceJsonOptions).ConfigureAwait(false);

        TestContext.WriteLine(
            "issue243 sql critical section: elapsed_ms={0:F3}, sessions={1}, open_transaction_sessions={2}, session_application_locks={3}",
            elapsed.TotalMilliseconds,
            criticalSection?.Sessions ?? 0,
            criticalSection?.OpenTransactionSessions ?? 0,
            criticalSection?.SessionApplicationLocks ?? 0);
    }

    public TestContext TestContext { get; set; } = null!;

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(
        IntegrationTestFixture fixture,
        string connectionString,
        BlockingDeleteHandler handler)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));

    private static async Task<SqlCriticalSectionSnapshot> ReadSnapshotAsync(
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

    private static async Task<BlockedUpdateEvidence> ObserveBlockedUpdateAsync(
        string connectionString,
        Guid centralArtifactId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "UPDATE [CentralArtifacts] SET [StateReasonCode] = [StateReasonCode] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", centralArtifactId);
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

    private static string FindRepositoryRoot()
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
