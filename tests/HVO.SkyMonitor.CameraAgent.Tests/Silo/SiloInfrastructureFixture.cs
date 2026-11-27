global using HVO.SkyMonitor.CameraAgent.Tests.Silo;

using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>Gives hostless CameraAgent tests a disposable versioned store across process-scope restarts.</summary>
[TestClass]
[TestProperty("Area", "Hosting")]
public sealed class SiloFixtureAssemblyCleanup
{
    [AssemblyCleanup]
    public static void ReleaseTemporaryRoots() => SiloInfrastructureFixture.ReleaseTemporaryRoots();
}

internal static class SiloInfrastructureFixture
{
    private static readonly Dictionary<string, Bucket> Buckets = new(StringComparer.Ordinal);
    private static readonly List<DirectoryInfo> TemporaryRoots = [];
    private static readonly Lock Gate = new();

    internal static void ReleaseTemporaryRoots()
    {
        foreach (var directory in TemporaryRoots)
        {
            if (directory.Exists) directory.Delete(recursive: true);
        }
    }

    internal static IServiceCollection AddSiloTestInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        const string rootSetting = "CameraAgent:RawIngressRoot";
        if (configuration.GetSection("CameraAgent:Silo").Exists())
            return services.AddCameraAgentInfrastructure(configuration);

        var root = configuration[rootSetting];
        Bucket bucket;
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            {
                var temporary = Directory.CreateTempSubdirectory("hvo-silo-test-");
                TemporaryRoots.Add(temporary);
                root = temporary.FullName;
            }
            else
            {
                root = Path.GetFullPath(root);
            }
            foreach (var unused in Buckets.Keys.Where(path => !Directory.Exists(path)).ToArray())
                Buckets.Remove(unused);
            if (!Buckets.TryGetValue(root, out var existing))
            {
                existing = new Bucket();
                Buckets.Add(root, existing);
            }
            bucket = existing;
        }

        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CameraAgent:CentralIntegration:Mode"] = "Disabled" })
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
                new SiloInstalledStore(installed, bucket.Store, _ => ValueTask.FromResult(true)), payloadSpool: null);
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

    internal static async ValueTask SettleRawAdmissionsAsync(IServiceProvider services, CancellationToken token)
        => await SettleRawAdmissionsAsync(services.GetRequiredService<RawCaptureIngress>(), token).ConfigureAwait(false);

    private static async ValueTask SettleRawAdmissionsAsync(RawCaptureIngress ingress, CancellationToken token)
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

    internal static async ValueTask<SiloExactLocator> ReadLocalLocatorAsync(
        IServiceProvider services, Guid artifactId, string revision, CancellationToken token)
    {
        var role = await services.GetRequiredService<SiloInstalledStorage>().Index
            .ReadRoleAsync(new(SiloIdentityKind.Member, artifactId.ToString("D"), revision), token).ConfigureAwait(false);
        Assert.IsNotNull(role);
        return Assert.ContainsSingle(role.Locators.Where(static copy => copy.Eligible && copy.CopyState == "active")).Locator;
    }

    private sealed class Bucket
    {
        public Bucket()
        {
            var instance = Guid.NewGuid();
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

        public IReadOnlyDictionary<string, string?> Configuration { get; }

        public SiloTestStore Store { get; }

        private void Put(string key, object definition)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(definition);
            var request = new SiloPutRequest(key, bytes.Length, SiloHash.Sha256(bytes),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)), new Dictionary<string, string>());
            _ = Store.PutCreateOnlyAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }
}
