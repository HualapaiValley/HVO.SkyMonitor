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
    public async Task PauseAndDrain_PostsFreshCommandIdAndWaitsForTheDurableBoundary()
    {
        var operationId = Guid.NewGuid();
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) => sequence switch
        {
            1 => State("Running", 2),
            2 => await CommandAsync(request, "/api/internal/deployment/lifecycle/pause", operationId, 2, cancellationToken),
            3 => State("PauseRequested", 3, rawLeased: 1),
            _ => State("Paused", 4, captureSequence: 9)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.PauseAndDrainAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(9, continuity.CaptureSequence);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task PauseAndDrain_FailsOnConcurrentCaptureControlChange()
    {
        using var handler = new ScriptedHandler((request, sequence, _) => Task.FromResult(sequence switch
        {
            1 => State("Running", 2),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.PauseAndDrainAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "lifecycle pause", StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_ReusesTheDurablePauseWithoutIssuingACommand()
    {
        using var handler = new ScriptedHandler((request, _, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
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
            1 => State("Initializing", 0),
            _ => State("Paused", 2)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var continuity = await client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None);

        Assert.AreEqual("Paused", continuity.CaptureState);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsWhenTheDurablePauseWasLost()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(State("Running", 4)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "did not preserve the durable lifecycle pause", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_PostsFreshCommandIdAgainstTheCurrentVersion()
    {
        var operationId = Guid.NewGuid();
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) => sequence switch
        {
            1 => State("Paused", 5),
            _ => await CommandAsync(request, "/api/internal/deployment/lifecycle/resume", operationId, 5, cancellationToken)
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        await client.ResumeAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_FailsOnConcurrentCaptureControlChange()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => Task.FromResult(sequence switch
        {
            1 => State("Paused", 5),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "lifecycle resume", StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);
    }

    private static async Task<HttpResponseMessage> CommandAsync(
        HttpRequestMessage request,
        string path,
        Guid operationId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(path, request.RequestUri?.AbsolutePath);
        Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var commandId = payload.RootElement.GetProperty("operationId").GetGuid();
        // Each command carries its own id so a retry converges on the durable state
        // instead of colliding with the earlier command's idempotency record.
        Assert.AreNotEqual(operationId, commandId);
        Assert.AreNotEqual(Guid.Empty, commandId);
        Assert.AreEqual(expectedVersion, payload.RootElement.GetProperty("expectedVersion").GetInt64());
        StringAssert.Contains(payload.RootElement.GetProperty("reason").GetString(), operationId.ToString("D"), StringComparison.Ordinal);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage State(string captureState, long version, long captureSequence = 0, long rawLeased = 0)
    {
        var json = $$"""
            {
              "captureControl": { "value": { "state": "{{captureState}}", "version": {{version}} } },
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
