using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The orbital feature as <c>AddCameraAgentInfrastructure</c> registers it: one shared store behind every contract, the
/// automation adapter in the periodic registry, the store initializer ahead of the automation runner, and a named
/// CelesTrak client with no retry, redirect or logging handler.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Orbital")]
public sealed class OrbitalElementsCompositionTests : OrbitalStoreTestBase
{
    private static readonly string[] OnlyLifetimeWrapper = ["LifetimeTrackingHttpMessageHandler"];

    [TestMethod]
    public async Task InfrastructureResolvesOneSharedOrbitalGraph()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);

        var store = provider.GetRequiredService<SqliteOrbitalElementsStore>();
        Assert.AreSame(store, provider.GetRequiredService<IOrbitalElementsStore>());
        Assert.IsInstanceOfType<OrbitalSnapshotReader>(provider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.AreSame(provider.GetRequiredService<IOrbitalSnapshotReader>(), provider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.AreSame(provider.GetRequiredService<SqliteOrbitalElementsStore>(), provider.GetRequiredService<IOrbitalSnapshotManifestReader>(),
            "Manifest reads come from the store that owns the root.");
        Assert.IsInstanceOfType<CelesTrakGpClient>(provider.GetRequiredService<IOrbitalElementsProviderClient>());
        var adapter = provider.GetRequiredService<OrbitalElementsAutomationAdapter>();
        Assert.AreSame(adapter, provider.GetServices<ILocalAutomationPeriodicTaskAdapter>()
            .Single(static item => item.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh));
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsRefreshJob>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsOperations>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsHealthEvaluator>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsTelemetry>());

        var descriptor = provider.GetRequiredService<ILocalAutomationTaskRegistry>().Describe()
            .Single(static item => item.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh);
        CollectionAssert.AreEqual(new[] { CelesTrakGpSource.DatasetKey }, descriptor.Targets.ToArray());
    }

    [TestMethod]
    public async Task StoreLivesInTheReservedDirectoryUnderTheRawIngressRoot()
    {
        var root = CreateRoot();
        var provider = Build(root);
        await using var lifetime = provider.ConfigureAwait(false);
        var availability = provider.GetRequiredService<OrbitalElementsAvailability>();

        Assert.AreEqual(OrbitalElementsAvailabilityState.Available,
            await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.IsTrue(File.Exists(Path.Combine(root, ".orbital-elements", SqliteOrbitalElementsStore.DatabaseFileName)));
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), ".orbital-elements"),
            OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(
                new CameraAgentHostOptions { RawIngressRoot = root }).RootDirectory);
    }

    [TestMethod]
    public void StoreAndClientSettingsComeFromTheBoundOptions()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "relative-ingress",
            OrbitalElements = new OrbitalElementsOptions
            {
                RequestTimeoutSeconds = 7,
                MaximumWireBytes = 4096,
                MaximumRetainedSnapshots = 5,
                MinimumRetentionHours = 30,
                MaximumQuarantinedPayloads = 2
            }
        };

        var store = OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(options);
        var client = OrbitalElementsServiceCollectionExtensions.CreateClientSettings(options.OrbitalElements);

        Assert.IsTrue(Path.IsPathFullyQualified(store.RootDirectory));
        Assert.AreEqual(".orbital-elements", Path.GetFileName(store.RootDirectory));
        Assert.AreEqual(SqliteOrbitalElementsStore.MinimumProviderAttemptInterval, store.MinimumAttemptInterval);
        Assert.AreEqual(5, store.MaximumRetainedSnapshots);
        Assert.AreEqual(TimeSpan.FromHours(30), store.MinimumRetention);
        Assert.AreEqual(2, store.MaximumQuarantinedPayloads);
        Assert.AreEqual(TimeSpan.FromSeconds(7), client.RequestTimeout);
        Assert.AreEqual(4096, client.MaximumWireBytes);
        Assert.AreEqual(CelesTrakGpSource.MaximumRawBytes, client.MaximumRawBytes);
        Assert.AreEqual(OrbitalElementsServiceCollectionExtensions.UserAgent, client.UserAgent);
    }

    [TestMethod]
    public async Task StoreInitializerIsRegisteredBeforeTheAutomationRunner()
    {
        var services = Services(CreateRoot());
        var provider = services.BuildServiceProvider();
        await using var lifetime = provider.ConfigureAwait(false);

        var hosted = services.Where(static item => item.ServiceType == typeof(IHostedService))
            .Select(static item => item.ImplementationType)
            .ToList();
        var initializer = hosted.IndexOf(typeof(OrbitalElementsInitializationService));
        var runner = hosted.IndexOf(typeof(LocalAutomationRunnerService));

        Assert.IsGreaterThanOrEqualTo(0, initializer);
        Assert.IsGreaterThan(initializer, runner, "The store must be initialized before the runner's first sweep.");
        Assert.AreEqual(1, hosted.Count(static type => type == typeof(OrbitalElementsInitializationService)));
        var registrations = services.Where(static item => item.ServiceType == typeof(IHostedService)).ToArray();
        var worker = registrations[runner - 1].ImplementationFactory;
        Assert.IsNotNull(worker, "The shared worker lifetime is registered immediately before the runner.");
        Assert.AreSame(provider.GetRequiredService<LocalAutomationOffSweepWorker>(), worker(provider));
        Assert.AreSame(provider.GetRequiredService<OrbitalSnapshotReader>(), provider.GetRequiredService<IOrbitalSnapshotReader>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredAutomationServicesCanStopBeforeOrAfterWorkerDisposal(bool disposeFirst)
    {
        var services = Services(CreateRoot());
        KeepOnlyAutomationHostedServices(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var lifetime = provider.ConfigureAwait(false);
        var automation = provider.GetServices<IHostedService>().ToArray();
        var worker = provider.GetRequiredService<LocalAutomationOffSweepWorker>();
        Assert.AreSame(worker, automation[0]);
        Assert.IsInstanceOfType<LocalAutomationRunnerService>(automation[1]);
        foreach (var service in automation)
        {
            await service.StartAsync(default).ConfigureAwait(false);
        }

        if (disposeFirst)
        {
            worker.Dispose();
        }
        // The host stops in reverse registration order: the runner stops the worker, then the host stops it.
        await automation[1].StopAsync(default).ConfigureAwait(false);
        await automation[0].StopAsync(default).ConfigureAwait(false);
    }

    /// <summary>
    /// The production automation registrations, with an active adapter on the registered worker whose cancellation
    /// callback may throw (#1936). A separately registered disposable singleton is created before the worker, so the
    /// provider disposes it after the worker. The host's instances are checked by identity and by behaviour: the hosted
    /// worker is the registered singleton, the runner's stop waits for that worker's reservation, and the hosted stop
    /// joins the same drain. Both stop callers receive the callback failure only after the adapter releases its
    /// reservation. Provider disposal, synchronous or asynchronous, then reaches the other singleton, and a later stop
    /// still reports the original failure. With <paramref name="callbackThrows"/> false this is the ordinary control.
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Synchronous provider disposal is one of the two cleanup paths under test.")]
    public async Task RegisteredAutomationShutdownCompletesProviderCleanupAfterACallbackFault(bool callbackThrows, bool disposeAsync)
    {
        var services = Services(CreateRoot());
        KeepOnlyAutomationHostedServices(services);
        services.AddSingleton<DisposalProbe>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var probe = provider.GetRequiredService<DisposalProbe>();
        var automation = provider.GetServices<IHostedService>().ToArray();
        var worker = provider.GetRequiredService<LocalAutomationOffSweepWorker>();
        var registered = Signal();
        var cancellationObserved = Signal();
        var release = Signal();
        var failure = callbackThrows ? new ObjectDisposedException("test callback") : null;
        var occurrence = CustomScriptTaskAdapterTests.Occurrence(CustomScriptTaskAdapterTests.Script());
        await AutomationShutdownAssertions.PreserveFirstFailureAsync(async () =>
        {
            Assert.HasCount(2, automation);
            Assert.AreSame(worker, automation[0], "The host stops the registered worker singleton.");
            Assert.IsInstanceOfType<LocalAutomationRunnerService>(automation[1]);
            foreach (var service in automation)
            {
                await service.StartAsync(default).ConfigureAwait(false);
            }
            using (var reservation = worker.TryReserve())
            {
                Assert.IsNotNull(reservation);
                reservation.Dispatch(AutomationShutdownAssertions.ControlledAdapter(registered, cancellationObserved, release, failure),
                    occurrence, occurrence.RunKey, default);
            }
            await registered.Task.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);

            // The host stops in reverse registration order: the runner first, then the hosted worker.
            var runnerStop = automation[1].StopAsync(default);
            await cancellationObserved.Task.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);
            var hostedStop = automation[0].StopAsync(default);
            Assert.IsFalse(runnerStop.IsCompleted, "The runner holds the registered worker and waits for its reservation.");
            Assert.IsFalse(hostedStop.IsCompleted, "The hosted worker joins the same drain.");
            Assert.IsFalse(worker.WhenIdleAsync().IsCompleted, "The adapter keeps its reservation until it returns.");
            Assert.IsFalse(probe.Disposed);
            release.SetResult();
            await AutomationShutdownAssertions.StopReportsAsync(runnerStop, failure).ConfigureAwait(false);
            await AutomationShutdownAssertions.StopReportsAsync(hostedStop, failure).ConfigureAwait(false);
            Assert.IsTrue(worker.WhenIdleAsync().IsCompletedSuccessfully, "The reservation is released before a stop caller returns.");

            AggregateException? disposalFailure = null;
            try
            {
                if (disposeAsync)
                {
                    await provider.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    provider.Dispose();
                }
            }
            catch (AggregateException error)
            {
                disposalFailure = error;
            }
            Assert.IsTrue(probe.Disposed,
                $"Provider cleanup must continue past the worker to the other singleton. Provider disposal reported: {disposalFailure}");
            Assert.IsNull(disposalFailure, "Provider disposal must not receive the stop failure.");
            await AutomationShutdownAssertions.StopReportsAsync(worker.StopAsync(default), failure).ConfigureAwait(false);
        }, async () =>
        {
            release.TrySetResult();
            await worker.WhenIdleAsync().WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Synchronous provider disposal is one of the two cleanup paths under test.")]
    public async Task RegisteredAutomationProviderDisposalWithoutStopDrainsAfterRunnerCancellationFault(bool disposeAsync)
    {
        var services = Services(CreateRoot());
        KeepOnlyAutomationHostedServices(services);
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        var registry = new LocalAutomationOffSweepTests.Registry();
        var logger = new OrbitalTestLogger<LocalAutomationRunnerService>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ILocalAutomationTaskRegistry>(registry);
        services.AddSingleton<ILogger<LocalAutomationRunnerService>>(logger);
        services.AddSingleton<DisposalProbe>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var probe = provider.GetRequiredService<DisposalProbe>();
        var automation = provider.GetServices<IHostedService>().ToArray();
        var worker = provider.GetRequiredService<LocalAutomationOffSweepWorker>();
        var registered = Signal();
        var cancellationObserved = Signal();
        var release = Signal();
        var failure = new InvalidOperationException("test runner cancellation callback");
        var adapter = AutomationShutdownAssertions.ControlledAdapter(registered, cancellationObserved, release, failure);
        registry.Execute = async _ =>
        {
            await adapter(registry.Token).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "done");
        };
        Task? disposal = null;
        await AutomationShutdownAssertions.PreserveFirstFailureAsync(async () =>
        {
            Assert.AreSame(worker, automation[0]);
            Assert.IsInstanceOfType<LocalAutomationRunnerService>(automation[1]);
            foreach (var service in automation)
            {
                await service.StartAsync(default).ConfigureAwait(false);
            }
            var store = provider.GetRequiredService<ILocalAutomationStore>();
            var saved = await store.SaveAsync(new("a-orbital", "disposal test", true, LocalAutomationTaskKind.OrbitalElementsRefresh,
                "synthetic", LocalAutomationTriggerKind.Periodic, 7200, 0, "test", "owner", "test"), default).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status, saved.ReasonCode);
            clock.Advance(TimeSpan.FromHours(2));
            clock.Advance(await clock.WaitForTimerAsync().ConfigureAwait(false));
            await registered.Task.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);

            disposal = Task.Run(async () =>
            {
                if (disposeAsync)
                {
                    await provider.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    provider.Dispose();
                }
            });
            await cancellationObserved.Task.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(250)))
                .ConfigureAwait(false);
            Assert.IsFalse(worker.WhenIdleAsync().IsCompleted, "The adapter still owns its reservation.");
            Assert.IsFalse(probe.Disposed, "The provider must reach the sibling only after the adapter drains.");
            release.SetResult();
            await disposal.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);

            Assert.IsTrue(worker.WhenIdleAsync().IsCompletedSuccessfully);
            Assert.IsTrue(probe.Disposed, "A callback fault must not interrupt provider disposal.");
            var diagnostic = Assert.ContainsSingle(logger.Entries.Where(static entry => entry.Exception is AggregateException));
            Assert.AreEqual(LogLevel.Warning, diagnostic.Level);
            Assert.AreSame(failure, ((AggregateException)diagnostic.Exception!).Flatten().InnerExceptions.Single());
        }, async () =>
        {
            release.TrySetResult();
            await worker.WhenIdleAsync().WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(false);
            // Await a running disposal without allowing its already-observed exception to replace the assertion.
            if (disposal is not null)
            {
                await disposal.WaitAsync(AutomationShutdownAssertions.Deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            await provider.DisposeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task NamedClientHasNoRetryRedirectCookieOrLoggingHandler()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var handler = factory.CreateHandler(CelesTrakGpSource.HttpClientName);
        var delegating = new List<string>();
        while (handler is DelegatingHandler outer)
        {
            delegating.Add(outer.GetType().Name);
            handler = outer.InnerHandler!;
        }

        CollectionAssert.AreEqual(OnlyLifetimeWrapper, delegating,
            "Only the factory's lifetime wrapper may sit in front of the primary handler: " + string.Join(", ", delegating));
        var primary = (SocketsHttpHandler)handler;
        Assert.IsFalse(primary.AllowAutoRedirect);
        Assert.IsFalse(primary.UseCookies);
        Assert.AreEqual(System.Net.DecompressionMethods.None, primary.AutomaticDecompression);
        Assert.AreEqual(1, primary.MaxConnectionsPerServer);
        Assert.IsNull(primary.ActivityHeadersPropagator);
        Assert.AreEqual(0, primary.MaxResponseDrainSize);
    }

    [TestMethod]
    public async Task NamedClientHasAnInfiniteTimeoutAndNoDefaultHeaders()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CelesTrakGpSource.HttpClientName);

        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout, "The client's TimeProvider deadline governs the request.");
        Assert.IsNull(client.BaseAddress);
        Assert.IsEmpty(client.DefaultRequestHeaders);
    }

    [TestMethod]
    [DataRow(null, "HVO.SkyMonitor.CameraAgent")]
    [DataRow("", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("   ", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("+0123abcd", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.2.3", "HVO.SkyMonitor.CameraAgent/1.2.3")]
    [DataRow("1.2.3+0123abcd", "HVO.SkyMonitor.CameraAgent/1.2.3")]
    [DataRow("1.2.3-beta.1+sha.0123abcd", "HVO.SkyMonitor.CameraAgent/1.2.3-beta.1")]
    [DataRow("1.0 beta", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0/2", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0é", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0\t", "HVO.SkyMonitor.CameraAgent")]
    public void UserAgentIsTheProductTokenAndAVersionWithoutBuildMetadata(string? informationalVersion, string expected)
        => Assert.AreEqual(expected, OrbitalElementsServiceCollectionExtensions.CreateUserAgent(informationalVersion));

    [TestMethod]
    public void AssemblyUserAgentIsValidAndCarriesNoSourceRevision()
    {
        var userAgent = OrbitalElementsServiceCollectionExtensions.UserAgent;

        Assert.IsTrue(CelesTrakGpClient.IsValidUserAgent(userAgent), userAgent);
        Assert.StartsWith(OrbitalElementsServiceCollectionExtensions.ProductToken, userAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("+", userAgent, StringComparison.Ordinal);
    }

    private static ServiceCollection Services(string rawIngressRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSiloTestInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CameraAgent:RawIngressRoot"] = rawIngressRoot,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        services.AddSingleton<IDeploymentLocationProtector>(new OrbitalPassThroughLocationProtector());
        return services;
    }

    private static ServiceProvider Build(string rawIngressRoot)
        => Services(rawIngressRoot).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    /// <summary>Retains the production worker and runner hosted descriptors without starting unrelated services.</summary>
    private static void KeepOnlyAutomationHostedServices(ServiceCollection services)
    {
        var hosted = services.Where(static item => item.ServiceType == typeof(IHostedService)).ToArray();
        var runnerIndex = Array.FindIndex(hosted, static item => item.ImplementationType == typeof(LocalAutomationRunnerService));
        Assert.IsGreaterThan(0, runnerIndex);
        foreach (var registration in hosted)
        {
            if (registration != hosted[runnerIndex - 1] && registration != hosted[runnerIndex])
            {
                services.Remove(registration);
            }
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A provider-owned disposable singleton whose cleanup the shutdown regression observes (#1936).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by the dependency injection provider under test.")]
    internal sealed class DisposalProbe : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}

/// <summary>The host's Data Protection location protector, which the automation registry's graph needs, stood in for.</summary>
internal sealed class OrbitalPassThroughLocationProtector : IDeploymentLocationProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
}
