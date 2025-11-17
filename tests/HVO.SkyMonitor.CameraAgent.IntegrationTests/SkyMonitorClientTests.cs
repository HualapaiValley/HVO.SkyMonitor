using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
public sealed class SkyMonitorClientTests
{
    [TestMethod]
    public async Task SkyMonitorApiClientCanAccessProtectedStatusAsync()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var apiClient = clientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);

        var response = await apiClient.GetAsync(new Uri("/api/v1.0/status/protected", UriKind.Relative))
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task SkyMonitorApiClientReusesAccessTokenAcrossRequestsAsync()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var apiClient = clientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);

        var protectedEndpoint = new Uri("/api/v1.0/status/protected", UriKind.Relative);
        for (var i = 0; i < 2; i++)
        {
            var response = await apiClient.GetAsync(protectedEndpoint).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }
}
