using System.Net;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class InstallationVerificationStartupTests
{
    [TestMethod]
    public async Task DeferredConfigurationFailureMakesRealHostUnhealthyThenRecoversOnRetry()
    {
        using var allowRetry = new ManualResetEventSlim();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        FailOnceStartupPipeline? pipeline = null;
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            // Keep the complete production graph. Fail only the initializer's first
            // graph validation, before it publishes capture configuration.
            services.RemoveAll<ICaptureProcessingPipelineFactory>();
            services.AddSingleton<ICaptureProcessingPipelineFactory>(provider => pipeline = new FailOnceStartupPipeline(
                ActivatorUtilities.CreateInstance<CaptureProcessingPipelineFactory>(provider), allowRetry));
            services.AddLogging(builder => builder.AddProvider(new InitializationFailureLogs(failure)));
        }, scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var message = await failure.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            Assert.IsFalse(message.Contains("synthetic-private", StringComparison.Ordinal));
            var capture = host.Services.GetRequiredService<CaptureLaneState>();
            Assert.AreEqual(CaptureLaneAvailability.Unhealthy, capture.Snapshot.Availability);
            using var unhealthy = await client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        }
        finally { allowRetry.Set(); }
        _ = await host.Services.GetRequiredService<ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(timeout.Token).ConfigureAwait(false);
        while (host.Services.GetRequiredService<CaptureLaneState>().Snapshot.OperationalFailureCount != 0)
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        using var recovered = await client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        Assert.IsGreaterThanOrEqualTo(2, pipeline!.Calls);
    }

    private sealed class FailOnceStartupPipeline(ICaptureProcessingPipelineFactory inner, ManualResetEventSlim allowRetry)
        : ICaptureProcessingPipelineFactory
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new IOException("synthetic-private initialization failure");
            if (!allowRetry.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Synthetic retry was not released.");
            return inner.CreateGraph(config);
        }
        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => inner.PreviewPlan(config);
    }

    private sealed class InitializationFailureLogs(TaskCompletionSource<string> failure) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new InitializationFailureLogger(failure);
        public void Dispose() { }
    }

    private sealed class InitializationFailureLogger(TaskCompletionSource<string> failure) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 7312) failure.TrySetResult(formatter(state, exception));
        }
    }
}
