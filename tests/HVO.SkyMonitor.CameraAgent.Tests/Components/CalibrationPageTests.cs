using Bunit;
using Bunit.JSInterop;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CalibrationPageTests
{
    private static readonly string[] ExpectedReferenceTitles =
    [
        "Bias reference / 0.001s / gain 82",
        "Dark reference / 10.000s / gain 82",
        "Flat reference / 2.000s / gain 82",
        "Defect reference / 0.001s / gain 82"
    ];

    [TestMethod]
    public void Render_ShowsStatusAcquisitionAndLibrary()
    {
        using var context = CreateContext(new CalibrationUiService(Status()));

        var cut = context.Render<CalibrationPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Reference library", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "No active references", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Correction fails closed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "No references", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "calibration.library.missing", StringComparison.Ordinal);
            Assert.AreEqual("Acquire references", cut.Find("#start-calibration-acquisition").TextContent.Trim());
            Assert.IsFalse(cut.Find("#start-calibration-acquisition").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void AcquireRetry_AfterUnavailable_ReusesPinnedRequestKeyAndVersion()
    {
        var service = new RetryingCalibrationUiService(Status());
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("#start-calibration-acquisition").Click();

        cut.Find("#calibration-acquire-confirm").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Result unavailable.", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("dialog fieldset").HasAttribute("disabled"));
        Assert.AreEqual("Retry acquisition", cut.Find("#calibration-acquire-confirm").TextContent.Trim());
        cut.Find("#calibration-acquire-confirm").Click();

        Assert.HasCount(2, service.Commands);
        Assert.AreEqual(service.Commands[0].Key, service.Commands[1].Key);
        Assert.AreEqual(service.Commands[0].Version, service.Commands[1].Version);
        Assert.AreEqual(82d, service.Commands[0].Request.Gain);
        Assert.AreEqual(1d, service.Commands[0].Request.Offset);
        Assert.AreEqual(-10d, service.Commands[0].Request.TemperatureC);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), service.Commands[0].Request.BiasExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(10), service.Commands[0].Request.DarkExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.Commands[0].Request.FlatExposure);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), service.Commands[0].Request.DefectExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(5), service.Commands[0].Request.ApplicableLightExposure);
        Assert.AreEqual(676, service.Commands[0].Request.SourceModel.Seed);
    }

    [TestMethod]
    public void Render_WhenAuthorizationIsRevoked_ClearsStateAndNavigatesToAccessDenied()
    {
        using var context = CreateContext(new CalibrationUiService(null));

        _ = context.Render<CalibrationPage>();

        Assert.IsTrue(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RunningAcquisition_ExposesCancellationAndIsDetachedFromComponentToken()
    {
        var service = new DelayedCalibrationUiService(Status());
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("#start-calibration-acquisition").Click();
        cut.Find("#calibration-acquire-confirm").Click();

        cut.WaitForElement("#cancel-calibration-acquisition");
        Assert.IsFalse(service.AcquisitionToken.CanBeCanceled);
        Assert.IsTrue(cut.Find("[id^=review-calibration-activate-]").HasAttribute("disabled"));
        cut.Find("#cancel-calibration-acquisition").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(1, service.CancelCount));
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void LateCancellationConflict_RefreshesTerminalStatusAndPreservesMessage()
    {
        using var context = CreateContext(new LateCancellationCalibrationUiService(Status()));
        var cut = context.Render<CalibrationPage>();

        cut.WaitForElement("#cancel-calibration-acquisition").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Cancel acquisition", cut.Markup, StringComparison.Ordinal);
            StringAssert.Contains(
                cut.Markup, "completed before cancellation", StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    public void TerminalAcquisitionResults_AreNotProjectedAsSuccessfulCommands()
    {
        var failed = CameraAgentCalibrationUiService.ProjectAcquisitionResult(
            Job(CalibrationAcquisitionStates.Failed, "calibration.library.acquisition-failure"),
            cancellationCommand: false);
        var lateCancel = CameraAgentCalibrationUiService.ProjectAcquisitionResult(
            Job(CalibrationAcquisitionStates.Published),
            cancellationCommand: true);
        var cancelledAcquire = CameraAgentCalibrationUiService.ProjectAcquisitionResult(
            Job(CalibrationAcquisitionStates.Cancelled),
            cancellationCommand: false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, failed.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, lateCancel.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, cancelledAcquire.Kind);
    }

    [TestMethod]
    public void AcquisitionObserverFailure_ReenablesAcquisitionAndShowsFixedError()
    {
        using var context = CreateContext(new ThrowingCalibrationUiService(Status()));
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("#start-calibration-acquisition").Click();
        cut.Find("#calibration-acquire-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var start = cut.Find("#start-calibration-acquisition");
            Assert.IsFalse(start.HasAttribute("disabled"));
            StringAssert.Contains(cut.Markup, "could not be observed", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ActivationConflict_RefreshesStateAndPreservesOperatorMessage()
    {
        using var context = CreateContext(new ConflictingCalibrationUiService(Status()));
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("[id^=review-calibration-activate-]").Click();

        cut.Find("#calibration-activation-confirm").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "Calibration state changed before activation.", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ActivationCancel_WhileCommandIsPending_KeepsConfirmationOpenAsync()
    {
        var service = new DelayedActivationCalibrationUiService(Status());
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();
        await cut.WaitForElement("[id^=review-calibration-activate-]").ClickAsync().ConfigureAwait(false);
        var dialog = cut.Find("dialog");

        var command = cut.Find("#calibration-activation-confirm").TriggerEventAsync("onclick", EventArgs.Empty);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("dialog .button.primary").HasAttribute("disabled")));

        await dialog.TriggerEventAsync("oncancel", EventArgs.Empty).ConfigureAwait(false);

        Assert.HasCount(1, cut.FindAll("dialog"));
        service.Complete();
        await command.ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
    }

    [TestMethod]
    public async Task AcquisitionCompletion_DoesNotRestoreTriggerFocusTwiceAsync()
    {
        var service = new DelayedCalibrationUiService(Status());
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();
        await cut.WaitForElement("#start-calibration-acquisition").ClickAsync().ConfigureAwait(false);
        await cut.Find("#calibration-acquire-confirm").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual(
            1,
            context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "focusById")));
        Assert.IsTrue(cut.FindAll("button").Any(button => button.TextContent.Trim() == "Cancel acquisition"));

        service.CompleteSuccess();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "Calibration acquisition published durably.", StringComparison.Ordinal));
        Assert.AreEqual(
            1,
            context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "focusById"));
    }

    [TestMethod]
    public void SynchronousAcquisitionCompletion_RestoresTriggerFocusOnce()
    {
        using var context = CreateContext(new SynchronousCalibrationUiService(Status()));
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("#start-calibration-acquisition").Click();

        cut.Find("#calibration-acquire-confirm").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "Calibration acquisition published durably.", StringComparison.Ordinal));
        Assert.AreEqual(
            1,
            context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "focusById"));
    }

    [TestMethod]
    public void Render_WithActiveBundle_ShowsReferencesSelectionAndReviewDate()
    {
        var active = Summary("bundle-active", new DateTimeOffset(2026, 1, 25, 8, 0, 0, TimeSpan.Zero));
        var service = new LibraryCalibrationUiService(
            Status() with { ActiveBundle = active, PublishedBundleCount = 2, LastSelectionReason = "calibration.library.selected" },
            [active, Summary("bundle-older")]);
        using var context = CreateContext(service);

        var cut = context.Render<CalibrationPage>();

        cut.WaitForAssertion(() => Assert.HasCount(4, cut.FindAll("article.ops-library-card")));
        var cards = cut.FindAll("article.ops-library-card strong").Select(static strong => strong.TextContent.Trim()).ToArray();
        CollectionAssert.AreEqual(ExpectedReferenceTitles, cards);
        StringAssert.Contains(cut.Markup, "Software-generated (VirtualSky)", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "3 source frames", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "bias / dark / flat / defect", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Compatible", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "10 days", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Validity interval ends 2026-01-25", StringComparison.Ordinal);
        Assert.AreEqual("bundle-active", cut.Find("#calibration-active-bundle").TextContent.Trim());
        Assert.AreEqual(1, service.DetailRequests.Count(static id => id == "bundle-active"));
        Assert.IsEmpty(cut.FindAll("#review-calibration-activate-bundle-active"));
        Assert.HasCount(1, cut.FindAll("#review-calibration-activate-bundle-older"));
        StringAssert.Contains(cut.Find("tr.current-row").TextContent, "bundle-active", StringComparison.Ordinal);

        cut.Find("#calibration-refresh").Click();

        // Published bundles are immutable, so a refresh does not refetch the active bundle's evidence.
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.DetailRequests.Count(static id => id == "bundle-active")));
    }

    [TestMethod]
    [DataRow("calibration.library.incompatible-readout", "Readout mismatch")]
    [DataRow("calibration.library.incompatible-exposure", "Exposure mismatch")]
    [DataRow("calibration.library.stale", "Outside validity")]
    [DataRow(null, "Not checked")]
    [DataRow("calibration.library.future-code", "calibration.library.future-code")]
    public void Render_MapsTheLastSelectionReasonToAnOperatorLabel(string? reason, string label)
    {
        using var context = CreateContext(new CalibrationUiService(Status() with { LastSelectionReason = reason }));

        var cut = context.Render<CalibrationPage>();

        cut.WaitForAssertion(() => Assert.AreEqual(
            label, cut.Find("#calibration-selection-heading").Closest("header")!.QuerySelector(".state-chip")!.TextContent.Trim()));
    }

    [TestMethod]
    public void Render_WhenAcquisitionIsUnavailable_DisablesAcquireAndExplainsWhy()
    {
        using var context = CreateContext(new CalibrationUiService(
            Status() with { AcquisitionUnavailableReason = "The camera configuration has not loaded yet." }));

        var cut = context.Render<CalibrationPage>();

        cut.WaitForAssertion(() =>
        {
            var start = cut.Find("#start-calibration-acquisition");
            Assert.IsTrue(start.HasAttribute("disabled"));
            Assert.AreEqual("The camera configuration has not loaded yet.", start.GetAttribute("title"));
            StringAssert.Contains(cut.Markup, "Reference acquisition is unavailable.", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void AcquireValidationError_StaysInTheDialogAndClosesWithIt()
    {
        var service = new RetryingCalibrationUiService(Status());
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();
        cut.WaitForElement("#start-calibration-acquisition").Click();

        cut.Find("#cal-light").Change("-1");
        cut.Find("#calibration-acquire-confirm").Click();

        const string error = "Gain, offset, temperature, seed, and positive finite exposures are required.";
        StringAssert.Contains(cut.Find("dialog").TextContent, error, StringComparison.Ordinal);
        Assert.IsEmpty(service.Commands);

        cut.FindAll("dialog footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.DoesNotContain(error, cut.Markup, StringComparison.Ordinal);
        Assert.AreEqual(
            "start-calibration-acquisition",
            context.JSInterop.Invocations.Last(static invocation => invocation.Identifier == "focusById").Arguments[0]);
    }

    [TestMethod]
    public void Inspect_OpensTheEvidenceDialogWithOrderedLineage()
    {
        var service = new LibraryCalibrationUiService(Status() with { PublishedBundleCount = 1 }, [Summary("bundle-older")]);
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();

        cut.WaitForElement("#inspect-calibration-bundle-older").Click();

        var dialog = cut.Find("dialog.calibration-detail");
        Assert.AreEqual("bundle-older", dialog.QuerySelector("#calibration-detail-heading")!.TextContent.Trim());
        Assert.HasCount(16, dialog.QuerySelectorAll("table[aria-label='Bundle artifacts'] tbody tr"));
        Assert.HasCount(4, dialog.QuerySelectorAll("details.calibration-lineage"));
        StringAssert.Contains(dialog.TextContent, "3 ordered sources", StringComparison.Ordinal);
        Assert.IsTrue(cut.FindAll("dialog footer button").Any(static button => button.TextContent.Trim() == "Review activate"));

        cut.FindAll("dialog footer button").Single(static button => button.TextContent.Trim() == "Close").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.AreEqual(
            "inspect-calibration-bundle-older",
            context.JSInterop.Invocations.Last(static invocation => invocation.Identifier == "focusById").Arguments[0]);
    }

    [TestMethod]
    public void Rollback_SendsThePreviousBundleWithThePinnedReason()
    {
        var active = Summary("bundle-active");
        var service = new LibraryCalibrationUiService(
            Status() with
            {
                ActiveBundle = active,
                PublishedBundleCount = 2,
                LastActivation = new CalibrationLibraryActivationSnapshot(
                    "activate", "bundle-older", "bundle-active", 7, DateTimeOffset.UnixEpoch, null)
            },
            [active, Summary("bundle-older")]);
        using var context = CreateContext(service);
        var cut = context.Render<CalibrationPage>();

        cut.WaitForElement("#review-calibration-rollback").Click();
        Assert.AreEqual("Roll back calibration?", cut.Find("#calibration-activation-heading").TextContent.Trim());
        cut.Find("#cal-activation-reason").Change("  flat was taken with the dome lit  ");
        cut.Find("#calibration-activation-confirm").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "Calibration command completed durably.", StringComparison.Ordinal));
        Assert.AreEqual(("bundle-older", 7L, "flat was taken with the dome lit"), service.Rollback);
    }

    private static BunitContext CreateContext(ICameraAgentCalibrationUiService service)
    {
        var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./Components/Pages/CalibrationPage.razor.js");
        module.SetupVoid("showModal", _ => true).SetVoidResult();
        module.SetupVoid("focusById", _ => true).SetVoidResult();
        context.Services.AddSingleton(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero)));
        return context;
    }

    private static CalibrationUiStatus Status()
        => new(
            7,
            null,
            null,
            0,
            0,
            "calibration.library.missing",
            DateTimeOffset.UnixEpoch,
            "calibration.library.reconciled",
            DateTimeOffset.UnixEpoch,
            null);

    private static CalibrationAcquisitionJobSnapshot Job(string state, string? failureReason = null)
    {
        var layout = new FrameLayoutDescriptor(
            2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, ushort.MaxValue, 8);
        var plan = new VirtualCalibrationAcquisitionPlanV1(
            VirtualCalibrationAcquisitionPlanV1.CurrentSchemaVersion,
            "virtual-test",
            "VirtualSky",
            "agent",
            "rig",
            new ProfileIdentityDescriptor("rig", "v1", new string('A', 64)),
            new ProfileIdentityDescriptor("sensor", "v1", new string('B', 64)),
            layout,
            layout,
            82,
            1,
            -10,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5),
            DateTimeOffset.UnixEpoch,
            null,
            DateTimeOffset.UnixEpoch,
            new VirtualCalibrationSourceModelV1(),
            new string('C', 64));
        return new CalibrationAcquisitionJobSnapshot(
            plan,
            new string('D', 64),
            state,
            state,
            1,
            state == CalibrationAcquisitionStates.Published ? "bundle-test" : null,
            failureReason,
            "owner",
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static CalibrationUiBundlePage BundlePage()
    {
        var layout = new FrameLayoutDescriptor(
            2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
            0, ushort.MaxValue, 8);
        var applicability = new CalibrationApplicabilityV1(
            "agent", "rig", new string('A', 64), new string('B', 64), layout, layout,
            82, 82, 1, 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), -10, -10,
            DateTimeOffset.UnixEpoch, null);
        return new CalibrationUiBundlePage(
            [new CalibrationUiBundleSummary(
                "bundle-conflict", new string('C', 64), "virtual-acquisition-v1", "published",
                null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new string('D', 64),
                new string('E', 64), applicability, 12, 4)],
            null);
    }

    private static CalibrationUiBundleSummary Summary(string bundleId, DateTimeOffset? effectiveUntil = null)
        => BundlePage().Items[0] with
        {
            BundleId = bundleId,
            Applicability = BundlePage().Items[0].Applicability with { EffectiveUntilUtc = effectiveUntil }
        };

    private static CalibrationUiBundleDetail Detail(CalibrationUiBundleSummary summary)
    {
        var exposures = new Dictionary<string, TimeSpan>
        {
            [CalibrationReferenceKinds.Bias] = TimeSpan.FromMilliseconds(1),
            [CalibrationReferenceKinds.Dark] = TimeSpan.FromSeconds(10),
            [CalibrationReferenceKinds.Flat] = TimeSpan.FromSeconds(2),
            [CalibrationReferenceKinds.Defect] = TimeSpan.FromMilliseconds(1)
        };
        var artifacts = new List<CalibrationUiArtifact>();
        // Stored in reverse so the page's own kind/role/index ordering is what the assertions observe.
        foreach (var kind in CalibrationReferenceKinds.All.Reverse())
        {
            var sources = Enumerable.Range(0, 3).Select(static _ => Guid.NewGuid()).ToArray();
            artifacts.Add(new CalibrationUiArtifact(
                kind, CalibrationLibraryArtifactRoles.Master, Guid.NewGuid(), new string('F', 64),
                exposures[kind], 82, 1, -10, null, sources, null));
            artifacts.AddRange(sources.Select((id, index) => new CalibrationUiArtifact(
                kind, CalibrationLibraryArtifactRoles.Source, id, new string('A', 64),
                exposures[kind], 82, 1, -10, index, [], null)));
        }
        return new CalibrationUiBundleDetail(summary, artifacts);
    }

    private class CalibrationUiService(CalibrationUiStatus? status) : ICameraAgentCalibrationUiService
    {
        protected CalibrationUiStatus? CurrentStatus { get; set; } = status;

        public ValueTask<OperatorUiResult<CalibrationUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(CurrentStatus is null
                ? OperatorUiResult<CalibrationUiStatus>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CalibrationUiStatus>.Success(CurrentStatus));

        public virtual ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
            int pageSize, string? cursor, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiBundlePage>.Success(
                new CalibrationUiBundlePage([], null)));

        public virtual ValueTask<OperatorUiResult<CalibrationUiBundleDetail>> GetBundleAsync(
            string bundleId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public virtual ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
            CalibrationUiAcquisitionRequest request,
            long expectedVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public virtual ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(
            string jobId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public virtual ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(
            string bundleId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public virtual ValueTask<OperatorUiResult<CalibrationUiStatus>> RollbackAsync(
            string bundleId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class DelayedCalibrationUiService(CalibrationUiStatus status) : CalibrationUiService(status)
    {
        private readonly TaskCompletionSource<OperatorUiResult<CalibrationUiAcquisition>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken AcquisitionToken { get; private set; }

        internal int CancelCount { get; private set; }

        public override ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
            int pageSize,
            string? cursor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiBundlePage>.Success(BundlePage()));

        internal void CompleteSuccess()
        {
            CurrentStatus = CurrentStatus! with { PendingAcquisition = null };
            _completion.TrySetResult(OperatorUiResult<CalibrationUiAcquisition>.Success(null!));
        }

        public override async ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
            CalibrationUiAcquisitionRequest request,
            long expectedVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            AcquisitionToken = cancellationToken;
            CurrentStatus = CurrentStatus! with
            {
                PendingAcquisition = new CalibrationUiAcquisition(
                    "virtual-test",
                    CalibrationAcquisitionStates.Acquiring,
                    "source-bias-0",
                    1,
                    null,
                    null,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    null,
                    Job(CalibrationAcquisitionStates.Cancelled).Plan)
            };
            return await _completion.Task.ConfigureAwait(false);
        }

        public override ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(
            string jobId,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            CancelCount++;
            CurrentStatus = CurrentStatus! with { PendingAcquisition = null };
            _completion.TrySetResult(OperatorUiResult<CalibrationUiAcquisition>.Failure(
                OperatorUiResultKind.Conflict, "Calibration acquisition was cancelled before publication."));
            return ValueTask.FromResult(OperatorUiResult<CalibrationUiAcquisition>.Success(null!));
        }
    }

    private sealed class ThrowingCalibrationUiService(CalibrationUiStatus status) : CalibrationUiService(status)
    {
        public override ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
            CalibrationUiAcquisitionRequest request,
            long expectedVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("private failure");
    }

    private sealed class LateCancellationCalibrationUiService : CalibrationUiService
    {
        internal LateCancellationCalibrationUiService(CalibrationUiStatus status)
            : base(status with
            {
                PendingAcquisition = new CalibrationUiAcquisition(
                    "virtual-test",
                    CalibrationAcquisitionStates.Acquiring,
                    "source-flat-0",
                    1,
                    null,
                    null,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    null,
                    Job(CalibrationAcquisitionStates.Cancelled).Plan)
            })
        {
        }

        public override ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(
            string jobId,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            CurrentStatus = CurrentStatus! with { PendingAcquisition = null };
            return ValueTask.FromResult(OperatorUiResult<CalibrationUiAcquisition>.Failure(
                OperatorUiResultKind.Conflict,
                "Calibration acquisition completed before cancellation."));
        }
    }

    private class ConflictingCalibrationUiService(CalibrationUiStatus status) : CalibrationUiService(status)
    {
        public override ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
            int pageSize,
            string? cursor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiBundlePage>.Success(BundlePage()));

        public override ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(
            string bundleId,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiStatus>.Failure(
                OperatorUiResultKind.Conflict, "Calibration state changed before activation."));
    }

    private sealed class DelayedActivationCalibrationUiService(CalibrationUiStatus status)
        : ConflictingCalibrationUiService(status)
    {
        private readonly TaskCompletionSource<OperatorUiResult<CalibrationUiStatus>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete() => _completion.TrySetResult(
            OperatorUiResult<CalibrationUiStatus>.Failure(
                OperatorUiResultKind.Invalid,
                "Synthetic activation result."));

        public override async ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(
            string bundleId,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
            => await _completion.Task.ConfigureAwait(false);
    }

    private sealed class SynchronousCalibrationUiService(CalibrationUiStatus status) : CalibrationUiService(status)
    {
        public override ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
            CalibrationUiAcquisitionRequest request,
            long expectedVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiAcquisition>.Success(null!));
    }

    private sealed class RetryingCalibrationUiService(CalibrationUiStatus status) : CalibrationUiService(status)
    {
        internal List<(CalibrationUiAcquisitionRequest Request, long Version, string Key)> Commands { get; } = [];

        public override ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
            CalibrationUiAcquisitionRequest request,
            long expectedVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Commands.Add((request, expectedVersion, idempotencyKey));
            return ValueTask.FromResult(Commands.Count == 1
                ? OperatorUiResult<CalibrationUiAcquisition>.Failure(
                    OperatorUiResultKind.Unavailable, "Result unavailable.")
                : OperatorUiResult<CalibrationUiAcquisition>.Success(null!));
        }
    }

    private sealed class LibraryCalibrationUiService(
        CalibrationUiStatus status,
        IReadOnlyList<CalibrationUiBundleSummary> bundles) : CalibrationUiService(status)
    {
        internal List<string> DetailRequests { get; } = [];

        internal (string BundleId, long Version, string? Reason)? Rollback { get; private set; }

        public override ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
            int pageSize, string? cursor, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiBundlePage>.Success(
                new CalibrationUiBundlePage(bundles, null)));

        public override ValueTask<OperatorUiResult<CalibrationUiBundleDetail>> GetBundleAsync(
            string bundleId, CancellationToken cancellationToken)
        {
            DetailRequests.Add(bundleId);
            return ValueTask.FromResult(OperatorUiResult<CalibrationUiBundleDetail>.Success(
                Detail(bundles.Single(bundle => bundle.BundleId == bundleId))));
        }

        public override ValueTask<OperatorUiResult<CalibrationUiStatus>> RollbackAsync(
            string bundleId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            Rollback = (bundleId, expectedVersion, reason);
            return ValueTask.FromResult(OperatorUiResult<CalibrationUiStatus>.Success(CurrentStatus!));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
