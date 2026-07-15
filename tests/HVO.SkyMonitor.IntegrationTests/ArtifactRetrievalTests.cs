using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ArtifactRetrievalTests
{
    [TestMethod]
    public async Task OwnerRetrieval_ReturnsMetadataExactBytesAndSingleRangesWithoutStorageReference()
    {
        var payload = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, payload).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        using var metadataResponse = await client.GetAsync(seeded.MetadataUri).ConfigureAwait(false);
        using var metadata = await JsonDocument.ParseAsync(
            await metadataResponse.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        using var fullResponse = await client.GetAsync(seeded.ContentUri).ConfigureAwait(false);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
        using var rangeResponse = await client.SendAsync(rangeRequest).ConfigureAwait(false);

        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        metadata.RootElement.GetProperty("ArtifactId").GetGuid().Should().Be(seeded.ArtifactId);
        metadata.RootElement.GetProperty("ChecksumSha256").GetString().Should().Be(seeded.Checksum);
        metadata.RootElement.GetRawText().Contains("minio", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        metadata.RootElement.GetRawText().Contains("StorageReference", StringComparison.Ordinal).Should().BeFalse();
        metadataResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        fullResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fullResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload);
        fullResponse.Headers.ETag!.Tag.Should().Be($"\"{seeded.Checksum}\"");
        fullResponse.Headers.GetValues(ArtifactRetrievalController.ChecksumHeader).Should().ContainSingle(seeded.Checksum);
        fullResponse.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        fullResponse.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rangeResponse.Content.Headers.ContentRange!.ToString().Should().Be("bytes 10-19/256");
        (await rangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload[10..20]);
    }

    [TestMethod]
    public async Task OwnerRetrieval_CrossOwnerAndMultipleRangeAreDeniedConsistently()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [1, 2, 3, 4]).ConfigureAwait(false);
        using var otherOwner = await CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var owner = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        using var crossOwner = await otherOwner.GetAsync(seeded.ContentUri).ConfigureAwait(false);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        rangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-0,2-2");
        using var unsupportedMultiRange = await owner.SendAsync(rangeRequest).ConfigureAwait(false);
        using var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        invalidRangeRequest.Headers.Range = new RangeHeaderValue(99, null);
        using var invalidRange = await owner.SendAsync(invalidRangeRequest).ConfigureAwait(false);

        crossOwner.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unsupportedMultiRange.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unsupportedMultiRange.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(1, 2, 3, 4);
        invalidRange.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        invalidRange.Content.Headers.ContentRange!.ToString().Should().Be("bytes */4");
    }

    [TestMethod]
    public async Task WorkerRetrieval_RequiresExactActiveLeaseContext()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [5, 6, 7, 8]).ConfigureAwait(false);
        var leaseToken = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
                && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
            db.CentralDerivativeJobs.Add(new CentralDerivativeJob
            {
                Id = jobId,
                SourceCentralArtifactId = artifact.Id,
                TargetRole = FrameArtifactRole.Preview,
                TargetRecipeVersion = "preview-v1",
                Status = CentralDerivativeJobStatus.Leased,
                AttemptCount = 1,
                MaxAttempts = 3,
                LeaseOwner = TestClients.SystemInternal.ClientId,
                LeaseToken = leaseToken,
                LeaseAcquiredAtUtc = DateTimeOffset.UtcNow,
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var client = await CreateSystemClientAsync().ConfigureAwait(false);

        using var missingContext = await client.GetAsync(seeded.ContentUri).ConfigureAwait(false);
        using var validRequest = CreateWorkerRequest(seeded.ContentUri, jobId, leaseToken);
        using var valid = await client.SendAsync(validRequest).ConfigureAwait(false);
        using var wrongRequest = CreateWorkerRequest(seeded.ContentUri, jobId, Guid.NewGuid());
        using var wrongWorker = await client.SendAsync(wrongRequest).ConfigureAwait(false);

        missingContext.StatusCode.Should().Be(HttpStatusCode.NotFound);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        (await valid.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(5, 6, 7, 8);
        wrongWorker.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)))
                .ConfigureAwait(false);
        }
        using var expiredRequest = CreateWorkerRequest(seeded.ContentUri, jobId, leaseToken);
        using var expired = await client.SendAsync(expiredRequest).ConfigureAwait(false);
        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Retrieval_ChecksumMismatchQuarantinesArtifactBeforeSendingBytes()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [9, 10, 11, 12],
            persistedChecksum: new string('A', 64)).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{seeded.Checksum}\""));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().NotContain(9);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
            && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        artifact.StateReasonCode.Should().Be("object.checksum-mismatch");
    }

    [TestMethod]
    public async Task Retrieval_MissingObjectReturnsUnavailableAndPreservesRetryableState()
    {
        var seeded = await SeedArtifactAsync(
            TestUsers.Operator.Email, [13, 14, 15, 16], putObject: false).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        using var response = await client.GetAsync(seeded.ContentUri).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
            && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        artifact.StateReasonCode.Should().Be("object.missing");
    }

    [TestMethod]
    public async Task Retrieval_ConcurrentReadersReturnExactBytes()
    {
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, payload).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync(seeded.ContentUri))).ConfigureAwait(false);
        try
        {
            foreach (var response in responses)
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Retrieval_ConcurrentCorruptionRequestsConvergeOnQuarantine()
    {
        var seeded = await SeedArtifactAsync(
            TestUsers.Operator.Email, [31, 32, 33, 34], persistedChecksum: new string('B', 64)).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync(seeded.ContentUri))).ConfigureAwait(false);
        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
            && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        artifact.StateReasonCode.Should().Be("object.checksum-mismatch");
    }

    [TestMethod]
    public async Task Retrieval_ConditionalRangeUsesApplicationChecksumEtag()
    {
        var payload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, payload).ConfigureAwait(false);
        using var client = await CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        staleRequest.Headers.Range = new RangeHeaderValue(0, 3);
        staleRequest.Headers.TryAddWithoutValidation("If-Range", $"\"{new string('0', 64)}\"");
        using var stale = await client.SendAsync(staleRequest).ConfigureAwait(false);
        using var currentRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        currentRequest.Headers.Range = new RangeHeaderValue(0, 3);
        currentRequest.Headers.TryAddWithoutValidation("If-Range", $"\"{seeded.Checksum}\"");
        using var current = await client.SendAsync(currentRequest).ConfigureAwait(false);
        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", $"W/\"{seeded.Checksum}\"");
        using var cached = await client.SendAsync(cachedRequest).ConfigureAwait(false);
        using var wildcardRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        wildcardRequest.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var wildcard = await client.SendAsync(wildcardRequest).ConfigureAwait(false);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, seeded.ContentUri);
        headRequest.Headers.Range = new RangeHeaderValue(0, 3);
        using var head = await client.SendAsync(headRequest).ConfigureAwait(false);
        using var unknownRangeRequest = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);
        unknownRangeRequest.Headers.TryAddWithoutValidation("Range", "items=0-3");
        using var unknownRange = await client.SendAsync(unknownRangeRequest).ConfigureAwait(false);

        stale.StatusCode.Should().Be(HttpStatusCode.OK);
        (await stale.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload);
        current.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await current.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload[..4]);
        cached.StatusCode.Should().Be(HttpStatusCode.NotModified);
        cached.Headers.CacheControl!.NoStore.Should().BeTrue();
        string.Join(", ", cached.Headers.Vary).Should().Be("Authorization, X-API-Key");
        wildcard.StatusCode.Should().Be(HttpStatusCode.NotModified);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.Should().Be(payload.LongLength);
        unknownRange.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknownRange.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(payload);
    }

    [TestMethod]
    public async Task RetentionRelease_BlocksActiveJobThenExpiresAndRemovesObjectAfterTerminalState()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [41, 42, 43, 44]).ConfigureAwait(false);
        Guid centralArtifactId;
        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
                && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
            centralArtifactId = artifact.Id;
            var job = new CentralDerivativeJob
            {
                SourceCentralArtifactId = artifact.Id,
                TargetRole = FrameArtifactRole.Preview,
                TargetRecipeVersion = "preview-v1",
                Status = CentralDerivativeJobStatus.Pending,
                MaxAttempts = 3,
                AvailableAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.CentralDerivativeJobs.Add(job);
            await db.SaveChangesAsync().ConfigureAwait(false);
            jobId = job.Id;
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Services.ICentralArtifactRetentionService>();
            (await retention.ReleaseAsync(centralArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(HVO.SkyMonitor.LogicHost.Services.CentralArtifactRetentionResult.Held);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure))
                .ConfigureAwait(false);
            var retention = scope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Services.ICentralArtifactRetentionService>();
            (await retention.ReleaseAsync(centralArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(HVO.SkyMonitor.LogicHost.Services.CentralArtifactRetentionResult.Released);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == centralArtifactId).ConfigureAwait(false);
            var retrieval = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetrievalService>();
            await retrieval.MarkUnavailableAsync(
                artifact, "object.checksum-mismatch", quarantine: true, CancellationToken.None).ConfigureAwait(false);
            var finalArtifact = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == centralArtifactId)
                .ConfigureAwait(false);
            finalArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
            finalArtifact.StateReasonCode.Should().Be("retention.expired");
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            var action = () => minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket("skymonitor-artifacts").WithObject(seeded.ObjectKey));
            await action.Should().ThrowAsync<Minio.Exceptions.MinioException>().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task RetentionRelease_RacingJobSchedulingNeverExpiresAnActivelyReferencedArtifact()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [51, 52, 53, 54]).ConfigureAwait(false);
            Guid centralArtifactId;
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                centralArtifactId = await db.CentralArtifacts.Where(item => item.ArtifactId == seeded.ArtifactId
                        && item.DevicePublicId == seeded.DevicePublicId)
                    .Select(item => item.Id)
                    .SingleAsync().ConfigureAwait(false);
            }
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduleTask = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var artifact = await db.CentralArtifacts.Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .SingleAsync(item => item.Id == centralArtifactId).ConfigureAwait(false);
                var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
                await scheduler.EnsureRequiredJobsAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
            });
            var releaseTask = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var retention = scope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Services.ICentralArtifactRetentionService>();
                return await retention.ReleaseAsync(centralArtifactId, CancellationToken.None).ConfigureAwait(false);
            });
            start.SetResult(true);
            await Task.WhenAll(scheduleTask, releaseTask).ConfigureAwait(false);
            var releaseResult = await releaseTask.ConfigureAwait(false);

            await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifactState = await verificationDb.CentralArtifacts.Where(item => item.Id == centralArtifactId)
                .Select(item => item.ObjectState)
                .SingleAsync().ConfigureAwait(false);
            var activeJobs = await verificationDb.CentralDerivativeJobs.CountAsync(job =>
                job.SourceCentralArtifactId == centralArtifactId
                && (job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.Leased
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure)).ConfigureAwait(false);
            if (releaseResult == CentralArtifactRetentionResult.Held)
            {
                artifactState.Should().Be(CentralArtifactObjectState.Available);
                activeJobs.Should().Be(2);
            }
            else
            {
                releaseResult.Should().Be(CentralArtifactRetentionResult.Released);
                artifactState.Should().Be(CentralArtifactObjectState.Expired);
                activeJobs.Should().Be(0);
            }
        }
    }

    [TestMethod]
    public async Task Retrieval_ClientCancellationStopsStreamingWithoutQuarantine()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [21, 22, 23, 24]).ConfigureAwait(false);
        var reader = new CancellationObjectReader();
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<HVO.SkyMonitor.LogicHost.Services.ICentralArtifactObjectReader>();
                services.AddSingleton<HVO.SkyMonitor.LogicHost.Services.ICentralArtifactObjectReader>(reader);
            }));
        using var client = factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(
            client,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, seeded.ContentUri);

        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            using var response = await responseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        (await reader.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Should().BeTrue();

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == seeded.ArtifactId
            && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
    }

    [TestMethod]
    public async Task Retrieval_StorageFailuresReturnUnavailableBeforeHeadersAndAbortAfterHeaders()
    {
        var seeded = await SeedArtifactAsync(TestUsers.Operator.Email, [61, 62, 63, 64]).ConfigureAwait(false);
        using (var beforeFactory = CreateReaderFactory(new FailureObjectReader(failDuringCopy: false)))
        using (var beforeClient = await CreateAuthenticatedClientAsync(beforeFactory).ConfigureAwait(false))
        using (var before = await beforeClient.GetAsync(seeded.ContentUri).ConfigureAwait(false))
        {
            before.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            before.Headers.CacheControl!.NoStore.Should().BeTrue();
            string.Join(", ", before.Headers.Vary).Should().Be("Authorization, X-API-Key");
        }

        using (var midstreamFactory = CreateReaderFactory(new FailureObjectReader(failDuringCopy: true)))
        using (var midstreamClient = await CreateAuthenticatedClientAsync(midstreamFactory).ConfigureAwait(false))
        {
            var action = () => midstreamClient.GetAsync(seeded.ContentUri);
            await action.Should().ThrowAsync<HttpRequestException>().ConfigureAwait(false);
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.ArtifactId == seeded.ArtifactId
            && item.DevicePublicId == seeded.DevicePublicId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
    }

    internal static async Task<HttpClient> CreateUserClientAsync(string username, string password)
    {
        var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(
            client,
            "/connect/token",
            username,
            password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<HttpClient> CreateSystemClientAsync()
    {
        var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            client,
            "/connect/token",
            TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret,
            string.Join(' ', TestClients.SystemInternal.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static HttpRequestMessage CreateWorkerRequest(Uri uri, Guid jobId, Guid leaseToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(ArtifactRetrievalController.JobIdHeader, jobId.ToString("D"));
        request.Headers.Add(ArtifactRetrievalController.LeaseTokenHeader, leaseToken.ToString("D"));
        return request;
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateReaderFactory(
        ICentralArtifactObjectReader reader)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICentralArtifactObjectReader>();
                services.AddSingleton(reader);
            }));

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        var client = factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(
            client,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    internal static async Task<SeededArtifact> SeedArtifactAsync(
        string ownerEmail,
        byte[] payload,
        string? persistedChecksum = null,
        bool putObject = true)
    {
        var fixture = AssemblyHooks.Fixture;
        var devicePublicId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var key = $"retrieval-tests/{Guid.NewGuid():N}";
        var checksum = persistedChecksum ?? Convert.ToHexString(SHA256.HashData(payload));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owner = await db.Users.SingleAsync(user => user.Email == ownerEmail).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var observatory = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = $"Retrieval {Guid.NewGuid():N}",
                TimeZoneId = "UTC",
                CreatedAtUtc = now,
                IsActive = true
            };
            var registration = new DeviceRegistration
            {
                DeviceId = $"retrieval-{Guid.NewGuid():N}",
                DevicePublicId = devicePublicId,
                ObservatoryId = observatory.Id,
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = "UTC",
                FriendlyName = "Retrieval Camera",
                OwnerUserId = owner.Id,
                OwnerDisplayName = owner.UserName!,
                OwnerConfirmationMethod = "SelfAttested",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = "hash",
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            var frame = new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = devicePublicId,
                ObservatoryId = observatory.Id,
                AgentId = registration.DeviceId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now,
                FirstReceivedAtUtc = now,
                RigId = "rig-1"
            };
            db.Observatories.Add(observatory);
            db.DeviceRegistrations.Add(registration);
            db.CentralFrames.Add(frame);
            db.CentralArtifacts.Add(new CentralArtifact
            {
                CentralFrameId = frame.Id,
                DevicePublicId = devicePublicId,
                ArtifactId = artifactId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "raw-v1",
                ManifestSchemaVersion = "v2",
                MediaType = "application/octet-stream",
                ByteLength = payload.LongLength,
                ChecksumSha256 = checksum,
                StorageReference = $"minio://skymonitor-artifacts/{key}",
                ReceivedAtUtc = now,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        if (putObject)
        {
            await PutObjectAsync(key, payload).ConfigureAwait(false);
        }
        return new SeededArtifact(
            devicePublicId,
            artifactId,
            checksum,
            key,
            new Uri($"/api/v1.0/devices/{devicePublicId:D}/artifacts/{artifactId:D}", UriKind.Relative),
            new Uri($"/api/v1.0/devices/{devicePublicId:D}/artifacts/{artifactId:D}/content", UriKind.Relative));
    }

    private static async Task PutObjectAsync(string key, byte[] payload)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        const string bucket = "skymonitor-artifacts";
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket)).ConfigureAwait(false);
        }
        await using var stream = new MemoryStream(payload, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithStreamData(stream)
            .WithObjectSize(payload.LongLength)
            .WithContentType("application/octet-stream")).ConfigureAwait(false);
    }

    internal sealed record SeededArtifact(
        Guid DevicePublicId,
        Guid ArtifactId,
        string Checksum,
        string ObjectKey,
        Uri MetadataUri,
        Uri ContentUri);

    private sealed class CancellationObjectReader : HVO.SkyMonitor.LogicHost.Services.ICentralArtifactObjectReader
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HVO.SkyMonitor.LogicHost.Services.CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => Task.FromResult(new HVO.SkyMonitor.LogicHost.Services.CentralArtifactObjectSnapshot(
                "test", "etag", artifact.ByteLength));

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public async Task CopyToAsync(
            HVO.SkyMonitor.LogicHost.Services.CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            HVO.SkyMonitor.LogicHost.Services.CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 21 }, cancellationToken).ConfigureAwait(false);
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class FailureObjectReader(bool failDuringCopy) : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => failDuringCopy
                ? Task.FromResult(new CentralArtifactObjectSnapshot("test", "etag", artifact.ByteLength))
                : Task.FromException<CentralArtifactObjectSnapshot>(new CentralArtifactStorageException());

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromResult(true);

        public async Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 61 }, cancellationToken).ConfigureAwait(false);
            throw new CentralArtifactStorageException();
        }
    }
}
