using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The CameraAgent container limits an installation records with the instance, renders into its Compose file, and
/// holds a re-run to: the defaults, operator-set limits, an instance an earlier installer completed without a record,
/// and a host whose memory is below the limit.
/// </summary>
public sealed partial class InstallerFlowTests
{
    private const long ResourceGibibyte = 1024L * 1024 * 1024;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_DefaultLimits_AreRecordedRenderedAndKeptByAnIdenticalReRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("4b7f0a3e-8c21-4d55-9e6a-1f2b3c4d5e61"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None);

            Assert.AreEqual(new ContainerResourceLimits(2m, 4 * ResourceGibibyte, 256, false, false), manifest.Resources);
            StringAssert.Contains(
                await File.ReadAllTextAsync(composePath),
                "    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n",
                StringComparison.Ordinal);
            var retainedManifest = await File.ReadAllTextAsync(manifestPath);

            await InstallResourceRequestAsync(request, runner, owner);
            Assert.AreEqual(retainedManifest, await File.ReadAllTextAsync(manifestPath));

            // Pinning a limit, even to the default, is a different request from the one this instance recorded.
            var pinned = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { Cpus = 2m }, runner, owner));
            StringAssert.Contains(pinned.Message, "different immutable inputs", StringComparison.Ordinal);
            Assert.AreEqual(retainedManifest, await File.ReadAllTextAsync(manifestPath));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_OperatorLimits_AreRecordedAsOperatorSetAndAReRunMustRepeatThem()
    {
        var bundle = RequireProductionCatalogBundle();
        var (defaultRequest, root) = ResourceRequest(bundle, Guid.Parse("6d1e2f3a-4b5c-4d7e-8f90-a1b2c3d4e5f6"));
        var request = defaultRequest with { Cpus = 3.5m, Memory = "6144m" };
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None);

            Assert.AreEqual(new ContainerResourceLimits(3.5m, 6 * ResourceGibibyte, 256, true, true), manifest.Resources);
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(first.ConfigRoot, "compose", "compose.yml")),
                "    cpus: \"3.5\"\n    mem_limit: 6G\n    pids_limit: 256\n",
                StringComparison.Ordinal);

            // The same limits written another way are the same request; leaving them out is not.
            await InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner);
            var omitted = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(defaultRequest, runner, owner));
            StringAssert.Contains(omitted.Message, "different immutable inputs", StringComparison.Ordinal);
            Assert.AreEqual(
                manifest,
                await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_CompletedInstanceRecordedWithoutLimits_ReRunKeepsTheOriginalLimits()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("8a9b0c1d-2e3f-4a5b-9c6d-7e8f9a0b1c2d"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            // An earlier installer recorded no limits and wrote the original lines.
            var earlier = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            Assert.IsTrue(earlier.Remove("resources"));
            var earlierManifest = earlier.ToJsonString();
            await File.WriteAllTextAsync(manifestPath, earlierManifest);

            await InstallResourceRequestAsync(request, runner, owner);

            Assert.AreEqual(earlierManifest, await File.ReadAllTextAsync(manifestPath));
            Assert.IsNull((await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).Resources);
            StringAssert.Contains(
                await File.ReadAllTextAsync(composePath),
                "      start_period: 20s\n    cpus: \"1\"\n    mem_limit: 1G\n    pids_limit: 256\n    logging:",
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_DockerHostWithLessMemoryThanTheLimit_InstallsWithTheLimitItAskedFor()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value)
        {
            DockerInfo = SupportedDockerInfo.Replace("\"MemTotal\":8442572800", "\"MemTotal\":2147483648", StringComparison.Ordinal)
        };
        var owner = new InstallerOwnerClient();
        try
        {
            var installed = await InstallResourceRequestAsync(request, runner, owner);

            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(
                Path.Combine(installed.InstanceRoot, "instance-manifest.json"), CancellationToken.None);
            Assert.AreEqual(CameraAgentContainerResources.Default, manifest.Resources);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    private static string RequireProductionCatalogBundle()
    {
        // Environment-gated like every installer flow contract: the real catalog bundle is restored by the Unit lane.
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }
        return bundle;
    }

    private static (InstallRequest Request, string Root) ResourceRequest(string bundle, Guid instanceId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-resources-{Guid.NewGuid():N}");
        return (new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = instanceId,
            FriendlyName = "Limits Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = $"sha256:{new string('b', 64)}",
            Port = SelectFreePort()
        }, root);
    }

    private static Task<InstallationResult> InstallResourceRequestAsync(
        InstallRequest request,
        InstallerProcessRunner runner,
        InstallerOwnerClient owner)
        => CameraAgentInstaller.InstallAsync(
            request,
            runner,
            _ => owner,
            RuntimeUid,
            RuntimeGid,
            CancellationToken.None,
            portAvailabilityProbe: new RecordingPortProbe().Probe,
            hostEnvironment: new FakeHostEnvironment());

    private static void DeleteResourceRoot(string root)
    {
        if (Directory.Exists(root))
        {
            SafeFileSystem.MakeTreeOwnerWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
