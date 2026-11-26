using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
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
    private static readonly Uri VerifyHistoryRoute = new(LifecyclePrefix + "/silo/verify-history", UriKind.Relative);
    private static readonly Uri VerifyInstallationRoute = new("/api/internal/owner-bootstrap/installation-verification", UriKind.Relative);

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task InstalledCameraAdmitsAndReopensItsExactRawVersionAfterInterruptedPublication(bool color, bool centered)
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
        var priorSettingsPath = Environment.GetEnvironmentVariable(DeploymentLocalSettings.PathVariable);
        Func<string, string>? configureCamera = centered ? json => CenteredConfiguration(json, color) : color ? ColorConfiguration : null;

        try
        {
            var keyDirectory = Directory.CreateDirectory(Path.Combine(root, "settings")).FullName;
            foreach (var (key, value) in settings)
                await File.WriteAllTextAsync(Path.Combine(keyDirectory, key.Replace(":", "__", StringComparison.Ordinal)), value, token)
                    .ConfigureAwait(false);
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, keyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, Path.Combine(root, DeploymentLocalSettings.FileName));
            var pending = await CaptureUntilInterruptedAsync(root, settings, lifecycleToken, pause, configureCamera, fault, token).ConfigureAwait(false);
            using var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
            await restarted.InitializeAsync(centered ? null : services => RemoveCaptureWorkers(services, stopCamera: true),
                settings, root, configureCamera).ConfigureAwait(false);
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
            using var verifier = restarted.CreateCameraAgentClient();
            using (var denied = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false))
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            verifier.DefaultRequestHeaders.Add("X-HVO-Installation-Token", lifecycleToken);
            using (var verification = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false))
            {
                verification.EnsureSuccessStatusCode();
                var history = await verification.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
                Assert.IsTrue(history?.VerifiedRoles >= 3);
            }
            if (centered)
            {
                var views = scope.ServiceProvider.GetRequiredService<ICenteredViewReadIndex>();
                var camera = new CameraKey(restarted.DeviceId, "installed-virtual");
                CenteredViewCurrent? current;
                while ((current = await views.ReadLastLiveAsync(camera, token).ConfigureAwait(false)) is null)
                    await Task.Delay(100, token).ConfigureAwait(false);
                var view = current.View;
                Assert.IsNotNull(await views.ReadRevisionAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false));
                var exact = await storage.Catalog.ReopenAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false);
                Assert.IsTrue(exact.Reopenable);
                Assert.IsTrue(exact.Items.Where(static item => item.Required).All(item => item.ServedFrom?.StoreId == storeId));
                var archiveId = SiloCenteredViews.LowerHex(CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
                {
                    executionId = view.Base.Definition.Reference.ExecutionId,
                    nodeId = "LocalStorage"
                })));
                Assert.IsTrue((await WaitForReopenAsync(storage, archiveId, "1", token).ConfigureAwait(false)).Reopenable);
            }
            Assert.IsNotNull(raw.ServedFrom);
            await admin.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = raw.ServedFrom.Key,
                VersionId = raw.ServedFrom.VersionId
            }, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Gone,
                (await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false)).Status);
            using var failed = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var failedHistory = await failed.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
            Assert.IsTrue(failedHistory?.FailedRoles > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, priorKeyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, priorSettingsPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string GroupId, string RevisionId)> CaptureUntilInterruptedAsync(string root,
        IReadOnlyDictionary<string, string?> settings, string lifecycleToken, Guid pause, Func<string, string>? configureCamera,
        InterruptManifestPublication fault,
        CancellationToken token)
    {
        using var host = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
        await host.InitializeAsync(services =>
        {
            RemoveCaptureWorkers(services, stopCamera: false);
            services.AddSingleton<ISiloFaults>(fault);
        }, settings, root, configureCamera).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var storage = scope.ServiceProvider.GetRequiredService<SiloInstalledStorage>();
        var loader = scope.ServiceProvider.GetRequiredService<ICameraAgentConfigurationLoader>();
        var configuration = await loader.LoadAsync(token).ConfigureAwait(false);
        scope.ServiceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration).DisposeSteps();
        using var client = host.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", lifecycleToken);
        using (var readiness = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            readiness.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                using var response = await client.GetAsync(VerifyInstallationRoute, readiness.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    response.EnsureSuccessStatusCode();
                    break;
                }
                await Task.Delay(50, readiness.Token).ConfigureAwait(false);
            }
        }
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
        configuration["rig"]!.AsObject().Remove("readout");
        var options = configuration["module"]!["options"]!;
        options["asi174Sensor"]!["enabled"] = false;
        options["asi178Sensor"] = new JsonObject { ["enabled"] = true };
        return configuration.ToJsonString();
    }

    private static string CenteredConfiguration(string json, bool color)
    {
        var configuration = JsonNode.Parse(color ? ColorConfiguration(json) : json)!;
        configuration["centeredExposure"] = new JsonObject
        {
            ["cameraId"] = "installed-virtual",
            ["executionVersion"] = CenteredExposureProfile.CurrentExecutionVersion
        };
        var moduleOptions = configuration["module"]!["options"]!.AsObject();
        moduleOptions.Remove("transientScenario");
        moduleOptions.Remove("cloudScenario");
        if (!color)
        {
            configuration["rig"]!["readout"] = JsonNode.Parse("""
                {
                  "roi": { "x": 0, "y": 0, "width": 64, "height": 48 },
                  "binX": 1, "binY": 1, "binningAlgorithm": "IdentityV1",
                  "pixelFormat": "Mono16", "sampleDepthBits": 12, "containerDepthBits": 16,
                  "packing": "ByteAligned", "storedCodeTransform": "RightAlignedV1", "levelCodeSpace": "NativeSample",
                  "blackLevel": 64, "whiteLevel": 4095, "strideBytes": 128, "byteOrder": "LittleEndian", "cfaPattern": "None"
                }
                """);
        }
        var pipeline = configuration["pipeline"]!;
        var steps = pipeline["steps"]!.AsArray();
        var calibration = steps.Single(step => step!["id"]!.GetValue<string>() == "Calibration")!.DeepClone();
        calibration["publication"] = new JsonObject { ["persistence"] = "durable-local" };
        calibration["options"]!["outputVariant"] = "qualified-linear";
        var scene = steps.Single(step => step!["id"]!.GetValue<string>() == "ProjectedScene")!.DeepClone();
        scene["publication"] = new JsonObject { ["persistence"] = "durable-local" };
        var storage = steps.Single(step => step!["id"]!.GetValue<string>() == "LocalStorage")!.DeepClone();
        storage["dependsOn"] = new JsonArray("Calibration");
        var options = storage["options"]!.AsObject();
        options.Remove("policies");
        options["storageRoot"] = Path.Combine(options["storageRoot"]!.GetValue<string>(), "archive");
        pipeline["steps"] = new JsonArray(calibration, scene, storage);
        return configuration.ToJsonString();
    }

    private static Dictionary<string, string?> InstalledSettings(SiloStorageBinding binding, Uri endpoint,
        (string Access, string Secret) writer, Guid pause, string token) => new(StringComparer.Ordinal)
        {
            ["LifecycleControl:Token"] = token,
            ["InstallationVerification:Token"] = token,
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
            format = CameraAgentSiloBinding.MarkerFormat,
            instanceId = binding.InstanceId,
            storeId = binding.StoreId,
            bucket = binding.Bucket,
            storageBindingId = binding.BindingId,
            stateContract = CameraAgentSiloBinding.StateContract
        });
        var fence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.FenceFormat,
            instanceId = binding.InstanceId,
            storageBindingId = binding.BindingId,
            writerGeneration = 1,
            writerAccessKeyId = accessKey
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
