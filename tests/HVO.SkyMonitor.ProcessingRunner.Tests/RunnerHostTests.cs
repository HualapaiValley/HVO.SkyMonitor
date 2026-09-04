using System.Net;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

[TestClass]
public sealed class RunnerHostTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task JobExecutionDownloadsInputsAndProducesAnOutcome()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        handler.Map(HttpMethod.Get, claim.Inputs[0].ContentPath, (_, _) => ScriptedHttpHandler.Bytes(RunnerTestData.NoOpPayload));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        var result = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim, ProcessingRunnerProtocol.MaximumTransferBytes, TimeProvider.System, CancellationToken.None);

        Assert.IsNull(result.Failure);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Outcome!.Status);
        Assert.AreEqual(1, result.InputBytes);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MissingInputsAreReportedAsObjectMissingWithTheArtifactIdentity()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        handler.Map(HttpMethod.Get, claim.Inputs[0].ContentPath, (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        var result = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim, ProcessingRunnerProtocol.MaximumTransferBytes, TimeProvider.System, CancellationToken.None);

        Assert.IsNull(result.Outcome);
        Assert.AreEqual("object.missing", result.Failure!.ReasonCode);
        Assert.IsTrue(result.Failure.Retryable);
        Assert.AreEqual(claim.Inputs[0].ArtifactId, result.Failure.UnavailableArtifactId);
        Assert.AreEqual(claim.LeaseToken, result.Failure.LeaseToken);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UnsupportedClaimVersionAndOversizedInputsFailBeforeAnyDownload()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        var version = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim with { ProtocolVersion = 99 }, 1024, TimeProvider.System, CancellationToken.None);
        Assert.AreEqual(ProcessingRunnerReasonCodes.InvalidCompletion, version.Failure!.ReasonCode);
        Assert.IsFalse(version.Failure.Retryable);
        var oversized = await RunnerJobExecution.ExecuteAsync(
            client, new ProcessingRecipeExecutor(), claim, maxTransferBytes: 0, TimeProvider.System, CancellationToken.None);
        Assert.AreEqual(ProcessingRunnerReasonCodes.TransferTooLarge, oversized.Failure!.ReasonCode);
        Assert.IsFalse(handler.Requests.Any(item => item.Method == "GET"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HostRegistersClaimsCompletesHeartbeatsAndRetiresOnIdleShutdown()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        var prefix = $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}";
        var claimed = 0;
        handler.MapJson(HttpMethod.Put, prefix, _ => RunnerTestData.Registration());
        handler.Map(HttpMethod.Post, $"{prefix}/claims", (_, _) => Interlocked.Increment(ref claimed) == 1
            ? ScriptedHttpHandler.Json(claim)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.Map(HttpMethod.Get, claim.Inputs[0].ContentPath, (_, _) => ScriptedHttpHandler.Bytes(RunnerTestData.NoOpPayload));
        handler.MapJson(HttpMethod.Post, $"{prefix}/jobs/{claim.JobId:D}/completion",
            _ => new ProcessingRunnerCompletionResponse(ProcessingOutcomeStatus.Produced, [Guid.NewGuid()], null));
        handler.MapJson(HttpMethod.Post, $"{prefix}/heartbeat",
            _ => new ProcessingRunnerHeartbeatResponse(ProcessingRunnerRegistrationStatus.Active, [], [], DateTimeOffset.UtcNow));
        handler.Map(HttpMethod.Delete, prefix, (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());
        using var output = new StringWriter();
        var host = new RunnerHost(
            client,
            new ProcessingRecipeExecutor(),
            new RunnerHostOptions(
                RunnerTestData.RunnerId, "Test", RunnerTestData.Capabilities(), 1,
                ProcessingRunnerProtocol.MaximumTransferBytes, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)),
            new RunnerLog(output, output, RunnerTestData.RunnerId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await host.RunAsync(timeout.Token);

        Assert.AreEqual(0, exit);
        var methods = handler.Requests.Select(item => $"{item.Method} {item.Path}").ToArray();
        Assert.IsTrue(methods.Contains($"PUT {prefix}"), string.Join('\n', methods));
        Assert.IsTrue(methods.Contains($"POST {prefix}/jobs/{claim.JobId:D}/completion"), string.Join('\n', methods));
        Assert.IsTrue(methods.Contains($"POST {prefix}/heartbeat"), string.Join('\n', methods));
        Assert.AreEqual($"DELETE {prefix}", methods[^1]);
        StringAssert.Contains(output.ToString(), "\"event\":\"job-completed\"", StringComparison.Ordinal);
        StringAssert.Contains(output.ToString(), "\"event\":\"idle-shutdown\"", StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HeartbeatCancellationStopsExecutionWithoutCompletingTheJob()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        var prefix = $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}";
        var claimed = 0;
        handler.MapJson(HttpMethod.Put, prefix, _ => RunnerTestData.Registration(heartbeat: TimeSpan.FromMilliseconds(100)));
        handler.Map(HttpMethod.Post, $"{prefix}/claims", (_, _) => Interlocked.Increment(ref claimed) == 1
            ? ScriptedHttpHandler.Json(claim)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.Map(HttpMethod.Get, claim.Inputs[0].ContentPath, (_, _) => ScriptedHttpHandler.Bytes(RunnerTestData.NoOpPayload));
        handler.MapJson(HttpMethod.Post, $"{prefix}/heartbeat",
            _ => new ProcessingRunnerHeartbeatResponse(ProcessingRunnerRegistrationStatus.Active, [claim.JobId], [], DateTimeOffset.UtcNow));
        handler.Map(HttpMethod.Delete, prefix, (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());
        using var output = new StringWriter();
        var executor = new BlockingExecutor();
        var host = new RunnerHost(
            client,
            executor,
            new RunnerHostOptions(
                RunnerTestData.RunnerId, "Test", RunnerTestData.Capabilities(), 1,
                ProcessingRunnerProtocol.MaximumTransferBytes, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)),
            new RunnerLog(output, output, RunnerTestData.RunnerId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await host.RunAsync(timeout.Token);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(executor.WasCanceled);
        Assert.IsFalse(handler.Requests.Any(item => item.Path.EndsWith("/completion", StringComparison.Ordinal)));
        StringAssert.Contains(output.ToString(), "\"event\":\"job-canceled\"", StringComparison.Ordinal);
    }

    private sealed class BlockingExecutor : IProcessingRecipeExecutor
    {
        public bool WasCanceled { get; private set; }

        public async ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
            return ProcessingOutcome.Skipped("never");
        }
    }
}
