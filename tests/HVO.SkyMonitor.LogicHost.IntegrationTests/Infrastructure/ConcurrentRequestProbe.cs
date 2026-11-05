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
}
