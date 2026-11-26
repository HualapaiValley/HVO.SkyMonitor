using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.Common.Configuration;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private const string LifecyclePrefix = "/api/internal/deployment/lifecycle";

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstalledCameraAdmitsAndReopensItsExactRawVersionAfterInterruptedPublication(bool color)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var rootAccess = RandomHex(12);
        var rootSecret = RandomHex(24);
        var instance = Guid.NewGuid();
        var pause = Guid.NewGuid();
        var lifecycleToken = RandomHex(24);
        var bucket = CameraAgentSiloBinding.BucketFor(instance);
        var storeId = CameraAgentSiloBinding.StoreIdFor(instance);
        var binding = new SiloStorageBinding(instance.ToString("D"), storeId, bucket);
        var container = BuildStockContainer(rootAccess, rootSecret, "on");
        await using var lease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var admin = SiloS3ObjectStore.CreateClient(Endpoint(container), rootAccess, rootSecret);
        await CreateVersionedBucketAsync(admin, bucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(container, binding, token).ConfigureAwait(false);
        await InstallBindingAsync(admin, binding, writer.Access, token).ConfigureAwait(false);
        var settings = InstalledSettings(binding, Endpoint(container), writer, pause, lifecycleToken);
        var root = Directory.CreateTempSubdirectory("silo-capture-").FullName;
        var fault = new InterruptManifestPublication();
        var priorKeyDirectory = Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable);

        try
        {
            var keyDirectory = Directory.CreateDirectory(Path.Combine(root, "settings")).FullName;
            foreach (var (key, value) in settings)
                await File.WriteAllTextAsync(Path.Combine(keyDirectory, key.Replace(":", "__", StringComparison.Ordinal)), value, token)
                    .ConfigureAwait(false);
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, keyDirectory);
            var pending = await CaptureUntilInterruptedAsync(root, settings, lifecycleToken, pause, color, fault, token).ConfigureAwait(false);
            using var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
            await restarted.InitializeAsync(services => RemoveCaptureWorkers(services, stopCamera: true), settings, root,
                color ? ColorConfiguration : null).ConfigureAwait(false);
            using var scope = restarted.CreateCameraAgentScope();
            var storage = scope.ServiceProvider.GetRequiredService<SiloInstalledStorage>();
            var reopened = await WaitForReopenAsync(storage, pending.GroupId, pending.RevisionId, token).ConfigureAwait(false);
            Assert.IsTrue(reopened.Reopenable);
            var raw = reopened.Items.Single(static item => item.Role.Kind == SiloIdentityKind.Member &&
                item.Role.Revision != SiloArtifactStorage.RawEvidenceRevision);
            var artifactId = Guid.ParseExact(raw.Role.Id, "D");
            var artifacts = scope.ServiceProvider.GetRequiredService<ICameraAgentArtifactService>();
            var result = await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            Assert.IsNotNull(result.Content);
            await using (result.Content.ConfigureAwait(false))
            {
                Assert.AreEqual(color ? "BayerRggb16" : "Mono16", result.Content.Descriptor?.Layout.PixelFormat.ToString());
                Assert.AreEqual(raw.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(result.Content, token).ConfigureAwait(false)));
            }
            var verifier = scope.ServiceProvider.GetRequiredService<ICameraAgentSiloVerification>();
            Assert.IsTrue((await verifier.VerifyAsync(token).ConfigureAwait(false)).VerifiedRoles >= 3);
            Assert.IsNotNull(raw.ServedFrom);
            await admin.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = raw.ServedFrom.Key, VersionId = raw.ServedFrom.VersionId
            }, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Gone,
                (await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false)).Status);
            Assert.IsTrue((await verifier.VerifyAsync(token).ConfigureAwait(false)).FailedRoles > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, priorKeyDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string GroupId, string RevisionId)> CaptureUntilInterruptedAsync(string root,
        IReadOnlyDictionary<string, string?> settings, string lifecycleToken, Guid pause, bool color, InterruptManifestPublication fault,
        CancellationToken token)
    {
        using var host = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
        await host.InitializeAsync(services =>
        {
            RemoveCaptureWorkers(services, stopCamera: false);
            services.AddSingleton<ISiloFaults>(fault);
        }, settings, root, color ? ColorConfiguration : null).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var storage = scope.ServiceProvider.GetRequiredService<SiloInstalledStorage>();
        using var client = host.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", lifecycleToken);
        while (true)
        {
            using var response = await client.PostAsJsonAsync(LifecyclePrefix + "/first-resume",
                new { FirstBootId = pause, ExpectedVersion = 1 }, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                break;
            }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        await fault.Interrupted.Task.WaitAsync(token).ConfigureAwait(false);
        var pending = (await storage.State.ListOpenAsync(8, token).ConfigureAwait(false)).Single();
        Assert.IsNull(await storage.Index.ReadPublicationAsync(pending.GroupId, pending.RevisionId, token).ConfigureAwait(false));
        var staged = await storage.State.ReadGroupAsync(pending.GroupId, pending.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.ManifestQualified, staged?.State);
        return pending;
    }

    private static async Task<SiloReopenResult> WaitForReopenAsync(SiloInstalledStorage storage, string group, string revision,
        CancellationToken token)
    {
        while (true)
        {
            if (storage.BindingVerified && await storage.Index.ReadPublicationAsync(group, revision, token).ConfigureAwait(false) is { State: "visible" })
                return await storage.Catalog.ReopenAsync(group, revision, token).ConfigureAwait(false);
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    private static void RemoveCaptureWorkers(IServiceCollection services, bool stopCamera)
    {
        foreach (var registration in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            (descriptor.ImplementationType?.GenericTypeArguments.Contains(typeof(CaptureDistributionService)) == true ||
                stopCamera && descriptor.ImplementationType == typeof(CameraCaptureService))).ToArray())
            services.Remove(registration);
    }

    private static string ColorConfiguration(string json)
    {
        var configuration = JsonNode.Parse(json)!;
        var sensor = configuration["rig"]!["sensor"]!;
        sensor["colorMode"] = "Color";
        sensor["pixelFormat"] = "BayerRggb16";
        sensor["responseMode"] = "BayerRaw";
        configuration["module"]!["options"]!["asi174Sensor"]!["enabled"] = false;
        return configuration.ToJsonString();
    }

    private static Dictionary<string, string?> InstalledSettings(SiloStorageBinding binding, Uri endpoint,
        (string Access, string Secret) writer, Guid pause, string token) => new(StringComparer.Ordinal)
    {
        ["LifecycleControl:Token"] = token,
        ["CameraAgent:FirstBootPause:Id"] = pause.ToString("D"),
        ["CameraAgent:Silo:StateContract"] = CameraAgentSiloBinding.StateContract,
        ["CameraAgent:Silo:Endpoint"] = endpoint.AbsoluteUri,
        ["CameraAgent:Silo:Bucket"] = binding.Bucket,
        ["CameraAgent:Silo:StoreId"] = binding.StoreId,
        ["CameraAgent:Silo:StorageBindingId"] = binding.BindingId,
        ["CameraAgent:Silo:WriterGeneration"] = "1",
        ["CameraAgent:Silo:AccessKey"] = writer.Access,
        ["CameraAgent:Silo:SecretKey"] = writer.Secret
    };

    private static async Task<(string Access, string Secret)> CreateWriterAsync(IContainer container, SiloStorageBinding binding,
        CancellationToken token)
    {
        var script = $$"""
            set -eu
            export MC_HOST_fixture="http://${MINIO_ROOT_USER}:${MINIO_ROOT_PASSWORD}@127.0.0.1:9000"
            cat > /tmp/hvo-silo-policy <<'JSON'
            {"Version":"2012-10-17","Statement":[
              {"Effect":"Allow","Action":["s3:GetBucketLocation","s3:GetBucketVersioning","s3:ListBucket","s3:ListBucketVersions"],"Resource":["arn:aws:s3:::{{binding.Bucket}}"]},
              {"Effect":"Allow","Action":["s3:GetObject","s3:GetObjectVersion","s3:PutObject","s3:DeleteObjectVersion"],"Resource":["arn:aws:s3:::{{binding.Bucket}}/*"]},
              {"Effect":"Deny","Action":["s3:PutObject","s3:DeleteObject","s3:DeleteObjectVersion"],"Resource":["arn:aws:s3:::{{binding.Bucket}}/meta/*"]}]}
            JSON
            mcli admin accesskey create fixture/ --policy /tmp/hvo-silo-policy --name hvo-writer-g1 --json
            """;
        var result = await container.ExecAsync(["/bin/sh", "-c", script], token).ConfigureAwait(false);
        Assert.AreEqual(0L, result.ExitCode, "The stock Silo writer credential could not be created.");
        using var document = JsonDocument.Parse(result.Stdout);
        return (document.RootElement.GetProperty("accessKey").GetString()!, document.RootElement.GetProperty("secretKey").GetString()!);
    }

    private static async Task InstallBindingAsync(AmazonS3Client admin, SiloStorageBinding binding, string accessKey, CancellationToken token)
    {
        var store = new SiloS3ObjectStore(admin,
            new(binding.StoreId, binding.Bucket, SiloStoreClass.Local, SiloRetrieval.Online, PersistenceQualified: true), TimeSpan.FromSeconds(30));
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.MarkerFormat, instanceId = binding.InstanceId, storeId = binding.StoreId,
            bucket = binding.Bucket, storageBindingId = binding.BindingId, stateContract = CameraAgentSiloBinding.StateContract
        });
        var fence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.FenceFormat, instanceId = binding.InstanceId, storageBindingId = binding.BindingId,
            writerGeneration = 1, writerAccessKeyId = accessKey
        });
        Assert.IsTrue((await store.PutCreateOnlyAsync(Put(CameraAgentSiloBinding.MarkerKey, marker), token).ConfigureAwait(false)).Created);
        Assert.IsTrue((await store.PutCreateOnlyAsync(Put(CameraAgentSiloBinding.FenceKey, fence), token).ConfigureAwait(false)).Created);
    }

    private static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private sealed class InterruptManifestPublication : ISiloFaults
    {
        public TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hit(SiloFaultPoint point)
        {
            if (point is not SiloFaultPoint.AfterManifestQualified &&
                !(Interrupted.Task.IsCompleted && point == SiloFaultPoint.AfterSegmentSpooled)) return;
            Interrupted.TrySetResult();
            throw new IOException("Interrupted after exact manifest qualification.");
        }
    }
}
