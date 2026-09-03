using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ApiVersioningContractTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task LogicHostRoutesAndDescribesVersionedStatusApiAsync()
    {
        using var client = HVO.SkyMonitor.IntegrationTests.AssemblyHooks.Fixture.Factory.CreateClient();

        using var supported = await client.GetAsync(
            new Uri("/api/v1.0/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, supported.StatusCode);
        CollectionAssert.Contains(supported.Headers.GetValues("api-supported-versions").ToArray(), "1.0");

        using var unsupported = await client.GetAsync(
            new Uri("/api/v2.0/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, unsupported.StatusCode);

        await AssertOpenApiPathAsync(client, "/api/v1/Status").ConfigureAwait(false);
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
