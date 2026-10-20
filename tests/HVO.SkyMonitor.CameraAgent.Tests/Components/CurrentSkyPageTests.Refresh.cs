using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

// Current Sky moves to a newer capture only once its overlays are complete, no more often than the operator's
// minimum interval, and keeps the operator's layer choices by kind across captures and page loads (#1382).
public sealed partial class CurrentSkyPageTests
{
    private const string LayerVisibilityKey = "hvo.currentSky.layerVisibility.v1:/";
    private static readonly Guid SecondCaptureId = Guid.Parse("00000000-0000-0000-0000-000000000021");
    private static readonly Guid ThirdCaptureId = Guid.Parse("00000000-0000-0000-0000-000000000031");

    [TestMethod]
    [DataRow("view-read")]
    [DataRow("preview-validation")]
    [DataRow("layer-availability")]
    [DataRow("layer-read")]
    [DataRow("dispatcher")]
    public void RefreshDiagnosticsNameTheSlowestPhaseWithoutDoubleCountingNestedReads(string slowPhase)
    {
        var clock = new ManualTimeProvider(OperatorUiTestData.Now);
        var logger = new RefreshLogger();
        using var timing = new CameraAgentCurrentSkyRefreshDiagnostics(clock, logger);
        using (CameraAgentCurrentSkyRefreshDiagnostics.Measure(clock, "view-read"))
        {
            if (slowPhase is "preview-validation" or "layer-availability")
            {
                using var nested = CameraAgentCurrentSkyRefreshDiagnostics.Measure(clock, slowPhase);
                clock.Advance(TimeSpan.FromSeconds(6));
            }
            else if (slowPhase == "view-read") clock.Advance(TimeSpan.FromSeconds(6));
        }
        using (CameraAgentCurrentSkyRefreshDiagnostics.Measure(clock, "layer-read"))
        {
            if (slowPhase == "layer-read") clock.Advance(TimeSpan.FromSeconds(6));
        }
        if (slowPhase == "dispatcher") clock.Advance(TimeSpan.FromSeconds(6));
        Assert.IsTrue(logger.Entries.All(static entry => entry.Level == LogLevel.Debug));
        timing.LogDeadline(TimeSpan.FromSeconds(5), false);
        timing.LogDeadline(TimeSpan.FromSeconds(5), false);
        var deadline = logger.Entries.Single(static entry => entry.Level == LogLevel.Information);
        StringAssert.Contains(deadline.Message, $"SlowestPhase={slowPhase}", StringComparison.Ordinal);
        StringAssert.Contains(deadline.Message, "FirstRefresh=False", StringComparison.Ordinal);
        Assert.IsTrue(logger.Entries.Any(static entry => entry.Level == LogLevel.Debug && entry.Message.Contains("6000 ms", StringComparison.Ordinal)) || slowPhase == "dispatcher");
        Assert.IsFalse(logger.Entries.Any(static entry => entry.Level >= LogLevel.Warning));
    }

    private sealed class RefreshLogger : ILogger<CurrentSkyPage>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    [TestMethod]
    public void FirstLoadCanFinishAfterFiveSecondsWithinItsThirtySecondGrace()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        service.CurrentSkyHandler = async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), clock, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(first);
        };
        var cut = context.Render<CurrentSkyPage>();
        clock.Advance(TimeSpan.FromSeconds(5));
        StringAssert.Contains(cut.Find(".sky-refresh-notice").TextContent,
            "The current image is still loading. Retrying automatically.", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".state-notice--error"));
        Assert.IsFalse(cut.Markup.Contains("after restart", StringComparison.OrdinalIgnoreCase));
        clock.Advance(TimeSpan.FromSeconds(15));
        WaitForFirst(cut, clock, first);
        Assert.IsEmpty(cut.FindAll(".sky-refresh-notice"));
    }

    [TestMethod]
    public void FirstLoadStopsAtThirtySecondsAndTheNextPollRecoversAutomatically()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var logger = new RefreshLogger();
        context.Services.AddSingleton<ILogger<CurrentSkyPage>>(logger);
        var first = View(WithStructuredBase());
        var reads = 0;
        var cancelled = false;
        service.CurrentSkyHandler = async token =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, clock, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(first);
        };
        var cut = context.Render<CurrentSkyPage>();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.IsFalse(Volatile.Read(ref cancelled));
        clock.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => Assert.IsTrue(Volatile.Read(ref cancelled)));
        cut.WaitForAssertion(() => Assert.IsTrue(clock.HasTimer(TimeSpan.FromSeconds(15))));
        var missed = logger.Entries.Single(static entry => entry.Level == LogLevel.Information);
        StringAssert.Contains(missed.Message, "missed 30 s bound. FirstRefresh=True", StringComparison.Ordinal);
        StringAssert.Contains(missed.Message, "SlowestPhase=view-read", StringComparison.Ordinal);
        Assert.IsTrue(logger.Entries.Any(static entry => entry.Level == LogLevel.Debug && entry.Message.Contains("view-read took 30000 ms", StringComparison.Ordinal)));
        Assert.IsEmpty(cut.FindAll(".state-notice--error"));
        StringAssert.Contains(cut.Find(".sky-refresh-notice").TextContent,
            "The current image is still loading. Retrying automatically.", StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(15));
        WaitForFirst(cut, clock, first);
        Assert.AreEqual(2, Volatile.Read(ref reads));
        Assert.IsEmpty(cut.FindAll(".sky-refresh-notice"));
    }

    [TestMethod]
    public void SteadyStateDeadlineKeepsCaptureStatusAndPollingThenClearsOnRecovery()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var reads = 0;
        service.CurrentSkyHandler = async token =>
        {
            if (Interlocked.Increment(ref reads) == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, clock, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(first);
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        var status = cut.Find(".dashboard-status").TextContent;
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(2, Volatile.Read(ref reads)));
        clock.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-refresh-notice").TextContent,
            "Current Sky couldn’t refresh within 5 s; showing the last image; retrying automatically.", StringComparison.Ordinal));
        Assert.AreEqual(status, cut.Find(".dashboard-status").TextContent);
        Assert.IsEmpty(service.CaptureReasons);
        Assert.IsNotNull(cut.Find(".current-sky-visual .sky-refresh-notice"));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
        clock.Advance(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.AreEqual(3, Volatile.Read(ref reads)));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".sky-refresh-notice")));
    }

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
    public void AnIntervalOfOnePollPeriodHoldsWhenTimerWakeUpsDrift()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase()) with { MinimumRefreshInterval = TimeSpan.FromSeconds(15) };
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);

        // This poll wakes half a second late and shows the next capture; the one after wakes on time, 14.5 s later.
        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15.5));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
        current = Next(first, ThirdCaptureId, 44);
        clock.Advance(TimeSpan.FromSeconds(14.5));

        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
    }

    [TestMethod]
    public void TheCurrentSkyReadAndTheNextCapturesLayersShareOneDeadline()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        var slow = false;
        service.CurrentSkyHandler = async token =>
        {
            if (slow) await Task.Delay(TimeSpan.FromSeconds(3), clock, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(current);
        };
        var layersCancelled = false;
        service.PresentationHandler = async (id, token) =>
        {
            if (id == SecondCaptureId)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), clock, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    layersCancelled = true;
                    throw;
                }
            }
            return OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64)));
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        var start = clock.GetUtcNow();

        // A 3 s read and a 3 s layer read cannot both fit in the 5 s refresh deadline.
        slow = true;
        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.IsTrue(clock.HasTimerDue(start.AddSeconds(18))));
        clock.Advance(TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(() => Assert.IsTrue(clock.HasTimerDue(start.AddSeconds(21))));
        clock.Advance(TimeSpan.FromSeconds(3));

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-refresh-notice").TextContent,
            "Current Sky couldn’t refresh within 5 s; showing the last image; retrying automatically.", StringComparison.Ordinal));
        Assert.IsTrue(Volatile.Read(ref layersCancelled));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
        cut.WaitForElement(".sky-layer-workspace");
    }

    [TestMethod]
    public async Task ARefreshThatWaitsForTheDispatcherPastItsDeadlineAppliesNothing()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        var nextLayers = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextLayersRequested = false;
        service.PresentationHandler = (id, _) =>
        {
            if (id != SecondCaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
            Volatile.Write(ref nextLayersRequested, true);
            return new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(nextLayers.Task);
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.IsTrue(Volatile.Read(ref nextLayersRequested)));

        // While the dispatcher is held, the layers arrive in time and the refresh queues its result; the deadline then
        // passes before the dispatcher runs it.
        await cut.InvokeAsync(() =>
        {
            nextLayers.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(SecondCaptureId, new string('D', 64))));
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            clock.Advance(TimeSpan.FromSeconds(5));
        }).ConfigureAwait(false);

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-refresh-notice").TextContent,
            "Current Sky couldn’t refresh within 5 s; showing the last image; retrying automatically.", StringComparison.Ordinal));
        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
        cut.WaitForElement(".sky-layer-workspace");
    }

    [TestMethod]
    public void OverlaysThatCannotBeReadNowLeaveTheCompleteImageUntilALaterRefresh()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        var secondReads = 0;
        service.PresentationHandler = (id, _) => ValueTask.FromResult(id == SecondCaptureId && Interlocked.Increment(ref secondReads) == 1
            ? OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Structured layers are temporarily unavailable.")
            : OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        cut.WaitForElement(".sky-layer-workspace");

        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(1, Volatile.Read(ref secondReads)));

        Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
        Assert.HasCount(1, cut.FindAll(".sky-layer-workspace"));
        Assert.IsEmpty(cut.FindAll(".sky-layer-unavailable"));

        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
        cut.WaitForElement(".sky-layer-workspace");
    }

    [TestMethod]
    public void ACaptureWhoseProcessingEndedWithoutReadableOverlaysStillReplacesTheImage()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var first = View(WithStructuredBase());
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(id == SecondCaptureId
            ? OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Structured layers are temporarily unavailable.")
            : OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);

        // The read found no layers for the capture, so they are not waited for.
        var ended = Next(first, SecondCaptureId, 43);
        current = ended with { Presentation = ended.Presentation with { StructuredLayersAvailable = false } };
        clock.Advance(TimeSpan.FromSeconds(15));

        cut.WaitForAssertion(() => Assert.AreEqual(CaptureLink(current), cut.Find(".layers-link").GetAttribute("href")));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent,
            "temporarily unavailable", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ADenialOfTheNextCapturesLayersIsNotDeferredByFullScreen()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context, module => module.Setup<bool>("requestFullScreen", _ => true).SetResult(true));
        var first = View(WithStructuredBase());
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        var denied = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = false;
        service.PresentationHandler = (id, _) =>
        {
            if (id != SecondCaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
            Volatile.Write(ref secondRead, true);
            return new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(denied.Task);
        };
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        current = Next(first, SecondCaptureId, 43);
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.IsTrue(Volatile.Read(ref secondRead)));

        await cut.Find("#current-sky-view-large").ClickAsync().ConfigureAwait(false);
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var redirected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.LocationChanged += (_, args) => redirected.TrySetResult(args.Location);
        denied.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unauthorized, "Denied"));

        var location = await redirected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(location.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".sky-image-stage img")));
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

    private static (TestOperatorUiService Service, ManualTimeProvider Clock) ConfigurePolling(
        BunitContext context,
        Action<BunitJSModuleInterop>? module = null)
    {
        var service = Configure(context);
        var clock = new ManualTimeProvider(OperatorUiTestData.Now);
        context.Services.AddSingleton<TimeProvider>(clock);
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var interop = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        interop.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module?.Invoke(interop);
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

        internal bool HasTimerDue(DateTimeOffset due)
        {
            lock (_gate)
            {
                return _timers.Any(timer => timer.DueUtc == due);
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
                        // A periodic timer keeps its schedule, as the runtime's does, however late it fired.
                        while (timer.DueUtc <= _now) timer.DueUtc += timer.Period;
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
