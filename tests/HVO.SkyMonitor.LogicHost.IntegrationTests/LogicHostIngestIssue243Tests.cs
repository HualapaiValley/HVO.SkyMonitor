using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class LogicHostIngestPerformanceTests
{
    private static readonly JsonSerializerOptions Issue243JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task DuplicateW2Verification_HoldsObjectFenceWithoutSqlTransactionOrBlockedWriter()
    {
        var fixture = AssemblyHooks.Fixture;
        var workload = CreateWorkload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16);
        await SeedWorkloadAsync(fixture, workload).ConfigureAwait(false);
        var upload = new UploadAllocator(workload, workload).Create(workload, 1).Single();
        using var initialClient = fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            initialClient,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            string.Join(' ', TestClients.SystemCameraAgent.Scopes)).ConfigureAwait(false);
        SetAuthorization(initialClient, token.AccessToken);
        var initial = await SendUploadAsync(
            initialClient, upload, workload.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Accepted, initial.StatusCode, initial.Body);
        AssertAcknowledgement(upload, initial.Body);

        Guid centralArtifactId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            centralArtifactId = await db.CentralArtifacts
                .Where(item => item.ArtifactId == upload.Manifest.Descriptor.Artifact.ArtifactId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
        }

        var applicationName = $"HVO.SkyMonitor.Issue249.Duplicate.{Guid.NewGuid():N}";
        var subjectConnection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        using var handler = new BlockingGetHandler { InnerHandler = new SocketsHttpHandler() };
        using var factory = CreateIssue243IngestFactory(fixture, subjectConnection, handler);
        using var client = factory.CreateClient();
        SetAuthorization(client, token.AccessToken);
        using var operationCancellation = new CancellationTokenSource();
        var started = Stopwatch.GetTimestamp();
        var duplicateTask = SendUploadAsync(client, upload, workload.Payload, operationCancellation.Token);
        SqlCriticalSectionSnapshot? snapshot = null;
        BlockedUpdateEvidence? blocker = null;
        UploadResponse? duplicate = null;
        try
        {
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            snapshot = await ReadIssue243IngestSnapshotAsync(
                fixture.SqlServerConnectionString, applicationName).ConfigureAwait(false);
            snapshot.Sessions.Should().BeGreaterThanOrEqualTo(2);
            snapshot.OpenTransactionSessions.Should().Be(0);
            snapshot.SessionApplicationLocks.Should().BeGreaterThanOrEqualTo(1);
            blocker = await ObserveIssue243IngestBlockedUpdateAsync(
                fixture.SqlServerConnectionString, centralArtifactId).ConfigureAwait(false);
            blocker.TimedOut.Should().BeFalse();
        }
        finally
        {
            handler.Release();
            duplicate = await EvidenceTaskCleanup.AwaitAsync(
                duplicateTask, operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.IsNotNull(duplicate);
        Assert.AreEqual(HttpStatusCode.Accepted, duplicate.StatusCode, duplicate.Body);
        AssertAcknowledgement(upload, duplicate.Body);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == centralArtifactId)
                .ConfigureAwait(false);
            Assert.AreEqual(CentralArtifactObjectState.Available, artifact.ObjectState);
            Assert.AreEqual(workload.ChecksumSha256, artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(workload.Payload.LongLength, artifact.ByteLength);
        }

        var repositoryRoot = GetRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(LogicHostIngestPerformanceTests),
            typeof(ApplicationDbContext)).ConfigureAwait(false);
        var output = Path.Combine(
            repositoryRoot, "TestResults", "issue-249", source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(output);
        var evidence = new
        {
            Schema = "hvo-issue-249-sql-ingest-critical-section-v1",
            Source = source,
            Workload = new
            {
                Id = "W2",
                Width = 3096,
                Height = 2080,
                Format = CameraPixelFormat.BayerRggb16.ToString(),
                PayloadBytes = workload.Payload.LongLength,
                Operation = "multipart duplicate streamed MinIO verification",
                CompetingWriterTimeoutSeconds = 1
            },
            Observation = new
            {
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                snapshot!.Sessions,
                snapshot.OpenTransactionSessions,
                snapshot.SessionApplicationLocks,
                CompetingWriterTimedOut = blocker!.TimedOut,
                CompetingWriterMilliseconds = blocker.ElapsedMilliseconds
            },
            Correctness = new
            {
                DuplicateAcknowledged = true,
                workload.ChecksumSha256,
                ByteLength = workload.Payload.LongLength,
                FinalObjectState = CentralArtifactObjectState.Available.ToString()
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "sql-duplicate-ingest-candidate-critical-section.json"),
            evidence,
            Issue243JsonOptions).ConfigureAwait(false);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateIssue243IngestFactory(
        IntegrationTestFixture fixture,
        string connectionString,
        BlockingGetHandler handler)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(IntegrationTestFixture.ExternalS3Endpoint)
                .WithCredentials(IntegrationTestFixture.ExternalS3AccessKey, IntegrationTestFixture.ExternalS3SecretKey)
                .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
                .Build());
            ObjectStoreTestClient.Replace(services);
        }));

    private static async Task<SqlCriticalSectionSnapshot> ReadIssue243IngestSnapshotAsync(
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

    private static async Task<BlockedUpdateEvidence> ObserveIssue243IngestBlockedUpdateAsync(
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

    private sealed class BlockingGetHandler : DelegatingHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
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
