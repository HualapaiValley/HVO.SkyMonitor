using System.Diagnostics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Elastic provider adapters (#430) against a Kestrel-hosted LogicHost and the real self-hosted runner launched as a
/// process by the local proof adapter. Local-only mode proves zero provider calls.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ElasticProviderIntegrationTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(120);

    [TestCleanup]
    public Task CleanupAsync() => DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory);

    [TestMethod]
    public async Task BacklogProvisionsALocalRunnerThatCompletesThePlacedJobThenScalesToZero()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // No backlog: nothing is provisioned.
        (await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(ElasticScalingDecision.Steady);

        var sourceId = await SeedPreviewJobAsync("elastic-e2e").ConfigureAwait(false);
        var decision = await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "runner-placed backlog provisions one instance");
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonBacklog);
        var instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        instance.State.Should().Be(nameof(ElasticRunnerInstanceState.Starting));
        instance.ProcessId.Should().NotBeNull();
        instance.RunnerId.Should().StartWith("elastic-local-process-");

        // The launched runner registers (cold start measured), claims through the runner protocol, and completes the job.
        await host.SampleUntilAsync(async () => (await host.SingleInstanceAsync().ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Running), CompletionTimeout).ConfigureAwait(false);
        instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        instance.ColdStartMilliseconds.Should().BeGreaterThan(0);
        await ElasticHost.WaitUntilAsync(async () => await host.JobStatusAsync(sourceId).ConfigureAwait(false) == CentralDerivativeJobStatus.Completed, CompletionTimeout).ConfigureAwait(false);
        var jobId = await host.PlacedJobIdAsync(sourceId).ConfigureAwait(false);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false);
            registration.CapabilitiesJson.Should().Contain("provider:local-process").And.Contain($"elastic-instance:{instance.InstanceId}", "provenance labels are advertised by the instance");
            var attempt = await db.CentralDerivativeJobAttempts.AsNoTracking().SingleAsync(item => item.CentralDerivativeJobId == jobId && item.AttemptNumber == 1).ConfigureAwait(false);
            attempt.WorkerId.Should().Be(instance.RunnerId, "the job was executed by the provisioned instance");
            attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        }

        // Idle beyond the scale-to-zero delay retires the instance and its registration; the process is gone.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await host.SampleUntilAsync(async () => (await host.SingleInstanceAsync().ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Stopped), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        instance.Reason.Should().Be(ElasticScalingPolicy.ReasonIdle);
        instance.StoppedAtUtc.Should().NotBeNull();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired);
            (await ElasticRunnerAutoscaler.InstanceMinutesTodayAsync(db, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false))
                .Should().BeGreaterThanOrEqualTo(1, "instance minutes are accounted");
        }
        ProcessIsAlive(instance.ProcessId!.Value).Should().BeFalse();
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task AKilledInstanceIsCleanedUpAsAnOrphanAndReportedByHealth()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        await SeedPreviewJobAsync("elastic-orphan").ConfigureAwait(false);
        (await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Provision.Should().Be(1);
        var instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        await host.SampleUntilAsync(async () => (await host.SingleInstanceAsync().ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Running), CompletionTimeout).ConfigureAwait(false);

        // Simulate a crash outside the host's control.
        using (var process = Process.GetProcessById(instance.ProcessId!.Value))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        instance.State.Should().Be(nameof(ElasticRunnerInstanceState.Orphaned));
        instance.Reason.Should().Be("process-exited");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired, "the registry no longer counts the orphan as capacity");
        }
        var health = await host.HealthAsync().ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Degraded);
        ((int)health.Data["orphansCleanedLastSample"]).Should().Be(1);
    }

    [TestMethod]
    public async Task LiveWorkIsRefusedByTheProviderBeforeAnyProcessStarts()
    {
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        // The provider is exercised directly here: the hosted autoscaler is stopped so its reconciliation cannot
        // retire the unrecorded instances this test launches.
        await host.Autoscaler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        var request = new ElasticRunnerProvisionRequest("livetest", "elastic-local-process-livetest",
            [ProcessingRunnerJobClass.CentralRecipe, ProcessingRunnerJobClass.CameraAgentLive], ["provider:local-process"], 1, null);
        await FluentActions.Awaiting(() => host.Provider.ProvisionAsync(request, CancellationToken.None))
            .Should().ThrowAsync<ElasticWorkloadClassException>().ConfigureAwait(false);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty("no process was started for the refused request");

        // Provisioning is idempotent per instance id: a retried request returns the instance already launched.
        var eligible = new ElasticRunnerProvisionRequest("idem0001", "elastic-local-process-idem0001", CentralElasticProviderOptions.EligibleJobClasses, ["provider:local-process"], 1, null);
        var first = await host.Provider.ProvisionAsync(eligible, CancellationToken.None).ConfigureAwait(false);
        var second = await host.Provider.ProvisionAsync(eligible, CancellationToken.None).ConfigureAwait(false);
        second.ProcessId.Should().Be(first.ProcessId);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().HaveCount(1);
        await host.Provider.RetireAsync("idem0001", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        // Provisioned runners claim through processing-runner-v1, which refuses live classes at claim for every runner
        // (Runner_CannotClaimLiveOrReplayClassesOrInProcessPlacedRecipes); the boundary refuses them one step earlier.
    }

    [TestMethod]
    public async Task LocalOnlyModeNeverCallsAProvider()
    {
        var counting = new CountingProvider();
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
            builder.ConfigureServices(services => services.AddSingleton<IElasticRunnerProvider>(counting));
        });
        await SeedPreviewJobAsync("elastic-local-only").ConfigureAwait(false);
        var autoscaler = factory.Services.GetServices<IHostedService>().OfType<ElasticRunnerAutoscaler>().Single();
        await autoscaler.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>().Value.Enabled.Should().BeFalse("ElasticProviders is absent by default");
        counting.Calls.Should().Be(0, "a disabled host never touches a provider adapter");
        var health = await new CentralElasticProviderHealthCheck(
            factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>(), counting,
            factory.Services.GetRequiredService<ElasticProviderTelemetry>())
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Healthy);
        ((bool)health.Data["enabled"]).Should().BeFalse();
        await autoscaler.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<Guid> SeedPreviewJobAsync(string scenario)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
            $"{scenario}-{suffix}", Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-5), SourcePayload, $"{scenario}-profile").ConfigureAwait(false);
    }

    private static async Task DisableClaimableJobsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        await db.CentralProcessingRunners.Where(runner => runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
    }

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class CountingProvider : IElasticRunnerProvider
    {
        public int Calls { get; private set; }
        public string Name => "counting";
        public ElasticProviderCapabilities Capabilities { get; } = new("counting", "x64", "test", true, []);
        public TimeSpan EstimateStartup() { Calls++; return TimeSpan.Zero; }
        public Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException(); }
        public Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<ElasticRunnerInstance>>([]); }
    }

    /// <summary>A Kestrel-hosted LogicHost with the local-process adapter enabled against the real runner binary in the test output.</summary>
    internal sealed class ElasticHost : IAsyncDisposable
    {
        private readonly string _secretFile;

        private ElasticHost(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string secretFile)
        {
            Factory = factory;
            _secretFile = secretFile;
            Autoscaler = factory.Services.GetServices<IHostedService>().OfType<ElasticRunnerAutoscaler>().Single();
            Provider = factory.Services.GetRequiredService<IElasticRunnerProvider>();
        }

        public WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> Factory { get; }

        public ElasticRunnerAutoscaler Autoscaler { get; }

        public IElasticRunnerProvider Provider { get; }

        public static async Task<ElasticHost> StartAsync(int maxInstances, TimeSpan scaleToZeroAfter)
        {
            // The runner is launched from its own build output (the copy in the test output carries no framework
            // reference): the apphost when present, otherwise the dll through the current dotnet host.
            var (executable, arguments) = ResolveRunnerLaunch();
            var secretFile = Path.Combine(Path.GetTempPath(), $"hvo-elastic-secret-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(secretFile, TestClients.SystemProcessingRunner.ClientSecret).ConfigureAwait(false);
            // The runner needs the host address before the host starts, so a free loopback port is chosen up front.
            var port = FreePort();
            var factory = AssemblyHooks.Fixture.CreateKestrelFactory()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ProcessingRunners:Enabled", "true");
                    builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
                    builder.UseSetting("ProcessingRunners:HeartbeatInterval", "00:00:01");
                    builder.UseSetting("ProcessingRunners:StaleAfter", "00:00:30");
                    builder.UseSetting("ProcessingRunners:ClaimBackoff", "00:00:01");
                    builder.UseSetting("ElasticProviders:Enabled", "true");
                    builder.UseSetting("ElasticProviders:Provider", "LocalProcess");
                    builder.UseSetting("ElasticProviders:MaxInstances", maxInstances.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    builder.UseSetting("ElasticProviders:ScaleToZeroAfter", scaleToZeroAfter.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                    builder.UseSetting("ElasticProviders:SampleInterval", "01:00:00");
                    builder.UseSetting("ElasticProviders:RetireGrace", "00:00:10");
                    builder.UseSetting("ElasticProviders:LocalProcess:Executable", executable);
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        builder.UseSetting($"ElasticProviders:LocalProcess:Arguments:{i}", arguments[i]);
                    }
                    builder.UseSetting("ElasticProviders:LocalProcess:WorkingDirectory", Path.GetDirectoryName(arguments.Length == 0 ? executable : arguments[0])!);
                    builder.UseSetting("ElasticProviders:LocalProcess:LogicHostUrl", $"http://127.0.0.1:{port}/");
                    builder.UseSetting("ElasticProviders:LocalProcess:ClientId", TestClients.SystemProcessingRunner.ClientId);
                    builder.UseSetting("ElasticProviders:LocalProcess:ClientSecretFile", secretFile);
                    builder.UseSetting("ElasticProviders:LocalProcess:IdleShutdown", "00:00:00");
                    builder.UseSetting("ElasticProviders:LocalProcess:AllowInsecureHttp", "true");
                });
            factory.UseKestrel(options => options.ListenLocalhost(port));
            factory.CreateClient();
            var published = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
            new Uri(published ?? "http://127.0.0.1:0/").Port.Should().Be(port, "the runner was told the address the host actually listens on");
            return new ElasticHost(factory, secretFile);
        }

        /// <summary>Locates the runner in its own build output for the test's configuration; exposed for the evidence harness.</summary>
        internal static (string Executable, string[] Arguments) ResolveRunnerLaunch()
        {
            var testBin = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = testBin.Parent?.Name ?? "Debug";
            var root = testBin;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                root = root.Parent;
            }
            root.Should().NotBeNull("the repository root is discoverable from the test output");
            // The runner is self-contained per runtime identifier, so its output sits under net10.0/<rid>/.
            var frameworkBin = Path.Combine(root!.FullName, "src", "HVO.SkyMonitor.ProcessingRunner", "bin", configuration, "net10.0");
            var appHostName = OperatingSystem.IsWindows() ? "HVO.SkyMonitor.ProcessingRunner.exe" : "HVO.SkyMonitor.ProcessingRunner";
            foreach (var runnerBin in new[] { Path.Combine(frameworkBin, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier), frameworkBin })
            {
                var appHost = Path.Combine(runnerBin, appHostName);
                if (File.Exists(appHost))
                {
                    return (appHost, []);
                }
            }
            var dll = Path.Combine(frameworkBin, "HVO.SkyMonitor.ProcessingRunner.dll");
            File.Exists(dll).Should().BeTrue($"the runner build output exists under {frameworkBin}");
            return (Environment.ProcessPath ?? "dotnet", [dll]);
        }

        private static int FreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async Task<CentralElasticRunnerInstance> SingleInstanceAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralElasticRunnerInstances.AsNoTracking().OrderByDescending(instance => instance.StartedAtUtc).FirstAsync().ConfigureAwait(false);
        }

        /// <summary>The seed helper returns the raw source artifact id; the runner-placed job is the preview job of that source.</summary>
        public async Task<Guid> PlacedJobIdAsync(Guid sourceArtifactId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sourceArtifactId && job.RecipeName == BuiltInProcessingRecipes.EncodedPreview)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
        }

        public async Task<CentralDerivativeJobStatus> JobStatusAsync(Guid sourceArtifactId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sourceArtifactId && job.RecipeName == BuiltInProcessingRecipes.EncodedPreview)
                .Select(job => job.Status).SingleAsync().ConfigureAwait(false);
        }

        public async Task SampleUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!await condition().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("The elastic condition was not reached in time.");
                }
                await Task.Delay(500).ConfigureAwait(false);
                await Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!await condition().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("The condition was not reached in time.");
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        public async Task<HealthCheckResult> HealthAsync()
            => await new CentralElasticProviderHealthCheck(
                    Factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>(), Provider,
                    Factory.Services.GetRequiredService<ElasticProviderTelemetry>())
                .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            foreach (var instance in await Provider.ListAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await Provider.RetireAsync(instance.InstanceId, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            await Factory.DisposeAsync().ConfigureAwait(false);
            File.Delete(_secretFile);
        }
    }
}
