using System.Diagnostics;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class FileCameraAgentScheduleReuseTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnchangedObserverExpandsOncePerIndependentLoad(bool solar)
    {
        using var fixture = new LoaderFixture(solar);
        using var expansions = new ExpansionCounter();
        var loader = fixture.CreateLoader();
        var first = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, expansions.Count);
        var second = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, expansions.Count, "A successful load cannot validate another invocation.");
        Assert.AreEqual(first.ResolveObservatory(), second.ResolveObservatory());
        Assert.AreEqual(2, fixture.Store.Calls);
    }

    [TestMethod]
    [DataRow("latitude")]
    [DataRow("longitude")]
    [DataRow("elevation")]
    [DataRow("timezone")]
    public async Task EveryChangedObserverComponentStillExpandsTwice(string component)
    {
        using var fixture = new LoaderFixture(solar: true);
        fixture.Store.Observer = component switch
        {
            "latitude" => LoaderFixture.Observer with { LatitudeDegrees = 36 },
            "longitude" => LoaderFixture.Observer with { LongitudeDegrees = -113 },
            "elevation" => LoaderFixture.Observer with { ElevationMeters = 1600 },
            _ => LoaderFixture.Observer with { TimeZoneId = "America/Phoenix" }
        };
        using var expansions = new ExpansionCounter();
        var config = await fixture.CreateLoader().LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, expansions.Count);
        Assert.AreEqual(fixture.Store.Observer, config.ResolveObservatory());
    }

    [TestMethod]
    public async Task InvalidEffectiveTimeZoneIsRejectedAfterConfiguredObserverValidation()
    {
        using var fixture = new LoaderFixture(solar: false);
        fixture.Store.Observer = LoaderFixture.Observer with { TimeZoneId = "HVO/invalid-timezone" };
        using var expansions = new ExpansionCounter();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.CreateLoader().LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("Capture schedule expansion is invalid.", error.Message);
        Assert.AreEqual(1, expansions.Count);
        Assert.AreEqual(1, fixture.Store.Calls);
    }

    [TestMethod]
    public async Task FailedAndCanceledLoadsCannotAuthorizeLaterWork()
    {
        using var fixture = new LoaderFixture(solar: false);
        using var expansions = new ExpansionCounter();
        var loader = fixture.CreateLoader();
        fixture.Store.Cancel = true;
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => loader.LoadAsync(canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, expansions.Count);
        fixture.Store.Cancel = false;
        _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, expansions.Count);
        using var alreadyCanceled = new CancellationTokenSource();
        await alreadyCanceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loader.LoadAsync(alreadyCanceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(2, expansions.Count);
        await fixture.EditAsync(node => node["schedule"]!["setpointProfiles"]![0]!["exposure"] = "00:00:00").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, expansions.Count, "Invalid configured schedules must fail before retained initialization.");
        Assert.AreEqual(2, fixture.Store.Calls);
    }

    [TestMethod]
    [DataRow("schedule")]
    [DataRow("envelope")]
    [DataRow("sensor-range")]
    public async Task ActualConfigurationEditsAreRevalidated(string input)
    {
        using var fixture = new LoaderFixture(solar: false);
        using var expansions = new ExpansionCounter();
        var loader = fixture.CreateLoader();
        _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.EditAsync(node =>
        {
            if (input == "schedule") node["schedule"]!["setpointProfiles"]![0]!["gain"] = 100000;
            else if (input == "envelope") node["rig"]!["pipeline"]!["envelope"]!["maxGain"] = -1;
            else
            {
                node["rig"]!["sensor"]!["simulationResponse"] = new JsonObject
                {
                    ["minimumGainControl"] = 0,
                    ["maximumGainControl"] = -1
                };
            }
        }).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, expansions.Count);
        Assert.AreEqual(1, fixture.Store.Calls);
    }

    [TestMethod]
    public async Task ConcurrentLoadsEachValidateBeforeAndAfterTheirOwnInitialization()
    {
        using var fixture = new LoaderFixture(solar: false);
        fixture.Store.Delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expansions = new ExpansionCounter();
        var loader = fixture.CreateLoader();
        var pending = Enumerable.Range(0, 4).Select(_ => loader.LoadAsync(CancellationToken.None)).ToArray();
        try
        {
            await fixture.Store.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.AreEqual(4, expansions.Count, "Each call validates before entering retained initialization.");
        }
        finally
        {
            fixture.Store.Delay.SetResult();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        Assert.AreEqual(4, expansions.Count);
        Assert.AreEqual(4, fixture.Store.Calls);
    }

    private sealed class ExpansionCounter : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _root = new Activity("schedule-reuse-test").Start();
        private int _count;
        internal ExpansionCounter()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == FileCameraAgentConfigurationLoader.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.TraceId == _root.TraceId && activity.OperationName == "configuration.schedule-expand-366-days")
                        Interlocked.Increment(ref _count);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }
        internal int Count => Volatile.Read(ref _count);
        public void Dispose() { _listener.Dispose(); _root.Dispose(); }
    }

    private sealed class LoaderFixture : IDisposable
    {
        internal static readonly ObservatoryLocation Observer = new(35, -114, 1500, "UTC");
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"hvo-schedule-reuse-{Guid.NewGuid():N}.json");
        internal LocationStore Store { get; } = new();
        internal LoaderFixture(bool solar)
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json")))!;
            if (solar)
            {
                foreach (var window in node["schedule"]!["weeklyWindows"]!.AsArray())
                {
                    window!["start"] = new JsonObject { ["kind"] = "AstronomicalDusk" };
                    window["end"] = new JsonObject { ["kind"] = "AstronomicalDawn", ["dayOffset"] = 1 };
                }
            }
            File.WriteAllText(_path, node.ToJsonString());
        }
        internal FileCameraAgentConfigurationLoader CreateLoader() => new(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = _path,
            AgentId = "schedule-reuse-test",
            Observatory = Observer,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance, Store);
        internal async Task EditAsync(Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(_path).ConfigureAwait(false))!;
            edit(node);
            await File.WriteAllTextAsync(_path, node.ToJsonString()).ConfigureAwait(false);
        }
        public void Dispose() => File.Delete(_path);
    }

    private sealed class LocationStore : IDeploymentLocationStore
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal ObservatoryLocation Observer { get; set; } = LoaderFixture.Observer;
        internal bool Cancel { get; set; }
        internal TaskCompletionSource? Delay { get; set; }
        internal TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeploymentLocationSnapshot? Active => null;
        public async ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 4) AllEntered.TrySetResult();
            if (Cancel) throw new OperationCanceledException(cancellationToken);
            if (Delay is not null) await Delay.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return DeploymentLocationSnapshot.Create(seed.LocationId, 2, seed.Source, seed.HorizontalAccuracyMeters,
                DateTimeOffset.UnixEpoch, null, Observer.LatitudeDegrees, Observer.LongitudeDegrees,
                Observer.ElevationMeters, Observer.TimeZoneId);
        }
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }
}
