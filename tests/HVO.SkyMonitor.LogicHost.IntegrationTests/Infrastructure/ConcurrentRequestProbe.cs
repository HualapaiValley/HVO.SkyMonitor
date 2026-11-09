namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Does not hide a rejected or faulted request behind a barrier timeout.</summary>
internal static class ConcurrentRequestProbe
{
    internal static async Task WaitForEntryAsync(
        Task entered, IReadOnlyList<Task<HttpResponseMessage>> requests, TimeSpan timeout, Func<string> progress)
    {
        try
        {
            await Task.WhenAny(entered, Task.WhenAny(requests)).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Generation probe timed out: {progress()}; request states={string.Join(',', requests.Select(task => task.Status))}", exception);
        }
        if (entered.IsCompleted)
        {
            await entered.ConfigureAwait(false);
            return;
        }
        var completed = requests.First(task => task.IsCompleted);
        var response = await completed.ConfigureAwait(false); // Preserve request faults/cancellation.
        throw new InvalidOperationException(
            $"Submission completed before generation probe entry: HTTP {(int)response.StatusCode}; {progress()}");
    }

    internal static async Task<HttpResponseMessage> WaitForResponseAsync(
        Task<HttpResponseMessage> request, TimeSpan timeout, Func<string> progress)
    {
        Task<HttpResponseMessage> completed;
        // Waiting on the completion signal separates the probe deadline from a subject TimeoutException.
        try { completed = await Task.WhenAny(request).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Submission timed out after generation release: {progress()}; request state={request.Status}", exception);
        }
        return await completed.ConfigureAwait(false);
    }

    internal static async Task DrainAfterFailureAsync(
        IReadOnlyList<Task<HttpResponseMessage>> requests, CancellationTokenSource cancellation, TimeSpan timeout)
    {
        var cancellationTask = cancellation.CancelAsync();
        var responses = Task.WhenAll(requests.Select(task => task.ContinueWith(static completed =>
        {
            if (completed.IsCompletedSuccessfully) { completed.Result.Dispose(); }
            else { _ = completed.Exception; }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
        // Observe cancellation callbacks and response cleanup even after the bounded wait ends.
        // Neither may replace the subject failure that brought the caller here.
        var observed = Task.WhenAll(cancellationTask, responses).ContinueWith(
            static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await observed.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* Continuations retain cleanup ownership. */ }
    }
}
