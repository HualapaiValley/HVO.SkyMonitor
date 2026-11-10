using System.Net;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class ConcurrentRequestProbeTests
{
    [TestMethod]
    public async Task EarlyRejectionIsReportedWithoutWaitingForTimeout()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)425);
        var action = () => ConcurrentRequestProbe.WaitForEntryAsync(
            new TaskCompletionSource().Task, [Task.FromResult(response)], TimeSpan.FromSeconds(30), () => "entered=0/1");
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HTTP 425*entered=0/1*").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RequestFaultIsPreserved()
    {
        var action = () => ConcurrentRequestProbe.WaitForEntryAsync(new TaskCompletionSource().Task,
            [Task.FromException<HttpResponseMessage>(new HttpRequestException("request-fault"))], TimeSpan.FromSeconds(30), () => "entered=0/1");
        await action.Should().ThrowAsync<HttpRequestException>().WithMessage("request-fault").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RequestCancellationIsPreserved()
    {
        var action = () => ConcurrentRequestProbe.WaitForEntryAsync(new TaskCompletionSource().Task,
            [Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true))], TimeSpan.FromSeconds(30), () => "entered=0/1");
        await action.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(8)]
    public async Task EntryDoesNotWaitForPausedRequests(int concurrency)
    {
        var requests = Enumerable.Range(0, concurrency).Select(_ => new TaskCompletionSource<HttpResponseMessage>().Task).ToArray();
        await ConcurrentRequestProbe.WaitForEntryAsync(Task.CompletedTask, requests, TimeSpan.FromSeconds(30), () => $"entered={concurrency}/{concurrency}")
            .ConfigureAwait(false);
        requests.Should().OnlyContain(task => !task.IsCompleted);
    }

    [TestMethod]
    public async Task TimeoutIncludesProgressAndRequestState()
    {
        var action = () => ConcurrentRequestProbe.WaitForEntryAsync(new TaskCompletionSource().Task,
            [new TaskCompletionSource<HttpResponseMessage>().Task], TimeSpan.Zero, () => "entered=0/1 verified=3");
        await action.Should().ThrowAsync<TimeoutException>().WithMessage("*entered=0/1 verified=3*WaitingForActivation*").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BoundedFailureCleanupPreservesFirstFailureAndDisposesLateResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException("first-entry-failure");
        async Task SubjectAsync()
        {
            try { throw original; }
            catch
            {
                await ConcurrentRequestProbe.DrainAfterFailureAsync([pending.Task], cancellation, TimeSpan.Zero).ConfigureAwait(false);
                throw;
            }
        }
        Func<Task> action = SubjectAsync;
        (await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        using var content = new DisposalContent();
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task FailureCleanupObservesThrowingCancellationCallbackAndRequestFault()
    {
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => throw new InvalidOperationException("callback-failure"));
        await ConcurrentRequestProbe.DrainAfterFailureAsync(
            [Task.FromException<HttpResponseMessage>(new HttpRequestException("request-failure"))],
            cancellation, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task ResponseTimeoutIncludesCurrentProgressAndRequestState()
    {
        var generationCalls = 1;
        var verifyCalls = 0;
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        var progress = () => $"generationCalls={generationCalls} verifyCalls={verifyCalls}";
        generationCalls = 5;
        verifyCalls = 3;
        var action = () => ConcurrentRequestProbe.WaitForResponseAsync(pending.Task, TimeSpan.Zero, progress);
        var error = (await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*after generation release*generationCalls=5 verifyCalls=3*WaitingForActivation*")
            .ConfigureAwait(false)).Which;
        error.InnerException.Should().BeOfType<TimeoutException>();
        pending.Task.IsCompleted.Should().BeFalse();
    }

    [TestMethod]
    public async Task ResponseWaitPreservesSubjectOutcome()
    {
        var original = new TimeoutException("subject-timeout");
        var failure = () => ConcurrentRequestProbe.WaitForResponseAsync(
            Task.FromException<HttpResponseMessage>(original), TimeSpan.Zero, () => "progress");
        (await failure.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        var cancelled = () => ConcurrentRequestProbe.WaitForResponseAsync(
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)), TimeSpan.Zero, () => "progress");
        await cancelled.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        (await ConcurrentRequestProbe.WaitForResponseAsync(Task.FromResult(response), TimeSpan.Zero, () => "progress")
            .ConfigureAwait(false)).Should().BeSameAs(response);
    }

    private sealed class DisposalContent() : ByteArrayContent([])
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }
}
