using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

// Current Sky moves to a newer capture only once its overlays are complete, no more often than the operator's
// minimum interval, and keeps the operator's layer choices by kind across captures and page loads (#1382).
public sealed partial class CurrentSkyPageTests
{
    private const string LayerVisibilityKey = "hvo.currentSky.layerVisibility.v1:/";
    private static readonly Guid SecondCaptureId = Guid.Parse("00000000-0000-0000-0000-000000000021");
    private static readonly Guid ThirdCaptureId = Guid.Parse("00000000-0000-0000-0000-000000000031");

    [TestMethod]
    public async Task ANewerCaptureIsNotShownUntilItsOverlaysAreComplete()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        var reads = 0;
        service.CurrentSkyHandler = _ =>
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);

        // The newer capture is still being processed; its camera state is current and is shown at once.
        var pending = Next(first, SecondCaptureId, 43) with { ProcessingPending = true };
        current = pending with
        {
            Presentation = pending.Presentation with
            {
                System = pending.Presentation.System with
                {
                    State = CameraAgentPresentationSystemState.Paused,
                    Message = "Capture is paused."
                }
            }
        };
        clock.Advance(TimeSpan.FromSeconds(60));
        cut.WaitForAssertion(() => Assert.AreEqual(2, Volatile.Read(ref reads)));
        cut.WaitForAssertion(() => Assert.AreEqual("Paused", cut.Find(".dashboard-status__state strong").TextContent));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));

        // Refresh image does not show a capture whose overlays are still being produced either.
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(3, Volatile.Read(ref reads));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));

        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
    }

    [TestMethod]
    public void TheNewestCompleteCaptureWinsOnceTheIntervalHasPassed()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var interval = TimeSpan.FromSeconds(60);
        var first = View(WithStructuredBase()) with { MinimumRefreshInterval = interval };
        var current = first;
        var reads = 0;
        service.CurrentSkyHandler = _ =>
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        };
        var layerReads = new List<Guid>();
        service.PresentationHandler = (id, _) =>
        {
            lock (layerReads) layerReads.Add(id);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);

        current = Next(first, SecondCaptureId, 43);
        foreach (var expectedReads in new[] { 2, 3 })
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            cut.WaitForAssertion(() => Assert.AreEqual(expectedReads, Volatile.Read(ref reads)));
            Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
        }
        current = Next(first, ThirdCaptureId, 44);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(4, Volatile.Read(ref reads)));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));

        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
        // The capture in between was never shown, and the new one arrived with its overlays in hand.
        cut.WaitForElement(".sky-layer-workspace");
        lock (layerReads) Assert.IsFalse(layerReads.Contains(SecondCaptureId));
    }

    [TestMethod]
    public void ACaptureWhoseOverlaysNeverComeIsShownOnceItsProcessingEnds()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(id == SecondCaptureId
            ? OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.NotFound,
                "Structured layers were not retained for this capture.")
            : OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);

        current = Next(first, SecondCaptureId, 43) with { ProcessingPending = true };
        clock.Advance(TimeSpan.FromSeconds(60));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href")));

        // Processing ended without producing layers: the page moves on and says so, as before.
        var ended = Next(first, SecondCaptureId, 43);
        current = ended with { Presentation = ended.Presentation with { StructuredLayersAvailable = false } };
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent,
            "not retained for this capture", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RememberedLayerChoicesApplyOnTheNextPageLoad()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult("{\"scene-annotation\":false}");
        module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".restore-layers").HasAttribute("disabled")));
        Assert.IsFalse(cut.Find(".sky-layer-controls input[data-layer-target]").HasAttribute("checked"));
        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
        Assert.AreEqual(LayerVisibilityKey, module.Invocations["readLayerVisibility"].Single().Arguments[0]);

        // Restoring the profile defaults forgets the remembered choice.
        await cut.Find(".restore-layers").ClickAsync().ConfigureAwait(false);
        Assert.IsTrue(cut.Find(".sky-layer-controls input[data-layer-target]").HasAttribute("checked"));
        cut.WaitForAssertion(() => CollectionAssert.AreEqual(
            new object?[] { LayerVisibilityKey, null },
            module.Invocations["writeLayerVisibility"][^1].Arguments.ToArray()));
    }

    [TestMethod]
    public async Task ALayerChoiceIsRememberedByKind()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".restore-layers").HasAttribute("disabled")));

        await cut.Find(".sky-layer-controls input[data-layer-target]").ChangeAsync(false).ConfigureAwait(false);

        cut.WaitForAssertion(() => CollectionAssert.AreEqual(
            new object?[] { LayerVisibilityKey, "{\"scene-annotation\":false}" },
            module.Invocations["writeLayerVisibility"].Single().Arguments.ToArray()));
    }

    private static (TestOperatorUiService Service, ManualTimeProvider Clock) ConfigurePolling(BunitContext context)
    {
        var service = Configure(context);
        var clock = new ManualTimeProvider(OperatorUiTestData.Now);
        context.Services.AddSingleton<TimeProvider>(clock);
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        return (service, clock);
    }

    // The poll timer starts after the first image is applied; advancing the clock before then would fire nothing.
    private static void WaitForFirst(IRenderedComponent<CurrentSkyPage> cut, ManualTimeProvider clock, CameraAgentCurrentSkyView first)
    {
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href")));
        cut.WaitForAssertion(() => Assert.IsTrue(clock.HasTimer(TimeSpan.FromSeconds(15))));
    }

    private static CameraAgentCurrentSkyView View(CameraAgentCurrentImagePresentation presentation) => new(presentation, null, null);

    private static CameraAgentCurrentSkyView Next(CameraAgentCurrentSkyView view, Guid captureId, long sequence)
        => view with
        {
            ProcessingPending = false,
            Presentation = view.Presentation with
            {
                DisplayCapture = view.Presentation.DisplayCapture! with { CaptureId = captureId, CaptureSequence = sequence },
                LatestCapture = view.Presentation.LatestCapture! with { CaptureId = captureId, CaptureSequence = sequence }
            }
        };

    private static string CaptureLink(CameraAgentCurrentSkyView view) => $"/gallery/{view.Presentation.DisplayCapture!.CaptureId:D}";

    /// <summary>A clock whose timers fire, and whose elapsed time moves, only when a test advances it.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _now.UtcTicks;
            }
        }

        internal bool HasTimer(TimeSpan period)
        {
            lock (_gate)
            {
                return _timers.Any(timer => timer.Period == period);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.DueUtc <= _now).ToArray();
                foreach (var timer in due)
                {
                    if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                    {
                        timer.DueUtc = _now + timer.Period;
                    }
                    else
                    {
                        _timers.Remove(timer);
                    }
                }
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.DueUtc = _now + dueTime;
                    timer.Period = period;
                    _timers.Add(timer);
                }
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset DueUtc { get; set; }
            internal TimeSpan Period { get; set; }

            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Schedule(this, dueTime, period);
                return true;
            }

            public void Dispose() => owner.Schedule(this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
