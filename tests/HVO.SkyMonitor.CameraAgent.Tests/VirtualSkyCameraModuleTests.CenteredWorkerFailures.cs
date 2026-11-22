using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredRegistrationHostedLoop_IsolatesRetainedInputFailure(bool invalidEvidence)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-registration-failure");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new CenteredClock();
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var (first, next) = await SeedCenteredWorkerWindowsAsync(provider, root, clock, token).ConfigureAwait(false);
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var frozen = await store.ReadCenteredWindowDispositionAsync(first.WindowIdentity, token).ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            Assert.IsTrue(frozen.Closure.CanRegister);

            Exception failure = invalidEvidence
                ? new InvalidDataException("The retained registration input differs from its frozen pin.")
                : new UnauthorizedAccessException("The worker cannot read the retained registration input.");
            var retained = provider.GetRequiredService<ICenteredWindowRegistrationSource>();
            var source = new CenteredRetainedInputFailure(retained, first.ReferenceCaptureId, failure);
            var logger = new CenteredWorkerFailureLogger<CenteredWindowRegistrationWorker>(6192);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            using var worker = new CenteredWindowRegistrationWorker(store, provider.GetRequiredService<CaptureProcessingPersistence>(),
                provider.GetRequiredService<ICenteredWindowJournal>(), source, clock, logger, ingress);
            using var host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(worker)).Build();
            await host.StartAsync(token).ConfigureAwait(false);
            try
            {
                var healthy = await WaitForCenteredWorkerDispositionAsync(worker, store, next.WindowIdentity,
                    disposition => disposition.Publication is not null, token).ConfigureAwait(false);
                Assert.AreEqual(next.ReferenceCaptureId, healthy.Publication!.ReferenceCaptureId);
                var reported = await logger.Failure.Task.WaitAsync(token).ConfigureAwait(false);
                Assert.AreSame(failure, reported.Exception);
                StringAssert.Contains(reported.Message, first.WindowIdentity, StringComparison.Ordinal);
                Assert.IsFalse(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);

                var failed = await store.ReadCenteredWindowDispositionAsync(first.WindowIdentity, token).ConfigureAwait(false);
                Assert.IsNotNull(failed);
                Assert.AreEqual(frozen.Closure.Identity, failed.Closure.Identity);
                Assert.IsNull(failed.Publication);
                Assert.AreEqual(invalidEvidence, failed.IsTerminal);
                if (!invalidEvidence)
                    Assert.IsTrue(await ReadCenteredTestHoldCountAsync(root, first.ReferenceCaptureId, token).ConfigureAwait(false) > 0);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            clock.UtcNow = frozen.Closure.Schedule.RegistrationDeadlineUtc;
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            var settled = await store.ReadCenteredWindowDispositionAsync(first.WindowIdentity, token).ConfigureAwait(false);
            Assert.IsNotNull(settled);
            Assert.AreEqual(frozen.Closure.Identity, settled.Closure.Identity);
            Assert.IsNull(settled.Publication);
            Assert.AreEqual(invalidEvidence ? "registration.invalid-retained-evidence" : "registration-deadline-expired",
                settled.FailureReason);
            Assert.AreEqual(0L, await ReadCenteredTestHoldCountAsync(root, first.ReferenceCaptureId, token).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredClosureHostedLoop_IsolatesAndRetriesInterruptedClose()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-close-failure");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var clock = new CenteredClock();
        var fault = new CenteredClosePermissionFailure();
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock,
                services => services.AddSingleton<ICaptureProcessingFaultInjector>(fault));
            var (first, next) = await SeedCenteredWorkerWindowsAsync(provider, root, clock, token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var journal = provider.GetRequiredService<ICenteredWindowJournal>();
            var logger = new CenteredWorkerFailureLogger<CenteredWindowCoordinator>(6191);
            var wakeup = provider.GetRequiredService<CenteredWindowWakeup>();
            using var coordinator = new CenteredWindowCoordinator(store, journal, clock, logger,
                provider.GetRequiredService<RawCaptureIngress>(), wakeup);
            fault.BlockedWindow = first.WindowIdentity;
            using var host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(coordinator)).Build();
            await host.StartAsync(token).ConfigureAwait(false);
            try
            {
                var healthy = await WaitForCenteredWorkerDispositionAsync(coordinator, store, next.WindowIdentity,
                    disposition => disposition.Closure.CanRegister, token).ConfigureAwait(false);
                Assert.AreEqual(next.DeadlineUtc, healthy.Closure.EvidenceCutoffUtc);
                var reported = await logger.Failure.Task.WaitAsync(token).ConfigureAwait(false);
                Assert.IsInstanceOfType<UnauthorizedAccessException>(reported.Exception);
                StringAssert.Contains(reported.Message, first.WindowIdentity, StringComparison.Ordinal);
                Assert.IsNull(await store.ReadCenteredWindowDispositionAsync(first.WindowIdentity, token).ConfigureAwait(false));
                Assert.IsTrue(await ReadCenteredTestHoldCountAsync(root, first.ReferenceCaptureId, token).ConfigureAwait(false) > 0);

                fault.BlockedWindow = null;
                wakeup.NotifyCommitted();
                var recovered = await WaitForCenteredWorkerDispositionAsync(coordinator, store, first.WindowIdentity,
                    disposition => disposition.Closure.CanRegister, token).ConfigureAwait(false);
                Assert.AreEqual(first.DeadlineUtc, recovered.Closure.EvidenceCutoffUtc);
                Assert.AreEqual(first.ReferenceCaptureId, recovered.Closure.Center.Input!.Key.CaptureId);
                Assert.IsFalse(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(CenteredWindowSnapshot First, CenteredWindowSnapshot Next)> SeedCenteredWorkerWindowsAsync(
        ServiceProvider provider, string root, CenteredClock clock, CancellationToken token)
    {
        var start = clock.UtcNow;
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        await using var camera = module.ConfigureAwait(false);
        await module.InitializeAsync(active, token).ConfigureAwait(false);
        var reader = provider.GetRequiredService<ICenteredWindowReader>();
        var windows = new List<CenteredWindowSnapshot>();
        // Keep the two registration plans from sharing a retained exposure.
        foreach (var scheduled in new[] { start, start.AddSeconds(12) })
        {
            var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, scheduled, 1, token)
                .ConfigureAwait(false);
            var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            var window = await reader.ReadAsync(receipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(window);
            Assert.IsNull(window.ClosureIdentity);
            windows.Add(window);
        }
        clock.UtcNow = windows[1].DeadlineUtc;
        return (windows[0], windows[1]);
    }

    private static async Task<CenteredWindowDisposition> WaitForCenteredWorkerDispositionAsync(BackgroundService worker,
        SqliteCaptureProcessingStore store, string identity, Func<CenteredWindowDisposition, bool> matches, CancellationToken token)
    {
        var execution = worker.ExecuteTask;
        Assert.IsNotNull(execution);
        while (!execution.IsCompleted)
        {
            var disposition = await store.ReadCenteredWindowDispositionAsync(identity, token).ConfigureAwait(false);
            if (disposition is not null && matches(disposition))
                return disposition;
            await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
        }
        await execution.ConfigureAwait(false);
        throw new AssertFailedException("The hosted centered worker exited before settling the window.");
    }

    private sealed class CenteredRetainedInputFailure(ICenteredWindowRegistrationSource retained, Guid capture, Exception failure)
        : ICenteredWindowRegistrationSource
    {
        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear,
            CancellationToken cancellationToken) => retained.ReadLinearAsync(key, linear, cancellationToken);

        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            retained.ReadExposureAsync(key, cancellationToken);

        public ValueTask<ProcessingArtifact?> ReadArtifactAsync(ExposureKey key, CenteredStackArtifactPin pin,
            CancellationToken cancellationToken) => key.CaptureId == capture
                ? ValueTask.FromException<ProcessingArtifact?>(failure)
                : retained.ReadArtifactAsync(key, pin, cancellationToken);
    }

    private sealed class CenteredClosePermissionFailure : ICaptureProcessingFaultInjector
    {
        private string? _blockedWindow;

        internal string? BlockedWindow { set => Volatile.Write(ref _blockedWindow, value); }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.BeforeCenteredWindowCloseCommit && nodeId == Volatile.Read(ref _blockedWindow))
                throw new UnauthorizedAccessException("The coordinator cannot commit the centered window close.");
        }
    }

    private sealed class CenteredWorkerFailureLogger<T>(int eventToCapture) : ILogger<T>
    {
        internal TaskCompletionSource<(Exception Exception, string Message)> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == eventToCapture && exception is not null)
                Failure.TrySetResult((exception, formatter(state, exception)));
        }
    }
}
