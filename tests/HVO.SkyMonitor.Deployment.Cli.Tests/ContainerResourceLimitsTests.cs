using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The CameraAgent container limits: the 2 CPU, 4 GiB and 256 PID defaults, the <c>--cpus</c>, <c>--memory</c> and
/// installer configuration syntax, refusal below the minimum, the request identity they bind, the Compose lines they
/// render, and the limits an upgrade gives its candidate.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContainerResourceLimitsTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary>The limit lines every installer wrote before limits were recorded, exactly as the template carried them.</summary>
    private const string OriginalLimitLines = "    cpus: \"1\"\n    mem_limit: 1G\n    pids_limit: 256\n";

    private static readonly string[] ValidArguments =
    [
        "cameraagent", "install",
        "--friendly-name", "North Camera",
        "--catalog-bundle", "/srv/hvo/catalog.bundle",
        "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}"
    ];

    [TestMethod]
    public void Parse_WithoutLimits_LeavesBothToTheTwoCpuFourGibibyteDefault()
    {
        var request = CommandLine.Parse(ValidArguments);

        Assert.IsNull(request.Cpus);
        Assert.IsNull(request.Memory);
        Assert.AreEqual(new ContainerResourceLimits(2m, 4 * Gibibyte, 256, false, false), request.ContainerResources());
        Assert.AreEqual(CameraAgentContainerResources.Default, request.ContainerResources());
    }

    [TestMethod]
    public void Parse_CpusAndMemory_AreRecordedAsSetByTheOperator()
    {
        var request = CommandLine.Parse([.. ValidArguments, "--cpus", "3.5", "--memory", "6g"]);

        Assert.AreEqual(3.5m, request.Cpus);
        Assert.AreEqual("6g", request.Memory);
        Assert.AreEqual(new ContainerResourceLimits(3.5m, 6 * Gibibyte, 256, true, true), request.ContainerResources());
    }

    [TestMethod]
    public void Parse_OneLimit_LeavesTheOtherToTheDefault()
    {
        var cpusOnly = CommandLine.Parse([.. ValidArguments, "--cpus", "4"]).ContainerResources();
        var memoryOnly = CommandLine.Parse([.. ValidArguments, "--memory", "8192m"]).ContainerResources();

        Assert.AreEqual(new ContainerResourceLimits(4m, 4 * Gibibyte, 256, true, false), cpusOnly);
        Assert.AreEqual(new ContainerResourceLimits(2m, 8 * Gibibyte, 256, false, true), memoryOnly);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("1.99")]
    [DataRow("0")]
    [DataRow("2.005")]
    [DataRow("1025")]
    public void Parse_CpusOutsideTheSupportedRange_IsRefused(string cpus)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse([.. ValidArguments, "--cpus", cpus]));

        StringAssert.Contains(exception.Message, "never limited to fewer than 2 CPUs", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("two")]
    [DataRow("-2")]
    [DataRow("2,5")]
    public void Parse_CpusThatIsNotANumber_IsRefused(string cpus)
    {
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse([.. ValidArguments, "--cpus", cpus]));
    }

    [TestMethod]
    [DataRow("3g")]
    [DataRow("4095m")]
    [DataRow("1G")]
    [DataRow("1025g")]
    [DataRow("4")]
    [DataRow("4gb")]
    [DataRow("4.5g")]
    [DataRow("04g")]
    [DataRow("4096k")]
    [DataRow("4 g")]
    public void Parse_MemoryOutsideTheSupportedRangeOrSyntax_IsRefused(string memory)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse([.. ValidArguments, "--memory", memory]));

        StringAssert.Contains(exception.Message, "never limited to less than 4 GiB", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("4g", 4L)]
    [DataRow("4G", 4L)]
    [DataRow("4096m", 4L)]
    [DataRow("4096M", 4L)]
    [DataRow("1024g", 1024L)]
    public void Parse_MemoryInMebibytesOrGibibytes_IsAccepted(string memory, long gibibytes)
    {
        Assert.AreEqual(
            gibibytes * Gibibyte,
            CommandLine.Parse([.. ValidArguments, "--memory", memory]).ContainerResources().MemoryBytes);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigMembers_SetTheSameLimitsAsTheFlags()
    {
        var expected = CommandLine.Parse([.. ValidArguments, "--cpus", "2.5", "--memory", "8g"]);
        var path = Path.GetTempFileName();
        try
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(
                CommandLine.Parse(ValidArguments),
                InstallRequestJsonContext.Default.InstallRequest))!.AsObject();
            json["cpus"] = 2.5m;
            json["memory"] = "8g";
            File.WriteAllText(path, json.ToJsonString());

            var actual = CommandLine.Parse(["cameraagent", "install", "--config", path]);

            Assert.AreEqual(expected, actual);
            Assert.AreEqual(new ContainerResourceLimits(2.5m, 8 * Gibibyte, 256, true, true), actual.ContainerResources());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigWithLimitsBelowTheMinimum_IsRefused()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var belowMinimum in new Action<JsonObject>[] { static json => json["cpus"] = 1m, static json => json["memory"] = "2g" })
            {
                var json = JsonNode.Parse(JsonSerializer.Serialize(
                    CommandLine.Parse(ValidArguments),
                    InstallRequestJsonContext.Default.InstallRequest))!.AsObject();
                belowMinimum(json);
                File.WriteAllText(path, json.ToJsonString());

                Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(["cameraagent", "install", "--config", path]));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Parse_LimitFlagsWithConfig_AreRefusedLikeEveryOtherValueOption()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--config", "/srv/hvo/install.json", "--cpus", "3"]));

        StringAssert.Contains(exception.Message, "--config cannot be combined with value options", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RequestIdentity_DefaultLimitsKeepTheEarlierIdentityAndSetLimitsBindTheirNormalizedValues()
    {
        var instanceId = Guid.NewGuid();
        var request = CommandLine.Parse(ValidArguments);
        var unset = CameraAgentInstaller.ComputeRequestSha256(request, instanceId);

        // A request that leaves both limits to the default is identified exactly as before limits existed, so an
        // installation an earlier installer began still matches; the explicit default is a different request because
        // the operator pinned it.
        Assert.AreEqual(unset, CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = null, Memory = null }, instanceId));
        Assert.AreNotEqual(unset, CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = 2m }, instanceId));
        Assert.AreNotEqual(unset, CameraAgentInstaller.ComputeRequestSha256(request with { Memory = "4g" }, instanceId));
        Assert.AreEqual(
            CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = 2.5m, Memory = "6g" }, instanceId),
            CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = 2.50m, Memory = "6144m" }, instanceId));
        Assert.AreNotEqual(
            CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = 3m }, instanceId),
            CameraAgentInstaller.ComputeRequestSha256(request with { Cpus = 4m }, instanceId));
        Assert.IsTrue(CameraAgentInstaller.RequestIdentityMatches(
            CameraAgentInstaller.ComputeLegacyRequestSha256(request, instanceId), request, instanceId));
        Assert.IsFalse(CameraAgentInstaller.RequestIdentityMatches(
            CameraAgentInstaller.ComputeLegacyRequestSha256(request, instanceId), request with { Cpus = 3m }, instanceId));
    }

    [TestMethod]
    public void RenderLimits_TheOriginalLimits_AreTheOriginalTemplateLinesByteForByte()
    {
        Assert.AreEqual(OriginalLimitLines, CameraAgentContainerResources.RenderLimits(CameraAgentContainerResources.Legacy));
        Assert.AreEqual(
            "    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n",
            CameraAgentContainerResources.RenderLimits(CameraAgentContainerResources.Default));
        Assert.AreEqual(
            "    cpus: \"2.5\"\n    mem_limit: 4608M\n    pids_limit: 256\n",
            CameraAgentContainerResources.RenderLimits(CameraAgentContainerResources.Resolve(2.50m, "4608m")));
    }

    [TestMethod]
    public void UpgradeTarget_KeepsOperatorLimitsAndMovesEveryOtherToTheDefault()
    {
        // No record: an instance an earlier installer completed, whose original limits no operator could set.
        Assert.AreEqual(CameraAgentContainerResources.Default, CameraAgentContainerResources.UpgradeTarget(null));
        // A limit left to an earlier default follows the current one; a limit the operator set is kept.
        Assert.AreEqual(
            new ContainerResourceLimits(3m, 4 * Gibibyte, 256, true, false),
            CameraAgentContainerResources.UpgradeTarget(new ContainerResourceLimits(3m, 5 * Gibibyte, 256, true, false)));
        Assert.AreEqual(
            new ContainerResourceLimits(2m, 6 * Gibibyte, 256, false, true),
            CameraAgentContainerResources.UpgradeTarget(new ContainerResourceLimits(3m, 6 * Gibibyte, 256, false, true)));
        var pinned = new ContainerResourceLimits(2m, 4 * Gibibyte, 256, true, true);
        Assert.AreEqual(pinned, CameraAgentContainerResources.UpgradeTarget(pinned));
    }

    [TestMethod]
    public void WithLimits_ReplacesOnlyTheCameraAgentLinesAndLeavesTheReplayRunnerBounded()
    {
        var compose = "services:\n  cameraagent:\n      start_period: 20s\n" + OriginalLimitLines + "    logging:\n" +
                      "  replay-runner:\n    cpus: \"0.5\"\n    mem_limit: 2G\n    pids_limit: 64\n";

        var limited = CameraAgentContainerResources.WithLimits(
            compose, CameraAgentContainerResources.Default, CameraAgentContainerResources.Legacy);

        Assert.AreEqual(
            compose.Replace(OriginalLimitLines, "    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n", StringComparison.Ordinal),
            limited);
        Assert.AreSame(compose, CameraAgentContainerResources.WithLimits(compose, CameraAgentContainerResources.Legacy, null));
    }

    [TestMethod]
    public void WithLimits_FileThatDisagreesWithItsRecordOrCarriesNoSingleBlock_IsRefused()
    {
        var compose = "services:\n  cameraagent:\n" + OriginalLimitLines;

        var disagrees = Assert.ThrowsExactly<InstallerException>(() => CameraAgentContainerResources.WithLimits(
            compose, CameraAgentContainerResources.Default, CameraAgentContainerResources.Default));
        StringAssert.Contains(disagrees.Message, "instance manifest records", StringComparison.Ordinal);
        foreach (var unrecognised in new[]
                 {
                     "services: {}\n",
                     compose + OriginalLimitLines,
                     "services:\n  cameraagent:\n    cpus: 1\n    mem_limit: 1G\n    pids_limit: 256\n",
                     // The runner's lines are never taken for the CameraAgent service's.
                     "services:\n  cameraagent:\n    image: x\n  replay-runner:\n" + OriginalLimitLines
                 })
        {
            Assert.ThrowsExactly<InstallerException>(
                () => CameraAgentContainerResources.WithLimits(unrecognised, CameraAgentContainerResources.Default, null),
                unrecognised);
        }
    }

    /// <summary>
    /// A start on a host whose Docker cannot enforce a CPU limit uses a copy of the model without every service's CPU
    /// limit (#1357), because Docker refuses a container with one there. Each line keeps its value as a comment, and
    /// every other limit is kept.
    /// </summary>
    [TestMethod]
    public void WithoutCpuLimits_WithholdsEveryServicesCpuLimitAndKeepsTheOtherLimits()
    {
        var compose = "services:\n  cameraagent:\n      start_period: 20s\n    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n" +
                      "    logging:\n  replay-runner:\n    cpus: \"0.5\"\n    mem_limit: 2G\n    pids_limit: 64\n";

        var withheld = CameraAgentContainerResources.WithoutCpuLimits(compose);

        Assert.AreEqual(
            "services:\n  cameraagent:\n      start_period: 20s\n" +
            "    # cpus: \"2\" withheld: Docker here cannot enforce a CPU limit (#1357)\n" +
            "    mem_limit: 4G\n    pids_limit: 256\n    logging:\n" +
            "  replay-runner:\n" +
            "    # cpus: \"0.5\" withheld: Docker here cannot enforce a CPU limit (#1357)\n" +
            "    mem_limit: 2G\n    pids_limit: 64\n",
            withheld);
        Assert.AreEqual(withheld, CameraAgentContainerResources.WithoutCpuLimits(withheld));
    }

    [TestMethod]
    public void IsValid_AcceptsOnlyLimitsThisInstallerCouldHaveRecorded()
    {
        Assert.IsTrue(CameraAgentContainerResources.IsValid(CameraAgentContainerResources.Default));
        Assert.IsTrue(CameraAgentContainerResources.IsValid(new ContainerResourceLimits(1024m, 1024 * Gibibyte, 256, true, true)));
        Assert.IsFalse(CameraAgentContainerResources.IsValid(CameraAgentContainerResources.Legacy));
        Assert.IsFalse(CameraAgentContainerResources.IsValid(new ContainerResourceLimits(2.001m, 4 * Gibibyte, 256, true, false)));
        Assert.IsFalse(CameraAgentContainerResources.IsValid(new ContainerResourceLimits(2m, (4 * Gibibyte) + 1, 256, false, true)));
        Assert.IsFalse(CameraAgentContainerResources.IsValid(new ContainerResourceLimits(2m, 4 * Gibibyte, 0, false, false)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Write_RendersTheRecordedLimitsForCameraAgentAndKeepsTheRunnerLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-resource-compose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var request = new InstallRequest
            {
                FriendlyName = "Limits Camera",
                OwnerEmail = "owner@example.test",
                ProductRoot = root,
                ImageReference = $"sha256:{new string('a', 64)}",
                CatalogBundle = Path.Combine(root, "catalog.bundle"),
                ReplayProfile = CameraAgentReplayProfile.LocalRunner
            };
            foreach (var (resources, expected) in new (ContainerResourceLimits?, string)[]
                     {
                         (CameraAgentContainerResources.Default, "    cpus: \"2\"\n    mem_limit: 4G\n    pids_limit: 256\n"),
                         (CameraAgentContainerResources.Resolve(3m, "6g"), "    cpus: \"3\"\n    mem_limit: 6G\n    pids_limit: 256\n"),
                         // An instance that records no limits keeps the original lines byte for byte.
                         (null, OriginalLimitLines)
                     })
            {
                var paths = InstallationPaths.Create(root, Guid.NewGuid(), HygV42.CatalogId);
                Directory.CreateDirectory(paths.CatalogRoot);
                var compose = ComposeDeployment.Write(
                    request,
                    paths,
                    Guid.Parse(Path.GetFileName(paths.InstanceRoot)),
                    Guid.NewGuid(),
                    NativeLinux.getuid(),
                    NativeLinux.getgid(),
                    $"sha256:{new string('a', 64)}",
                    HygV42.PackageVersion,
                    Path.Combine(paths.ConfigRoot, "owner-password"),
                    "verification-token",
                    "lifecycle-token",
                    passwordAuthorityEnabled: false,
                    resources);

                var text = File.ReadAllText(compose.ComposeFile);
                var runnerStart = text.IndexOf("\n  replay-runner:", StringComparison.Ordinal);
                StringAssert.Contains(text[..runnerStart], "      start_period: 20s\n" + expected + "    logging:", StringComparison.Ordinal);
                StringAssert.Contains(text[runnerStart..], "    cpus: \"0.5\"\n    mem_limit: 2G\n    pids_limit: 64\n", StringComparison.Ordinal);
                Assert.DoesNotContain("<cameraagent-resource-limits>", text);
                // A start on a host that cannot enforce a CPU limit withholds both services' CPU limits from the
                // rendered model and keeps their other limits (#1357).
                var withheld = CameraAgentContainerResources.WithoutCpuLimits(text);
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(withheld, "^ *cpus:", System.Text.RegularExpressions.RegexOptions.Multiline));
                StringAssert.Contains(withheld, "\n    mem_limit: 2G\n    pids_limit: 64\n", StringComparison.Ordinal);
            }
        }
        finally
        {
            SafeFileSystem.MakeTreeOwnerWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
