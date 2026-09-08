using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Asp.Versioning.ApiExplorer;
using HVO.SkyMonitor.AgentCore;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ApiVersioningContractTests
{
    [TestMethod]
    public async Task CameraAgentRoutesAndDescribesVersionedFramesApiAsync()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<CaptureScheduleBoundary>("{}"));
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();

        using var supported = await client.GetAsync(
            new Uri("/api/v1.0/frames/latest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, supported.StatusCode);

        using var unsupported = await client.GetAsync(
            new Uri("/api/v2.0/frames/latest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, unsupported.StatusCode);

        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var versions = scope.ServiceProvider.GetRequiredService<IApiVersionDescriptionProvider>();
        var version = versions.ApiVersionDescriptions.Single();
        Assert.AreEqual("v1", version.GroupName);
        Assert.AreEqual("1.0", version.ApiVersion.ToString());

        var descriptions = scope.ServiceProvider
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(static group => group.Items);
        var frames = descriptions.Single(static description =>
            description.RelativePath == "api/v1/frames/latest");
        Assert.AreEqual("v1", frames.GroupName);
        Assert.AreEqual(HttpMethod.Get.Method, frames.HttpMethod);

        await AssertOpenApiPathAsync(client, "/api/v1/frames/latest").ConfigureAwait(false);
    }

    private static async Task AssertOpenApiPathAsync(HttpClient client, string expectedPath)
    {
        using var response = await client.GetAsync(
            new Uri("/openapi/v1.json", UriKind.Relative)).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, content);
        using var document = JsonDocument.Parse(content);
        var paths = document.RootElement.GetProperty("paths");

        Assert.IsTrue(
            paths.TryGetProperty(expectedPath, out _),
            $"OpenAPI did not contain '{expectedPath}'. Actual paths: {string.Join(", ", paths.EnumerateObject().Select(static path => path.Name))}");
        Assert.IsFalse(paths.EnumerateObject().Any(static path =>
            path.Name.Contains("{version}", StringComparison.Ordinal)));
    }
}
