using System.Net;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class OwnerBootstrapClientTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_ExplicitPendingScrubRetriesWithinTheOriginalBudget(bool verify)
    {
        using var handler = new ScriptedHandler((_, count, _) => Task.FromResult(count == 1
            ? PendingScrub()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidBody()) }));
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromSeconds(3));
        Assert.AreEqual("owner-ready", await ReadAsync(client, verify, CancellationToken.None));
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_PendingRetriesCannotRestartOrExtendTheDeadline(bool verify)
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(PendingScrub()));
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromMilliseconds(2300));
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReadAsync(client, verify, CancellationToken.None));
        Assert.AreEqual(TimeoutMessage, error.Message);
        Assert.IsNull(error.InnerException);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
        Assert.IsTrue(handler.RequestCount is >= 2 and <= 3, "Retries must spend the current budget, not create a fresh one.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_FinalBodySharesTheBudgetAlreadySpentWaitingForScrub(bool verify)
    {
        using var handler = new ScriptedHandler((_, count, _) => Task.FromResult(count == 1
            ? PendingScrub()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedContent(token => Task.Delay(TimeSpan.FromSeconds(1), token), ValidBody())
            }));
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromMilliseconds(1500));
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReadAsync(client, verify, CancellationToken.None));
        Assert.AreEqual(TimeoutMessage, error.Message);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_CallerCancellationDuringPendingDelayRemainsCancellation(bool verify)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedHandler((_, _, _) =>
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(40));
            return Task.FromResult(PendingScrub());
        });
        var client = new OwnerBootstrapClient(BaseAddress, handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadAsync(client, verify, cancellation.Token));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_GenericUnavailableResponseDoesNotBecomeARetry(bool verify)
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = new OwnerBootstrapClient(BaseAddress, handler);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => ReadAsync(client, verify, CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ProtectedRead_PendingScrubNeverBypassesFinalIdentityComparison()
    {
        using var handler = new ScriptedHandler((_, count, _) => Task.FromResult(count == 1
            ? PendingScrub()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidBody().Replace("test-agent", "different-agent", StringComparison.Ordinal))
            }));
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReadAsync(client, true, CancellationToken.None));
        StringAssert.Contains(error.Message, "identities that differ", StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);
    }

    private static HttpResponseMessage PendingScrub()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.Add(OwnerBootstrapClient.VerificationStateHeader, OwnerBootstrapClient.ScrubPendingState);
        return response;
    }
}
