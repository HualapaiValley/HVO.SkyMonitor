global using HVO.SkyMonitor.CameraAgent.Tests.Silo;

using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

internal static class SiloInfrastructureFixture
{
    internal const string VersionSnapshotFileName = "silo-fixture-versions.json";

    // A root can be registered before setup creates it and reopened after its host is disposed.
    // Keep its bucket and cached provider until assembly cleanup, when all tests have finished.
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Bucket> Buckets = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ServiceProvider> ProductStores = new(StringComparer.Ordinal);
    private static readonly List<string> TemporaryRoots = [];

    internal static void ReleaseTemporaryRoots()
    {
        lock (Gate)
        {
            foreach (var store in ProductStores.Values)
                store.Dispose();
            ProductStores.Clear();
            Buckets.Clear();
            foreach (var root in TemporaryRoots)
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            TemporaryRoots.Clear();
        }
    }

    internal static Bucket GetOrCreateBucket(string root)
    {
        var key = StorageRootIdentity.Key(root);
        lock (Gate)
        {
            if (!Buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                Buckets.Add(key, bucket);
            }
            return bucket;
        }
    }

    internal static IServiceCollection AddSiloTestInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        const string rootSetting = "CameraAgent:RawIngressRoot";
        if (configuration.GetSection("CameraAgent:Silo").Exists())
            return services.AddCameraAgentInfrastructure(configuration);

        var root = configuration[rootSetting];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            StorageRootIdentity.AreEqual(root, Path.GetTempPath()))
        {
            lock (Gate)
            {
                root = Directory.CreateTempSubdirectory("hvo-silo-test-").FullName;
                TemporaryRoots.Add(root);
            }
        }
        root = StorageRootIdentity.CanonicalPath(root);
        var bucket = GetOrCreateBucket(root);

        var settings = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { [rootSetting] = root })
            .AddInMemoryCollection(bucket.Configuration).Build();
        services.TryAddSingleton<IConfiguration>(settings);
        services.AddCameraAgentInfrastructure(settings);
        services.AddSingleton(bucket.Store);
        services.AddSingleton(provider =>
        {
            var installed = provider.GetRequiredService<SiloInstalledSettings>();
            var options = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>();
            var clock = provider.GetRequiredService<TimeProvider>();
            var storage = new SiloInstalledStorage(installed, options, clock, provider.GetService<ISiloFaults>(),
                provider.GetService<Func<ISiloObjectStore, ISiloObjectStore>>(),
                new SiloInstalledStore(installed, bucket.Store, _ => ValueTask.FromResult(true)), payloadSpool: null,
                capacity: new InMemoryStoreCapacity());
            try
            {
                storage.VerifyReadinessAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                return storage;
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        });
        return services;
    }

    // The in-memory store's capacity must not depend on the mount hosting its temporary journal and spool.
    private sealed class InMemoryStoreCapacity : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string path) => new(100 * SiloDiskBudget.GiB, 80 * SiloDiskBudget.GiB);
    }

    internal static async ValueTask SettleRawAdmissionsAsync(IServiceProvider services, CancellationToken token)
        => await SettleRawAdmissionsAsync(services.GetRequiredService<RawCaptureIngress>(), token).ConfigureAwait(false);

    internal static async ValueTask SettleRawAdmissionsAsync(RawCaptureIngress ingress, CancellationToken token)
    {
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        while (true)
        {
            var result = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
            Assert.IsNull(result.Status, "Silo raw admissions must settle before reading archived evidence.");
            if (!result.More) return;
        }
    }

    internal static async ValueTask<RawCaptureReceipt?> AcceptAndSettleAsync(
        this IRawCaptureIngress ingress, CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken token)
    {
        var receipt = await ingress.AcceptAsync(configuration, submission, token).ConfigureAwait(false);
        if (receipt is not null)
            await SettleRawAdmissionsAsync((RawCaptureIngress)ingress, token).ConfigureAwait(false);
        return receipt;
    }

    internal static async ValueTask SettleViewChangesAsync(IServiceProvider services, CancellationToken token)
    {
        var views = services.GetRequiredService<SqliteCenteredViews>();
        while ((await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false)).More)
        {
        }
    }

    internal static async Task<byte[]> ReadArtifactBytesAsync(IServiceProvider services, Guid artifactId, CancellationToken token)
    {
        var result = await services.GetRequiredService<ICameraAgentArtifactService>()
            .OpenContentAsync(artifactId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
        Assert.IsNotNull(result.Content);
        await using var content = result.Content.ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await result.Content.CopyToAsync(buffer, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    internal static ServiceProvider OpenStore(string rawIngressRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSiloTestInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CameraAgent:RawIngressRoot"] = rawIngressRoot }).Build());
        return services.BuildServiceProvider();
    }

    // Several journal fixtures construct and reopen stores directly rather than host services.
    internal static IServiceProvider ProductStore(string root)
    {
        root = StorageRootIdentity.CanonicalPath(root);
        var key = StorageRootIdentity.Key(root);
        lock (Gate)
        {
            if (!ProductStores.TryGetValue(key, out var provider))
            {
                provider = OpenStore(root);
                ProductStores.Add(key, provider);
            }
            return provider;
        }
    }

    internal static SiloArtifactStorage ProductStorage(string root)
        => ProductStore(root).GetRequiredService<SiloArtifactStorage>();

    internal static CameraAgentArtifactService OpenArtifactReader(
        IOptions<CameraAgentHostOptions> options, SqliteCaptureProcessingStore store)
        => new(options, store, new CameraAgentPreviewEncoder(), silo: ProductStorage(options.Value.RawIngressRoot));

    internal static SqliteCaptureProcessingStore OpenProcessingStore(IOptions<CameraAgentHostOptions> options)
        => new(options, silo: ProductStorage(options.Value.RawIngressRoot));

    internal static ValueTask PublishRawFixtureAsync(SiloArtifactStorage storage, RawIngressJournalEntry entry,
        ArtifactManifestV2 manifest, ReadOnlyMemory<byte> pixels, CancellationToken token)
        => storage.PublishAsync(RawCaptureIngress.CreateSiloDraft(entry, manifest, "raw-1"),
            (member, readToken) => SiloArtifactStorage.OpenMemoryAsync(
                member.Role == "raw" ? pixels : entry.ManifestJson, readToken), token);

    internal static async ValueTask<DurableProcessingOutput> PublishMetadataFixtureAsync(
        SiloArtifactStorage storage, ReconstructionDescriptor descriptor, string producer, ProcessingProduct product,
        CancellationToken token)
    {
        var output = CaptureProcessingPersistence.DescribeMetadataProduct(descriptor, producer, product);
        await storage.PublishProcessingAsync(output.ArtifactId.ToString("N"), descriptor,
            [new(output.ArtifactId, output.Artifact.Role.ToString(), output.Artifact.MediaType, output.Artifact.ChecksumSha256,
                product.Payload, output.EvidenceJson, output.ContentIdentitySha256, output.OutputIdentitySha256, producer,
                output.RecipeIdentitySha256)], token).ConfigureAwait(false);
        return output;
    }

    internal static async ValueTask PublishArtifactAsync(
        SiloArtifactStorage storage, ReconstructionDescriptor descriptor, string revision, ReadOnlyMemory<byte> payload,
        string evidenceRevision, byte[] evidence, CancellationToken token)
    {
        var artifact = descriptor.Artifact;
        var member = SiloArtifactStorage.Member(artifact.ArtifactId, revision, artifact.Role == FrameArtifactRole.Raw ? "raw" : artifact.Role.ToString(),
            SiloRetentionClass.Full, artifact.MediaType, payload.Length, artifact.ChecksumSha256);
        var metadata = SiloArtifactStorage.Member(artifact.ArtifactId, evidenceRevision, "fixture-evidence",
            SiloRetentionClass.Metadata, "application/json", evidence.Length, SiloHash.Sha256(evidence));
        var draft = new SiloGroupDraft("fixture-artifact", artifact.ArtifactId.ToString("N"), "1",
            descriptor.Timing.DurableIngressUtc, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc,
            [member, metadata], [], new([]));
        await storage.PublishAsync(draft,
            (item, ct) => SiloArtifactStorage.OpenMemoryAsync(item == member ? payload : evidence, ct), token)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<SiloExactLocator> ReadLocalLocatorAsync(
        IServiceProvider services, Guid artifactId, string revision, CancellationToken token)
    {
        var role = await services.GetRequiredService<SiloInstalledStorage>().Index
            .ReadRoleAsync(new(SiloIdentityKind.Member, artifactId.ToString("D"), revision), token).ConfigureAwait(false);
        Assert.IsNotNull(role);
        return Assert.ContainsSingle(role.Locators.Where(static copy => copy.Eligible && copy.CopyState == "active")).Locator;
    }

    internal static async ValueTask<byte[]> ReadProductBytesAsync(
        IServiceProvider services, DurableProcessingOutput output, CancellationToken token)
    {
        var locator = await ReadLocalLocatorAsync(services, output.ArtifactId, output.OutputIdentitySha256, token)
            .ConfigureAwait(false);
        return services.GetRequiredService<SiloTestStore>().Read(locator.Key, locator.VersionId);
    }

    internal static ValueTask<RetainedMember> OpenMemberFixtureAsync(
        string root, DurableProcessingOutput output, CancellationToken token)
        => OpenMemberFixtureAsync(root, output.ArtifactId, output.OutputIdentitySha256, token);

    internal static async ValueTask<RetainedMember> OpenMemberFixtureAsync(
        string root, Guid artifactId, string revision, CancellationToken token)
    {
        var services = ProductStore(root);
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var role = await storage.Index.ReadRoleAsync(new(SiloIdentityKind.Member, artifactId.ToString("D"), revision), token)
            .ConfigureAwait(false);
        Assert.IsNotNull(role);
        var locator = Assert.ContainsSingle(role.Locators.Where(static copy => copy.Eligible && copy.CopyState == "active")).Locator;
        return new(services, storage, new(SiloIdentityKind.Member, role.Role.Id, role.Role.Revision, role.Sha256, role.Length), locator);
    }

    internal sealed class RetainedMember(IServiceProvider services, SiloInstalledStorage storage,
        SiloObjectIdentity identity, SiloExactLocator locator)
    {
        private readonly SiloTestStore _objects = services.GetRequiredService<SiloTestStore>();

        internal void SetUnavailable(bool unavailable) => _objects.SetVersionUnavailable(locator, unavailable);

        // Repeating the same byte flip restores the original version bytes.
        internal void FlipByte() => _objects.Damage(locator);

        internal ValueTask ExpireAsync(CancellationToken token)
            => ExpireLocalMemberAsync(services, Guid.Parse(identity.Id), identity.Revision, token);

        internal async ValueTask RecordCopyStateAsync(SiloAvailabilityState state, CancellationToken token)
        {
            await storage.ArchiveJournal.AppendAsync(
                [new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, identity, state,
                    services.GetRequiredService<TimeProvider>().GetUtcNow(), "observed test copy state", locator)],
                Guid.NewGuid().ToString("N"), token).ConfigureAwait(false);
            await storage.Catalog.CatchUpAsync(await storage.State.ReadWriterEvidenceAsync(token).ConfigureAwait(false),
                null, token).ConfigureAwait(false);
        }
    }

    internal static async ValueTask ExpireLocalMemberAsync(
        IServiceProvider services, Guid artifactId, string revision, CancellationToken token)
    {
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var key = new SiloRoleKey(SiloIdentityKind.Member, artifactId.ToString("D"), revision);
        var role = await storage.Index.ReadRoleAsync(key, token).ConfigureAwait(false);
        Assert.IsNotNull(role);
        var locator = Assert.ContainsSingle(role.Locators.Where(static copy => copy.Eligible && copy.CopyState == "active")).Locator;
        var identity = new SiloObjectIdentity(key.Kind, key.Id, key.Revision, role.Sha256, role.Length);
        var intent = Guid.NewGuid().ToString("N");
        await storage.ArchiveJournal.AppendAsync(
            [new SiloExpiryIntentRecord(intent, SiloExpiryScope.Role, identity, "last-copy-policy:operator",
                [new(locator, "local")], "retention elapsed", LastCopy: true)], intent + ":intent", token).ConfigureAwait(false);
        services.GetRequiredService<SiloTestStore>().Delete(locator);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await storage.ArchiveJournal.AppendAsync(
            [new SiloExpiryDispositionRecord(intent, locator, SiloExpiryOutcome.Deleted, now, "NoSuchVersion"),
                new SiloAvailabilityRecord(SiloAvailabilityScope.Role, identity, SiloAvailabilityState.PolicyExpired,
                    now, "last copy expired", IntentId: intent)], intent + ":settlement", token).ConfigureAwait(false);
        await storage.Catalog.CatchUpAsync(await storage.State.ReadWriterEvidenceAsync(token).ConfigureAwait(false),
            null, token).ConfigureAwait(false);
        Assert.AreEqual(SiloDisposition.PolicyExpired, (await storage.Index.ReadRoleAsync(key, token).ConfigureAwait(false))?.Disposition);
    }

    internal sealed class Bucket
    {
        public Bucket() : this(Guid.NewGuid())
        {
        }

        internal Bucket(Guid instance)
        {
            Instance = instance;
            var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
                CameraAgentSiloBinding.BucketFor(instance));
            const string accessKey = "testfixturewriter";
            Configuration = new Dictionary<string, string?>
            {
                ["CameraAgent:Silo:StateContract"] = CameraAgentSiloBinding.StateContract,
                ["CameraAgent:Silo:StoreId"] = binding.StoreId,
                ["CameraAgent:Silo:Bucket"] = binding.Bucket,
                ["CameraAgent:Silo:StorageBindingId"] = binding.BindingId,
                ["CameraAgent:Silo:WriterGeneration"] = "1",
                ["CameraAgent:Silo:Endpoint"] = "http://127.0.0.1",
                ["CameraAgent:Silo:AccessKey"] = accessKey,
                ["CameraAgent:Silo:SecretKey"] = "testfixturesecret"
            };
            Store = new SiloTestStore(binding.StoreId, binding.Bucket);
            Put(CameraAgentSiloBinding.MarkerKey, new
            {
                format = CameraAgentSiloBinding.MarkerFormat,
                instanceId = binding.InstanceId,
                storeId = binding.StoreId,
                bucket = binding.Bucket,
                storageBindingId = binding.BindingId,
                stateContract = CameraAgentSiloBinding.StateContract
            });
            Put(CameraAgentSiloBinding.FenceKey, new
            {
                format = CameraAgentSiloBinding.FenceFormat,
                instanceId = binding.InstanceId,
                storageBindingId = binding.BindingId,
                writerGeneration = 1,
                writerAccessKeyId = accessKey
            });
        }

        public Guid Instance { get; }

        public IReadOnlyDictionary<string, string?> Configuration { get; }

        public SiloTestStore Store { get; }

        internal void Configure(IDictionary<string, string?> overrides)
        {
            foreach (var setting in Configuration) overrides[setting.Key] = setting.Value;
        }

        internal void RegisterInstalledStorage(IServiceCollection services)
        {
            services.AddSingleton(Store);
            services.AddSingleton(provider =>
            {
                var installed = provider.GetRequiredService<SiloInstalledSettings>();
                return new SiloInstalledStorage(
                    installed, provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
                    provider.GetRequiredService<TimeProvider>(), provider.GetService<ISiloFaults>(),
                    provider.GetService<Func<ISiloObjectStore, ISiloObjectStore>>(),
                    new SiloInstalledStore(installed, Store, _ => ValueTask.FromResult(true)), payloadSpool: null);
            });
        }

        private void Put(string key, object definition)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(definition);
            var request = new SiloPutRequest(key, bytes.Length, SiloHash.Sha256(bytes),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)), new Dictionary<string, string>());
            _ = Store.PutCreateOnlyAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }
}
