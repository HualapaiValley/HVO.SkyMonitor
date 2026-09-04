using System.Net;
using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentLifecycleClientTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:5130");

    [TestMethod]
    public async Task PauseAndDrain_AcceptsIdempotencyConflictWhenDurableStateIsAlreadyPaused()
    {
        using var handler = new ScriptedHandler((request, sequence) => sequence switch
        {
            1 => State("Running", 2),
            2 => Post(request, "/api/internal/deployment/lifecycle/pause", HttpStatusCode.Conflict),
            _ => State("Paused", 3)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.PauseAndDrainAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(3, continuity.CaptureVersion);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task PauseAndDrain_RejectsIdempotencyConflictWhenCaptureKeepsRunning()
    {
        using var handler = new ScriptedHandler((request, sequence) => sequence switch
        {
            1 => State("Running", 2),
            2 => Post(request, "/api/internal/deployment/lifecycle/pause", HttpStatusCode.Conflict),
            _ => State("Running", 5)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.PauseAndDrainAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "capture control state changed", StringComparison.Ordinal);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_ReusesDurablePauseWithoutIssuingAnotherCommand()
    {
        using var handler = new ScriptedHandler((request, sequence) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return State("Paused", 2, captureSequence: 17);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.ConfirmDrainedAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(17, continuity.CaptureSequence);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsWhenTheDurablePauseWasLost()
    {
        using var handler = new ScriptedHandler((_, _) => State("Running", 4));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "did not preserve the durable lifecycle pause", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_AcceptsIdempotencyConflictWhenCaptureAlreadyRuns()
    {
        using var handler = new ScriptedHandler((request, sequence) => sequence switch
        {
            1 => State("Paused", 2),
            2 => Post(request, "/api/internal/deployment/lifecycle/resume", HttpStatusCode.Conflict),
            _ => State("Running", 3)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        await client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None);

        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_RejectsIdempotencyConflictWhenCaptureStaysPaused()
    {
        using var handler = new ScriptedHandler((request, sequence) => sequence switch
        {
            1 => State("Paused", 2),
            2 => Post(request, "/api/internal/deployment/lifecycle/resume", HttpStatusCode.Conflict),
            _ => State("Paused", 2)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "capture control state changed", StringComparison.Ordinal);
        Assert.AreEqual(3, handler.RequestCount);
    }

    private static HttpResponseMessage Post(HttpRequestMessage request, string path, HttpStatusCode status)
    {
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(path, request.RequestUri?.AbsolutePath);
        Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
        return new HttpResponseMessage(status);
    }

    private static HttpResponseMessage State(string captureState, long version, long captureSequence = 0)
    {
        var json = $$"""
            {
              "captureControl": { "value": { "state": "{{captureState}}", "version": {{version}} } },
              "rawIngress": { "value": { "pendingCount": 0, "leasedCount": 0 } },
              "captureLanes": { "value": { "pendingCount": 0, "leasedCount": 0 } },
              "captureProcessing": { "value": { "pendingCount": 0, "leasedCount": 0 } },
              "artifactOutbox": { "value": { "pendingCount": 0, "leasedCount": 0 } },
              "captureSequence": {{captureSequence}}
            }
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request, RequestCount));
        }
    }
}
