using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OrbitalElementsHealthCheckTests
{
    [TestMethod]
    public async Task HealthCheckReportsDisabledRefreshAsHealthyWithOnlyAllowlistedDataAsync()
    {
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var orbital = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "orbital-elements");
        Assert.AreEqual("Healthy", orbital.GetProperty("status").GetString());
        var data = orbital.GetProperty("data");
        Assert.AreEqual("Disabled", data.GetProperty("Availability").GetString());
        Assert.AreEqual("orbital.disabled", data.GetProperty("Reason").GetString());
        Assert.AreEqual("Configure", data.GetProperty("RecoveryAction").GetString());
        Assert.AreEqual(3, data.EnumerateObject().Count());
        Assert.IsFalse(orbital.GetRawText().Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
    }
}
