using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentLifecycleClientTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:5130");

    [TestMethod]
    public async Task PauseAndDrain_PostsStateTargetedCommandAndWaitsForTheDurableBoundary()
    {
        var operationId = Guid.NewGuid();
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) => sequence switch
        {
            1 => await CommandAsync(request, "pause", operationId, cancellationToken),
            2 => State("PauseRequested", 3, rawLeased: 1),
            _ => State("Paused", 4, captureSequence: 9)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.PauseAndDrainAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(9, continuity.CaptureSequence);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task PauseAndDrain_ReportsARejectedCommandAsAnInstallerFailure()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.PauseAndDrainAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "rejected the lifecycle pause command with status 401", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_ReusesTheDurablePauseWithoutIssuingACommand()
    {
        using var handler = new ScriptedHandler((request, _, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
            return Task.FromResult(State("Paused", 2, captureSequence: 17));
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None);

        Assert.AreEqual(17, continuity.CaptureSequence);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_WaitsThroughRestartInitialization()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => Task.FromResult(sequence switch
        {
            1 => State("Initializing", 0, initialized: false),
            2 => State("Unavailable", 0, initialized: false),
            _ => State("Paused", 2)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_RetriesTransientReadFailuresUntilTheBoundaryAppears()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => sequence switch
        {
            1 => throw new HttpRequestException("connection refused"),
            2 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(State("Paused", 2))
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsWhenTheDurablePauseWasLost()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(State("Running", 4)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "'Running' instead of the durable lifecycle pause", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsFastWhenInitializedCaptureControlIsUnavailable()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(State("Unavailable", 3, initialized: true)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "'Unavailable'", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_PostsStateTargetedCommand()
    {
        var operationId = Guid.NewGuid();
        using var handler = new ScriptedHandler((request, _, cancellationToken) => CommandAsync(request, "resume", operationId, cancellationToken));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        await client.ResumeAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_ReportsARejectedCommandAsAnInstallerFailure()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "rejected the lifecycle resume command with status 500", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_ReportsATransportFailureAsAnInstallerFailure()
    {
        using var handler = new ScriptedHandler((_, _, _) => throw new HttpRequestException("connection reset"));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "did not accept the lifecycle resume command", StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> CommandAsync(
        HttpRequestMessage request,
        string action,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual($"/api/internal/deployment/lifecycle/{action}", request.RequestUri?.AbsolutePath);
        Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var commandId = payload.RootElement.GetProperty("operationId").GetGuid();
        // Each command carries its own id so a retry converges on the durable state
        // instead of replaying an earlier idempotency record, and it targets a state
        // rather than a capture-control version.
        Assert.AreNotEqual(operationId, commandId);
        Assert.AreNotEqual(Guid.Empty, commandId);
        Assert.IsFalse(payload.RootElement.TryGetProperty("expectedVersion", out _));
        StringAssert.Contains(payload.RootElement.GetProperty("reason").GetString(), operationId.ToString("D"), StringComparison.Ordinal);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage State(
        string captureState,
        long version,
        long captureSequence = 0,
        long rawLeased = 0,
        bool initialized = true)
    {
        var json = $$"""
            {
              "captureControl": { "value": { "state": "{{captureState}}", "version": {{version}}, "isInitialized": {{(initialized ? "true" : "false")}} } },
              "rawIngress": { "value": { "pendingCount": 0, "leasedCount": {{rawLeased}} } },
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
}
