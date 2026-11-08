using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
[TestCategory("Integration")]
public sealed class RunManifestEndpointTests
{
    [TestMethod]
    [DataRow("anonymous", HttpStatusCode.Unauthorized)]
    [DataRow("outsider", HttpStatusCode.Forbidden)]
    [DataRow("owner", HttpStatusCode.NotFound)]
    public async Task ManifestDownloadEnforcesOperationsReadPolicy(string account, HttpStatusCode expected)
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        if (account != "anonymous")
        {
            var user = account == "owner"
                ? await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false)
                : new ApplicationUser { UserName = $"manifest-outsider-{Guid.NewGuid():N}", EmailConfirmed = true };
            Assert.IsNotNull(user);
            if (account == "outsider") Assert.IsTrue((await users.CreateAsync(user).ConfigureAwait(false)).Succeeded);
            client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, user.Id);
        }
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/operations/processing-graphs/executions/{Guid.NewGuid():D}/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(body.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task VirtualCaptureDownloadsItsRetainedFrozenEvidenceWithoutStorageDetails()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var operations = scope.ServiceProvider.GetRequiredService<IProcessingGraphOperations>();
        ProcessingGraphExecutionDetail? detail = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (detail is null)
        {
            var runs = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, deadline.Token).ConfigureAwait(false);
            foreach (var run in runs.Where(static run => run.Status == ProcessingGraphExecutionStatus.Completed))
            {
                var candidate = await operations.ReadRunManifestAsync(run.ExecutionId, deadline.Token).ConfigureAwait(false);
                if (candidate?.Nodes.SelectMany(static node => node.Outputs).Any() == true) { detail = candidate; break; }
            }
            if (detail is null) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/operations/processing-graphs/executions/{detail.Execution.ExecutionId:D}/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.IsTrue(bytes.Length <= ProcessingRunManifestBounds.MaximumBytes);
        using var document = JsonDocument.Parse(bytes);
        var json = document.RootElement;
        Assert.AreEqual("hvo-cameraagent-run-manifest-v1", json.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(detail.Execution.LocalPlanIdentitySha256, json.GetProperty("localPlanIdentitySha256").GetString());
        Assert.AreEqual(detail.Execution.GraphRevisionId, json.GetProperty("graphRevisionId").GetString());
        foreach (var node in json.GetProperty("nodes").EnumerateArray())
        {
            var stored = detail.Nodes.Single(value => value.NodeId == node.GetProperty("nodeId").GetString());
            foreach (var output in node.GetProperty("outputs").EnumerateArray())
            {
                var artifact = stored.Outputs.Single(value => value.ArtifactId == output.GetProperty("artifactId").GetGuid());
                Assert.AreEqual(artifact.PayloadSha256, output.GetProperty("payloadSha256").GetString());
                CollectionAssert.AreEqual(artifact.SourceArtifactIds.ToArray(), output.GetProperty("sourceArtifactIds").EnumerateArray().Select(value => value.GetGuid()).ToArray());
            }
        }
        var text = json.GetRawText();
        foreach (var forbidden in new[] { "relativePath", "leaseOwner", "diagnostics", "password", "triggerReference", "failureReason" })
            Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
    }
}
