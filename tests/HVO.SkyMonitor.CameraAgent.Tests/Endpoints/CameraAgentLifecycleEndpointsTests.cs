using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentLifecycleEndpointsTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("wrong-token", false)]
    [DataRow("exact-token", true)]
    public void IsAuthorized_RequiresExactConfiguredToken(string? supplied, bool expected)
    {
        var context = new DefaultHttpContext();
        if (supplied is not null) context.Request.Headers["X-HVO-Installation-Token"] = supplied;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LifecycleControl:Token"] = "exact-token" })
            .Build();

        Assert.AreEqual(expected, CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration));
    }
}
