using System.Net;
using System.Text.Json;
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
        using var fixture = new CameraAgentIntegrationFixture();
        await InitializeRetainedCaptureFixtureAsync(fixture).ConfigureAwait(false);
        using var scope = fixture.CreateCameraAgentScope();
        var (reference, consumer) = await PrepareRetainedCapturesAsync(fixture, scope.ServiceProvider).ConfigureAwait(false);
        var executionId = consumer.Detail.Execution.ExecutionId;
        var consumerArtifactId = consumer.Preview.ArtifactId;
        var referenceArtifactId = reference.Preview.ArtifactId;
        var missing = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            AuxiliaryRunManifestEvidenceFixture.CreateAsync(fixture.StorageRoot, consumerArtifactId, Guid.Empty)).ConfigureAwait(false);
        StringAssert.Contains(missing.Message, $"Reference artifact {Guid.Empty:D} must be retained", StringComparison.Ordinal);
        var auxiliary = await AuxiliaryRunManifestEvidenceFixture.CreateAsync(
            fixture.StorageRoot, consumerArtifactId, referenceArtifactId).ConfigureAwait(false);
        await using var auxiliaryLifetime = auxiliary.ConfigureAwait(false);
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        var path = $"/api/v1/operations/processing-graphs/executions/{executionId:D}";
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
