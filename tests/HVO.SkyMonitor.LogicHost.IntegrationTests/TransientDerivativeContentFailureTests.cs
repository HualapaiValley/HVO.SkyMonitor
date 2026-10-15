using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class TransientDerivativeContentFailureTests
{
    private const string Bucket = "skymonitor-artifacts";
    private static readonly byte[] Payload = [61, 62, 63, 64];

    [TestMethod]
    public async Task InitialLookup_SupportedStorageFailuresReturnSanitizedUnavailableAndReleaseLock()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key);
        using var factory = CreateFactory(store);
        using var client = await CreateClientAsync(factory).ConfigureAwait(false);
        var failures = Enum.GetValues<ObjectStoreFailureKind>()
            .Select(kind => (Exception)new ObjectStoreException(kind, seed.Key, new IOException("private-provider-detail")))
            .Concat([new CentralArtifactStorageException("private-storage-detail"), new IOException("private-io-detail")]);
        foreach (var failure in failures)
        {
            store.StatFailure = failure;
            using var response = await client.GetAsync(seed.Path).ConfigureAwait(false);
            await AssertUnavailableAsync(response).ConfigureAwait(false);
            await AssertReleasedAndUnchangedAsync(seed).ConfigureAwait(false);
        }
        store.StatCalls.Should().Be(Enum.GetValues<ObjectStoreFailureKind>().Length + 2);
        store.ReadCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task UnrelatedLookupFailure_IsNotNormalizedAndReleasesLock()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var failure = new InvalidOperationException("programming-error");
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key)
        {
            StatFailure = failure
        };
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var retrieval = new CentralTransientDerivativeRetrievalService(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), store);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "event-content-test-admin"), new Claim("account_type", "User"),
            new Claim("scope", "api.admin")], CanonicalCredentialClaims.BearerAuthenticationType));
        var lookup = () => retrieval.GetAsync(admin, seed.EventId, seed.DerivativeId, CancellationToken.None);
        (await lookup.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false)).Which.Should().BeSameAs(failure);
        await AssertReleasedAndUnchangedAsync(seed).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StreamingFailures_ReturnUnavailableBeforeHeadersAndAbortAfterHeaders()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key);
        using var factory = CreateFactory(store);
        using var client = await CreateClientAsync(factory).ConfigureAwait(false);
        store.ReadFailure = new IOException("private-read-detail");
        using (var response = await client.GetAsync(seed.Path).ConfigureAwait(false))
        {
            await AssertUnavailableAsync(response).ConfigureAwait(false);
            response.Headers.ETag.Should().BeNull();
            response.Headers.Contains(ArtifactRetrievalController.ChecksumHeader).Should().BeFalse();
            response.Content.Headers.ContentDisposition.Should().BeNull();
        }
        await AssertReleasedAndUnchangedAsync(seed).ConfigureAwait(false);
        store.ReadFailure = null;
        store.TruncateRead = true;
        var request = () => client.GetAsync(seed.Path);
        await request.Should().ThrowAsync<HttpRequestException>().ConfigureAwait(false);
        await AssertReleasedAndUnchangedAsync(seed).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestCancellation_PreservesCancellationAndReleasesApplicationLock(bool duringStreaming)
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key)
        {
            WaitForCancellation = !duringStreaming,
            WaitForReadCancellation = duringStreaming
        };
        using var factory = CreateFactory(store);
        using var client = await CreateClientAsync(factory).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = client.GetAsync(seed.Path, cancellation.Token);
        await store.FaultStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await ((Func<Task>)(async () => { using var response = await request.ConfigureAwait(false); }))
            .Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        await store.FaultCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // The server unwinds asynchronously after the client observes cancellation.
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), seed.StorageReference, CancellationToken.None)
            .ConfigureAwait(false);
        objectLock.Should().NotBeNull();
        store.ReadCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task DeniedAndCrossScopeCallers_NeverReachInitialStorageLookup()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key)
        {
            StatFailure = new IOException("private-foreign-key")
        };
        using var factory = CreateFactory(store);
        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(seed.Path).ConfigureAwait(false))
        {
            response.IsSuccessStatusCode.Should().BeFalse();
        }
        using (var viewer = await CreateClientAsync(factory, viewer: true).ConfigureAwait(false))
        using (var response = await viewer.GetAsync(seed.Path).ConfigureAwait(false))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().BeEmpty();
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            owner.Id, owner.Email!, "event-content-scope", ApiKeyAccessLevel.Read, seed.ObservatoryId,
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None).ConfigureAwait(false);
        using var scoped = factory.CreateClient();
        scoped.DefaultRequestHeaders.Add("X-API-Key", key.PlaintextKey);
        using var denied = await scoped.GetAsync(seed.Path).ConfigureAwait(false);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await denied.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().BeEmpty();
        store.StatCalls.Should().Be(0);
        store.ReadCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task ExistingContentOutcomes_PreserveBytesRangeConditionalIntegrityAndExpiry()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var store = new FaultingStore(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(), seed.Key);
        using var factory = CreateFactory(store);
        using var client = await CreateClientAsync(factory).ConfigureAwait(false);
        using var full = await client.GetAsync(seed.Path).ConfigureAwait(false);
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        (await full.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(Payload);
        full.Headers.ETag!.Tag.Should().Be($"\"{seed.Checksum}\"");
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, seed.Path);
        rangeRequest.Headers.Range = new RangeHeaderValue(1, 2);
        using var range = await client.SendAsync(rangeRequest).ConfigureAwait(false);
        range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        range.Content.Headers.ContentRange!.ToString().Should().Be("bytes 1-2/4");
        (await range.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(Payload[1..3]);
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, seed.Path);
        conditionalRequest.Headers.IfNoneMatch.Add(full.Headers.ETag);
        using var conditional = await client.SendAsync(conditionalRequest).ConfigureAwait(false);
        conditional.StatusCode.Should().Be(HttpStatusCode.NotModified);
        using var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, seed.Path);
        invalidRangeRequest.Headers.Range = new RangeHeaderValue(99, 100);
        using var invalidRange = await client.SendAsync(invalidRangeRequest).ConfigureAwait(false);
        invalidRange.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        store.WrongGeneration = true;
        using var integrity = await client.GetAsync(seed.Path).ConfigureAwait(false);
        integrity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertReleasedAndUnchangedAsync(seed).ConfigureAwait(false);
        store.WrongGeneration = false;
        using var missing = await client.GetAsync(new Uri(seed.Path.ToString().Replace(
            seed.DerivativeId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal), UriKind.Relative))
            .ConfigureAwait(false);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralTransientDerivativeOutputIntents.Where(item => item.DerivativeId == seed.DerivativeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ObjectState, CentralArtifactObjectState.Expired))
            .ConfigureAwait(false);
        var calls = store.StatCalls;
        using var expired = await client.GetAsync(seed.Path).ConfigureAwait(false);
        expired.StatusCode.Should().Be(HttpStatusCode.Gone);
        store.StatCalls.Should().Be(calls);
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>().ConfigureAwait(false);
        problem!.Status.Should().Be(503);
        problem.Title.Should().Be("Derivative content is temporarily unavailable.");
        problem.Detail.Should().BeNull();
        problem.Extensions.Should().BeEmpty();
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().NotContain("private-").And.NotContain("object://").And.NotContain("events/");
    }

    private static async Task AssertReleasedAndUnchangedAsync(Seed seed)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var objectLock = await CentralObjectApplicationLock.TryAcquireAsync(
            db, seed.StorageReference, CancellationToken.None).ConfigureAwait(false);
        objectLock.Should().NotBeNull("lookup and response completion must release the content lock");
        var intent = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .SingleAsync(item => item.DerivativeId == seed.DerivativeId).ConfigureAwait(false);
        intent.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        intent.StorageETag.Should().Be(seed.Generation);
        intent.StateReasonCode.Should().BeNull();
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(IObjectStore store)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton(store);
        }));

    private static async Task<HttpClient> CreateClientAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, bool viewer = false)
    {
        var client = factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(client, "/connect/token",
            viewer ? TestUsers.Viewer.Username : TestUsers.Operator.Username,
            viewer ? TestUsers.Viewer.Password : TestUsers.Operator.Password,
            TestClients.WebUI.ClientId, string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<Seed> SeedAsync()
    {
        var first = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, Payload).ConfigureAwait(false);
        var second = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, Payload).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registrations = await db.CentralFrames.Where(item => item.DevicePublicId == first.DevicePublicId ||
            item.DevicePublicId == second.DevicePublicId).Select(item => new
            {
                item.RegistrationId,
                item.ObservatoryId,
                item.DevicePublicId
            }).ToArrayAsync().ConfigureAwait(false);
        var firstRegistration = registrations.Single(item => item.DevicePublicId == first.DevicePublicId);
        var secondRegistration = registrations.Single(item => item.DevicePublicId == second.DevicePublicId);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = firstRegistration.ObservatoryId,
            UserId = viewer.Id,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(db, fixture,
            registrationIds: fixture.Extraction.OrderedSources.Select((_, index) => index % 2 == 0
                ? firstRegistration.RegistrationId : secondRegistration.RegistrationId).ToArray()).ConfigureAwait(false);
        _ = await new CentralTransientEventPersistence(db).AppendAsync(fixture.Request with
        {
            CentralDerivativeJobId = seeded.JobId
        }, CancellationToken.None).ConfigureAwait(false);
        var current = await db.CentralTransientEventCurrent.SingleAsync(item =>
            item.LatestEventVersionId == fixture.Event.EventVersionId).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = seeded.Artifacts[0].Id,
            TargetRole = FrameArtifactRole.Combined,
            TargetRecipeVersion = "event-content-test-v1",
            TargetVariant = "event-content-test",
            RecipeName = "event-content-test",
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = hash,
            ExpectedRecipeIdentitySha256 = hash,
            RequestIdentitySha256 = hash,
            Status = CentralDerivativeJobStatus.Completed,
            MaxAttempts = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };
        var derivativeJob = new CentralTransientDerivativeJob
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            CentralTransientEventId = current.CentralTransientEventId,
            SourceEventVersionId = fixture.Event.EventVersionId,
            RequestIdentitySha256 = hash,
            ProducerSchemaVersion = "transient-derivative-producer-v1",
            ProducerName = "event-content-test",
            ProducerVersion = "v1",
            RecipeIdentitySha256 = hash,
            OptionsIdentitySha256 = hash,
            CanonicalRequestJson = "{}",
            CanonicalRequestByteLength = 2,
            CanonicalRequestSha256 = ProcessingIdentity.ComputePayloadSha256("{}"u8.ToArray()),
            ExpectedOutputCount = 5,
            CreatedAtUtc = now
        };
        var key = $"events/private-{Guid.NewGuid():N}.bin";
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        await using var payload = new MemoryStream(Payload);
        await store.PutAsync(Bucket, key, payload, Payload.Length, "application/octet-stream", CancellationToken.None)
            .ConfigureAwait(false);
        var stat = await store.StatAsync(Bucket, key, CancellationToken.None).ConfigureAwait(false);
        var intent = new CentralTransientDerivativeOutputIntent
        {
            DerivativeJob = derivativeJob,
            CentralDerivativeJobId = job.Id,
            CentralTransientEventId = current.CentralTransientEventId,
            Kind = TransientDerivativeKind.Reconstruction,
            DerivativeId = Guid.NewGuid(),
            ArtifactId = Guid.NewGuid(),
            ArtifactRole = FrameArtifactRole.Combined,
            ArtifactVariant = "event-content-test",
            MediaType = "application/octet-stream",
            ByteLength = Payload.Length,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(Payload)),
            OutputIdentitySha256 = hash,
            StorageReference = $"object://{Bucket}/{key}",
            StorageETag = stat.Generation,
            ObjectState = CentralArtifactObjectState.Available,
            CreatedAtUtc = now,
            ObjectVerifiedAtUtc = now
        };
        var derivative = new CentralTransientDerivativeRecord
        {
            DerivativeJob = derivativeJob,
            CentralDerivativeJobId = job.Id,
            CentralTransientEventId = current.CentralTransientEventId,
            SourceEventVersionId = fixture.Event.EventVersionId,
            OutputIntent = intent,
            OutputIntentId = intent.Id,
            DerivativeId = intent.DerivativeId,
            CreatedUtc = now,
            Kind = intent.Kind,
            ArtifactId = intent.ArtifactId,
            ArtifactRole = intent.ArtifactRole,
            ArtifactVariant = intent.ArtifactVariant,
            MediaType = intent.MediaType,
            ByteLength = intent.ByteLength,
            ArtifactChecksumSha256 = intent.ChecksumSha256,
            RecipeIdentitySha256 = hash,
            OptionsIdentitySha256 = hash,
            OutputIdentitySha256 = hash,
            LimitationsJson = "[]"
        };
        db.CentralTransientDerivatives.Add(derivative);
        await db.SaveChangesAsync().ConfigureAwait(false);
        intent.CommittedAtUtc = now;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(current.CentralTransientEventId, intent.DerivativeId, firstRegistration.ObservatoryId, key,
            intent.StorageReference, stat.Generation, intent.ChecksumSha256);
    }

    private sealed record Seed(Guid EventId, Guid DerivativeId, Guid ObservatoryId, string Key,
        string StorageReference, string Generation, string Checksum)
    {
        public Uri Path => new($"/api/v1.0/transient-events/{EventId:D}/derivatives/{DerivativeId:D}/content", UriKind.Relative);
    }

    private sealed class FaultingStore(IObjectStore inner, string targetKey) : IObjectStore
    {
        public Exception? StatFailure { get; set; }
        public Exception? ReadFailure { get; set; }
        public bool TruncateRead { get; set; }
        public bool WrongGeneration { get; set; }
        public bool WaitForReadCancellation { get; set; }
        public bool WaitForCancellation { get; set; }
        public int StatCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public TaskCompletionSource FaultStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FaultCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ObjectStoreObjectMetadata> StatAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            if (key != targetKey) return await inner.StatAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            StatCalls++;
            if (WaitForCancellation)
            {
                FaultStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { FaultCancelled.TrySetResult(); throw; }
            }
            if (StatFailure is not null) throw StatFailure;
            var stat = await inner.StatAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return WrongGeneration ? stat with { Generation = "private-wrong-generation" } : stat;
        }

        public async Task ReadAsync(string bucket, string key, string? generation,
            Func<Stream, CancellationToken, Task> reader, CancellationToken cancellationToken)
        {
            if (key == targetKey)
            {
                ReadCalls++;
                if (WaitForReadCancellation)
                {
                    FaultStarted.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { FaultCancelled.TrySetResult(); throw; }
                }
                if (ReadFailure is not null) throw ReadFailure;
                if (TruncateRead)
                {
                    await using var partial = new MemoryStream(Payload[..1]);
                    await reader(partial, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            await inner.ReadAsync(bucket, key, generation, reader, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
            => inner.BucketExistsAsync(bucket, cancellationToken);
        public Task PutAsync(string bucket, string key, Stream content, long contentLength, string contentType,
            CancellationToken cancellationToken) => inner.PutAsync(bucket, key, content, contentLength, contentType, cancellationToken);
        public Task CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken cancellationToken)
            => inner.CopyAsync(bucket, sourceKey, destinationKey, cancellationToken);
        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
            => inner.DeleteAsync(bucket, key, cancellationToken);
        public IAsyncEnumerable<ObjectStoreItem> ListAsync(string bucket, string prefix, CancellationToken cancellationToken,
            string? startAfter = null) => inner.ListAsync(bucket, prefix, cancellationToken, startAfter);
    }
}
