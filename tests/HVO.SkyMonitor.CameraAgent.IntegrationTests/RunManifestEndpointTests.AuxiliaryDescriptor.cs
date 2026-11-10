using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class RunManifestEndpointTests
{
    [TestMethod]
    public async Task CorruptAuxiliaryDescriptorRefusesOnlyManifestAndRecoversOverAuthenticatedHttp()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var operations = scope.ServiceProvider.GetRequiredService<IProcessingGraphOperations>();
        ProcessingGraphExecutionDetail? detail = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (detail is null)
        {
            var runs = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, deadline.Token).ConfigureAwait(false);
            var completed = runs.Where(static run => run.Status == ProcessingGraphExecutionStatus.Completed).ToArray();
            if (completed.Length >= 2)
            {
                foreach (var run in completed)
                {
                    var candidate = await operations.ReadRunManifestAsync(run.ExecutionId, deadline.Token).ConfigureAwait(false);
                    if (candidate?.Nodes.SelectMany(static node => node.Outputs).Any() == true) { detail = candidate; break; }
                }
            }
            if (detail is null) await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        var auxiliary = await AuxiliaryRunManifestEvidenceFixture.CreateAsync(AssemblyHooks.Fixture.StorageRoot,
            detail.Nodes.SelectMany(static node => node.Outputs).First().ArtifactId).ConfigureAwait(false);
        await using var auxiliaryLifetime = auxiliary.ConfigureAwait(false);
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        var path = $"/api/v1/operations/processing-graphs/executions/{detail.Execution.ExecutionId:D}";
        using var before = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
        var expected = await before.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        using (var manifest = JsonDocument.Parse(expected))
            Assert.IsTrue(manifest.RootElement.GetProperty("nodes").EnumerateArray()
                .SelectMany(static node => node.GetProperty("inputs").EnumerateArray())
                .Any(input => input.GetProperty("artifactId").GetGuid() == auxiliary.ReferenceArtifactId &&
                    input.GetProperty("kind").GetString() == "AuxiliaryProcessingOutput"));
        var ordinary = await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        await auxiliary.CorruptAsync().ConfigureAwait(false);
        using var refused = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsByteArrayAsync().ConfigureAwait(false)))
        {
            Assert.AreEqual("The retained run evidence is corrupt and cannot be downloaded.", problem.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(problem.RootElement.TryGetProperty("schemaVersion", out _));
        }
        Assert.AreEqual(ordinary, await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false));
        await auxiliary.RestoreReferenceAsync().ConfigureAwait(false);
        using var recovered = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        CollectionAssert.AreEqual(expected, await recovered.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual(ordinary, await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false));
    }
}
