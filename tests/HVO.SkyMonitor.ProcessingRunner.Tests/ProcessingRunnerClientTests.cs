using System.Net;
using System.Text;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

[TestClass]
public sealed class ProcessingRunnerClientTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task TokenIsAcquiredOnceAndEveryCallCarriesTheRunnerIdentity()
    {
        using var handler = new ScriptedHttpHandler();
        handler.MapJson(HttpMethod.Put, $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}", _ => RunnerTestData.Registration());
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());
        var request = new ProcessingRunnerRegistrationRequest(
            RunnerTestData.RunnerId, "Test", RunnerTestData.Capabilities(), 1, DateTimeOffset.UtcNow);

        var first = await client.RegisterAsync(request, CancellationToken.None);
        var second = await client.RegisterAsync(request, CancellationToken.None);

        Assert.AreEqual(1, handler.TokenRequests);
        Assert.AreEqual(first.RunnerId, second.RunnerId);
        var registrations = handler.Requests.Where(item => item.Method == "PUT").ToArray();
        Assert.AreEqual(2, registrations.Length);
        foreach (var registration in registrations)
        {
            Assert.AreEqual("Bearer", registration.Headers.Authorization?.Scheme);
            Assert.AreEqual(RunnerTestData.RunnerId, registration.Headers.GetValues(ProcessingRunnerProtocol.RunnerIdHeader).Single());
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ClaimReturnsNullWhenNothingIsClaimable()
    {
        using var handler = new ScriptedHttpHandler();
        handler.Map(HttpMethod.Post, $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}/claims",
            (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        Assert.IsNull(await client.ClaimAsync(new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DownloadVerifiesLengthAndChecksumUnderTheJobLease()
    {
        var claim = RunnerTestData.NoOpClaim();
        var input = claim.Inputs[0];
        using var handler = new ScriptedHttpHandler();
        var body = RunnerTestData.NoOpPayload;
        handler.Map(HttpMethod.Get, input.ContentPath, (_, _) => ScriptedHttpHandler.Bytes(body));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        var payload = await client.DownloadInputAsync(claim, input, CancellationToken.None);
        CollectionAssert.AreEqual(RunnerTestData.NoOpPayload, payload);
        var download = handler.Requests.Single(item => item.Method == "GET");
        Assert.AreEqual(claim.JobId.ToString("D"), download.Headers.GetValues(ProcessingRunnerProtocol.JobIdHeader).Single());
        Assert.AreEqual(claim.LeaseToken.ToString("D"), download.Headers.GetValues(ProcessingRunnerProtocol.LeaseTokenHeader).Single());
        Assert.AreEqual(RunnerTestData.RunnerId, download.Headers.GetValues(ProcessingRunnerProtocol.RunnerIdHeader).Single());

        body = [7];
        var tampered = await Assert.ThrowsExactlyAsync<ProcessingRunnerProtocolException>(() =>
            client.DownloadInputAsync(claim, input, CancellationToken.None));
        Assert.AreEqual(ProcessingRunnerReasonCodes.PayloadChecksumMismatch, tampered.ReasonCode);
        body = [0, 0];
        var longer = await Assert.ThrowsExactlyAsync<ProcessingRunnerProtocolException>(() =>
            client.DownloadInputAsync(claim, input, CancellationToken.None));
        Assert.AreEqual(ProcessingRunnerReasonCodes.PayloadLengthMismatch, longer.ReasonCode);
        body = [];
        var shorter = await Assert.ThrowsExactlyAsync<ProcessingRunnerProtocolException>(() =>
            client.DownloadInputAsync(claim, input, CancellationToken.None));
        Assert.AreEqual(ProcessingRunnerReasonCodes.PayloadLengthMismatch, shorter.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CompletionIsMultipartWithOutcomeAndPayloadParts()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        handler.MapJson(HttpMethod.Post, $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}/jobs/{claim.JobId:D}/completion",
            _ => new ProcessingRunnerCompletionResponse(ProcessingOutcomeStatus.Produced, [Guid.NewGuid()], null));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());
        var request = new ProcessingRunnerCompletionRequest(
            claim.LeaseToken, ProcessingOutcomeStatus.Produced, null, null, [], 1, TimeSpan.FromMilliseconds(3));
        var payload = new byte[] { 9, 8, 7 };

        var response = await client.CompleteAsync(claim.JobId, request, [payload], CancellationToken.None);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, response.Status);
        var completion = handler.Requests.Single(item => item.Path.EndsWith("/completion", StringComparison.Ordinal));
        StringAssert.StartsWith(completion.ContentType, "multipart/form-data", StringComparison.Ordinal);
        var body = Encoding.Latin1.GetString(completion.Body);
        StringAssert.Contains(body, $"name={ProcessingRunnerProtocol.OutcomePartName}", StringComparison.Ordinal);
        StringAssert.Contains(body, $"name={ProcessingRunnerProtocol.PayloadPartPrefix}0", StringComparison.Ordinal);
        StringAssert.Contains(body, "\"leaseToken\":\"" + claim.LeaseToken.ToString("D") + "\"", StringComparison.Ordinal);
        StringAssert.Contains(body, Encoding.Latin1.GetString(payload), StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LeaseProblemsAreClassifiedFromStatusAndReasonCode()
    {
        var claim = RunnerTestData.NoOpClaim();
        using var handler = new ScriptedHttpHandler();
        var path = $"/{ProcessingRunnerProtocol.RoutePrefix}/{RunnerTestData.RunnerId}/jobs/{claim.JobId:D}/lease";
        handler.Map(HttpMethod.Post, path, (_, _) => ScriptedHttpHandler.Problem(HttpStatusCode.Conflict, ProcessingRunnerReasonCodes.LeaseStale, "stale"));
        using var http = RunnerTestData.CreateHttpClient(handler);
        using var client = new ProcessingRunnerClient(http, RunnerTestData.ClientOptions());

        var stale = await Assert.ThrowsExactlyAsync<ProcessingRunnerClientException>(() =>
            client.RenewAsync(claim.JobId, claim.LeaseToken, CancellationToken.None));
        Assert.IsTrue(stale.IsLeaseStale);
        Assert.AreEqual(ProcessingRunnerReasonCodes.LeaseStale, stale.ReasonCode);

        handler.Map(HttpMethod.Post, path, (_, _) => ScriptedHttpHandler.Problem(HttpStatusCode.Gone, ProcessingRunnerReasonCodes.LeaseCanceled, "canceled"));
        var canceled = await Assert.ThrowsExactlyAsync<ProcessingRunnerClientException>(() =>
            client.RenewAsync(claim.JobId, claim.LeaseToken, CancellationToken.None));
        Assert.IsTrue(canceled.IsLeaseCanceled);
    }
}
