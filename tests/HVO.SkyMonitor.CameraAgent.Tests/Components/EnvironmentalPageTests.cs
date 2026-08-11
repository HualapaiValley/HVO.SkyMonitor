using Bunit;
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
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(new(
                true,
                Epoch,
                2,
                512,
                0,
                [new EnvironmentalUiSource(
                    "virtual-rain", EnvironmentalObservationKind.RainState, true, true, "Fresh",
                    EnvironmentalAcquisitionDisposition.Produced, "produced", Epoch, 2, Epoch.AddSeconds(30), 0)],
                [new EnvironmentalAcquisitionAttemptRecord(
                    1, "virtual-rain", EnvironmentalObservationKind.RainState, true,
                    EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionDisposition.Produced,
                    "produced", Guid.NewGuid(), null, null, Epoch, Epoch.AddMilliseconds(10))])),
            History = cursor => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
                [new EnvironmentalUiObservation(
                    Guid.NewGuid(), cursor is null ? "virtual-rain" : "older-rain",
                    EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean,
                    null, true, EnvironmentalObservationQuality.Good, null, Epoch, Epoch.AddSeconds(45))],
                cursor is null ? "older-cursor" : null))
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "virtual-rain", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Fresh", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "True", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "produced", StringComparison.Ordinal);
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
        var observationId = Guid.NewGuid();
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(new(
                true, Epoch, 0, 0, 0,
                [new EnvironmentalUiSource(
                    "virtual-rain", EnvironmentalObservationKind.RainState, false, true, "Never observed",
                    null, null, null, null, null, 0)],
                [])),
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
        cut.WaitForElement("#environment-reason").Change("operator verification");

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
        var calls = 0;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(++calls == 1
            ? OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The environmental source is already acquiring an observation.")
            : OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Produced, Guid.NewGuid()), Replayed: true)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("retry verification");

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
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(new(
                Receipt(EnvironmentalAcquisitionDisposition.Failed, null), Replayed: false)));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("failure verification");

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
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => new ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            completion.Task);
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("single flight");

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
        var calls = 0;
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(++calls == 1
            ? OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Conflict, "The idempotency key is already bound to another request.")
            : OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental command capacity is unavailable."));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("outcome verification");

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
        var completion = new TaskCompletionSource<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => new ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>>(
            completion.Task);
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        await cut.WaitForElement("#environment-reason").ChangeAsync(
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
        await cut.WaitForElement("#environment-reason").ChangeAsync(
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

        Assert.AreEqual(0, service.HistoryCalls);
    }

    [TestMethod]
    public async Task DisposalWhileInitialHistoryIsPendingDoesNotApplyHistoryResult()
    {
        using var context = new BunitContext();
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
            [new EnvironmentalUiObservation(
                Guid.NewGuid(), "late-source", EnvironmentalObservationKind.RainState,
                EnvironmentalObservationUnit.Boolean, null, true, EnvironmentalObservationQuality.Good,
                null, Epoch, Epoch)], null)));
        await returned.Task.ConfigureAwait(false);
        await Task.Yield();

        Assert.IsFalse(cut.Markup.Contains("late-source", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidReasonDoesNotInvokeMutationService()
    {
        using var context = new BunitContext();
        var service = CreateOnDemandService();
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("   ");

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
        var service = CreateOnDemandService();
        service.Acquire = (_, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized for this operation."));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);
        var cut = context.Render<EnvironmentalPage>();
        cut.WaitForElement("#environment-reason").Change("authorization verification");

        cut.Find("form").Submit();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => StringAssert.EndsWith(
            navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RefreshFailureDoesNotHideDurableReceipt()
    {
        using var context = new BunitContext();
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
        cut.WaitForElement("#environment-reason").Change("refresh verification");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "observation was committed", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Markup, "receipt is durable", StringComparison.OrdinalIgnoreCase);
        });

        cut.Find("#environment-reason").Change("   ");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            var result = cut.Find(".command-result");
            StringAssert.Contains(result.TextContent, "1 to 128 characters", StringComparison.Ordinal);
            Assert.IsFalse(result.TextContent.Contains("receipt is durable", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("alert", result.GetAttribute("role"));
            Assert.AreEqual("assertive", result.GetAttribute("aria-live"));
        });
    }

    private static TestEnvironmentalUiService CreateOnDemandService() => new()
    {
        Status = OperatorUiResult<EnvironmentalUiStatus>.Success(new(
            true, Epoch, 0, 0, 0,
            [new EnvironmentalUiSource(
                "virtual-rain", EnvironmentalObservationKind.RainState, false, true, "Never observed",
                null, null, null, null, null, 0)],
            []))
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

    private sealed class TestEnvironmentalUiService : ICameraAgentEnvironmentalUiService
    {
        public OperatorUiResult<EnvironmentalUiStatus> Status { get; init; } =
            OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.");
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
        public List<string> IdempotencyKeys { get; } = [];
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return StatusRead?.Invoke(cancellationToken) ?? ValueTask.FromResult(Status);
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
            return HistoryRead?.Invoke(cursor, cancellationToken) ?? ValueTask.FromResult(History(cursor));
        }
    }
}
