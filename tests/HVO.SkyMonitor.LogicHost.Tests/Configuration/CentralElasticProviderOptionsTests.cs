using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.LogicHost.Tests.Configuration;

[TestClass]
public sealed class CentralElasticProviderOptionsTests
{
    private static CentralElasticProviderOptions Enabled(int maxInstances = 4, int minWarm = 0, int perInstance = 1, int dailyLimit = 0,
        TimeSpan? queueDeadline = null, TimeSpan? scaleToZero = null)
        => new()
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = maxInstances,
            MinWarmInstances = minWarm,
            MaxConcurrencyPerInstance = perInstance,
            MaxInstanceMinutesPerDay = dailyLimit,
            QueueDeadline = queueDeadline ?? TimeSpan.FromMinutes(10),
            ScaleToZeroAfter = scaleToZero ?? TimeSpan.FromMinutes(5),
            LocalProcess = new CentralLocalProcessElasticOptions
            {
                Executable = "/opt/hvo/runner",
                LogicHostUrl = "https://logichost.local/",
                ClientSecretFile = "/run/secrets/runner"
            }
        };

    [TestMethod]
    [TestCategory("Unit")]
    public void DisabledByDefaultAndValidWithoutAnyProviderConfiguration()
    {
        var options = new CentralElasticProviderOptions();
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(CentralElasticProviderKind.None, options.Provider);
        Assert.IsTrue(options.Validate(out _));
        CollectionAssert.AreEqual(
            new[] { ProcessingRunnerJobClass.CentralRecipe, ProcessingRunnerJobClass.CameraAgentArchivedReplay },
            CentralElasticProviderOptions.EligibleJobClasses.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnabledConfigurationIsValidatedIncludingTheLocalProcessSettings()
    {
        Assert.IsTrue(Enabled().Validate(out _));
        Assert.IsFalse(new CentralElasticProviderOptions { Enabled = true }.Validate(out var noProvider));
        StringAssert.Contains(noProvider, "Provider");
        Assert.IsFalse(Enabled(maxInstances: 0).Validate(out _));
        Assert.IsFalse(Enabled(minWarm: 5).Validate(out _), "the warm minimum cannot exceed the maximum");
        Assert.IsFalse(Enabled(queueDeadline: TimeSpan.Zero).Validate(out _));
        var missingSecret = Enabled();
        missingSecret = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            LocalProcess = new CentralLocalProcessElasticOptions { Executable = "/opt/hvo/runner", LogicHostUrl = "https://logichost.local/" }
        };
        Assert.IsFalse(missingSecret.Validate(out var secretError));
        StringAssert.Contains(secretError, "ClientSecretFile");
        var reservedLabel = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            Labels = ["provider:cloud"],
            LocalProcess = Enabled().LocalProcess
        };
        Assert.IsFalse(reservedLabel.Validate(out _), "provider and instance labels are set by the host, not by configuration");
        var poolLabel = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            Labels = ["pool-mode:reserved"],
            LocalProcess = Enabled().LocalProcess
        };
        Assert.IsFalse(poolLabel.Validate(out _), "pool control labels come from the Pool option only");
        foreach (var badPool in new[] { "shared", "Blue", "a_b", "a.b", new string('a', 60) })
        {
            var options = new CentralElasticProviderOptions
            {
                Enabled = true,
                Provider = CentralElasticProviderKind.LocalProcess,
                Pool = badPool,
                LocalProcess = Enabled().LocalProcess
            };
            Assert.IsFalse(options.Validate(out _), $"pool '{badPool}' must fail the pool grammar");
        }
        Assert.IsTrue(new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            Pool = "blue-1",
            LocalProcess = Enabled().LocalProcess
        }.Validate(out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LogicHostUrlMirrorsTheRunnerRuleAndElasticNeedsTheRunnerProtocol()
    {
        static CentralElasticProviderOptions WithUrl(string url, bool allowInsecure = false) => new()
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            LocalProcess = new CentralLocalProcessElasticOptions
            {
                Executable = "/opt/hvo/runner",
                LogicHostUrl = url,
                ClientSecretFile = "/run/secrets/runner",
                AllowInsecureHttp = allowInsecure
            }
        };
        Assert.IsFalse(WithUrl("ftp://logichost.local/").Validate(out var scheme), "a non-HTTP scheme fails at startup, not in each child");
        StringAssert.Contains(scheme, "http or https");
        Assert.IsFalse(WithUrl("http://logichost.local/").Validate(out var insecure), "plain http to a remote host needs the explicit opt-in the runner requires");
        StringAssert.Contains(insecure, "AllowInsecureHttp");
        Assert.IsTrue(WithUrl("http://logichost.local/", allowInsecure: true).Validate(out _));
        Assert.IsTrue(WithUrl("http://127.0.0.1:5000/").Validate(out _), "loopback http is the runner's own exception");
        Assert.IsTrue(WithUrl("http://localhost:5000/").Validate(out _));
        Assert.IsTrue(WithUrl("https://logichost.local/").Validate(out _));

        Assert.IsFalse(Enabled().ValidateRunnerProtocol(runnerProtocolEnabled: false, out var protocol), "instances register through the runner protocol");
        StringAssert.Contains(protocol, "ProcessingRunners:Enabled");
        Assert.IsTrue(Enabled().ValidateRunnerProtocol(runnerProtocolEnabled: true, out _));
        Assert.IsTrue(new CentralElasticProviderOptions().ValidateRunnerProtocol(runnerProtocolEnabled: false, out _), "a disabled feature needs nothing");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstanceIdleShutdownIsCoordinatedWithTheScalingPolicy()
    {
        var idle = new CentralLocalProcessElasticOptions { Executable = "/opt/hvo/runner", LogicHostUrl = "https://logichost.local/", ClientSecretFile = "/run/secrets/runner", IdleShutdown = TimeSpan.FromMinutes(2) };
        var scaled = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            ScaleToZeroAfter = TimeSpan.FromMinutes(5),
            SampleInterval = TimeSpan.FromSeconds(15),
            RetireGrace = TimeSpan.FromSeconds(30),
            LocalProcess = idle
        };
        Assert.AreEqual(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(60), scaled.EffectiveInstanceIdleShutdown(keepWarm: false),
            "an instance never exits before the host's scale-to-zero window has passed");
        var warm = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MinWarmInstances = 1,
            LocalProcess = idle
        };
        Assert.AreEqual(TimeSpan.Zero, warm.EffectiveInstanceIdleShutdown(keepWarm: true), "warm-minimum instances never self-terminate");
        Assert.AreNotEqual(TimeSpan.Zero, warm.EffectiveInstanceIdleShutdown(keepWarm: false), "excess capacity above the warm minimum keeps the safety net");
        var longer = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            ScaleToZeroAfter = TimeSpan.FromMinutes(1),
            LocalProcess = new CentralLocalProcessElasticOptions { Executable = "/x", LogicHostUrl = "https://l/", ClientSecretFile = "/s", IdleShutdown = TimeSpan.FromMinutes(30) }
        };
        Assert.AreEqual(TimeSpan.FromMinutes(30), longer.EffectiveInstanceIdleShutdown(keepWarm: false));
        Assert.IsFalse(new CentralElasticProviderOptions { RetireGrace = TimeSpan.FromSeconds(-1) }.Validate(out _), "RetireGrace is consumed even while disabled");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InheritedEnvironmentKeepsRuntimeEssentialsAndDropsHostSecrets()
    {
        var environment = new System.Collections.Hashtable
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/hvo",
            ["DOTNET_ROOT"] = "/usr/share/dotnet",
            ["LC_ALL"] = "C.UTF-8",
            ["ConnectionStrings__skymonitordb"] = "Server=x;Password=y",
            ["HVO_BOOTSTRAP_CLIENT_SECRET"] = "s",
            ["MINIO_ACCESS_KEY"] = "k",
            ["REDIS_PASSWORD"] = "p",
            ["DOTNET_SOME_TOKEN"] = "t",
            ["ASPNETCORE_ENVIRONMENT"] = "Production"
        };
        var filtered = LocalProcessElasticRunnerProvider.FilterInheritedEnvironment(environment);
        CollectionAssert.AreEquivalent(new[] { "PATH", "HOME", "DOTNET_ROOT", "LC_ALL" }, filtered.Keys.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LiveWorkloadClassIsRefusedBeforeAnyProviderCall()
    {
        Assert.IsTrue(ElasticWorkloadClass.IsEligible(ProcessingRunnerJobClass.CentralRecipe));
        Assert.IsTrue(ElasticWorkloadClass.IsEligible(ProcessingRunnerJobClass.CameraAgentArchivedReplay));
        Assert.IsFalse(ElasticWorkloadClass.IsEligible(ProcessingRunnerJobClass.CameraAgentLive));
        var exception = Assert.ThrowsExactly<ElasticWorkloadClassException>(() =>
            ElasticWorkloadClass.EnsureEligible([ProcessingRunnerJobClass.CentralRecipe, ProcessingRunnerJobClass.CameraAgentLive]));
        Assert.AreEqual(ProcessingRunnerJobClass.CameraAgentLive, exception.JobClass);
        var request = new ElasticRunnerProvisionRequest("abc", "elastic-local-process-abc", [ProcessingRunnerJobClass.CameraAgentLive], [], 1, null);
        Assert.ThrowsExactly<ElasticWorkloadClassException>(() => ElasticWorkloadClass.EnsureEligible(request.JobClasses));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ScalingPolicyFollowsBacklogWithinEveryBound()
    {
        var options = Enabled(maxInstances: 3, perInstance: 2);
        var startup = TimeSpan.FromSeconds(20);
        var backlog = new ElasticScalingInput(5, TimeSpan.FromSeconds(30), 0, 0, 0, TimeSpan.Zero, null, 0);
        var occupied = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, perInstance: 1), new ElasticScalingInput(1, TimeSpan.FromSeconds(5), 1, 0, 0, TimeSpan.Zero, null, 0, InFlight: 1), startup);
        Assert.AreEqual(1, occupied.Provision, "a busy single-slot instance plus one queued job needs a second instance");
        var decision = ElasticScalingPolicy.Decide(options, backlog, startup);
        Assert.AreEqual((3, 0, ElasticScalingPolicy.ReasonBacklog), (decision.Provision, decision.Retire, decision.Reason), "ceil(5/2) = 3 instances");

        var capped = ElasticScalingPolicy.Decide(options, backlog with { Backlog = 50 }, startup);
        Assert.AreEqual(3, capped.Provision, "bounded by MaxInstances");

        var entitled = ElasticScalingPolicy.Decide(options, backlog with { Backlog = 50, EntitledConcurrency = 2 }, startup);
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonEntitlementBound), (entitled.Provision, entitled.Reason), "entitlements bound the useful concurrency");

        var alreadyRunning = ElasticScalingPolicy.Decide(options, backlog with { Running = 2, Starting = 1 }, startup);
        Assert.AreEqual(ElasticScalingDecision.Steady, alreadyRunning);

        var warm = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, minWarm: 1), new ElasticScalingInput(0, TimeSpan.Zero, 0, 0, 0, TimeSpan.Zero, null, 0), startup);
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonWarmMinimum), (warm.Provision, warm.Reason));
        var lostWarm = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, minWarm: 1), new ElasticScalingInput(0, TimeSpan.Zero, 1, 0, 0, TimeSpan.Zero, null, 0, InFlight: 0, WarmInstances: 0), startup);
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonWarmMinimum), (lostWarm.Provision, lostWarm.Reason), "an excess instance does not satisfy the warm minimum; a warm replacement is provisioned");
        var warmSatisfied = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, minWarm: 1), new ElasticScalingInput(0, TimeSpan.Zero, 1, 0, 0, TimeSpan.Zero, null, 0, InFlight: 0, WarmInstances: 1), startup);
        Assert.AreEqual(ElasticScalingDecision.Steady, warmSatisfied);
        var atCapacity = ElasticScalingPolicy.Decide(Enabled(maxInstances: 2, minWarm: 2), new ElasticScalingInput(0, TimeSpan.Zero, 2, 0, 0, TimeSpan.Zero, null, 0, InFlight: 0, WarmInstances: 1), startup);
        Assert.AreEqual((0, 1, ElasticScalingPolicy.ReasonWarmMinimum), (atCapacity.Provision, atCapacity.Retire, atCapacity.Reason), "at capacity an excess instance is replaced by a warm one");

        var daily = ElasticScalingPolicy.Decide(Enabled(dailyLimit: 60), backlog with { InstanceMinutesToday = 60 }, startup);
        Assert.AreEqual((0, 0, ElasticScalingPolicy.ReasonDailyLimit), (daily.Provision, daily.Retire, daily.Reason), "the daily limit blocks new instances");
        var drain = ElasticScalingPolicy.Decide(Enabled(dailyLimit: 60), backlog with { Running = 2, Starting = 1, InstanceMinutesToday = 60 }, startup);
        Assert.AreEqual((0, 3, ElasticScalingPolicy.ReasonDailyLimit), (drain.Provision, drain.Retire, drain.Reason), "existing and registering capacity drains once the daily budget is spent");
        var underLimit = ElasticScalingPolicy.Decide(Enabled(dailyLimit: 60), backlog with { InstanceMinutesToday = 59 }, startup);
        Assert.IsTrue(underLimit.Provision > 0);

        // Sizing follows the concurrency the running instances actually registered, not the configured value alone:
        // an adopted single-slot instance under a four-slot configuration does not absorb three queued jobs.
        var adopted = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, perInstance: 4), new ElasticScalingInput(3, TimeSpan.FromSeconds(5), 1, 0, 0, TimeSpan.Zero, null, 0, Capacity: 1), startup);
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonBacklog), (adopted.Provision, adopted.Reason), "one more four-slot instance covers the two jobs the single-slot instance cannot");
        var matched = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, perInstance: 4), new ElasticScalingInput(3, TimeSpan.FromSeconds(5), 1, 0, 0, TimeSpan.Zero, null, 0, Capacity: 4), startup);
        Assert.AreEqual(ElasticScalingDecision.Steady, matched, "a registered four-slot instance holds three jobs");
        var entitledCapacity = ElasticScalingPolicy.Decide(Enabled(maxInstances: 3, perInstance: 4), new ElasticScalingInput(9, TimeSpan.FromSeconds(5), 1, 0, 0, TimeSpan.Zero, EntitledConcurrency: 2, 0, Capacity: 1), startup);
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonEntitlementBound), (entitledCapacity.Provision, entitledCapacity.Reason), "the entitlement bound is applied against registered capacity too");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PlacementPolicyRetainsWorkLocallyWhenColdStartCannotMeetTheDeadlineAndScalesToZeroWhenIdle()
    {
        var options = Enabled(maxInstances: 2, minWarm: 1, queueDeadline: TimeSpan.FromMinutes(1), scaleToZero: TimeSpan.FromMinutes(2));
        var lateBacklog = new ElasticScalingInput(4, TimeSpan.FromSeconds(50), 0, 0, 0, TimeSpan.Zero, null, 0);
        var rejected = ElasticScalingPolicy.Decide(options, lateBacklog, TimeSpan.FromSeconds(20));
        Assert.AreEqual((1, ElasticScalingPolicy.ReasonColdStartExceedsDeadline), (rejected.Provision, rejected.Reason),
            "the oldest work cannot be served within the deadline after a cold start, so only the warm minimum is provisioned");
        var accepted = ElasticScalingPolicy.Decide(options, lateBacklog with { OldestBacklogAge = TimeSpan.FromSeconds(10) }, TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, accepted.Provision);

        var idle = new ElasticScalingInput(0, TimeSpan.Zero, 2, 0, 2, TimeSpan.FromMinutes(3), null, 0, InFlight: 0, WarmInstances: 1);
        var retire = ElasticScalingPolicy.Decide(options, idle, TimeSpan.FromSeconds(20));
        Assert.AreEqual((0, 1, ElasticScalingPolicy.ReasonIdle), (retire.Provision, retire.Retire, retire.Reason), "scale down to the warm minimum only");
        var notYet = ElasticScalingPolicy.Decide(options, idle with { LongestIdle = TimeSpan.FromMinutes(1) }, TimeSpan.FromSeconds(20));
        Assert.AreEqual(ElasticScalingDecision.Steady, notYet);
        var toZero = ElasticScalingPolicy.Decide(Enabled(maxInstances: 2, scaleToZero: TimeSpan.FromMinutes(2)), idle, TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, toZero.Retire, "without a warm minimum every idle instance is retired");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstanceDescriptionIsWithheldWhenTheRunnerCannotBeProbed()
    {
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            LocalProcess = new CentralLocalProcessElasticOptions
            {
                Executable = Path.Combine(Path.GetTempPath(), $"hvo-missing-runner-{Guid.NewGuid():N}"),
                LogicHostUrl = "https://logichost.local/",
                ClientSecretFile = "/run/secrets/runner"
            }
        };
        using var provider = new LocalProcessElasticRunnerProvider(
            Microsoft.Extensions.Options.Options.Create(settings), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalProcessElasticRunnerProvider>.Instance);
        Assert.IsNull(provider.ProbeConfiguredRunner(), "a missing executable cannot be probed");
        Assert.IsNull(provider.ProbeConfiguredRunner(), "a failed probe is not repeated before the retry interval");
        Assert.IsNull(provider.DescribeInstance(3, ["provider:local-process", "b", "a", "a"]), "nothing stands in for a runner that cannot be probed: the host provisions nothing until a probe succeeds");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CapabilityProbeAcceptsOnlyASuccessfulRunnerAdvertisement()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("The probe fixture is a POSIX shell script.");
        }
        var advertised = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, null, null)
            with
        { RuntimeIdentifier = "probe-rid" };
        var json = System.Text.Json.JsonSerializer.Serialize(advertised, ProcessingRunnerProtocol.SerializerOptions);
        foreach (var (exitCode, accepted) in new[] { (1, false), (0, true) })
        {
            var script = Path.Combine(Path.GetTempPath(), $"hvo-probe-{Guid.NewGuid():N}.sh");
            File.WriteAllText(script, $"#!/bin/sh\ncat <<'JSON'\n{json}\nJSON\nexit {exitCode}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            try
            {
                var settings = new CentralElasticProviderOptions
                {
                    Enabled = true,
                    Provider = CentralElasticProviderKind.LocalProcess,
                    LocalProcess = new CentralLocalProcessElasticOptions { Executable = script, LogicHostUrl = "https://logichost.local/", ClientSecretFile = "/run/secrets/runner" }
                };
                using var provider = new LocalProcessElasticRunnerProvider(
                    Microsoft.Extensions.Options.Options.Create(settings), TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalProcessElasticRunnerProvider>.Instance);
                var probed = provider.ProbeConfiguredRunner();
                if (accepted)
                {
                    Assert.IsNotNull(probed, "a warm runner's advertisement (exit 0) is accepted");
                    var described = provider.DescribeInstance(2, ["b", "a"]);
                    Assert.IsNotNull(described);
                    Assert.AreEqual("probe-rid", described.RuntimeIdentifier, "instances are described by the probed runner");
                    Assert.AreEqual(2, described.MaxConcurrency);
                    CollectionAssert.AreEqual(new[] { "a", "b" }, described.Labels.ToArray());
                }
                else
                {
                    Assert.IsNull(probed, "a runner whose warmup is incomplete (exit 1) prints capabilities but would abort at startup; its advertisement is refused");
                    Assert.IsNull(provider.DescribeInstance(2, []), "a refused advertisement describes nothing, so no instance is provisioned");
                }
            }
            finally
            {
                File.Delete(script);
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ScaleDownGuardUsesTheEntitlementBoundedDemand()
    {
        Assert.AreEqual(101, ElasticRunnerAutoscaler.EffectiveDemand(100, 1, null));
        Assert.AreEqual(2, ElasticRunnerAutoscaler.EffectiveDemand(100, 1, 2), "entitlements bound the concurrency the queue can use, so idle instances above that bound may retire");
        Assert.AreEqual(3, ElasticRunnerAutoscaler.EffectiveDemand(2, 1, 10));
        Assert.AreEqual(0, ElasticRunnerAutoscaler.EffectiveDemand(5, 0, -1));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LocalProcessEnvironmentCarriesTheRunnerContractAndProvenanceLabels()
    {
        var request = new ElasticRunnerProvisionRequest(
            "0123456789abcdef", "elastic-local-process-0123456789abcdef", CentralElasticProviderOptions.EligibleJobClasses,
            ["provider:local-process", "elastic-instance:0123456789abcdef", "pool:blue", "pool-mode:reserved"], 2, "blue");
        var environment = LocalProcessElasticRunnerProvider.ComposeEnvironment(request, Enabled());
        Assert.AreEqual("https://logichost.local/", environment["HVO_RUNNER_LOGICHOST_URL"]);
        Assert.AreEqual(request.RunnerId, environment["HVO_RUNNER_ID"]);
        Assert.AreEqual("/run/secrets/runner", environment["HVO_RUNNER_CLIENT_SECRET_FILE"]);
        Assert.IsFalse(environment.ContainsKey("HVO_RUNNER_CLIENT_SECRET"), "the secret value is never composed into the environment");
        Assert.IsTrue(environment["HVO_RUNNER_STOP_FILE"].EndsWith("hvo-elastic-0123456789abcdef.stop", StringComparison.Ordinal), "the provider-neutral drain signal is wired");
        Assert.AreEqual("0", environment["HVO_RUNNER_IDLE_SHUTDOWN_SECONDS"], "the default local idle shutdown is coordinated: no self-termination unless configured");
        Assert.AreEqual("30", environment["HVO_RUNNER_SHUTDOWN_GRACE_SECONDS"], "the child drains for the host's RetireGrace, so a longer grace lets long jobs finish");
        var longGrace = LocalProcessElasticRunnerProvider.ComposeEnvironment(request, new CentralElasticProviderOptions { Enabled = true, Provider = CentralElasticProviderKind.LocalProcess, RetireGrace = TimeSpan.FromMinutes(5), LocalProcess = Enabled().LocalProcess });
        Assert.AreEqual("300", longGrace["HVO_RUNNER_SHUTDOWN_GRACE_SECONDS"]);
        Assert.IsTrue(request.KeepWarm == false);
        Assert.AreEqual("2", environment["HVO_RUNNER_MAX_CONCURRENCY"]);
        Assert.AreEqual("provider:local-process,elastic-instance:0123456789abcdef,pool:blue,pool-mode:reserved", environment["HVO_RUNNER_LABELS"]);
        Assert.IsTrue(ProcessingRunnerProtocol.IsValidRunnerId(request.RunnerId));
        var access = new LeaseScopedArtifactAccessAdapter(new Uri("https://logichost.local/")).Describe();
        Assert.IsFalse(access.DistributesCredentials);
        CollectionAssert.AreEquivalent(
            new[] { ProcessingRunnerProtocol.RunnerIdHeader, ProcessingRunnerProtocol.JobIdHeader, ProcessingRunnerProtocol.LeaseTokenHeader },
            access.RequiredHeaders.ToArray());
    }
}
