using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    private static readonly CenteredViewStream CenteredStream = new(
        new("agent-test", "camera-1"),
        Guid.Parse("19230000-0000-0000-0000-000000000001"),
        "centered-gen-1");

    [TestMethod]
    public async Task CenteredStartupSequenceDisplaysSinglesHolds103SwitchesInPlaceToStackAndAdvancesTo104()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);

        var exp101 = CenteredExposure(101, OperatorUiTestData.Now.AddSeconds(-50));
        var exp102 = CenteredExposure(102, OperatorUiTestData.Now.AddSeconds(-40));
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-30));
        var exp104 = CenteredExposure(104, OperatorUiTestData.Now.AddSeconds(-20));
        var exp105 = CenteredExposure(105, OperatorUiTestData.Now.AddSeconds(-10));
        var exp106 = CenteredExposure(106, OperatorUiTestData.Now);

        var single101 = CenteredSingle(exp101, 1);
        var single102 = CenteredSingle(exp102, 2);
        var single103 = CenteredSingle(exp103, 3);
        var stack103 = CenteredStack(exp103, [exp101, exp102, exp103, exp104, exp105], 4);
        var stack104 = CenteredStack(exp104, [exp102, exp103, exp104, exp105, exp106], 5);

        CameraAgentCurrentSkyView activeView = CenteredSkyView(
            single101, exp101, CenteredViewLiveState.Warming, clock.GetUtcNow());
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(activeView));

        var cut = context.Render<CurrentSkyPage>();

        // 101 as single, warming state
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#101", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#101", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("1 frame (single)", cut.Find("[data-centered-contributors]").TextContent);
            Assert.AreEqual("Warming", cut.Find("[data-centered-state]").TextContent);
            StringAssert.Contains(cut.Find(".image-heading h2").TextContent, "Single exposure", StringComparison.Ordinal);
        });

        // 102 as single
        clock.Advance(TimeSpan.FromSeconds(15));
        activeView = CenteredSkyView(single102, exp102, CenteredViewLiveState.Warming, clock.GetUtcNow());
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#102", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#102", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("1 frame (single)", cut.Find("[data-centered-contributors]").TextContent);
        });

        // 103 as single
        clock.Advance(TimeSpan.FromSeconds(15));
        activeView = CenteredSkyView(single103, exp103, CenteredViewLiveState.Warming, clock.GetUtcNow());
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#103", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("1 frame (single)", cut.Find("[data-centered-contributors]").TextContent);
        });

        // 104 arrives -> hold single 103 while acquisition head moves to 104 and state is Building stack
        clock.Advance(TimeSpan.FromSeconds(5));
        activeView = CenteredSkyView(single103, exp104, CenteredViewLiveState.Building, clock.GetUtcNow());
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#104", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("1 frame (single)", cut.Find("[data-centered-contributors]").TextContent);
            Assert.AreEqual("Building stack", cut.Find("[data-centered-state]").TextContent);
        });

        // 105 arrives -> switch in place to stack centered on 103 (5 frames), even within MinimumRefreshInterval!
        clock.Advance(TimeSpan.FromSeconds(2));
        activeView = CenteredSkyView(stack103, exp105, CenteredViewLiveState.Ready, clock.GetUtcNow(),
            minimumRefresh: TimeSpan.FromSeconds(60));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#105", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("5 frames", cut.Find("[data-centered-contributors]").TextContent);
            Assert.AreEqual("Ready", cut.Find("[data-centered-state]").TextContent);
            StringAssert.Contains(cut.Find(".image-heading h2").TextContent, "Live stack centered on #103", StringComparison.Ordinal);
        });

        // 106 arrives -> advance to stack centered on 104
        clock.Advance(TimeSpan.FromSeconds(15));
        activeView = CenteredSkyView(stack104, exp106, CenteredViewLiveState.Ready, clock.GetUtcNow());
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("#106", cut.Find("[data-centered-head]").TextContent);
            Assert.AreEqual("5 frames", cut.Find("[data-centered-contributors]").TextContent);
            StringAssert.Contains(cut.Find(".image-heading h2").TextContent, "Live stack centered on #104", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public async Task CenteredSingleModeAdvancesImmediatelyAndPauseRetainsLastViewWithUpdatingAge()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);

        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-30));
        var exp104 = CenteredExposure(104, clock.GetUtcNow().AddSeconds(-20));
        var exp105 = CenteredExposure(105, clock.GetUtcNow().AddSeconds(-10));
        var exp106 = CenteredExposure(106, clock.GetUtcNow());

        var single103 = CenteredSingle(exp103, 1);
        var single104 = CenteredSingle(exp104, 2);
        var single105 = CenteredSingle(exp105, 3);
        var single106 = CenteredSingle(exp106, 4);

        CenteredView? currentSingle = single104;
        var liveState = CenteredViewLiveState.Building;
        CenteredView? centeredView = single103;
        var observedMode = CenteredLiveMode.Centered;

        service.CenteredCurrentSkyHandler = (mode, _) =>
        {
            observedMode = mode;
            var view = mode == CenteredLiveMode.Single ? currentSingle : centeredView;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(view, exp105, liveState, clock.GetUtcNow(), mode: mode, minimumRefresh: TimeSpan.FromSeconds(120))));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent));

        // Switch to Single exposure mode -> immediately advances to single 104
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Single exposure")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(CenteredLiveMode.Single, observedMode);
            Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
        });

        // Single mode advances immediately to 105 on next poll even with 120s minimum refresh interval
        currentSingle = single105;
        clock.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.AreEqual("#105", cut.Find("[data-centered-reference]").TextContent));

        // Pause live updates locally -> retains 105 even when 106 arrives, while age advances
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Pause live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent));

        currentSingle = single106;
        clock.Advance(TimeSpan.FromSeconds(30));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#105", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent);
            StringAssert.Contains(cut.Find(".dashboard-status").TextContent, "55s", StringComparison.Ordinal);
        });

        // Resume -> advances to 106; then backend capture pause with null view still retains 106 with updating age
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Resume live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("#106", cut.Find("[data-centered-reference]").TextContent));

        currentSingle = null;
        liveState = CenteredViewLiveState.Paused;
        clock.Advance(TimeSpan.FromSeconds(12));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#106", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent);
            StringAssert.Contains(cut.Find(".dashboard-status").TextContent, "57s", StringComparison.Ordinal);
        });

        // Switching back to Centered live shows the active centered stack (#103)
        liveState = CenteredViewLiveState.Ready;
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Centered live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(CenteredLiveMode.Centered, observedMode);
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
        });
    }

    [TestMethod]
    public async Task CenteredBaseNeverWaitsForOptionalLayersAndDistinguishesEveryReadinessState()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var refId = exp103.Capture.CaptureId;

        // Initial revision: base is ready immediately while ALL optional layers are still pending (Waiting)
        CenteredViewLayer[] initialLayers =
        [
            PendingLayer("star-annotations", refId),
            PendingLayer("scene-constellations", refId),
            PendingLayer("MeasuredTransientTrails", refId, CenteredViewLayerScope.Contributor),
            PendingLayer(AstrometricDiagnosticPresentation.ResidualsLayer, refId),
            DisabledLayer("cloud-mask", refId),
            PendingLayer("predicted-satellites", refId, CenteredViewLayerScope.Contributor)
        ];
        var rev1 = CenteredSingle(exp103, 1, initialLayers);
        var current = rev1;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, exp103, CenteredViewLiveState.Warming, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();

        // Base renders immediately with 0 ready layers and pending checkboxes disabled
        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find(".sky-layer-canvas img"));
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.IsEmpty(cut.FindAll(".centered-layer-controls input:not(:disabled)"));
            Assert.AreEqual("Pending", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Disabled", cut.Find("[data-layer-kind='cloud-mask'] .layer-readiness-tag").TextContent);
        });

        // Second revision on the same base: distinct Ready, Partial, Pending, NoDetection, Incompatible, Disabled states!
        var starDrawable = Drawable("star-annotations", 20, enabledByDefault: true, rev1.Base.Definition.Preview.Product.Compatibility);
        var constDrawable = Drawable("scene-constellations", 10, enabledByDefault: true, rev1.Base.Definition.Preview.Product.Compatibility);
        CenteredViewLayer[] rev2Layers =
        [
            new(new("star-annotations", CenteredViewLayerScope.Reference, refId), 2, EvidencePin("stars"), clock.GetUtcNow(), CenteredViewReadiness.Ready, null, starDrawable),
            new(new("scene-constellations", CenteredViewLayerScope.Reference, refId), 3, EvidencePin("const"), clock.GetUtcNow(), CenteredViewReadiness.Partial, "centered.partial-coverage", constDrawable),
            new(new("MeasuredTransientTrails", CenteredViewLayerScope.Contributor, refId), 4, EvidencePin("trails"), clock.GetUtcNow(), CenteredViewReadiness.AssessedEmpty, null, null),
            new(new(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, refId), 5, null, clock.GetUtcNow(), CenteredViewReadiness.Incompatible, "centered.no-accepted-fit", null),
            DisabledLayer("cloud-mask", refId),
            PendingLayer("predicted-satellites", refId, CenteredViewLayerScope.Contributor)
        ];
        current = CenteredRevision(rev1.Base, rev1.Revision.RevisionId, 2, rev2Layers);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Partial", cut.Find("[data-layer-kind='scene-constellations'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("No detection", cut.Find("[data-layer-kind='MeasuredTransientTrails'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Incompatible", cut.Find($"[data-layer-kind='{AstrometricDiagnosticPresentation.ResidualsLayer}'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Disabled", cut.Find("[data-layer-kind='cloud-mask'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Pending", cut.Find("[data-layer-kind='predicted-satellites'] .layer-readiness-tag").TextContent);

            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='MeasuredTransientTrails'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find($"[data-layer-kind='{AstrometricDiagnosticPresentation.ResidualsLayer}'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='cloud-mask'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='predicted-satellites'] input").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task CenteredPersonalVisibilityCarriesOverOnlyWhenNewBaseIsCompatibleAndLatestFitStaysSeparate()
    {
        var astrometry = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var latestSummary = CameraAgentLatestAstrometry.FromVerified(astrometry)!;

        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-20));
        var exp104 = CenteredExposure(104, clock.GetUtcNow().AddSeconds(-10));
        var exp105 = CenteredExposure(105, clock.GetUtcNow());

        var compatible = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var incompatible = new PresentationCompatibilityDescriptor(320, 240, new string('D', 64), new string('E', 64));

        var single103 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compatible, enabledByDefault: true)],
            compatibility: compatible);
        var single104 = CenteredSingle(exp104, 2,
            [ReadyLayer("star-annotations", exp104.Capture.CaptureId, 1, compatible, enabledByDefault: true)],
            compatibility: compatible);
        var single105Incompatible = CenteredSingle(exp105, 3,
            [ReadyLayer("star-annotations", exp105.Capture.CaptureId, 1, incompatible, enabledByDefault: true)],
            compatibility: incompatible, width: 320, height: 240);

        var current = single103;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, current.Base.Definition.Reference, CenteredViewLiveState.Ready, clock.GetUtcNow())));
        service.LatestAstrometryHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, latestSummary)));
        service.AstrometryHandler = (id, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(
                new(id, "NotRetained", "assessment-not-retained")));

        var cut = context.Render<CurrentSkyPage>();

        // Wait for ready star-annotations and latest fit headline (for a different capture than 103)
        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, "For a different capture.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, latestSummary.SolutionIdentitySha256, StringComparison.Ordinal);
            Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });

        // Operator turns OFF star-annotations on 103
        await cut.Find("[data-layer-kind='star-annotations'] input").ChangeAsync(false).ConfigureAwait(false);
        Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));

        // Advance to compatible base 104 -> remembered OFF visibility carries over!
        current = single104;
        clock.Advance(TimeSpan.FromSeconds(15));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
        });

        // Advance to incompatible base 105 (different geometry compatibility) -> personal visibility NOT carried over; profile default ON applies!
        current = single105Incompatible;
        clock.Advance(TimeSpan.FromSeconds(15));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#105", cut.Find("[data-centered-reference]").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
        });
    }

    [TestMethod]
    public async Task CenteredReconnectOutOfOrderBaseAndLateTrailOn103CannotReplace104()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var publisher = new TestCenteredViewPublisher();
        context.Services.AddSingleton<ICenteredViewPublisher>(publisher);

        var exp101 = CenteredExposure(101, clock.GetUtcNow().AddSeconds(-50));
        var exp102 = CenteredExposure(102, clock.GetUtcNow().AddSeconds(-40));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-30));
        var exp104 = CenteredExposure(104, clock.GetUtcNow().AddSeconds(-20));
        var exp105 = CenteredExposure(105, clock.GetUtcNow().AddSeconds(-10));
        var exp106 = CenteredExposure(106, clock.GetUtcNow());

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var stack103 = CenteredStack(exp103, [exp101, exp102, exp103, exp104, exp105], 5,
            [ReadyLayer("MeasuredTransientTrails", exp105.Capture.CaptureId, 7, compat, scope: CenteredViewLayerScope.Contributor)]);
        var stack104Rev10 = CenteredStack(exp104, [exp102, exp103, exp104, exp105, exp106], 10,
            [PendingLayer("MeasuredTransientTrails", exp105.Capture.CaptureId, CenteredViewLayerScope.Contributor)]);
        var stack104Rev8 = CenteredRevision(stack104Rev10.Base, null, 8, []);
        var stack104Rev12 = CenteredRevision(stack104Rev10.Base, stack104Rev10.Revision.RevisionId, 12,
            [ReadyLayer("star-annotations", exp104.Capture.CaptureId, 9, compat)]);

        var current = stack104Rev10;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, exp106, CenteredViewLiveState.Ready, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent));

        // Out-of-order base (103 with late trail) arrives on reconnect -> MUST NOT replace 104!
        current = stack103;
        publisher.RaiseChanged(CenteredStream);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual(stack104Rev10.Base.BaseId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-base"));
        });

        // Older revision (#8 < #10) on same base 104 -> MUST NOT regress!
        current = stack104Rev8;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(stack104Rev10.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));

        // Newer revision (#12 > #10) on same base 104 via publisher wakeup -> swaps atomically in place!
        current = stack104Rev12;
        publisher.RaiseChanged(CenteredStream);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(stack104Rev12.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task CenteredArchiveOffersSingleAndStackRevisionsSavedSelectionAndLegacyFallback()
    {
        var exp101 = CenteredExposure(101, OperatorUiTestData.Now.AddSeconds(-50));
        var exp102 = CenteredExposure(102, OperatorUiTestData.Now.AddSeconds(-40));
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-30));
        var exp104 = CenteredExposure(104, OperatorUiTestData.Now.AddSeconds(-20));
        var exp105 = CenteredExposure(105, OperatorUiTestData.Now.AddSeconds(-10));
        var capture103Id = exp103.Capture.CaptureId;
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));

        var single103 = CenteredSingle(exp103, 1, [PendingLayer("star-annotations", capture103Id)], compat);
        var stack103Initial = CenteredStack(exp103, [exp101, exp102, exp103, exp104, exp105], 2,
            [ReadyLayer("star-annotations", capture103Id, 1, compat, enabledByDefault: true)], compat);
        var stack103LastLive = CenteredRevision(stack103Initial.Base, stack103Initial.Revision.RevisionId, 3,
        [
            ReadyLayer("star-annotations", capture103Id, 1, compat, enabledByDefault: true),
            ReadyLayer("scene-constellations", capture103Id, 2, compat, enabledByDefault: true)
        ]);
        var stack103LaterTrail = CenteredRevision(stack103Initial.Base, stack103LastLive.Revision.RevisionId, 8,
        [
            ReadyLayer("star-annotations", capture103Id, 1, compat, enabledByDefault: true),
            ReadyLayer("scene-constellations", capture103Id, 2, compat, enabledByDefault: true),
            ReadyLayer("MeasuredTransientTrails", exp105.Capture.CaptureId, 5, compat, enabledByDefault: false, scope: CenteredViewLayerScope.Contributor)
        ]);

        var singleArchiveBase = new CenteredViewArchiveBase(single103.Base, single103.Revision.RevisionId, single103.Revision.RevisionId, single103.Revision.RevisionId);
        var stackArchiveBase = new CenteredViewArchiveBase(stack103Initial.Base, stack103Initial.Revision.RevisionId, stack103LaterTrail.Revision.RevisionId, stack103LastLive.Revision.RevisionId);

        CenteredViewLiveInterval[] stackLiveHistory =
        [
            new(stack103Initial.Base.BaseId, stack103Initial.Revision.RevisionId, 2, OperatorUiTestData.Now.AddSeconds(-25), 3, OperatorUiTestData.Now.AddSeconds(-22)),
            new(stack103Initial.Base.BaseId, stack103LastLive.Revision.RevisionId, 3, OperatorUiTestData.Now.AddSeconds(-22), 5, OperatorUiTestData.Now.AddSeconds(-15))
        ];
        CameraAgentCenteredArchiveBaseSummary[] summaries =
        [
            new(singleArchiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(singleArchiveBase, [single103.Revision], [])),
            new(stackArchiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(stackArchiveBase,
                [stack103Initial.Revision, stack103LastLive.Revision, stack103LaterTrail.Revision], stackLiveHistory))
        ];

        // 1. Open archive 103 -> offers Single exposure & Live stack centered on #103, and distinctly labels later archive enrichment
        using (var context = new BunitContext())
        {
            var service = Configure(context);
            var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
            module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();

            var selectionId = Guid.Parse("19230000-0000-0000-0000-000000009999");
            IReadOnlyList<string>? savedEnabledLayers = null;
            service.SaveCenteredSelectionHandler = (refId, bId, rId, enabled, _) =>
            {
                Assert.AreEqual(capture103Id, refId);
                Assert.AreEqual(stack103LastLive.Base.BaseId, bId);
                Assert.AreEqual(stack103LastLive.Revision.RevisionId, rId);
                savedEnabledLayers = enabled;
                return ValueTask.FromResult(OperatorUiResult<CenteredViewSavedSelection>.Success(
                    new(selectionId, bId, rId, enabled.ToArray(), OperatorUiTestData.Now)));
            };

            var detail = CenteredDetailView(capture103Id, summaries, stack103LastLive, savedSelection: null, selectionError: null);
            var cut = context.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));

            cut.WaitForAssertion(() =>
            {
                var baseLinks = cut.FindAll(".centered-archive-bases a");
                Assert.HasCount(2, baseLinks);
                StringAssert.Contains(baseLinks[0].TextContent, "Single exposure", StringComparison.Ordinal);
                StringAssert.Contains(baseLinks[1].TextContent, "Live stack centered on #103 (5 frames)", StringComparison.Ordinal);

                var revLinks = cut.FindAll(".centered-archive-revisions a");
                Assert.HasCount(3, revLinks);
                Assert.AreEqual("Initial", revLinks[0].GetAttribute("data-revision-role"));
                Assert.AreEqual("LastPublishedLive", revLinks[1].GetAttribute("data-revision-role"));
                Assert.AreEqual("LaterArchiveEnrichment", revLinks[2].GetAttribute("data-revision-role"));
                StringAssert.Contains(revLinks[2].TextContent, "Later archive enrichment", StringComparison.Ordinal);
                Assert.IsFalse(cut.Find(".save-centered-selection").HasAttribute("disabled"));
            });

            // Turn off constellations and save personal selection
            await cut.Find("[data-layer-kind='scene-constellations'] input").ChangeAsync(false).ConfigureAwait(false);
            await cut.Find(".save-centered-selection").ClickAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() =>
            {
                Assert.IsNotNull(savedEnabledLayers);
                Assert.HasCount(1, savedEnabledLayers);
                Assert.AreEqual(CenteredViewLinks.ArchiveSelection(capture103Id, selectionId),
                    cut.Find(".centered-save-result a").GetAttribute("href"));
            });
        }

        // 2. Fresh browser with conflicting localStorage opens the saved personal selection -> reproduces exact saved layers!
        using (var freshContext = new BunitContext())
        {
            Configure(freshContext);
            var module = freshContext.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            // Fresh browser has opposite localStorage preference (stars OFF, constellations ON)
            module.Setup<string?>("readLayerVisibility", _ => true).SetResult("{\"star-annotations\":false,\"scene-constellations\":true}");

            var starId = stack103LastLive.Revision.Manifest.Layers.Single(layer => layer.LayerKind == "star-annotations").LayerIdentitySha256;
            var saved = new CenteredViewSavedSelection(Guid.NewGuid(), stack103LastLive.Base.BaseId, stack103LastLive.Revision.RevisionId, [starId], OperatorUiTestData.Now);
            var detail = CenteredDetailView(capture103Id, summaries, stack103LastLive, savedSelection: saved, selectionError: null);

            var cut = freshContext.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
            cut.WaitForAssertion(() =>
            {
                StringAssert.Contains(cut.Find(".centered-saved-selection").TextContent, "Saved personal selection", StringComparison.Ordinal);
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
                Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
            });
        }

        // 3. Explicit mismatched/missing centered target -> shows explicit error and never substitutes another base
        using (var errorContext = new BunitContext())
        {
            Configure(errorContext);
            var detail = CenteredDetailView(capture103Id, summaries, selectedView: null, savedSelection: null,
                selectionError: "The requested centered view revision was not found or does not belong to this capture.");
            var cut = errorContext.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
            cut.WaitForAssertion(() =>
            {
                StringAssert.Contains(cut.Find(".centered-selection-error").TextContent, "not found or does not belong to this capture", StringComparison.Ordinal);
                Assert.IsEmpty(cut.FindAll(".sky-layer-workspace"));
            });
        }

        // 4. Older legacy archive capture (CenteredArchive == null) -> renders actual retained stages & historical availability
        using (var legacyContext = new BunitContext())
        {
            Configure(legacyContext);
            var legacyCapture = OperatorUiTestData.Capture();
            var legacyDetail = new CameraAgentCaptureDetailView(legacyCapture,
                new(CameraAgentPresentationStage.Annotated, OperatorUiTestData.CurrentImage().Stages));
            var cut = legacyContext.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, legacyDetail));
            cut.WaitForAssertion(() =>
            {
                Assert.IsEmpty(cut.FindAll(".centered-archive-controls"));
                Assert.IsNotNull(cut.Find(".stage-switcher"));
                StringAssert.Contains(cut.Find(".stage-unavailable").TextContent, "Calibrated", StringComparison.Ordinal);
            });
        }
    }

    [TestMethod]
    public void CenteredWarmupPublisherWakesAdvanceReadySinglesWithoutWaitingForRefreshThrottle()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var publisher = new TestCenteredViewPublisher();
        context.Services.AddSingleton<ICenteredViewPublisher>(publisher);

        var exp101 = CenteredExposure(101, clock.GetUtcNow().AddSeconds(-4));
        var exp102 = CenteredExposure(102, clock.GetUtcNow().AddSeconds(-2));
        var exp103 = CenteredExposure(103, clock.GetUtcNow());

        var single101 = CenteredSingle(exp101, 1);
        var single102 = CenteredSingle(exp102, 2);
        var single103 = CenteredSingle(exp103, 3);

        var current = single101;
        var head = exp101;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, head, CenteredViewLiveState.Warming, clock.GetUtcNow(),
                    minimumRefresh: TimeSpan.FromSeconds(15))));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("#101", cut.Find("[data-centered-reference]").TextContent));

        // 2s later (< 15s throttle): automatic publisher wake advances ready single 102 immediately
        clock.Advance(TimeSpan.FromSeconds(2));
        current = single102;
        head = exp102;
        publisher.RaiseChanged(CenteredStream);
        cut.WaitForAssertion(() => Assert.AreEqual("#102", cut.Find("[data-centered-reference]").TextContent));

        // 2s later (< 15s throttle): automatic publisher wake advances ready single 103 immediately
        clock.Advance(TimeSpan.FromSeconds(2));
        current = single103;
        head = exp103;
        publisher.RaiseChanged(CenteredStream);
        cut.WaitForAssertion(() => Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent));
    }

    [TestMethod]
    public async Task CenteredAstrometryInvalidationAndNullGuardsNeverNullLayersOrSubstituteLegacyOverlaysAsync()
    {
        var astrometry = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var fitArtifactId = Guid.NewGuid();
        var pinned = astrometry with { ArtifactId = fitArtifactId };
        var fitPin = new CenteredViewArtifact(fitArtifactId, new string('3', 64), new string('4', 64));

        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var exp103 = CenteredExposure(103, astrometry.Solution!.Assessment.Frame.ExposureStartUtc);
        var captureId = astrometry.CaptureId;
        exp103 = exp103 with
        {
            Input = exp103.Input with
            {
                Key = exp103.Input.Key with { CaptureId = captureId },
                Raw = exp103.Input.Raw with
                {
                    Capture = exp103.Input.Raw.Capture with { CaptureId = captureId },
                    Artifact = exp103.Input.Raw.Artifact with { ArtifactId = astrometry.Solution.Assessment.Frame.SourceArtifactId }
                }
            }
        };
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var residualsDrawable = Drawable(AstrometricDiagnosticPresentation.ResidualsLayer, 412, false, compat);
        var residualsLayer = new CenteredViewLayer(
            new(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, captureId),
            1, fitPin, clock.GetUtcNow(), CenteredViewReadiness.Ready, null, residualsDrawable);
        var single103 = CenteredSingle(exp103, 1, [residualsLayer], compat);

        var legacyLayerReads = 0;
        service.PresentationHandler = (_, _) =>
        {
            Interlocked.Increment(ref legacyLayerReads);
            throw new InvalidOperationException("Legacy GetLayeredPresentationAsync must never run in centered mode.");
        };
        service.CurrentPresentationHandler = (_, _) =>
        {
            Interlocked.Increment(ref legacyLayerReads);
            throw new InvalidOperationException("Legacy GetCurrentLayeredPresentationAsync must never run in centered mode.");
        };

        var invalidateSelected = false;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow())));
        service.ExactAstrometryHandler = (id, evidence, _) =>
        {
            Assert.AreEqual(captureId, id);
            Assert.AreEqual(fitPin, evidence);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(pinned));
        };
        service.LatestAstrometrySelectionHandler = (_, _, selected) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(
                new(true, null) { SelectedAssessmentInvalidated = invalidateSelected && selected is not null }));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find(".sky-layer-workspace"));
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
        });

        // Trigger astrometry invalidation on the displayed centered capture -> must NOT crash or null centered layers or invoke legacy reads!
        invalidateSelected = true;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find(".sky-layer-workspace"));
            Assert.AreEqual(single103.Base.BaseId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-base"));
            Assert.AreEqual(0, Volatile.Read(ref legacyLayerReads));
        });

        // Null-guard path: centered view with null/incompatible presentation renders base CaptureImage cleanly without NRE
        using var nullContext = new BunitContext();
        var (nullService, nullClock) = ConfigurePolling(nullContext);
        var baseOnlyView = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, nullClock.GetUtcNow());
        baseOnlyView = baseOnlyView with
        {
            Centered = baseOnlyView.Centered! with
            {
                Presentation = null,
                PresentationStatus = CameraAgentLayeredPresentationStatus.Unavailable,
                PresentationReason = "Centered overlays are temporarily unavailable."
            }
        };
        nullService.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(baseOnlyView));

        var nullCut = nullContext.Render<CurrentSkyPage>();
        nullCut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(nullCut.FindAll(".sky-layer-workspace"));
            var img = nullCut.Find(".capture-image img");
            StringAssert.Contains(img.GetAttribute("src"), single103.Base.BaseId, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void CenteredOverlayOnlyRevisionKeepsBasePreviewUrlAndWorkspaceStable()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var publisher = new TestCenteredViewPublisher();
        context.Services.AddSingleton<ICenteredViewPublisher>(publisher);

        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var rev1 = CenteredSingle(exp103, 1, [PendingLayer("star-annotations", exp103.Capture.CaptureId)], compat);
        var rev2 = CenteredRevision(rev1.Base, rev1.Revision.RevisionId, 2,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compat, enabledByDefault: true)]);

        var current = rev1;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, exp103, CenteredViewLiveState.Warming, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();
        string? initialSrc = null;
        cut.WaitForAssertion(() =>
        {
            var workspace = cut.Find(".sky-layer-workspace");
            Assert.AreEqual(rev1.Revision.RevisionId, workspace.GetAttribute("data-centered-revision"));
            initialSrc = cut.Find(".sky-layer-canvas > img").GetAttribute("src");
            Assert.IsNotNull(initialSrc);
            StringAssert.Contains(initialSrc, rev1.Base.BaseId, StringComparison.Ordinal);
            Assert.IsFalse(initialSrc.Contains(rev1.Revision.RevisionId, StringComparison.Ordinal),
                "Base preview URL must not embed the revision ID.");
        });

        // Overlay-only revision on the SAME base -> URL stays identical and SVG updates in-place
        current = rev2;
        publisher.RaiseChanged(CenteredStream);
        cut.WaitForAssertion(() =>
        {
            var workspace = cut.Find(".sky-layer-workspace");
            Assert.AreEqual(rev2.Revision.RevisionId, workspace.GetAttribute("data-centered-revision"));
            Assert.AreEqual(initialSrc, cut.Find(".sky-layer-canvas > img").GetAttribute("src"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsNotNull(cut.Find(".sky-layer-overlay svg g[data-layer-kind='star-annotations']"));
        });
    }

    [TestMethod]
    public async Task CenteredHoldAndNoOpRefreshFreshnessFromReadyToStaleOnSameViewAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-5));
        var single103 = CenteredSingle(exp103, 1);

        var freshness = CameraAgentPresentationImageFreshness.Current;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var view = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                view with { Presentation = view.Presentation with { ImageFreshness = freshness } }));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Image current", cut.Find(".current-sky-summary .state-chip").TextContent);
        });

        // Same base, same revision (NoOp path) -> Delayed
        freshness = CameraAgentPresentationImageFreshness.Delayed;
        clock.Advance(TimeSpan.FromSeconds(30));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Image delayed", cut.Find(".current-sky-summary .state-chip").TextContent);
            Assert.AreEqual("Next image delayed", cut.Find(".live-indicator").TextContent);
        });

        // Same base, same revision (NoOp path) -> Stale
        freshness = CameraAgentPresentationImageFreshness.Stale;
        clock.Advance(TimeSpan.FromSeconds(60));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Image stale", cut.Find(".current-sky-summary .state-chip").TextContent);
            Assert.AreEqual("Image stale", cut.Find(".live-indicator").TextContent);
        });
    }

    [TestMethod]
    public async Task CenteredArchiveReopenAndIncompatibleBasesIgnoreConflictingBrowserVisibilityPrefsAsync()
    {
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-20));
        var exp104 = CenteredExposure(104, OperatorUiTestData.Now.AddSeconds(-10));
        var compatA = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var compatB = new PresentationCompatibilityDescriptor(320, 240, new string('D', 64), new string('E', 64));

        var singleA = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compatA, enabledByDefault: true)], compatA);
        var singleBRev1 = CenteredSingle(exp104, 2,
            [ReadyLayer("star-annotations", exp104.Capture.CaptureId, 1, compatB, enabledByDefault: true)], compatB, 320, 240);
        var singleBRev2 = CenteredRevision(singleBRev1.Base, singleBRev1.Revision.RevisionId, 3,
        [
            ReadyLayer("star-annotations", exp104.Capture.CaptureId, 1, compatB, enabledByDefault: true),
            ReadyLayer("scene-constellations", exp104.Capture.CaptureId, 2, compatB, enabledByDefault: true)
        ]);

        // 1. Plain centered archive reopen (no SavedSelection) MUST NOT be overridden by browser localStorage prefs!
        using (var archiveContext = new BunitContext())
        {
            Configure(archiveContext);
            var readCount = 0;
            var writeCount = 0;
            var module = archiveContext.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            module.Setup<string?>("readLayerVisibility", _ =>
            {
                readCount++;
                return true;
            }).SetResult("{\"star-annotations\":false}");
            module.SetupVoid("writeLayerVisibility", _ =>
            {
                writeCount++;
                return true;
            }).SetVoidResult();

            var archiveBase = new CenteredViewArchiveBase(singleA.Base, singleA.Revision.RevisionId, singleA.Revision.RevisionId, singleA.Revision.RevisionId);
            CameraAgentCenteredArchiveBaseSummary[] summaries =
                [new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, [singleA.Revision], []))];
            var detail = CenteredDetailView(exp103.Capture.CaptureId, summaries, singleA, savedSelection: null, selectionError: null);

            var cut = archiveContext.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
            cut.WaitForAssertion(() =>
            {
                Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            });
            Assert.AreEqual(0, readCount);
            Assert.AreEqual(0, writeCount);
        }

        // 2. Live incompatible base transition clears stale visibility AND scopes browser storage by compatibility!
        var storage = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var liveContext = new BunitContext())
        {
            var (service, clock) = ConfigurePolling(liveContext);
            var module = liveContext.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            module.Setup<string?>("readLayerVisibility", invocation => true)
                .SetResult(null);
            module.SetupVoid("writeLayerVisibility", invocation =>
            {
                var key = (string)invocation.Arguments[0]!;
                var val = (string?)invocation.Arguments[1];
                if (val is null) storage.Remove(key);
                else storage[key] = val;
                return true;
            }).SetVoidResult();

            var current = singleA;
            service.CenteredCurrentSkyHandler = (_, _) =>
                ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                    CenteredSkyView(current, current.Base.Definition.Reference, CenteredViewLiveState.Ready, clock.GetUtcNow())));

            var cut = liveContext.Render<CurrentSkyPage>();
            cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked")));

            // Toggle star-annotations OFF on compatA
            await cut.Find("[data-layer-kind='star-annotations'] input").ChangeAsync(false).ConfigureAwait(false);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            Assert.AreEqual(1, storage.Count);

            // Switch to incompatible base B rev 1 -> profile default ON applies
            current = singleBRev1;
            clock.Advance(TimeSpan.FromSeconds(15));
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() =>
            {
                Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            });

            // Deliver rev 2 on base B (same compatB) -> stale OFF from compatA MUST NOT leak back!
            current = singleBRev2;
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() =>
            {
                Assert.AreEqual(singleBRev2.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            });
        }

        // 3. Reloading a fresh page on compatB with compatA's saved localStorage key does NOT apply compatA's prefs!
        using (var reloadContext = new BunitContext())
        {
            var (service, clock) = ConfigurePolling(reloadContext);
            var module = reloadContext.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            string? readKey = null;
            module.Setup<string?>("readLayerVisibility", invocation =>
            {
                readKey = (string)invocation.Arguments[0]!;
                return !storage.ContainsKey(readKey);
            }).SetResult(null);

            service.CenteredCurrentSkyHandler = (_, _) =>
                ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                    CenteredSkyView(singleBRev1, exp104, CenteredViewLiveState.Ready, clock.GetUtcNow())));

            var cut = reloadContext.Render<CurrentSkyPage>();
            cut.WaitForAssertion(() =>
            {
                Assert.IsNotNull(readKey);
                Assert.IsFalse(storage.ContainsKey(readKey));
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            });
        }
    }

    [TestMethod]
    public async Task CenteredSelectedFrameDiagnosticsUsesExactEvidencePinAndNeverFallsBackToCaptureIdAsync()
    {
        var astrometry = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var fitArtifactId = Guid.NewGuid();
        var pinned = astrometry with { ArtifactId = fitArtifactId };
        var fitPin = new CenteredViewArtifact(fitArtifactId, new string('5', 64), new string('6', 64));

        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var captureId = astrometry.CaptureId;
        var exp103 = CenteredExposure(103, astrometry.Solution!.Assessment.Frame.ExposureStartUtc);
        exp103 = exp103 with
        {
            Input = exp103.Input with
            {
                Key = exp103.Input.Key with { CaptureId = captureId },
                Raw = exp103.Input.Raw with
                {
                    Capture = exp103.Input.Raw.Capture with { CaptureId = captureId },
                    Artifact = exp103.Input.Raw.Artifact with { ArtifactId = astrometry.Solution.Assessment.Frame.SourceArtifactId }
                }
            }
        };
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var rev1NoPin = CenteredSingle(exp103, 1, [PendingLayer(AstrometricDiagnosticPresentation.ResidualsLayer, captureId)], compat);
        var rev2Pinned = CenteredRevision(rev1NoPin.Base, rev1NoPin.Revision.RevisionId, 2,
        [
            new(new(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, captureId),
                2, fitPin, clock.GetUtcNow(), CenteredViewReadiness.Ready, null,
                Drawable(AstrometricDiagnosticPresentation.ResidualsLayer, 412, false, compat))
        ]);

        var unpinnedCalls = 0;
        CenteredViewArtifact? observedPin = null;
        service.AstrometryHandler = (_, _) =>
        {
            Interlocked.Increment(ref unpinnedCalls);
            throw new InvalidOperationException("Unpinned capture-ID astrometry lookup must not be used in centered mode.");
        };
        service.ExactAstrometryHandler = (id, evidence, _) =>
        {
            Assert.AreEqual(captureId, id);
            Assert.IsNotNull(evidence);
            observedPin = evidence;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(pinned));
        };

        var current = rev1NoPin;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(current, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent));
        Assert.AreEqual(0, Volatile.Read(ref unpinnedCalls));
        Assert.IsNull(observedPin);

        // Rev 2 pins the exact fit artifact -> exact evidence-pinned lookup runs and renders Accepted fit
        current = rev2Pinned;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(fitPin, observedPin);
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
        });
        Assert.AreEqual(0, Volatile.Read(ref unpinnedCalls));
    }

    [TestMethod]
    public async Task CenteredPublisherChangedOffloadsWakeOffCommittingThreadAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var publisher = new TestCenteredViewPublisher();
        context.Services.AddSingleton<ICenteredViewPublisher>(publisher);

        var exp101 = CenteredExposure(101, clock.GetUtcNow().AddSeconds(-5));
        var exp102 = CenteredExposure(102, clock.GetUtcNow());
        var single101 = CenteredSingle(exp101, 1);
        var single102 = CenteredSingle(exp102, 2);

        using var inRaiseChangedOnCurrentThread = new ThreadLocal<bool>();
        var raiseReturned = new ManualResetEventSlim(false);
        var wakeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wakeObservedInsideRaise = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        service.CenteredCurrentSkyHandler = async (_, ct) =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                return OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                    CenteredSkyView(single101, exp101, CenteredViewLiveState.Warming, clock.GetUtcNow()));
            }
            wakeObservedInsideRaise.TrySetResult(inRaiseChangedOnCurrentThread.Value);
            Assert.IsTrue(raiseReturned.Wait(TimeSpan.FromSeconds(5), ct),
                "Wake handler ran synchronously before RaiseChanged returned.");
            await wakeGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(single102, exp102, CenteredViewLiveState.Warming, clock.GetUtcNow()));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("#101", cut.Find("[data-centered-reference]").TextContent));

        // Causality check: RaiseChanged MUST return before the wake handler runs on this thread
        // and MUST NOT block while the wake handler waits on wakeGate/raiseReturned!
        inRaiseChangedOnCurrentThread.Value = true;
        try
        {
            publisher.RaiseChanged(CenteredStream);
        }
        finally
        {
            inRaiseChangedOnCurrentThread.Value = false;
            raiseReturned.Set();
        }

        var ranSynchronouslyInsideRaise = await wakeObservedInsideRaise.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(ranSynchronouslyInsideRaise, "Publisher wake must not execute synchronously inside RaiseChanged.");

        wakeGate.SetResult();
        cut.WaitForAssertion(() => Assert.AreEqual("#102", cut.Find("[data-centered-reference]").TextContent));
    }

    [TestMethod]
    public async Task CenteredReadyBaseRendersImmediatelyWhileOverlayReadIsPendingAndIgnoresStaleOverlayResultsAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp101 = CenteredExposure(101, clock.GetUtcNow().AddSeconds(-30));
        var exp102 = CenteredExposure(102, clock.GetUtcNow().AddSeconds(-20));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));

        var single101 = CenteredSingle(exp101, 1,
            [ReadyLayer("star-annotations", exp101.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);
        var single102Rev1 = CenteredSingle(exp102, 2,
            [ReadyLayer("star-annotations", exp102.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);
        var single102Rev2 = CenteredRevision(single102Rev1.Base, single102Rev1.Revision.RevisionId, 3,
        [
            ReadyLayer("star-annotations", exp102.Capture.CaptureId, 1, compat, enabledByDefault: true),
            ReadyLayer("scene-constellations", exp102.Capture.CaptureId, 2, compat, enabledByDefault: true)
        ]);
        var single103 = CenteredSingle(exp103, 4,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);

        var pendingReads = new Dictionary<(string BaseId, string RevisionId), TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>>();
        TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>> GateFor(CenteredView view)
        {
            var key = (view.Base.BaseId, view.Revision.RevisionId);
            if (!pendingReads.TryGetValue(key, out var tcs))
            {
                tcs = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingReads[key] = tcs;
            }
            return tcs;
        }

        var current = single101;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(current, current.Base.Definition.Reference, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };
        service.CenteredPresentationHandler = (baseId, revisionId, _) =>
        {
            var key = (baseId, revisionId);
            if (!pendingReads.TryGetValue(key, out var tcs))
            {
                tcs = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingReads[key] = tcs;
            }
            return new ValueTask<OperatorUiResult<CameraAgentCenteredPresentationRead>>(tcs.Task);
        };

        // 1. Base 101 renders IMMEDIATELY while its optional overlay read is still pending!
        var tcs101 = GateFor(single101);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#101", cut.Find("[data-centered-reference]").TextContent);
            var img = cut.Find(".capture-image img");
            StringAssert.Contains(img.GetAttribute("src"), single101.Base.BaseId, StringComparison.Ordinal);
            Assert.IsFalse(tcs101.Task.IsCompleted);
            StringAssert.Contains(cut.Find(".stage-status").TextContent, "while optional overlays load", StringComparison.Ordinal);
        });

        // Release 101 overlay read -> validated layers attach cleanly!
        tcs101.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single101), null)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single101.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
        });

        // 2. Advance to base 102 rev 1 (overlay read held) -> superseded by rev 2 -> stale rev 1 result ignored!
        var tcs102Rev1 = GateFor(single102Rev1);
        var tcs102Rev2 = GateFor(single102Rev2);
        current = single102Rev1;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#102", cut.Find("[data-centered-reference]").TextContent);
            StringAssert.Contains(cut.Find(".capture-image img").GetAttribute("src"), single102Rev1.Base.BaseId, StringComparison.Ordinal);
        });

        current = single102Rev2;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);

        // Release stale rev 1 BEFORE rev 2 -> MUST be ignored!
        tcs102Rev1.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single102Rev1), null)));
        await cut.InvokeAsync(() => { }).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".sky-layer-workspace"));

        // Release rev 2 -> attaches rev 2 with both layers!
        tcs102Rev2.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single102Rev2), null)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single102Rev2.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
        });

        // 3. Advance to base 103 while a stale overlay result for base 102 cannot overwrite base 103!
        var tcs103 = GateFor(single103);
        current = single103;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            StringAssert.Contains(cut.Find(".capture-image img").GetAttribute("src"), single103.Base.BaseId, StringComparison.Ordinal);
        });
        tcs103.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single103), null)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single103.Base.BaseId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-base"));
            Assert.AreEqual(single103.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
        });
    }

    [TestMethod]
    public async Task CenteredHoldPathResolvesExpectedIntervalWhenFactsAreAbsentAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var t0 = clock.GetUtcNow();
        var exp103 = CenteredExposure(103, t0);
        var exp104 = CenteredExposure(104, t0.AddSeconds(10));
        var single103 = CenteredSingle(exp103, 1);
        var single104 = CenteredSingle(exp104, 2);

        var current = single103;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(current, exp104, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Facts = null,
                Centered = sky.Centered! with { ExpectedCaptureInterval = TimeSpan.FromSeconds(10) }
            }));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("Image current", cut.Find(".current-sky-summary .state-chip").TextContent);
        });

        // Pause live so incoming 104 takes the hold path with _facts == null!
        await cut.FindAll(".centered-live-controls button").Single(b => b.TextContent == "Pause live")
            .ClickAsync().ConfigureAwait(false);

        current = single104;
        clock.Advance(TimeSpan.FromSeconds(25));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            Assert.AreEqual("Image delayed", cut.Find(".current-sky-summary .state-chip").TextContent);
        });

        clock.Advance(TimeSpan.FromSeconds(40));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Image stale", cut.Find(".current-sky-summary .state-chip").TextContent);
        });
    }

    [TestMethod]
    public async Task CenteredRecoveredLayerOnSameRevisionBecomesAvailableAndToggleableAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var captureId = exp103.Capture.CaptureId;
        var starLayer = ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true);
        var constLayer = ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true);
        var single103 = CenteredSingle(exp103, 1, [starLayer, constLayer], compat);

        var fullPresentation = ProjectCenteredPresentation(single103);
        var partialPresentation = fullPresentation with
        {
            PresentationIdentitySha256 = new string('8', 64),
            Layers = [fullPresentation.Layers.Single(layer => layer.Kind == "star-annotations")]
        };

        var activePresentation = partialPresentation;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with { Presentation = activePresentation }
            }));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.AreEqual("Unavailable", cut.Find("[data-layer-kind='scene-constellations'] .layer-readiness-tag").TextContent);
            Assert.IsTrue(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
        });

        // Recover scene-constellations on the EXACT SAME (BaseId, RevisionId) via manual refresh!
        activePresentation = fullPresentation;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='scene-constellations'] .layer-readiness-tag").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
        });

        // Recovered layer is interactive and toggleable
        await cut.Find("[data-layer-kind='scene-constellations'] input").ChangeAsync(false).ConfigureAwait(false);
        Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
    }

    [TestMethod]
    public async Task CenteredIncompatibleBaseWithoutPresentationResetsVisibilityBeforePresentationArrivesAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();

        var compatA = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var compatB = new PresentationCompatibilityDescriptor(320, 240, new string('D', 64), new string('E', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-20));
        var exp104 = CenteredExposure(104, clock.GetUtcNow().AddSeconds(-10));

        var baseA = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compatA, enabledByDefault: true)], compatA);
        var baseB = CenteredSingle(exp104, 2,
            [ReadyLayer("star-annotations", exp104.Capture.CaptureId, 1, compatB, enabledByDefault: true)], compatB, 320, 240);

        var current = baseA;
        var includePresentation = true;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(current, current.Base.Definition.Reference, CenteredViewLiveState.Ready, clock.GetUtcNow());
            if (!includePresentation)
            {
                sky = sky with
                {
                    Centered = sky.Centered! with
                    {
                        Presentation = null,
                        PresentationStatus = CameraAgentLayeredPresentationStatus.Unavailable,
                        PresentationReason = "Temporary overlay outage"
                    }
                };
            }
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked")));

        // Turn stars OFF on base A
        await cut.Find("[data-layer-kind='star-annotations'] input").ChangeAsync(false).ConfigureAwait(false);
        Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));

        // Incompatible base B arrives initially WITHOUT a presentation
        current = baseB;
        includePresentation = false;
        clock.Advance(TimeSpan.FromSeconds(15));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#104", cut.Find("[data-centered-reference]").TextContent);
            Assert.IsEmpty(cut.FindAll(".sky-layer-workspace"));
        });

        // Base B's presentation now arrives -> MUST NOT inherit base A's incompatible OFF visibility!
        includePresentation = true;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find(".sky-layer-workspace"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
        });
    }

    [TestMethod]
    public async Task CenteredPinnedDiagnosticsRetriesTransientReadCapacityOnRefreshAsync()
    {
        var astrometry = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var fitArtifactId = Guid.NewGuid();
        var pinned = astrometry with { ArtifactId = fitArtifactId };
        var fitPin = new CenteredViewArtifact(fitArtifactId, new string('7', 64), new string('8', 64));

        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var captureId = astrometry.CaptureId;
        var exp103 = CenteredExposure(103, astrometry.Solution!.Assessment.Frame.ExposureStartUtc);
        exp103 = exp103 with
        {
            Input = exp103.Input with
            {
                Key = exp103.Input.Key with { CaptureId = captureId },
                Raw = exp103.Input.Raw with
                {
                    Capture = exp103.Input.Raw.Capture with { CaptureId = captureId },
                    Artifact = exp103.Input.Raw.Artifact with { ArtifactId = astrometry.Solution.Assessment.Frame.SourceArtifactId }
                }
            }
        };
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var residualsLayer = new CenteredViewLayer(
            new(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, captureId),
            1, fitPin, clock.GetUtcNow(), CenteredViewReadiness.Ready, null,
            Drawable(AstrometricDiagnosticPresentation.ResidualsLayer, 412, false, compat));
        var single103 = CenteredSingle(exp103, 1, [residualsLayer], compat);

        var attempts = 0;
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow())));
        service.ExactAstrometryHandler = (id, evidence, _) =>
        {
            Assert.AreEqual(captureId, id);
            Assert.AreEqual(fitPin, evidence);
            return Interlocked.Increment(ref attempts) == 1
                ? ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(
                    new(captureId, "Unavailable", "read-capacity")))
                : ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(pinned));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, Volatile.Read(ref attempts));
            Assert.AreEqual("Unavailable", cut.Find(".astrometry-card .state-chip").TextContent);
        });

        // Manual refresh retries the transient read-capacity failure against the exact pin and succeeds!
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, Volatile.Read(ref attempts));
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
        });
    }

    [TestMethod]
    public void CenteredWholeOverlayReadFailureMarksLayerRowsUnavailable()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var captureId = exp103.Capture.CaptureId;
        var partialConstellations = new CenteredViewLayer(
            new("scene-constellations", CenteredViewLayerScope.Reference, captureId),
            2, EvidencePin("const"), clock.GetUtcNow(), CenteredViewReadiness.Partial, "centered.partial-coverage",
            Drawable("scene-constellations", 10, true, compat));
        var single103 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", captureId, 1, compat), partialConstellations], compat);

        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.Unavailable,
                    PresentationReason = "Centered overlays are temporarily unavailable.",
                    PresentationDeferred = false
                }
            }));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            var starRow = cut.Find("[data-layer-kind='star-annotations']");
            Assert.AreEqual("Unavailable", starRow.GetAttribute("data-layer-readiness"));
            Assert.AreEqual("Unavailable", starRow.QuerySelector(".layer-readiness-tag--unavailable")!.TextContent);
            Assert.IsTrue(starRow.QuerySelector("input")!.HasAttribute("disabled"));

            var constRow = cut.Find("[data-layer-kind='scene-constellations']");
            Assert.AreEqual("Unavailable", constRow.GetAttribute("data-layer-readiness"));
            Assert.AreEqual("Unavailable", constRow.QuerySelector(".layer-readiness-tag--unavailable")!.TextContent);
            Assert.IsTrue(constRow.QuerySelector("input")!.HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task CenteredGuardsBlockLegacyLayerReadsRequireReferenceEqualityAndIgnorePreCenteredPrefsAsync()
    {
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-10));
        var single103 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);
        var presentation = ProjectCenteredPresentation(single103);
        var clonedPresentation = presentation with { };
        var previewUrl = CameraAgentCenteredViewProjection.BoundPreviewUrl(single103);

        // 1. Hero ReferenceEquals guard (Opus L3)
        Assert.IsTrue(CurrentSkyPage.CanShowCenteredLayeredHero(true, false, single103, presentation, presentation, previewUrl));
        Assert.IsFalse(CurrentSkyPage.CanShowCenteredLayeredHero(true, false, single103, presentation, clonedPresentation, previewUrl));
        Assert.IsFalse(CurrentSkyPage.CanShowCenteredLayeredHero(false, false, single103, presentation, presentation, previewUrl));
        Assert.IsFalse(CurrentSkyPage.CanShowCenteredLayeredHero(true, true, single103, presentation, presentation, previewUrl));
        Assert.IsFalse(CurrentSkyPage.CanShowCenteredLayeredHero(true, false, single103, presentation, presentation, null));

        // 2. TryLoadLayers centered guard on archive refresh with null presentation (Opus L3)
        using (var archiveContext = new BunitContext())
        {
            var service = Configure(archiveContext);
            var legacyCalls = 0;
            service.PresentationHandler = (_, _) =>
            {
                Interlocked.Increment(ref legacyCalls);
                throw new InvalidOperationException("Legacy GetLayeredPresentationAsync must not run in centered archive mode.");
            };
            service.CurrentPresentationHandler = (_, _) =>
            {
                Interlocked.Increment(ref legacyCalls);
                throw new InvalidOperationException("Legacy GetCurrentLayeredPresentationAsync must not run in centered archive mode.");
            };

            var archiveBase = new CenteredViewArchiveBase(single103.Base, single103.Revision.RevisionId, single103.Revision.RevisionId, single103.Revision.RevisionId);
            CameraAgentCenteredArchiveBaseSummary[] summaries =
                [new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, [single103.Revision], []))];
            var detail = CenteredDetailView(exp103.Capture.CaptureId, summaries, single103, savedSelection: null, selectionError: null);
            detail = detail with
            {
                CenteredArchive = detail.CenteredArchive! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.Unavailable
                }
            };

            var cut = archiveContext.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            Assert.AreEqual(0, Volatile.Read(ref legacyCalls));
        }

        // 3. Initial browser-preference hydration cannot apply unscoped pre-centered prefs to a centered base (Opus L4)
        using (var hydrationContext = new BunitContext())
        {
            var (service, clock) = ConfigurePolling(hydrationContext);
            var module = hydrationContext.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var readKeys = new List<string>();
            module.Setup<string?>("readLayerVisibility", invocation =>
            {
                var key = (string)invocation.Arguments[0]!;
                readKeys.Add(key);
                return !key.Contains(":centered:", StringComparison.Ordinal);
            }).SetResult("{\"star-annotations\":false}");
            module.Setup<string?>("readLayerVisibility", invocation =>
            {
                var key = (string)invocation.Arguments[0]!;
                return key.Contains(":centered:", StringComparison.Ordinal);
            }).SetResult(null);

            var initialGate = new TaskCompletionSource<OperatorUiResult<CameraAgentCurrentSkyView>>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CenteredCurrentSkyHandler = (_, _) => new ValueTask<OperatorUiResult<CameraAgentCurrentSkyView>>(initialGate.Task);

            // Render while initial view read is still pending (firstRender occurs before centered mode is known)
            var cut = hydrationContext.Render<CurrentSkyPage>();
            cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find(".sky-refresh-notice")));

            // Release centered base with star-annotations enabledByDefault=true
            initialGate.SetResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow())));
            cut.WaitForAssertion(() =>
            {
                Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
                Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            });
            Assert.IsFalse(readKeys.Any(static key => !key.Contains(":centered:", StringComparison.Ordinal)),
                $"Unscoped pre-centered key was unexpectedly read: {string.Join(", ", readKeys)}");
        }
    }

    [TestMethod]
    public async Task CenteredSynchronouslyBlockingLiveOverlayReadNeverBlocksReadyBaseRenderAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp101 = CenteredExposure(101, clock.GetUtcNow().AddSeconds(-10));
        var single101 = CenteredSingle(exp101, 1,
            [ReadyLayer("star-annotations", exp101.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);

        var overlayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSyncOverlay = new ManualResetEventSlim(false);
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(single101, exp101, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };
        service.CenteredPresentationHandler = (_, _, ct) =>
        {
            overlayEntered.TrySetResult();
            // Block synchronously BEFORE returning/yielding to prove the Blazor renderer thread
            // is never held before StateHasChanged() renders the ready base!
            Assert.IsTrue(releaseSyncOverlay.Wait(TimeSpan.FromSeconds(5), ct),
                "Expected test to release synchronous overlay probe.");
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single101), null)));
        };

        var cut = context.Render<CurrentSkyPage>();
        try
        {
            cut.WaitForAssertion(() =>
            {
                Assert.AreEqual("#101", cut.Find("[data-centered-reference]").TextContent);
                var img = cut.Find(".capture-image img");
                StringAssert.Contains(img.GetAttribute("src"), single101.Base.BaseId, StringComparison.Ordinal);
                Assert.IsFalse(releaseSyncOverlay.IsSet);
            });
            await overlayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(releaseSyncOverlay.IsSet);
        }
        finally
        {
            releaseSyncOverlay.Set();
        }

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single101.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task CenteredArchivedBaseRendersImmediatelyWhileOverlayReadStallsAndAppliesSavedSelectionOnCompletionAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-20));
        var captureId = exp103.Capture.CaptureId;
        var starLayer = ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true);
        var constLayer = ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true);
        var single103 = CenteredSingle(exp103, 1, [starLayer, constLayer], compat);
        var starId = single103.Revision.Manifest.Layers.Single(layer => layer.LayerKind == "star-annotations").LayerIdentitySha256;
        var savedSelection = new CenteredViewSavedSelection(
            Guid.NewGuid(), single103.Base.BaseId, single103.Revision.RevisionId, [starId], OperatorUiTestData.Now);

        var archiveBase = new CenteredViewArchiveBase(single103.Base, single103.Revision.RevisionId, single103.Revision.RevisionId, single103.Revision.RevisionId);
        CameraAgentCenteredArchiveBaseSummary[] summaries =
            [new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, [single103.Revision], []))];
        var detail = CenteredDetailView(captureId, summaries, single103, savedSelection: savedSelection, selectionError: null);
        detail = detail with
        {
            CenteredArchive = detail.CenteredArchive! with
            {
                Presentation = null,
                PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                PresentationReason = null,
                PresentationDeferred = true
            }
        };

        var overlayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverlay = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CenteredPresentationHandler = (baseId, revisionId, _) =>
        {
            Assert.AreEqual(single103.Base.BaseId, baseId);
            Assert.AreEqual(single103.Revision.RevisionId, revisionId);
            overlayStarted.TrySetResult();
            return new ValueTask<OperatorUiResult<CameraAgentCenteredPresentationRead>>(releaseOverlay.Task);
        };

        var cut = context.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
        cut.WaitForAssertion(() =>
        {
            var img = cut.Find(".capture-image img");
            StringAssert.Contains(img.GetAttribute("src"), single103.Base.BaseId, StringComparison.Ordinal);
            Assert.IsFalse(releaseOverlay.Task.IsCompleted);
            StringAssert.Contains(cut.Find(".stage-status").TextContent, "while optional overlays load", StringComparison.Ordinal);
        });
        await overlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Release stalled archive overlay read -> exact saved selection layers apply cleanly!
        releaseOverlay.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single103), null)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single103.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
        });
    }

    [TestMethod]
    public async Task CenteredPauseCancelsInFlightOverlayOnlyRevisionAndDropsLateCompletionUntilResumeAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var captureId = exp103.Capture.CaptureId;
        var rev1 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true)], compat);
        var rev2 = CenteredRevision(rev1.Base, rev1.Revision.RevisionId, 2,
        [
            ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true),
            ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true)
        ]);

        var current = rev1;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(current, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };

        var rev2FirstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rev2FirstGate = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rev2FirstToken = CancellationToken.None;
        var rev2CallCount = 0;

        service.CenteredPresentationHandler = (_, revisionId, ct) =>
        {
            if (revisionId == rev1.Revision.RevisionId)
            {
                return ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                    new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(rev1), null)));
            }
            if (Interlocked.Increment(ref rev2CallCount) == 1)
            {
                rev2FirstToken = ct;
                rev2FirstStarted.TrySetResult();
                return new ValueTask<OperatorUiResult<CameraAgentCenteredPresentationRead>>(rev2FirstGate.Task);
            }
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(rev2), null)));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(rev1.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
        });

        // Deliver newer overlay-only rev2 for the SAME base -> starts in-flight load while rev1 remains displayed
        current = rev2;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await rev2FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(rev1.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));

        // Pause live while rev2 is still loading -> cancels in-flight load and pins rev1!
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Pause live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent));
        Assert.IsTrue(rev2FirstToken.IsCancellationRequested, "Pause must cancel the in-flight overlay presentation read.");

        // Simulate late completion racing cancellation -> MUST be dropped while paused!
        rev2FirstGate.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(rev2), null)));
        await cut.InvokeAsync(() => { }).ConfigureAwait(false);
        Assert.AreEqual(rev1.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
        Assert.IsEmpty(cut.FindAll("[data-layer-kind='scene-constellations']"));

        // Resume live -> starts fresh load and advances cleanly to rev2!
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Resume live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(rev2.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
            Assert.IsGreaterThanOrEqualTo(2, Volatile.Read(ref rev2CallCount));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredDeferredRecoveredLayerOnSameRevisionRebindsAndBecomesToggleableAsync(bool manualRefresh)
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        var bindCount = 0;
        module.Setup<string>("bindLayerToggles", _ =>
        {
            Interlocked.Increment(ref bindCount);
            return true;
        }).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.SetupVoid("writeLayerVisibility", _ => true).SetVoidResult();

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var captureId = exp103.Capture.CaptureId;
        var starLayer = ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true);
        var constLayer = ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true);
        var single103 = CenteredSingle(exp103, 1, [starLayer, constLayer], compat);

        var fullPresentation = ProjectCenteredPresentation(single103);
        var partialPresentation = fullPresentation with
        {
            PresentationIdentitySha256 = new string('8', 64),
            Layers = [fullPresentation.Layers.Single(layer => layer.Kind == "star-annotations")]
        };

        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };

        var presentationCalls = 0;
        service.CenteredPresentationHandler = (baseId, revisionId, _) =>
        {
            Assert.AreEqual(single103.Base.BaseId, baseId);
            Assert.AreEqual(single103.Revision.RevisionId, revisionId);
            var call = Interlocked.Increment(ref presentationCalls);
            var payload = call == 1 ? partialPresentation : fullPresentation;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                new(CameraAgentLayeredPresentationStatus.Found, payload, null)));
        };

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, Volatile.Read(ref presentationCalls));
            Assert.AreEqual(1, Volatile.Read(ref bindCount));
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.AreEqual("Unavailable", cut.Find("[data-layer-kind='scene-constellations'] .layer-readiness-tag").TextContent);
            Assert.IsTrue(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
        });

        // Recover scene-constellations on the EXACT SAME (BaseId, RevisionId) through the production deferred path
        // via either automatic poll OR manual refresh!
        if (manualRefresh)
        {
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        cut.WaitForAssertion(() =>
        {
            Assert.IsGreaterThanOrEqualTo(2, Volatile.Read(ref presentationCalls));
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='scene-constellations'] .layer-readiness-tag").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
            Assert.IsGreaterThanOrEqualTo(2, Volatile.Read(ref bindCount));
        });

        await cut.Find("[data-layer-kind='scene-constellations'] input").ChangeAsync(false).ConfigureAwait(false);
        Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
    }

    [TestMethod]
    public async Task CenteredDisposeAsyncHandlesConcurrentCancellationWithoutNullReferenceExceptionAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.SetupVoid("disconnect", _ => true).SetVoidResult();

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var single103 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", exp103.Capture.CaptureId, 1, compat, enabledByDefault: true)], compat);

        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(single103, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };

        var overlayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentDisposeTask = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<CurrentSkyPage>? rendered = null;
        service.CenteredPresentationHandler = async (_, _, ct) =>
        {
            using var registration = ct.Register(() =>
            {
                if (rendered is not null)
                {
                    concurrentDisposeTask.TrySetResult(rendered.Instance.DisposeAsync().AsTask());
                }
            });
            overlayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new UnreachableException();
        };

        rendered = context.Render<CurrentSkyPage>();
        await overlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await rendered.Instance.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var racingDispose = await concurrentDisposeTask.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await racingDispose.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CenteredPauseAllowsSameRevisionOverlayLoadToFinishWhileStillBlockingNewerRevisionAsync()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var captureId = exp103.Capture.CaptureId;
        var rev1 = CenteredSingle(exp103, 1,
            [ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true)], compat);
        var rev2 = CenteredRevision(rev1.Base, rev1.Revision.RevisionId, 2,
        [
            ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true),
            ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true)
        ]);

        var current = rev1;
        service.CenteredCurrentSkyHandler = (_, _) =>
        {
            var sky = CenteredSkyView(current, exp103, CenteredViewLiveState.Ready, clock.GetUtcNow());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(sky with
            {
                Centered = sky.Centered! with
                {
                    Presentation = null,
                    PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                    PresentationReason = null,
                    PresentationDeferred = true
                }
            }));
        };

        var rev1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rev1Gate = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rev1Token = CancellationToken.None;
        var rev2Calls = 0;
        service.CenteredPresentationHandler = (_, revisionId, ct) =>
        {
            if (revisionId == rev1.Revision.RevisionId)
            {
                rev1Token = ct;
                rev1Started.TrySetResult();
                return new ValueTask<OperatorUiResult<CameraAgentCenteredPresentationRead>>(rev1Gate.Task);
            }
            Interlocked.Increment(ref rev2Calls);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(rev2), null)));
        };

        var cut = context.Render<CurrentSkyPage>();
        await rev1Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("#103", cut.Find("[data-centered-reference]").TextContent);
            StringAssert.Contains(cut.Find(".stage-status").TextContent, "while optional overlays load", StringComparison.Ordinal);
        });

        // Pause live while rev1's SAME-revision overlay load is in flight, and server advances to rev2.
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Pause live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent));
        Assert.IsFalse(rev1Token.IsCancellationRequested, "Pause must NOT cancel an in-flight same-revision overlay load.");

        current = rev2;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(0, Volatile.Read(ref rev2Calls), "Paused live view must NOT load or advance to rev2.");

        // Release rev1's same-revision overlay load -> finishes cleanly while paused and stays pinned on rev1!
        rev1Gate.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(rev1), null)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Paused", cut.Find("[data-centered-state]").TextContent);
            Assert.AreEqual(rev1.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.IsEmpty(cut.FindAll("[data-layer-kind='scene-constellations']"));
            Assert.IsFalse(cut.Find(".stage-status").TextContent.Contains("while optional overlays load", StringComparison.Ordinal));
        });
        Assert.AreEqual(0, Volatile.Read(ref rev2Calls));

        // Resume live -> now advances cleanly to rev2!
        await cut.FindAll(".centered-live-controls button").Single(button => button.TextContent == "Resume live")
            .ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(rev2.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task CenteredArchivedOverlayArrivingWhileViewerOpenAppliesWhenViewerClosesAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);
        module.Setup<bool>("requestFullScreen", _ => true).SetResult(true);

        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var exp103 = CenteredExposure(103, OperatorUiTestData.Now.AddSeconds(-20));
        var captureId = exp103.Capture.CaptureId;
        var starLayer = ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true);
        var constLayer = ReadyLayer("scene-constellations", captureId, 2, compat, enabledByDefault: true);
        var single103 = CenteredSingle(exp103, 1, [starLayer, constLayer], compat);
        var starId = single103.Revision.Manifest.Layers.Single(layer => layer.LayerKind == "star-annotations").LayerIdentitySha256;
        var savedSelection = new CenteredViewSavedSelection(
            Guid.NewGuid(), single103.Base.BaseId, single103.Revision.RevisionId, [starId], OperatorUiTestData.Now);

        var archiveBase = new CenteredViewArchiveBase(single103.Base, single103.Revision.RevisionId, single103.Revision.RevisionId, single103.Revision.RevisionId);
        CameraAgentCenteredArchiveBaseSummary[] summaries =
            [new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, [single103.Revision], []))];
        var detail = CenteredDetailView(captureId, summaries, single103, savedSelection: savedSelection, selectionError: null);
        detail = detail with
        {
            CenteredArchive = detail.CenteredArchive! with
            {
                Presentation = null,
                PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                PresentationReason = null,
                PresentationDeferred = true
            }
        };

        var overlayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverlay = new TaskCompletionSource<OperatorUiResult<CameraAgentCenteredPresentationRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CenteredPresentationHandler = (_, _, _) =>
        {
            overlayStarted.TrySetResult();
            return new ValueTask<OperatorUiResult<CameraAgentCenteredPresentationRead>>(releaseOverlay.Task);
        };

        var cut = context.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
        await overlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Enter fullscreen viewer while archive overlays are still loading.
        await cut.Find("#current-sky-view-large").ClickAsync().ConfigureAwait(false);

        // Archive overlay read completes while viewer is open -> retained, not mutated mid-fullscreen.
        releaseOverlay.SetResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
            new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single103), null)));
        await cut.InvokeAsync(() => { }).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".sky-layer-workspace"));

        // Close fullscreen viewer -> deferred archive overlay applies immediately with exact saved selection!
        await cut.Instance.ViewerClosedAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(single103.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.IsTrue(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("checked"));
            Assert.IsFalse(cut.Find("[data-layer-kind='scene-constellations'] input").HasAttribute("checked"));
        });
    }

    [TestMethod]
    public async Task CenteredArchivedManualRefreshRecoversFailedOverlayAndTransientAstrometryAsync()
    {
        var astrometry = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var fitArtifactId = Guid.NewGuid();
        var pinned = astrometry with { ArtifactId = fitArtifactId };
        var fitPin = new CenteredViewArtifact(fitArtifactId, new string('A', 64), new string('B', 64));

        using var context = new BunitContext();
        var service = Configure(context);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.Setup<string?>("readLayerVisibility", _ => true).SetResult(null);

        var captureId = astrometry.CaptureId;
        var exp103 = CenteredExposure(103, astrometry.Solution!.Assessment.Frame.ExposureStartUtc);
        exp103 = exp103 with
        {
            Input = exp103.Input with
            {
                Key = exp103.Input.Key with { CaptureId = captureId },
                Raw = exp103.Input.Raw with
                {
                    Capture = exp103.Input.Raw.Capture with { CaptureId = captureId },
                    Artifact = exp103.Input.Raw.Artifact with { ArtifactId = astrometry.Solution.Assessment.Frame.SourceArtifactId }
                }
            }
        };
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var starLayer = ReadyLayer("star-annotations", captureId, 1, compat, enabledByDefault: true);
        var residualsLayer = new CenteredViewLayer(
            new(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, captureId),
            2, fitPin, OperatorUiTestData.Now, CenteredViewReadiness.Ready, null,
            Drawable(AstrometricDiagnosticPresentation.ResidualsLayer, 412, false, compat));
        var single103 = CenteredSingle(exp103, 1, [starLayer, residualsLayer], compat);

        var archiveBase = new CenteredViewArchiveBase(single103.Base, single103.Revision.RevisionId, single103.Revision.RevisionId, single103.Revision.RevisionId);
        CameraAgentCenteredArchiveBaseSummary[] summaries =
            [new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, [single103.Revision], []))];
        var detail = CenteredDetailView(captureId, summaries, single103, savedSelection: null, selectionError: null);
        detail = detail with
        {
            Capture = detail.Capture with { ExposureStartedUtc = astrometry.Solution!.Assessment.Frame.ExposureStartUtc },
            CenteredArchive = detail.CenteredArchive! with
            {
                Presentation = null,
                PresentationStatus = CameraAgentLayeredPresentationStatus.NotRetained,
                PresentationReason = null,
                PresentationDeferred = true
            }
        };

        var allowRecovery = 0;
        var overlayAttempts = 0;
        var astrometryAttempts = 0;
        service.CenteredPresentationHandler = (_, _, _) =>
        {
            Interlocked.Increment(ref overlayAttempts);
            return Volatile.Read(ref allowRecovery) == 0
                ? ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                    new(CameraAgentLayeredPresentationStatus.Unavailable, null, "Temporary archive overlay outage.")))
                : ValueTask.FromResult(OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                    new(CameraAgentLayeredPresentationStatus.Found, ProjectCenteredPresentation(single103), null)));
        };
        service.ExactAstrometryHandler = (_, _, _) =>
        {
            Interlocked.Increment(ref astrometryAttempts);
            return Volatile.Read(ref allowRecovery) == 0
                ? ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(
                    new(captureId, "Unavailable", "read-capacity")))
                : ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(pinned));
        };

        var cut = context.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, detail));
        cut.WaitForAssertion(() =>
        {
            Assert.IsGreaterThanOrEqualTo(1, Volatile.Read(ref overlayAttempts));
            Assert.IsGreaterThanOrEqualTo(1, Volatile.Read(ref astrometryAttempts));
            Assert.AreEqual("Unavailable", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.AreEqual("Unavailable", cut.Find(".astrometry-card .state-chip").TextContent);
        });

        var overlayBeforeRefresh = Volatile.Read(ref overlayAttempts);
        var astrometryBeforeRefresh = Volatile.Read(ref astrometryAttempts);

        // Archive manual refresh retries BOTH the failed archive overlay read AND transient pinned astrometry!
        Volatile.Write(ref allowRecovery, 1);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.IsGreaterThan(overlayBeforeRefresh, Volatile.Read(ref overlayAttempts));
            Assert.IsGreaterThan(astrometryBeforeRefresh, Volatile.Read(ref astrometryAttempts));
            Assert.AreEqual(single103.Revision.RevisionId, cut.Find(".sky-layer-workspace").GetAttribute("data-centered-revision"));
            Assert.AreEqual("Ready", cut.Find("[data-layer-kind='star-annotations'] .layer-readiness-tag").TextContent);
            Assert.IsFalse(cut.Find("[data-layer-kind='star-annotations'] input").HasAttribute("disabled"));
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
        });
    }

    private static CenteredViewExposure CenteredExposure(long sequence, DateTimeOffset startedUtc)
    {
        var captureId = Guid.Parse($"19230000-0000-0000-0000-{sequence:D12}");
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 640, 480, 640, new byte[640 * 480]).Descriptor;
        descriptor = descriptor with
        {
            Capture = descriptor.Capture with { AgentId = "agent-test", CaptureId = captureId, CaptureSequence = sequence },
            Timing = descriptor.Timing with { ExposureStartedUtc = startedUtc, ExposureEndedUtc = startedUtc.AddSeconds(2) },
            Artifact = descriptor.Artifact with { ArtifactId = Guid.Parse($"19231000-0000-0000-0000-{sequence:D12}") }
        };
        var input = new ExposureInputV1(
            new(CenteredStream.Camera, CenteredStream.RunId, captureId),
            descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        return new(input, Guid.Parse($"19232000-0000-0000-0000-{sequence:D12}"),
            new(Guid.Parse($"19233000-0000-0000-0000-{sequence:D12}"), new string('A', 64), new string('B', 64)));
    }

    private static CenteredView CenteredSingle(
        CenteredViewExposure reference,
        long order,
        IReadOnlyList<CenteredViewLayer>? layers = null,
        PresentationCompatibilityDescriptor? compatibility = null,
        int width = 640,
        int height = 480)
    {
        var compat = compatibility ?? new PresentationCompatibilityDescriptor(width, height, new string('B', 64), new string('C', 64));
        var previewArtifactId = Guid.Parse($"19234000-0000-0000-0000-{reference.Capture.CaptureSequence:D12}");
        var preview = new CenteredViewPreview(
            new(previewArtifactId, new string('C', 64), "image/jpeg", compat),
            new string('D', 64),
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "Skia JPEG",
            "jpeg-v1",
            new(width, height, 0, 0, width, height, width, height))
        {
            Source = CenteredViewImageSource.QualifiedLinear
        };
        var definition = new CenteredViewBaseDefinition(
            CenteredStream,
            reference,
            reference.Capture.CaptureSequence,
            CenteredViewKind.Single,
            reference.Linear!,
            preview,
            [new(reference, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { method = "identity" }))],
            null);
        return CenteredRevision(new(SqliteCenteredViews.BaseIdentity(definition), definition, OperatorUiTestData.Now), null, order, layers ?? []);
    }

    private static CenteredView CenteredStack(
        CenteredViewExposure reference,
        IReadOnlyList<CenteredViewExposure> contributors,
        long order,
        IReadOnlyList<CenteredViewLayer>? layers = null,
        PresentationCompatibilityDescriptor? compatibility = null)
    {
        var compat = compatibility ?? new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var previewArtifactId = Guid.Parse($"19235000-0000-0000-0000-{reference.Capture.CaptureSequence:D12}");
        var preview = new CenteredViewPreview(
            new(previewArtifactId, new string('E', 64), "image/jpeg", compat),
            new string('F', 64),
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "Skia JPEG",
            "jpeg-v1",
            new(640, 480, 0, 0, 640, 480, 640, 480))
        {
            Source = CenteredViewImageSource.RegisteredLinear
        };
        var closedWindow = new CenteredViewArtifact(Guid.Parse($"19236000-0000-0000-0000-{reference.Capture.CaptureSequence:D12}"), new string('1', 64), new string('2', 64));
        var science = new CenteredViewArtifact(Guid.Parse($"19237000-0000-0000-0000-{reference.Capture.CaptureSequence:D12}"), new string('3', 64), new string('4', 64));
        var members = contributors.Select(member => new CenteredViewContributor(
            member,
            member.Capture.CaptureId == reference.Capture.CaptureId ? "ReferenceIdentity" : "VerifiedCentroidSimilarity",
            JsonSerializer.SerializeToElement(new { method = "test" }))).ToArray();
        var definition = new CenteredViewBaseDefinition(
            CenteredStream,
            reference,
            reference.Capture.CaptureSequence,
            CenteredViewKind.Stack,
            science,
            preview,
            members,
            closedWindow);
        return CenteredRevision(new(SqliteCenteredViews.BaseIdentity(definition), definition, OperatorUiTestData.Now), null, order, layers ?? []);
    }

    private static CenteredView CenteredRevision(
        CenteredViewBase viewBase,
        string? previousRevisionId,
        long order,
        IReadOnlyList<CenteredViewLayer> layers)
    {
        var normalized = SqliteCenteredViews.NormalizedLayers(viewBase.Definition, layers);
        var manifest = LayeredPresentationJson.CreateManifest(
            viewBase.Definition.Preview.Product,
            sceneIdentitySha256: null,
            normalized.Where(static layer => layer.Drawable is not null).Select(static layer => layer.Drawable!));
        var revisionId = SqliteCenteredViews.RevisionIdentity(viewBase.BaseId, previousRevisionId, normalized);
        return new(viewBase, new(revisionId, viewBase.BaseId, order, OperatorUiTestData.Now.AddSeconds(order), manifest, normalized));
    }

    private static CenteredViewLayer PendingLayer(string kind, Guid captureId, CenteredViewLayerScope scope = CenteredViewLayerScope.Reference) =>
        new(new(kind, scope, captureId), 0, null, null, CenteredViewReadiness.Waiting, "centered.awaiting-product", null);

    private static CenteredViewLayer DisabledLayer(string kind, Guid captureId, CenteredViewLayerScope scope = CenteredViewLayerScope.Reference) =>
        new(new(kind, scope, captureId), 0, null, null, CenteredViewReadiness.NotConfigured, "centered.layer-not-configured", null);

    private static CenteredViewLayer ReadyLayer(
        string kind,
        Guid captureId,
        long evidenceOrder,
        PresentationCompatibilityDescriptor compatibility,
        bool enabledByDefault = true,
        CenteredViewLayerScope scope = CenteredViewLayerScope.Reference)
    {
        var drawable = Drawable(kind, 20, enabledByDefault, compatibility);
        return new(new(kind, scope, captureId), evidenceOrder, EvidencePin($"{kind}-{captureId:N}"),
            OperatorUiTestData.Now, CenteredViewReadiness.Ready, null, drawable);
    }

    private static PresentationLayerV1 Drawable(
        string kind,
        int zOrder,
        bool enabledByDefault,
        PresentationCompatibilityDescriptor compatibility) =>
        LayeredPresentationJson.CreateLayer(
            kind,
            new(Guid.NewGuid(), new string('7', 64), PresentationLayerPayloadJson.MediaType, compatibility),
            sceneIdentitySha256: null,
            PresentationCoordinateSpace.ScenePixels,
            "renderer-v1",
            "style-v1",
            zOrder,
            PresentationBlendMode.Normal,
            1_000_000,
            enabledByDefault,
            default);

    private static CenteredViewArtifact EvidencePin(string seed)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        return new(Guid.NewGuid(), hash, hash);
    }

    private static CameraAgentLayeredPresentation ProjectCenteredPresentation(CenteredView view)
    {
        var layers = view.Revision.Manifest.Layers.Select((layer, index) => new CameraAgentPresentationLayer(
            layer.LayerIdentitySha256,
            layer.LayerKind,
            FormattableString.Invariant($"hvo-layer-{index}"),
            layer.ZOrder,
            layer.EnabledByDefault,
            layer.OpacityMillionths,
            layer.RendererVersion,
            layer.StyleVersion)).ToArray();
        var groups = string.Concat(layers.Select(layer => $"<g id=\"{layer.DomGroupId}\" data-layer-kind=\"{layer.Kind}\"></g>"));
        var w = view.Base.Definition.Preview.Transform.PreviewWidth;
        var h = view.Base.Definition.Preview.Transform.PreviewHeight;
        var svg = Encoding.UTF8.GetBytes(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\">{groups}</svg>"));
        return new CameraAgentLayeredPresentation(
            view.Base.Definition.Reference.Capture.CaptureId,
            view.Base.Definition.Preview.Product.ArtifactId,
            view.Revision.Manifest.ManifestIdentitySha256,
            view.Revision.RevisionId,
            new string('9', 64),
            w,
            h,
            layers,
            svg);
    }

    private static CameraAgentCurrentSkyView CenteredSkyView(
        CenteredView? view,
        CenteredViewExposure admissionHead,
        CenteredViewLiveState state,
        DateTimeOffset observedUtc,
        CenteredLiveMode mode = CenteredLiveMode.Centered,
        TimeSpan? minimumRefresh = null)
    {
        var headCapture = new CameraAgentPresentationCapture(
            admissionHead.Capture.CaptureId,
            admissionHead.Capture.CaptureSequence,
            admissionHead.ExposureStartedUtc,
            Math.Max(0, (long)(observedUtc - admissionHead.ExposureStartedUtc).TotalSeconds),
            GalleryEvidenceOrigin.Simulated);
        var displayCapture = view is null
            ? null
            : new CameraAgentPresentationCapture(
                view.Base.Definition.Reference.Capture.CaptureId,
                view.Base.Definition.Reference.Capture.CaptureSequence,
                view.Base.Definition.Reference.ExposureStartedUtc,
                Math.Max(0, (long)(observedUtc - view.Base.Definition.Reference.ExposureStartedUtc).TotalSeconds),
                GalleryEvidenceOrigin.Simulated);
        var systemState = state switch
        {
            CenteredViewLiveState.Paused => CameraAgentPresentationSystemState.Paused,
            CenteredViewLiveState.Warming => CameraAgentPresentationSystemState.Starting,
            CenteredViewLiveState.Stale => CameraAgentPresentationSystemState.Standby,
            CenteredViewLiveState.Gap => CameraAgentPresentationSystemState.Unavailable,
            _ => CameraAgentPresentationSystemState.Capturing
        };
        var presentation = new CameraAgentCurrentImagePresentation(
            observedUtc,
            view is null ? CameraAgentPresentationImageFreshness.Empty : CameraAgentPresentationImageFreshness.Current,
            new(systemState, $"Centered state: {state}", observedUtc),
            headCapture,
            displayCapture,
            false,
            null,
            [],
            view is not null,
            false);
        var rendered = view is null ? null : ProjectCenteredPresentation(view);
        var liveContext = new CenteredViewLiveContext(CenteredStream, headCapture, observedUtc, state, null);
        var centered = new CameraAgentCenteredLiveView(
            liveContext,
            mode,
            view,
            view is null ? null : new(view.Base.BaseId, view.Revision.RevisionId, view.Revision.Order, observedUtc, null, null),
            rendered,
            rendered is null ? CameraAgentLayeredPresentationStatus.NotRetained : CameraAgentLayeredPresentationStatus.Found,
            null,
            RetainedFromPriorRun: false,
            view is null ? null : CameraAgentCenteredViewProjection.BoundPreviewUrl(view));
        return new CameraAgentCurrentSkyView(presentation, null, null)
        {
            ProcessingPending = false,
            MinimumRefreshInterval = minimumRefresh ?? TimeSpan.FromSeconds(15),
            Centered = centered
        };
    }

    private static CameraAgentCaptureDetailView CenteredDetailView(
        Guid captureId,
        IReadOnlyList<CameraAgentCenteredArchiveBaseSummary> bases,
        CenteredView? selectedView,
        CenteredViewSavedSelection? savedSelection,
        string? selectionError)
    {
        var capture = OperatorUiTestData.Capture(captureId) with { CaptureSequence = 103 };
        var presentation = new CameraAgentCapturePresentation(null, []);
        var rendered = selectedView is null ? null : ProjectCenteredPresentation(selectedView);
        var archive = new CameraAgentCenteredArchiveView(
            captureId,
            bases,
            selectedView,
            rendered,
            rendered is null ? CameraAgentLayeredPresentationStatus.NotRetained : CameraAgentLayeredPresentationStatus.Found,
            null,
            savedSelection,
            selectionError,
            selectedView is null ? null : CameraAgentCenteredViewProjection.BoundPreviewUrl(selectedView));
        return new CameraAgentCaptureDetailView(capture, presentation) { CenteredArchive = archive };
    }

    private sealed class TestCenteredViewPublisher : ICenteredViewPublisher
    {
        public event Action<CenteredViewStream>? Changed;

        public void RaiseChanged(CenteredViewStream stream) => Changed?.Invoke(stream);

        public ValueTask<CenteredView> RegisterBaseAsync(CenteredViewBaseDefinition definition, IReadOnlyList<CenteredViewLayer> initialLayers, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CenteredView?> PublishLayerAsync(string baseId, CenteredViewLayer layer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CenteredViewCurrent?> SelectLiveAsync(CenteredViewStream stream, string baseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CenteredViewSavedSelection?> SaveAsync(string owner, Guid selectionId, string baseId, string revisionId, IReadOnlyList<string> enabledLayerIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
