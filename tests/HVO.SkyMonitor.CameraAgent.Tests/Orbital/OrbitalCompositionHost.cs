using System.Collections.Concurrent;
using System.Net;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The orbital feature as <c>AddCameraAgentInfrastructure</c> composes it: the real automation store, runner, registry,
/// orbital store, availability initializer, adapter, job, CelesTrak client and normalizer, operations, health and
/// reader. Only the clock and the client's primary HTTP handler are substituted. The handler answers from a script
/// and its substitution is verified before anything runs, so no test can reach the network.
/// </summary>
internal sealed class OrbitalCompositionHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly OrbitalElementsInitializationService _initializer;

    private OrbitalCompositionHost(ServiceProvider provider, IServiceCollection services, ScriptedCelesTrakHandler handler,
        SignalingTimeProvider clock, string rawIngressRoot)
    {
        _provider = provider;
        Services = services;
        Handler = handler;
        Clock = clock;
        RawIngressRoot = rawIngressRoot;
        _initializer = ActivatorUtilities.CreateInstance<OrbitalElementsInitializationService>(provider);
        Runner = ActivatorUtilities.CreateInstance<LocalAutomationRunnerService>(provider);
    }

    public IServiceCollection Services { get; }

    public IServiceProvider Provider => _provider;

    public ScriptedCelesTrakHandler Handler { get; }

    public SignalingTimeProvider Clock { get; }

    public string RawIngressRoot { get; }

    public string OrbitalRoot => Path.Combine(RawIngressRoot, OrbitalElementsServiceCollectionExtensions.StoreDirectoryName);

    public LocalAutomationRunnerService Runner { get; }

    public SqliteLocalAutomationStore Automation => _provider.GetRequiredService<SqliteLocalAutomationStore>();

    public SqliteOrbitalElementsStore Store => _provider.GetRequiredService<SqliteOrbitalElementsStore>();

    public OrbitalElementsOperations Operations => _provider.GetRequiredService<OrbitalElementsOperations>();

    public OrbitalElementsHealthEvaluator Health => _provider.GetRequiredService<OrbitalElementsHealthEvaluator>();

    public IOrbitalSnapshotReader Reader => _provider.GetRequiredService<IOrbitalSnapshotReader>();

    /// <summary>
    /// Composes and starts the feature the way the host does: the orbital initializer first, then the automation
    /// store the runner initializes. The runner's own polling loop is never started; tests sweep explicitly. A
    /// <paramref name="meter"/> replaces only the telemetry's meter, so a listener can observe exactly this host.
    /// </summary>
    public static async Task<OrbitalCompositionHost> StartAsync(string rawIngressRoot, SignalingTimeProvider clock,
        bool? enabled = true, ScriptedCelesTrakHandler? handler = null, System.Diagnostics.Metrics.Meter? meter = null)
    {
        handler ??= new ScriptedCelesTrakHandler();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CameraAgent:RawIngressRoot"] = rawIngressRoot,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
        };
        if (enabled is { } value)
        {
            settings["CameraAgent:OrbitalElements:Enabled"] = value ? "true" : "false";
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IDeploymentLocationProtector>(new OrbitalPassThroughLocationProtector());
        services.AddHttpClient(CelesTrakGpSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        if (meter is not null)
        {
            services.AddSingleton(_ => new OrbitalElementsTelemetry(clock, meter));
        }
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        OrbitalCompositionHost? host = null;
        try
        {
            RequireScriptedPrimaryHandler(provider, handler);
            host = new OrbitalCompositionHost(provider, services, handler, clock, rawIngressRoot);
            await host._initializer.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await host.Automation.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return host;
        }
        catch
        {
            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Saves the reserved definition through the real automation store and its real adapter validation.</summary>
    public async Task<LocalAutomationDefinitionState> SaveDefinitionAsync(int interval = OrbitalElementsAutomationAdapter.MinimumIntervalSeconds,
        bool enabled = true, long expectedVersion = 0)
    {
        var saved = await Automation.SaveAsync(new LocalAutomationSaveRequest(OrbitalElementsAutomationAdapter.DefinitionId,
            "Orbital elements refresh", enabled, LocalAutomationTaskKind.OrbitalElementsRefresh, CelesTrakGpSource.DatasetKey,
            LocalAutomationTriggerKind.Periodic, interval, expectedVersion, Guid.NewGuid().ToString("N"), "owner",
            "orbital schedule"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status, saved.ReasonCode);
        return saved.State.Definitions.Single(static item =>
            item.Definition.DefinitionId == OrbitalElementsAutomationAdapter.DefinitionId);
    }

    public async Task<LocalAutomationDefinitionState> DefinitionAsync()
        => (await Automation.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions.Single(static item =>
            item.Definition.DefinitionId == OrbitalElementsAutomationAdapter.DefinitionId);

    public async Task<IReadOnlyList<LocalAutomationRun>> RunsAsync()
        => (await Automation.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs
            .Where(static run => run.DefinitionId == OrbitalElementsAutomationAdapter.DefinitionId)
            .OrderBy(static run => run.Sequence)
            .ToArray();

    // Existing scenario assertions inspect terminal outcomes, so this helper explicitly drains
    // the newly off-sweep work. Worker-specific tests call Runner.SweepAsync directly.
    public async Task SweepAsync()
    {
        await Runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await _provider.GetRequiredService<LocalAutomationOffSweepWorker>().WhenIdleAsync().ConfigureAwait(false);
    }

    /// <summary>Moves the clock to <paramref name="instant"/>; it never moves backwards.</summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        var delta = instant - Clock.GetUtcNow();
        Assert.IsTrue(delta >= TimeSpan.Zero, "The test clock never moves backwards.");
        Clock.Advance(delta);
    }

    public Task<OrbitalProviderState> StateAsync() => Store.GetStateAsync(CancellationToken.None).AsTask();

    public Task<OrbitalElementsCommandResult> RefreshAsync(string key)
        => Operations.RefreshAsync(new OrbitalElementsRefreshRequest(key, "owner", "operator refresh"), CancellationToken.None)
            .AsTask();

    public long Scalar(string sql) => Convert.ToInt64(OrbitalElementsStoreTestData.Scalar(OrbitalRoot, sql),
        System.Globalization.CultureInfo.InvariantCulture);

    public string? Text(string sql) => OrbitalElementsStoreTestData.Scalar(OrbitalRoot, sql) as string;

    public async ValueTask DisposeAsync()
    {
        _initializer.Dispose();
        await Runner.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Runner.Dispose();
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private static void RequireScriptedPrimaryHandler(IServiceProvider provider, HttpMessageHandler expected)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(CelesTrakGpSource.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }
        if (!ReferenceEquals(handler, expected))
        {
            throw new InvalidOperationException("The CelesTrak primary handler was not substituted; refusing to run.");
        }
    }
}

/// <summary>
/// A CelesTrak stand-in that answers each request from the next scripted response and records it. With nothing
/// scripted it fails the request in-process, so an unexpected attempt is visible as a count, never as network traffic.
/// </summary>
internal sealed class ScriptedCelesTrakHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public int Count => _requests.Count;

    public TaskCompletionSource RequestStarted { get; private set; } = NewSignal();

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _script.Enqueue(respond);

    public void EnqueueBody(byte[] body) => Enqueue((_, _) => Task.FromResult(
        CelesTrakGpTestData.Response(HttpStatusCode.OK, new TrackingContent(body))));

    public void EnqueueFixture(string name) => EnqueueBody(CelesTrakGpTestData.ReadFixture(name));

    public void EnqueueStatus(int status) => Enqueue((_, _) => Task.FromResult(
        CelesTrakGpTestData.Response((HttpStatusCode)status, TrackingContent.Of("{\"provider\":\"body never read\"}"))));

    public void EnqueueTransportFailure() => Enqueue(static (_, _) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic connection refused",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));

    /// <summary>Never answers; only the client's own deadline or the caller's cancellation ends the request.</summary>
    public void EnqueueHang() => Enqueue(static async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    });

    public void ResetSignal() => RequestStarted = NewSignal();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri,
            request.Headers.NonValidated.Select(static h => (h.Key, Values: string.Join(", ", h.Value))).ToArray(),
            request.Content is not null));
        RequestStarted.TrySetResult();
        return _script.TryDequeue(out var respond)
            ? respond(request, cancellationToken)
            : Task.FromException<HttpResponseMessage>(new InvalidOperationException("No scripted CelesTrak response."));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
