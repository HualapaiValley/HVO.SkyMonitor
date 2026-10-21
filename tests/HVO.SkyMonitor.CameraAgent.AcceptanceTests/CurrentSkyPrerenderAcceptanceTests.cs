using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The acceptance fixture's typed async lifetime must remain accessible.")]
public sealed class CurrentSkyPrerenderAcceptanceTests
{
    private const string LoadingNotice = "The current image is still loading. Retrying automatically.";
    private const string CompletedPresentation = "Prerender presentation read completed.";
    // An I/O watchdog, not the page's refresh deadline. The read is released by a signal, never a sleep.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task DirectNavigationStreamsLoadingNoticeWhilePresentationReadIsPendingThenCompletes()
    {
        var presentation = new GatedPresentation();
        await using var host = await CreateHostAsync(presentation).ConfigureAwait(false);
        using var client = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        var pending = presentation.BlockNextRead();
        var responseTask = client.GetAsync(new Uri("/", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await pending.Entered.Task.WaitAsync(ProbeTimeout).ConfigureAwait(false);
            using var response = await responseTask.WaitAsync(ProbeTimeout).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            var initial = await ReadThroughNoticeAsync(reader).WaitAsync(ProbeTimeout).ConfigureAwait(false);

            Assert.IsFalse(pending.Release.Task.IsCompleted, "The presentation gate must still be closed when bytes reach the client.");
            Assert.IsFalse(pending.Returned.Task.IsCompleted);
            StringAssert.Contains(initial, "sky-refresh-notice", StringComparison.Ordinal);
            StringAssert.Contains(initial, "<!--Blazor:", StringComparison.Ordinal);
            Assert.IsFalse(initial.Contains(CompletedPresentation, StringComparison.Ordinal));

            pending.Release.SetResult();
            var completion = await reader.ReadToEndAsync().WaitAsync(ProbeTimeout).ConfigureAwait(false);
            StringAssert.Contains(completion, CompletedPresentation, StringComparison.Ordinal);
            StringAssert.Contains(completion, "<blazor-ssr", StringComparison.Ordinal);
            Assert.IsFalse(completion.Contains("sky-refresh-notice", StringComparison.Ordinal));
            Assert.IsTrue(pending.Returned.Task.IsCompletedSuccessfully);

            using var next = await client.GetAsync(new Uri("/", UriKind.Relative)).ConfigureAwait(false);
            next.EnsureSuccessStatusCode();
            var recovered = await next.Content.ReadAsStringAsync().ConfigureAwait(false);
            StringAssert.Contains(recovered, CompletedPresentation, StringComparison.Ordinal);
            Assert.IsFalse(recovered.Contains(LoadingNotice, StringComparison.Ordinal));
        }
        catch (TimeoutException)
        {
            Assert.Fail("HTTP prerender must deliver the loading notice while the initial presentation read is gated, then complete after release. " +
                $"ReadEntered={pending.Entered.Task.IsCompletedSuccessfully}; GateReleased={pending.Release.Task.IsCompleted}; " +
                $"ReadReturned={pending.Returned.Task.IsCompleted}; ResponseHeadersDelivered={responseTask.IsCompletedSuccessfully}.");
        }
        finally
        {
            // Also release a non-streaming red run before disposing the real host; do not wait for its 30s deadline.
            pending.Release.TrySetResult();
            using var cleanup = await responseTask.WaitAsync(ProbeTimeout).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectNavigationDoesNotReadOrStreamPresentationForUnauthorizedUsers(bool authenticatedNonOwner)
    {
        var presentation = new GatedPresentation();
        await using var host = await CreateHostAsync(presentation).ConfigureAwait(false);
        using var client = authenticatedNonOwner
            ? await host.CreateOwnerClientAsync(CameraAgentKestrelFixture.NonOwnerPassword, CameraAgentKestrelFixture.NonOwnerEmail).ConfigureAwait(false)
            : new HttpClient { BaseAddress = host.BaseAddress };

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative)).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.AreEqual(authenticatedNonOwner ? "/Account/AccessDenied" : "/Account/Login", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.AreEqual(0, presentation.Reads, "Authorization must run before presentation access or streaming.");
        Assert.IsFalse(html.Contains(LoadingNotice, StringComparison.Ordinal));
        Assert.IsFalse(html.Contains(CompletedPresentation, StringComparison.Ordinal));
    }

    private static Task<CameraAgentKestrelFixture> CreateHostAsync(GatedPresentation presentation) =>
        CameraAgentKestrelFixture.CreateAsync(services =>
        {
            services.RemoveAll<ICameraAgentCurrentImagePresentationService>();
            services.AddSingleton<ICameraAgentCurrentImagePresentationService>(presentation);
        });

    private static async Task<string> ReadThroughNoticeAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (text.Length < 512 * 1024)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            Assert.IsGreaterThan(0, count, "The response ended before the loading notice arrived.");
            text.Append(buffer, 0, count);
            var received = text.ToString();
            if (received.Contains(LoadingNotice, StringComparison.Ordinal)) return received;
        }
        Assert.Fail("The HTTP loading probe exceeded its bounded response size.");
        return string.Empty;
    }

    private sealed class PendingRead
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedPresentation : ICameraAgentCurrentImagePresentationService
    {
        private PendingRead? _next;
        private int _reads;

        internal int Reads => Volatile.Read(ref _reads);

        internal PendingRead BlockNextRead()
        {
            var pending = new PendingRead();
            Assert.IsNull(Interlocked.CompareExchange(ref _next, pending, null));
            return pending;
        }

        public async ValueTask<CameraAgentCurrentImagePresentation> GetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            var pending = Interlocked.Exchange(ref _next, null);
            if (pending is not null)
            {
                pending.Entered.SetResult();
                await pending.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                pending.Returned.SetResult();
            }
            return new(DateTimeOffset.UnixEpoch, CameraAgentPresentationImageFreshness.Empty,
                new(CameraAgentPresentationSystemState.Capturing, CompletedPresentation, DateTimeOffset.UnixEpoch),
                null, null, false, null, [], false, false);
        }

        public ValueTask<CameraAgentCapturePresentation> ProjectCaptureAsync(CameraAgentGalleryCapture capture, CancellationToken cancellationToken)
            => throw new NotSupportedException("The HTTP initial-read probe does not project archived captures.");
    }
}
