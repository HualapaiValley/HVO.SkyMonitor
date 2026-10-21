using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SpaceWeatherHealthCheckTests
{
    [TestMethod]
    public async Task TheRealHostReportsDisabledSpaceWeatherAsHealthy()
    {
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var check = json.RootElement.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("name").GetString() == "space-weather");
        Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
        Assert.AreEqual("space-weather.disabled", check.GetProperty("data").GetProperty("Reason").GetString());
        Assert.DoesNotContain(AssemblyHooks.Fixture.StorageRoot, check.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
