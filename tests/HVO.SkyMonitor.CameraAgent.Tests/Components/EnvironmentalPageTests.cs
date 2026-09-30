using Bunit;
using AngleSharp.Dom;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalPageTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SuccessRendersFreshSourceHistoryAttemptsAndLoadsOlderPage()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
                [Source("virtual-rain", EnvironmentalObservationKind.RainState, onDemand: true, freshness: "Fresh",
                    disposition: EnvironmentalAcquisitionDisposition.Produced, ageSeconds: 2)],
                attempts: [new EnvironmentalAcquisitionAttemptRecord(
                    1, "virtual-rain", EnvironmentalObservationKind.RainState, true,
                    EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionDisposition.Produced,
                    "produced", Guid.NewGuid(), null, null, Epoch, Epoch.AddMilliseconds(10))],
                storedCount: 2,
                storedBytes: 512)),
            History = cursor => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
                [Observation(
                    EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean, null, true,
                    sourceId: cursor is null ? "virtual-rain" : "older-rain")],
                cursor is null ? "older-cursor" : null))
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "virtual-rain", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Fresh", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".environment-history-table tbody").TextContent, "Rain", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".environment-attempt-table tbody").TextContent, "produced", StringComparison.Ordinal);
        });
        cut.FindAll("button").Single(button => button.TextContent.Contains("Older", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("older-cursor", service.LastCursor);
            StringAssert.Contains(cut.Markup, "older-rain", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void OnDemandRequestRendersDurableReceiptAndRefreshesHistory()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var observationId = Guid.NewGuid();
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
                [Source("virtual-rain", EnvironmentalObservationKind.RainState, onDemand: true)])),
            Acquire = (_, _, _, _) => ValueTask.FromResult(OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                new EnvironmentalAcquisitionReceipt(
                    "virtual-rain",
                    EnvironmentalAcquisitionTrigger.OnDemand,
                    EnvironmentalAcquisitionDisposition.Produced,
                    "produced",
                    observationId,
                    Epoch,
                    Epoch.AddSeconds(1),
                    Epoch,
                    Epoch.AddMinutes(5)),
                Replayed: false)))
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("operator verification");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, service.AcquireCalls);
            Assert.AreEqual("virtual-rain", service.LastSourceId);
            Assert.AreEqual("operator verification", service.LastReason);
            StringAssert.StartsWith(service.LastIdempotencyKey, "ui-", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, observationId.ToString(), StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "observation was committed", StringComparison.OrdinalIgnoreCase);
            Assert.IsGreaterThanOrEqualTo(2, service.StatusCalls);
        });
    }

    [TestMethod]
    public void UnavailableRetryReusesIdempotencyKeyAndRendersReplayedReceipt()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var calls = 0;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(++calls == 1
            ? OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The environmental source is already acquiring an observation.")
            : OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: true)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("retry verification");

        cut.Find("form").Submit();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "already acquiring", StringComparison.Ordinal));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, service.AcquireCalls);
            Assert.AreEqual(service.IdempotencyKeys[0], service.IdempotencyKeys[1]);
            StringAssert.Contains(cut.Markup, "idempotent receipt", StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    public void FailedDurableReceiptIsAnnouncedWithoutFabricatedObservation()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Failed, null), Replayed: false)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("failure verification");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("alert", cut.Find(".command-result").GetAttribute("role"));
            StringAssert.Contains(cut.Markup, "No observation committed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "without a new observation", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void SubmissionIsSingleFlightWhileCommandIsPending()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => new ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            completion.Task);
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("single flight");

        cut.Find("form").Submit();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, service.AcquireCalls);
            Assert.IsTrue(cut.Find("button[type=submit]").HasAttribute("disabled"));
            Assert.IsFalse(service.LastCancellationToken.CanBeCanceled);
        });
        completion.SetResult(OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
            Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: false)));
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "observation was committed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ConflictAndCapacityOutcomesRemainExplicitAndDoNotFabricateReceipts()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var calls = 0;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(++calls == 1
            ? OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Conflict, "The idempotency key is already bound to another request.")
            : OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental command capacity is unavailable."));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("outcome verification");

        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "already bound", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("Disposition reason", StringComparison.Ordinal));
        });
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "capacity is unavailable", StringComparison.Ordinal);
            Assert.AreNotEqual(service.IdempotencyKeys[0], service.IdempotencyKeys[1]);
            Assert.IsFalse(cut.Markup.Contains("No observation committed", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task DisposalWhileCommandIsPendingAllowsSettlementWithoutRenderingOrDuplicateExecution()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => new ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            completion.Task);
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        await OpenDialog(cut).Find("#environment-reason").ChangeAsync(
            new ChangeEventArgs { Value = "dispose verification" }).ConfigureAwait(false);
        var submit = cut.Find("form").SubmitAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.AcquireCalls));

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        completion.SetResult(OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
            Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: false)));
        await submit.ConfigureAwait(false);

        Assert.AreEqual(1, service.AcquireCalls);
        Assert.IsFalse(service.LastCancellationToken.CanBeCanceled);
    }

    [TestMethod]
    public async Task DisposalWhileStatusRefreshIsPendingDoesNotReadHistoryOrDisposedLifetime()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var refreshCompletion = new TaskCompletionSource<OperatorUiResult<EnvironmentalUiStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.StatusRead = _ => service.StatusCalls == 1
            ? ValueTask.FromResult(service.Status)
            : new ValueTask<OperatorUiResult<EnvironmentalUiStatus>>(refreshCompletion.Task);
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: false)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        await OpenDialog(cut).Find("#environment-reason").ChangeAsync(
            new ChangeEventArgs { Value = "refresh disposal" }).ConfigureAwait(false);
        var submit = cut.Find("form").SubmitAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StatusCalls));

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        refreshCompletion.SetResult(service.Status);
        await submit.ConfigureAwait(false);

        Assert.AreEqual(1, service.HistoryCalls);
        Assert.AreEqual(1, service.AcquireCalls);
    }

    [TestMethod]
    public async Task DisposalWhileInitialStatusIsPendingDoesNotContinueIntoHistory()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalUiStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.StatusRead = async _ =>
        {
            var result = await completion.Task.ConfigureAwait(false);
            returned.TrySetResult();
            return result;
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StatusCalls));

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        completion.SetResult(service.Status);
        await returned.Task.ConfigureAwait(false);
        await Task.Yield();

        Assert.AreEqual(0, service.LatestCalls);
        Assert.AreEqual(0, service.HistoryCalls);
    }

    [TestMethod]
    public async Task DisposalWhileInitialHistoryIsPendingDoesNotApplyHistoryResult()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalUiHistoryPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.HistoryRead = async (_, _) =>
        {
            var result = await completion.Task.ConfigureAwait(false);
            returned.TrySetResult();
            return result;
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.HistoryCalls));

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        completion.SetResult(OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
            [Observation(
                EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean, null, true,
                sourceId: "late-source")], null)));
        await returned.Task.ConfigureAwait(false);
        await Task.Yield();

        Assert.IsFalse(cut.Markup.Contains("late-source", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidReasonDoesNotInvokeMutationService()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("   ");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, service.AcquireCalls);
            StringAssert.Contains(cut.Markup, "1 to 128 characters", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void UnauthorizedMutationClearsCommandStateAndNavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized for this operation."));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("authorization verification");

        cut.Find("form").Submit();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => StringAssert.EndsWith(
            navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RefreshFailureDoesNotHideDurableReceipt()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var historyCalls = 0;
        var service = CreateOnDemandService();
        service.History = _ => ++historyCalls == 1
            ? OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new([], null))
            : OperatorUiResult<EnvironmentalUiHistoryPage>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental history is unavailable.");
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: false)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("refresh verification");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "observation was committed", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Markup, "receipt is durable", StringComparison.OrdinalIgnoreCase);
        });

        // Reopening the dialog starts a new request: the durable receipt stays on the page until then, and
        // a validation error for the new request is announced inside the dialog without the old warning.
        OpenDialog(cut).Find("#environment-reason").Change("   ");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            var result = cut.Find("dialog .command-result");
            StringAssert.Contains(result.TextContent, "1 to 128 characters", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("receipt is durable", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("alert", result.GetAttribute("role"));
        });
    }

    private static TestEnvironmentalUiService CreateOnDemandService() => new()
    {
        Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
            [Source("virtual-rain", EnvironmentalObservationKind.RainState, onDemand: true)]))
    };

    private static EnvironmentalAcquisitionReceipt Receipt(
        EnvironmentalAcquisitionDisposition disposition,
        Guid? observationId) => new(
            "virtual-rain",
            EnvironmentalAcquisitionTrigger.OnDemand,
            disposition,
            disposition.ToString(),
            observationId,
            Epoch,
            Epoch.AddSeconds(1));

    [TestMethod]
    public void UnauthorizedStatusNavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.")
        });

        var cut = context.Render<EnvironmentalPage>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => StringAssert.EndsWith(
            navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailureRendersOnlySanitizedAlert()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.")
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("alert", cut.Find("[role=alert]").GetAttribute("role"));
            StringAssert.Contains(cut.Markup, "Environmental status is unavailable.", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void ReadingsShowTheNewestRecordedValueForEachSlotAndNameWhatIsMissing()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
            [
                Source("air", EnvironmentalObservationKind.AirTemperature),
                Source("pressure", EnvironmentalObservationKind.AtmosphericPressure),
                Source("cloud", EnvironmentalObservationKind.CloudCover),
                Source("rain", EnvironmentalObservationKind.RainState),
                Source("wind", EnvironmentalObservationKind.WindSpeed),
                Source("humidity", EnvironmentalObservationKind.RelativeHumidity),
            ])),
            Latest = () => OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Success(
            [
                Observation(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, 12.34, null,
                    sourceKind: EnvironmentalObservationSourceKind.Simulated, observedAgo: TimeSpan.FromSeconds(42)),
                Observation(EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationUnit.Pascals, 101325, null,
                    observedAgo: TimeSpan.FromMinutes(20), staleAfter: TimeSpan.FromMinutes(10)),
                Observation(EnvironmentalObservationKind.CloudCover, EnvironmentalObservationUnit.Fraction, 0.35, null,
                    quality: EnvironmentalObservationQuality.Suspect),
                Observation(EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean, null, false),
                Observation(EnvironmentalObservationKind.WindSpeed, EnvironmentalObservationUnit.MetersPerSecond, 3.26, null),
                Observation(EnvironmentalObservationKind.WindGust, EnvironmentalObservationUnit.MetersPerSecond, 6.8, null),
                Observation(EnvironmentalObservationKind.WindDirection, EnvironmentalObservationUnit.DegreesTrue, 271.6, null),
            ])
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            AssertReading(cut, "air-temperature", "12.3 C", "Fresh / 42s ago / Simulated");
            AssertReading(cut, "pressure", "1013 hPa", "Stale / 20m ago");
            AssertReading(cut, "cloud", "35%", "Suspect quality");
            AssertReading(cut, "precipitation", "None", "Fresh");
            AssertReading(cut, "wind", "3.3 m/s", "Gust 6.8 m/s / From 272 deg");
            AssertReading(cut, "relative-humidity", "--", "No observation yet");
            AssertReading(cut, "sky-quality", "--", "No source configured");
            Assert.AreEqual(1, service.LatestCalls);
            Assert.HasCount(2, cut.FindAll(".ops-reading.empty").Where(item =>
                item.GetAttribute("data-slot") is "relative-humidity" or "sky-quality"));
        });
    }

    [TestMethod]
    public void SourceScheduleNamesTriggersFailuresAndTheNextPoll()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
            [
                Source("periodic-air", EnvironmentalObservationKind.AirTemperature, freshness: "Fresh",
                    triggers: [EnvironmentalAcquisitionTrigger.Periodic], periodSeconds: 300, nextPoll: Epoch.AddSeconds(42)),
                Source("capture-cloud", EnvironmentalObservationKind.CloudCover, required: true, freshness: "Stale",
                    triggers: [EnvironmentalAcquisitionTrigger.EveryNthCapture], everyNth: 3, nextPoll: Epoch.AddMinutes(5),
                    disposition: EnvironmentalAcquisitionDisposition.TimedOut, failures: 2),
                Source("manual-rain", EnvironmentalObservationKind.RainState, onDemand: true,
                    triggers: [EnvironmentalAcquisitionTrigger.OnDemand]),
            ]))
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("#environment-sources-heading").ParentElement!.ParentElement!.TextContent,
                "Next poll 42s", StringComparison.Ordinal);
            var air = cut.Find("tr[data-source='periodic-air']").TextContent;
            StringAssert.Contains(air, "Every 300 seconds", StringComparison.Ordinal);
            StringAssert.Contains(air, "Air Temperature / Optional", StringComparison.Ordinal);
            var cloud = cut.Find("tr[data-source='capture-cloud']");
            StringAssert.Contains(cloud.TextContent, "Every 3 captures", StringComparison.Ordinal);
            StringAssert.Contains(cloud.TextContent, "Required", StringComparison.Ordinal);
            Assert.AreEqual("Timed Out / 2 in a row", cloud.QuerySelector("small.failure")!.TextContent);
            Assert.AreEqual("state-chip warning", cloud.QuerySelector(".state-chip")!.ClassName);
            StringAssert.Contains(cut.Find("tr[data-source='manual-rain']").TextContent, "On demand", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DeliveryPolicyShowsTheOutboxOnlyWhenExportIsEnabled(bool exportEnabled)
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var delivery = exportEnabled
            ? new EnvironmentalUiDelivery(true, "Degraded", Epoch.AddMinutes(-2), 3, 2, 1, Epoch.AddMinutes(-7))
            : new EnvironmentalUiDelivery(false, "Disabled", null, 0, 0, 0, null);
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
                [Source("air", EnvironmentalObservationKind.AirTemperature)],
                storedCount: 12, storedBytes: 4096, delivery: delivery))
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            var facts = cut.Find(".environment-delivery").TextContent;
            var badge = cut.Find("#environment-delivery-heading").ParentElement!.NextElementSibling!.TextContent;
            StringAssert.Contains(facts, "14 days", StringComparison.Ordinal);
            StringAssert.Contains(facts, "12", StringComparison.Ordinal);
            if (exportEnabled)
            {
                Assert.AreEqual("Degraded", badge);
                StringAssert.Contains(facts, "3 (2 retrying), 1 quarantined", StringComparison.Ordinal);
                Assert.IsFalse(facts.Contains("Not exported", StringComparison.Ordinal));
            }
            else
            {
                Assert.AreEqual("Local only", badge);
                StringAssert.Contains(facts, "Not exported", StringComparison.Ordinal);
                StringAssert.Contains(facts, "Export disabled", StringComparison.Ordinal);
            }
        });
    }

    [TestMethod]
    public void AcquireIsDisabledWithAnAnnouncedReasonWhenNoSourceSupportsOnDemand()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status(
                [Source("periodic-air", EnvironmentalObservationKind.AirTemperature)]))
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            var acquire = cut.Find("#environment-acquire");
            Assert.IsTrue(acquire.HasAttribute("disabled"));
            StringAssert.Contains(cut.Find("#" + acquire.GetAttribute("aria-describedby")).TextContent,
                "No configured source supports on-demand acquisition", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public void DisabledAcquisitionIsNamedAndHistoryStaysReadable()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(Status([], enabled: false)),
            History = _ => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
                [Observation(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, 4.5, null)],
                null))
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".environment-disabled").TextContent, "Environmental acquisition is disabled.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#environment-acquire-reason").TextContent, "disabled in this agent's configuration", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".environment-history-table tbody").TextContent, "4.5 C", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "No environmental sources are configured.", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void KindFilterReadsHistoryForTheChosenKindFromTheNewestPage()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        service.History = cursor => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new([], cursor is null ? "next" : null));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.HistoryCalls));
        cut.FindAll("button").Single(button => button.TextContent.Contains("Older", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("next", service.LastCursor));

        cut.Find("#environment-history-kind").Change(nameof(EnvironmentalObservationKind.CloudCover));

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(3, service.HistoryCalls);
            Assert.AreEqual(EnvironmentalObservationKind.CloudCover, service.LastKind);
            Assert.IsNull(service.LastCursor);
        });
        cut.Find("#environment-history-kind").Change(string.Empty);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(4, service.HistoryCalls);
            Assert.IsNull(service.LastKind);
        });
    }

    [TestMethod]
    public void CancellingAnUncertainRetryReleasesItsKeyAndLocksOnlyWhileUncertain()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
            OperatorUiResultKind.Unavailable, "The environmental request outcome is unknown."));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        OpenDialog(cut).Find("#environment-reason").Change("pinned verification");

        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find("dialog fieldset").HasAttribute("disabled"));
            Assert.AreEqual("Retry request", cut.Find("#environment-acquire-confirm").TextContent);
            StringAssert.Contains(cut.Find("dialog .dialog-note").TextContent, "Retry the same request.", StringComparison.Ordinal);
        });

        cut.FindAll("dialog footer button").Single(button => button.TextContent == "Cancel").Click();
        Assert.IsEmpty(cut.FindAll("dialog"));
        OpenDialog(cut).Find("#environment-reason").Change("pinned verification");
        Assert.IsFalse(cut.Find("dialog fieldset").HasAttribute("disabled"));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, service.AcquireCalls);
            Assert.AreNotEqual(service.IdempotencyKeys[0], service.IdempotencyKeys[1]);
        });
    }

    [TestMethod]
    public void LatestReadingFailureLeavesTheRestOfThePageReadable()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = CreateOnDemandService();
        service.Latest = () => OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Failure(
            OperatorUiResultKind.Unavailable, "Current environmental readings are unavailable.");
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Current environmental readings are unavailable.", cut.Find(".environment-inline-warning").TextContent);
            Assert.HasCount(1, cut.FindAll("tr[data-source='virtual-rain']"));
            Assert.AreEqual(1, service.HistoryCalls);
            Assert.IsFalse(cut.Find("#environment-acquire").HasAttribute("disabled"));
        });
    }

    private static IRenderedComponent<EnvironmentalPage> OpenDialog(IRenderedComponent<EnvironmentalPage> cut)
    {
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#environment-acquire").HasAttribute("disabled")));
        cut.Find("#environment-acquire").Click();
        cut.WaitForElement("#environment-reason");
        return cut;
    }

    private static void AssertReading(IRenderedComponent<EnvironmentalPage> cut, string slot, string value, string state)
    {
        var reading = cut.Find($".ops-reading[data-slot='{slot}']");
        Assert.AreEqual(value, reading.QuerySelector("strong")!.TextContent, slot);
        StringAssert.Contains(string.Join(" / ", reading.QuerySelectorAll("small").Select(item => item.TextContent)),
            state, StringComparison.Ordinal, slot);
    }

    private static EnvironmentalUiStatus Status(
        IReadOnlyList<EnvironmentalUiSource> sources,
        IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>? attempts = null,
        bool enabled = true,
        long storedCount = 0,
        long storedBytes = 0,
        EnvironmentalUiDelivery? delivery = null) => new(
            enabled,
            Epoch,
            storedCount,
            storedBytes,
            0,
            sources,
            attempts ?? [],
            14,
            delivery ?? new EnvironmentalUiDelivery(false, "Disabled", null, 0, 0, 0, null));

    private static EnvironmentalUiSource Source(
        string id,
        EnvironmentalObservationKind kind,
        bool required = false,
        bool onDemand = false,
        string freshness = "Never observed",
        EnvironmentalAcquisitionDisposition? disposition = null,
        double? ageSeconds = null,
        DateTimeOffset? nextPoll = null,
        int failures = 0,
        EnvironmentalAcquisitionTrigger[]? triggers = null,
        int periodSeconds = 60,
        int everyNth = 1) => new(
            id,
            kind,
            required,
            onDemand,
            freshness,
            disposition,
            disposition?.ToString(),
            ageSeconds is null ? null : Epoch.AddSeconds(-ageSeconds.Value),
            ageSeconds,
            nextPoll,
            failures,
            triggers ?? [EnvironmentalAcquisitionTrigger.Periodic],
            periodSeconds,
            everyNth);

    private static EnvironmentalUiObservation Observation(
        EnvironmentalObservationKind kind,
        EnvironmentalObservationUnit unit,
        double? numeric,
        bool? boolean,
        string sourceId = "virtual-source",
        EnvironmentalObservationSourceKind sourceKind = EnvironmentalObservationSourceKind.Measured,
        EnvironmentalObservationQuality quality = EnvironmentalObservationQuality.Good,
        TimeSpan? observedAgo = null,
        TimeSpan? staleAfter = null)
    {
        var observed = Epoch - (observedAgo ?? TimeSpan.FromSeconds(5));
        return new(Guid.NewGuid(), sourceId, sourceKind, kind, unit, numeric, boolean, quality, null,
            observed, observed + (staleAfter ?? TimeSpan.FromMinutes(5)));
    }

    private sealed class TestEnvironmentalUiService : ICameraAgentEnvironmentalUiService
    {
        public OperatorUiResult<EnvironmentalUiStatus> Status { get; init; } =
            OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.");
        public Func<OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>> Latest { get; set; } =
            () => OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Success([]);
        public Func<string?, OperatorUiResult<EnvironmentalUiHistoryPage>> History { get; set; } =
            _ => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new([], null));
        public Func<CancellationToken, ValueTask<OperatorUiResult<EnvironmentalUiStatus>>>? StatusRead { get; set; }
        public Func<string?, CancellationToken,
            ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>>>? HistoryRead
        { get; set; }
        public Func<string, string, string, CancellationToken,
            ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>> Acquire
        { get; set; } =
            (_, _, _, _) => ValueTask.FromResult(
                OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The environmental request could not be completed."));
        public string? LastCursor { get; private set; }
        public string? LastSourceId { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public string? LastReason { get; private set; }
        public int AcquireCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int HistoryCalls { get; private set; }
        public int LatestCalls { get; private set; }
        public EnvironmentalObservationKind? LastKind { get; private set; }
        public List<string> IdempotencyKeys { get; } = [];
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return StatusRead?.Invoke(cancellationToken) ?? ValueTask.FromResult(Status);
        }

        public ValueTask<OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>> GetLatestReadingsAsync(
            CancellationToken cancellationToken)
        {
            LatestCalls++;
            return ValueTask.FromResult(Latest());
        }

        public ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>> AcquireAsync(
            string sourceId,
            string idempotencyKey,
            string reason,
            CancellationToken cancellationToken)
        {
            AcquireCalls++;
            LastSourceId = sourceId;
            LastIdempotencyKey = idempotencyKey;
            LastReason = reason;
            LastCancellationToken = cancellationToken;
            IdempotencyKeys.Add(idempotencyKey);
            return Acquire(sourceId, idempotencyKey, reason, cancellationToken);
        }

        public ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
            EnvironmentalObservationKind? kind,
            int pageSize,
            string? cursor,
            CancellationToken cancellationToken)
        {
            HistoryCalls++;
            LastCursor = cursor;
            LastKind = kind;
            return HistoryRead?.Invoke(cursor, cancellationToken) ?? ValueTask.FromResult(History(cursor));
        }
    }
}
