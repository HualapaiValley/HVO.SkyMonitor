using System.Collections.Immutable;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

/// <summary>Real stores, policy, runner and operations; only time and HTTP are synthetic.</summary>
internal sealed class SpaceWeatherTestHost : IDisposable
{
    internal static DateTimeOffset Start => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "swpc-host-" + Guid.NewGuid().ToString("N"));
    internal ManualTimeProvider Clock { get; } = new(Start);
    internal List<(int Id, string Text)> Logs { get; } = [];
    internal SqliteSwpcStore Sqlite { get; }
    internal ObservedStore Store { get; }
    internal TestClient Client { get; }
    internal SpaceWeatherWriteGate Gate { get; } = new();
    internal SpaceWeatherTelemetry Telemetry { get; }
    internal SwpcAttemptRunner Runner { get; }
    internal SpaceWeatherRefreshJob Job { get; }
    internal SpaceWeatherAutomationAdapter Adapter { get; }
    internal SpaceWeatherOperations Operations { get; }
    internal SpaceWeatherHealthEvaluator Health { get; }
    internal SpaceWeatherAvailability Availability { get; }
    internal SqliteLocalAutomationStore Automation { get; }
    internal IOptions<CameraAgentHostOptions> Options { get; }
    internal Registry Tasks { get; }
    internal LocalAutomationOffSweepWorker Worker { get; } = new();

    internal SpaceWeatherTestHost(bool enabled = true, SwpcProduct[]? products = null)
    {
        Options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Root,
            SpaceWeather = new SpaceWeatherOptions { Enabled = enabled, Products = products ?? Enum.GetValues<SwpcProduct>() }
        });
        Sqlite = new(SpaceWeatherServiceCollectionExtensions.CreateStoreSettings(Options.Value), Logger<SqliteSwpcStore>());
        Store = new(Sqlite); Client = new(Clock); Telemetry = new(Clock);
        Availability = new(Store, Logger<SpaceWeatherAvailability>());
        Runner = new(Store, new SpaceWeatherMeasuredClient(Client, Telemetry, Clock), Clock, SwpcJitter.None);
        Job = new(Store, Runner, Availability, Gate, Options, Clock, Telemetry, Logger<SpaceWeatherRefreshJob>());
        Adapter = new(Job); Tasks = new(Adapter);
        Automation = new(Tasks, new Captures(), Options, Clock, NullLogger<SqliteLocalAutomationStore>.Instance);
        Operations = new(Store, Runner, Availability, Gate, Job, Automation, Options, Clock, Logger<SpaceWeatherOperations>());
        Health = new(Store, Availability, Options, Clock);
    }

    internal async Task InitializeAsync()
    {
        Assert.IsTrue(await Availability.EnsureAvailableAsync(default).ConfigureAwait(false));
        await Automation.InitializeAsync(default).ConfigureAwait(false);
    }
    internal async Task SaveAsync(bool enabled = true)
    {
        var result = await Automation.SaveAsync(new(SpaceWeatherAutomationAdapter.DefinitionId, "SWPC refresh", enabled,
            LocalAutomationTaskKind.SpaceWeatherRefresh, "swpc", LocalAutomationTriggerKind.Periodic, 120, 0, "save", "owner", "configure"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, result.Status, result.ReasonCode);
    }
    internal ValueTask<LocalAutomationExecution> RunAsync(SwpcAttemptTrigger trigger = SwpcAttemptTrigger.Scheduled, CancellationToken ct = default)
        => Job.ExecuteAsync("test-run", trigger, Clock.GetUtcNow(), ct);
    internal LocalAutomationRunnerService CreateRunner() => new(Automation, Tasks, new Captures(), Options, Clock,
        NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: Worker);
    internal ValueTask<SwpcStoreView> ViewAsync() => Store.ReadAsync(Clock.GetUtcNow(), default);
    internal async Task<DateTimeOffset> HaltAsync(int status = 403)
    {
        Client.Status = status;
        await RunAsync(SwpcAttemptTrigger.Manual).ConfigureAwait(false);
        var view = await ViewAsync().ConfigureAwait(false);
        return status == 403 ? view.ProviderHalt!.SinceUtc : view.Products[SwpcProduct.Alerts].ProductHalt!.SinceUtc;
    }
    internal long Count(string table)
    {
        Assert.IsTrue(table is "swpc_receipts" or "swpc_acks" or "swpc_attempts");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(Root, ".space-weather", "space-weather.db") + ";Mode=ReadOnly;Pooling=False");
        db.Open(); using var command = db.CreateCommand();
#pragma warning disable CA2100 // Closed table vocabulary asserted above; never user input.
        command.CommandText = "SELECT count(*) FROM " + table + ";";
#pragma warning restore CA2100
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    internal ILogger<T> Logger<T>() => new TestLogger<T>(Logs);
    public void Dispose()
    {
        Worker.StopAsync(default).GetAwaiter().GetResult(); Worker.Dispose();
        Job.Dispose(); Runner.Dispose(); Automation.Dispose(); Telemetry.Dispose(); Availability.Dispose(); Gate.Dispose(); Sqlite.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    internal sealed class Registry(SpaceWeatherAutomationAdapter adapter) : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [adapter.Describe()];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => adapter.Validate(definition);
        public bool SupportsRunNow(LocalAutomationDefinition definition) => true;
        public bool RunsOffSweep(LocalAutomationDefinition definition) => true;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string key, CancellationToken ct)
            => adapter.ExecuteAsync(LocalAutomationOccurrence.Create(new(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null), key, Start), ct);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken ct) => adapter.ExecuteAsync(occurrence, ct);
    }
    private sealed class Captures : ILocalAutomationCaptureSequenceSource
    {
        public ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken) => ValueTask.FromResult<long?>(null);
    }
    private sealed class TestLogger<T>(List<(int Id, string Text)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.IsNull(exception, "SWPC logs never carry exception messages or stack traces.");
            lock (entries) entries.Add((eventId.Id, formatter(state, exception)));
        }
    }

    internal sealed class TestClient(ManualTimeProvider clock) : ISwpcClient
    {
        internal int Status { get; set; } = 200;
        internal int Calls { get; private set; }
        internal TimeSpan AdvancePerFetch { get; set; }
        internal Func<SwpcProduct, byte[]> Body { get; set; } = Capture;
        internal Func<SwpcProduct, CancellationToken, ValueTask<SwpcHttpResponse>>? Fetch { get; set; }
        internal static byte[] Capture(SwpcProduct product) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "space-weather", "swpc-v1", "capture-20261019T1857Z", product switch
            {
                SwpcProduct.Alerts => "alerts.json",
                SwpcProduct.Scales => "noaa-scales.json",
                SwpcProduct.KpForecast => "noaa-planetary-k-index-forecast.json",
                _ => "3-day-forecast.txt"
            }));
        public ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (Fetch is not null) return Fetch(product, ct);
            var start = clock.GetUtcNow(); clock.Advance(AdvancePerFetch);
            return ValueTask.FromResult(new SwpcHttpResponse(product, start, clock.GetUtcNow(), SwpcTransportFailure.None,
                Status, validators?.EntityTag is not null, SwpcSource.MediaType(product), "validator-sentinel", null, null,
                Status == 200 ? Body(product) : default));
        }
    }

    /// <summary>Fault injection and call order, forwarding all durable behavior to SQLite.</summary>
    internal sealed class ObservedStore(ISwpcStore inner) : ISwpcStore
    {
        internal List<string> Calls { get; } = [];
        internal Exception? RetentionFailure { get; set; }
        internal Exception? ReadFailure { get; set; }
        internal bool AckConflictOnce { get; set; }
        internal Exception? HealthFailureOnce { get; set; }
        internal Func<DateTimeOffset, CancellationToken, ValueTask<SwpcStoreView>>? ReadOverride { get; set; }
        internal Func<DateTimeOffset, CancellationToken, ValueTask<SwpcStoreHealth>>? HealthOverride { get; set; }
        internal int HealthReads { get; private set; }
        internal Func<SwpcStoreHealth, SwpcStoreHealth>? HealthTransform { get; set; }
        public ValueTask InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public ValueTask<SwpcStoreView> ReadAsync(DateTimeOffset atUtc, CancellationToken ct)
        {
            Calls.Add("read");
            if (ReadFailure is { } failure) throw failure;
            if (ReadOverride is { } read) return read(atUtc, ct);
            return inner.ReadAsync(atUtc, ct);
        }
        public ValueTask<SwpcWriteResult> BeginAttemptAsync(SwpcAttemptStart request, CancellationToken ct)
        { Calls.Add("begin"); return inner.BeginAttemptAsync(request, ct); }
        public ValueTask<SwpcWriteResult> CompleteAttemptAsync(SwpcAttemptCompletion request, SwpcCommitProjection project, CancellationToken ct)
            => inner.CompleteAttemptAsync(request, project, ct);
        public ValueTask<SwpcWriteResult> ChangeHaltAsync(SwpcHaltCommand request, CancellationToken ct)
        { Calls.Add("halt"); return inner.ChangeHaltAsync(request, ct); }
        public async ValueTask<SwpcWriteResult> AcknowledgeAsync(SwpcAcknowledgeCommand request, CancellationToken ct)
        {
            Calls.Add("ack");
            if (AckConflictOnce)
            {
                AckConflictOnce = false;
                return new(SwpcWriteStatus.Conflict, "space-weather.revision-conflict", await inner.ReadAsync(request.AtUtc, ct).ConfigureAwait(false), 0, [], []);
            }
            return await inner.AcknowledgeAsync(request, ct).ConfigureAwait(false);
        }
        public ValueTask<SwpcPurgeResult> ApplyRetentionAsync(DateTimeOffset atUtc, CancellationToken ct)
        {
            Calls.Add("retention");
            if (RetentionFailure is { } failure) throw failure;
            return inner.ApplyRetentionAsync(atUtc, ct);
        }
        public ValueTask<SwpcDiagnosticsPage> ReadDiagnosticsAsync(SwpcDiagnosticsQuery request, CancellationToken ct) => inner.ReadDiagnosticsAsync(request, ct);
        public async ValueTask<SwpcStoreHealth> ReadHealthAsync(DateTimeOffset atUtc, CancellationToken ct)
        {
            HealthReads++; Calls.Add("health");
            if (HealthFailureOnce is { } failure) { HealthFailureOnce = null; throw failure; }
            var health = HealthOverride is { } read ? await read(atUtc, ct).ConfigureAwait(false)
                : await inner.ReadHealthAsync(atUtc, ct).ConfigureAwait(false);
            return HealthTransform?.Invoke(health) ?? health;
        }
        public void Dispose() { }
    }
}
