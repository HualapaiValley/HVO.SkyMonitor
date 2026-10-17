using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The CameraAgent container limits an installation records with the instance, renders into its Compose file, and
/// holds a re-run to: the defaults, operator-set limits, an instance an earlier installer completed without a record,
/// and a host whose memory is below the limit, whose advisory the human form renders on standard error and the
/// <c>--json</c> result reports as a warning.
/// </summary>
public sealed partial class InstallerFlowTests
{
    private const long ResourceGibibyte = 1024L * 1024 * 1024;

    // What the installer reports for a 2 GiB Docker host and the default 4 GiB memory limit.
    private const string MemoryAdvisoryMessage =
        "docker info .MemTotal: observed MemTotal=2147483648 bytes; expected at least 4294967296 bytes (4 GiB) for the " +
        "CameraAgent memory limit. " + HostPrerequisites.MemoryCapacityRemediation;

    private static string LowMemoryDockerInfo =>
        SupportedDockerInfo.Replace("\"MemTotal\":8442572800", "\"MemTotal\":2147483648", StringComparison.Ordinal);

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

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_JsonInstallOnAHostWithLessMemoryThanTheLimit_ReportsTheAdvisoryAsAWarning()
    {
        var bundle = RequireProductionCatalogBundle();
        var (defaultRequest, root) = ResourceRequest(bundle, Guid.Parse("5e6f7a8b-9c0d-4e1f-8a2b-3c4d5e6f7a8b"));
        var request = defaultRequest with { Json = true };
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value)
        {
            DockerInfo = LowMemoryDockerInfo
        };
        var owner = new InstallerOwnerClient();
        try
        {
            var (installed, error) = await InstallCapturingStandardErrorAsync(request, runner, owner);

            // Standard error stays reserved for an error object; the advisory is in the result instead.
            Assert.AreEqual(string.Empty, error);
            var reported = ReportedJson(installed);
            Assert.AreEqual("Installed", (string?)reported["outcome"]);
            AssertMemoryAdvisory(reported);

            // The retained result is the reported one without its warnings, so a completed re-run still matches it,
            // and reports the advisory again.
            var resultPath = Path.Combine(installed.StateRoot, "deployment", "installation-result.json");
            var retained = await File.ReadAllTextAsync(resultPath);
            Assert.IsTrue(reported.Remove("warnings"));
            Assert.IsTrue(JsonNode.DeepEquals(reported, JsonNode.Parse(retained)), retained);

            var (rerun, rerunError) = await InstallCapturingStandardErrorAsync(request, runner, owner);
            Assert.AreEqual(string.Empty, rerunError);
            AssertMemoryAdvisory(ReportedJson(rerun));
            Assert.AreEqual(retained, await File.ReadAllTextAsync(resultPath));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_JsonDryRun_ReportsTheMemoryAdvisoryAsAWarningAndNoneOnASupportedHost()
    {
        var bundle = RequireProductionCatalogBundle();
        var (defaultRequest, root) = ResourceRequest(bundle, Guid.Parse("7c8d9e0f-1a2b-4c3d-9e4f-5a6b7c8d9e0f"));
        var request = defaultRequest with { DryRun = true, Json = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var (supported, supportedError) = await InstallCapturingStandardErrorAsync(
                request, new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value), owner);

            Assert.AreEqual(string.Empty, supportedError);
            var supportedPlan = ReportedJson(supported);
            Assert.AreEqual("Planned", (string?)supportedPlan["outcome"]);
            Assert.IsEmpty(ReportedWarnings(supportedPlan));

            var (planned, error) = await InstallCapturingStandardErrorAsync(
                request,
                new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value)
                {
                    DockerInfo = LowMemoryDockerInfo
                },
                owner);

            Assert.AreEqual(string.Empty, error);
            var plan = ReportedJson(planned);
            Assert.AreEqual("Planned", (string?)plan["outcome"]);
            AssertMemoryAdvisory(plan);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_HumanInstallOnAHostWithLessMemoryThanTheLimit_RendersTheAdvisoryOnStandardError()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("9e0f1a2b-3c4d-4e5f-8a6b-7c8d9e0f1a2b"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value)
        {
            DockerInfo = LowMemoryDockerInfo
        };
        var owner = new InstallerOwnerClient();
        try
        {
            var (installed, error) = await InstallCapturingStandardErrorAsync(request, runner, owner);

            Assert.AreEqual(InstallationOutcome.Installed, installed.Outcome);
            StringAssert.Contains(
                error,
                $"CameraAgent host prerequisites for product root {root}: satisfied.",
                StringComparison.Ordinal);
            StringAssert.Contains(
                error,
                "- [advisory] docker-host-memory-below-limit (capacity) at " + MemoryAdvisoryMessage,
                StringComparison.Ordinal);
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

    private static async Task<(InstallationResult Result, string Error)> InstallCapturingStandardErrorAsync(
        InstallRequest request,
        InstallerProcessRunner runner,
        InstallerOwnerClient owner)
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var result = await InstallResourceRequestAsync(request, runner, owner);
            return (result, error.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>The result exactly as <c>cameraagent install --json</c> writes it to standard output.</summary>
    private static JsonObject ReportedJson(InstallationResult result)
        => JsonNode.Parse(JsonSerializer.Serialize(result, DeploymentJsonContext.Default.InstallationResult))!.AsObject();

    private static JsonArray ReportedWarnings(JsonObject reported)
    {
        var warnings = reported["warnings"] as JsonArray;
        Assert.IsNotNull(warnings, $"the JSON result reports a warnings array: {reported}");
        return warnings;
    }

    private static void AssertMemoryAdvisory(JsonObject reported)
    {
        var warning = ReportedWarnings(reported).Single()!.AsObject();
        Assert.AreEqual("docker-host-memory-below-limit", (string?)warning["code"]);
        Assert.AreEqual(MemoryAdvisoryMessage, (string?)warning["message"]);
    }

    private static void DeleteResourceRoot(string root)
    {
        if (Directory.Exists(root))
        {
            SafeFileSystem.MakeTreeOwnerWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
