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
