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

    // Polling tests use short budgets so they exercise the real loop without
    // sleeping through production intervals.
    private static readonly LifecycleBudgets FastBudgets = new(
        ReadTimeout: TimeSpan.FromMilliseconds(500),
        DrainDeadline: TimeSpan.FromSeconds(5),
        DrainPollInterval: TimeSpan.FromMilliseconds(10));

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
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

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
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

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
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

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

    [TestMethod]
    public async Task ConfirmDrained_KeepsWaitingForAnUninitializedPausedBoundary()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => Task.FromResult(sequence switch
        {
            1 => State("Paused", 2, initialized: false),
            _ => State("Paused", 2)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

        var continuity = await client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None);

        Assert.IsTrue(continuity.CaptureInitialized);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsFastWhenTheStateReadIsRejected()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "rejected the lifecycle state read with status 404", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_KeepsTheObservedProgressWhenAReadIsRejected()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => Task.FromResult(sequence switch
        {
            1 => State("PauseRequested", 3, rawLeased: 2),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets with { ReadTimeout = TimeSpan.FromSeconds(1) });

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "rejected the lifecycle state read with status 404; last observed capture control 'PauseRequested' (initialized: True, leased raw/lane/processing/outbox 2/0/0/0).", StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_FailsFastWhenTheStatePayloadCannotBeParsed()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "captureControl": { "value": { "state": 7 } } }""", Encoding.UTF8, "application/json")
        }));
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, FastBudgets);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "could not be parsed", StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConfirmDrained_ReportsTheLastObservedStateWhenTheDeadlineExpires()
    {
        using var handler = new ScriptedHandler((_, sequence, _) => sequence switch
        {
            1 => Task.FromResult(State("Unavailable", 0, rawLeased: 3, initialized: false)),
            _ => throw new HttpRequestException("connection refused")
        });
        // The deadline and read budget leave room for a cold first read before the retries begin.
        var budgets = FastBudgets with { DrainDeadline = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromSeconds(1) };
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ConfirmDrainedAsync("lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "before the lifecycle deadline", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "last observed capture control 'Unavailable' (initialized: False, leased raw/lane/processing/outbox 3/0/0/0)", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "the last state read failed: CameraAgent lifecycle state could not be read", StringComparison.Ordinal);
        Assert.IsTrue(handler.RequestCount > 1, $"expected retries before the deadline, saw {handler.RequestCount} requests");
    }

    [TestMethod]
    public async Task PauseAndDrain_SharesOneDrainBudgetBetweenTheCommandAndTheBoundary()
    {
        var operationId = Guid.NewGuid();
        var budgets = new LifecycleBudgets(
            ReadTimeout: TimeSpan.FromMilliseconds(100),
            DrainDeadline: TimeSpan.FromSeconds(2),
            DrainPollInterval: TimeSpan.FromMilliseconds(10));
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                // The pause consumes most of the shared drain budget.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                return await CommandAsync(request, "pause", operationId, cancellationToken);
            }
            return State("Initializing", 0, initialized: false);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.PauseAndDrainAsync(operationId, "lifecycle-token", CancellationToken.None));

        stopwatch.Stop();
        StringAssert.Contains(exception.Message, "last observed capture control 'Initializing'", StringComparison.Ordinal);
        // A stacked budget would poll for a further full drain deadline (about 3.5 s in total).
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"the boundary poll did not share the drain budget: {stopwatch.Elapsed}");
    }

    [TestMethod]
    public async Task PauseAndDrain_KeepsAReadBudgetForTheBoundaryAfterASlowPause()
    {
        var operationId = Guid.NewGuid();
        var budgets = new LifecycleBudgets(
            ReadTimeout: TimeSpan.FromSeconds(3),
            DrainDeadline: TimeSpan.FromSeconds(2),
            DrainPollInterval: TimeSpan.FromMilliseconds(10));
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                // The pause leaves less of the shared budget than the confirming read needs.
                await Task.Delay(TimeSpan.FromMilliseconds(1600), cancellationToken);
                return await CommandAsync(request, "pause", operationId, cancellationToken);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            return State("Paused", 4, captureSequence: 11);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);

        var continuity = await client.PauseAndDrainAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual(11, continuity.CaptureSequence);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PauseAndDrain_ReportsTheOnlyBoundaryReadWhenItExhaustsItsBudget()
    {
        var operationId = Guid.NewGuid();
        var budgets = new LifecycleBudgets(
            ReadTimeout: TimeSpan.FromSeconds(1),
            DrainDeadline: TimeSpan.FromSeconds(2),
            DrainPollInterval: TimeSpan.FromMilliseconds(10));
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                // The pause leaves less than one read budget, so the boundary gets the floor read only.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                return await CommandAsync(request, "pause", operationId, cancellationToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return State("Paused", 4);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.PauseAndDrainAsync(operationId, "lifecycle-token", CancellationToken.None));

        StringAssert.Contains(exception.Message, "the last state read failed: CameraAgent did not report its lifecycle state within its budget", StringComparison.Ordinal);
        // The pause and the single floor-granted read are the only requests.
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_WaitsThroughStartupInitializationForTheAcknowledgement()
    {
        var operationId = Guid.NewGuid();
        var budgets = FastBudgets with { DrainDeadline = TimeSpan.FromSeconds(2) };
        using var handler = new ScriptedHandler(async (request, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return await CommandAsync(request, "resume", operationId, cancellationToken);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);

        await client.ResumeAsync(operationId, "lifecycle-token", CancellationToken.None);

        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Resume_ReportsAnUnacknowledgedCommandAsAnInstallerFailure()
    {
        // The read budget is deliberately far larger than the drain budget so a
        // resume that used the wrong one would exceed the elapsed-time bound.
        var budgets = FastBudgets with { DrainDeadline = TimeSpan.FromMilliseconds(100), ReadTimeout = TimeSpan.FromSeconds(20) };
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new CameraAgentLifecycleClient(BaseAddress, handler, budgets);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => client.ResumeAsync(Guid.NewGuid(), "lifecycle-token", CancellationToken.None));

        stopwatch.Stop();
        StringAssert.Contains(exception.Message, "did not acknowledge the lifecycle resume command within its budget", StringComparison.Ordinal);
        // A resume must honour the configured drain budget rather than a fixed longer one.
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the resume did not honour its budget: {stopwatch.Elapsed}");
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
