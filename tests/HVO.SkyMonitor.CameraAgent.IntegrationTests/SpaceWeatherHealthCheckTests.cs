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
        var data = check.GetProperty("data");
        Assert.AreEqual("space-weather.disabled", data.GetProperty("Reason").GetString());
        Assert.AreEqual("Disabled", data.GetProperty("Availability").GetString());
        Assert.AreEqual(2, data.EnumerateObject().Count());
        Assert.DoesNotContain(AssemblyHooks.Fixture.StorageRoot, check.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
