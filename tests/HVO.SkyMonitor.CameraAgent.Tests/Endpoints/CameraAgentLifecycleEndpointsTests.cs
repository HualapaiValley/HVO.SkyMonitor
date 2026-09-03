using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;

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

    [TestMethod]
    [DataRow(true, "exact-token", true)]
    [DataRow(false, "exact-token", false)]
    [DataRow(true, "wrong-token", false)]
    public void OwnerRecovery_RequiresExactUnixSocketAndLifecycleCredential(
        bool localSocket,
        string suppliedToken,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-HVO-Installation-Token"] = suppliedToken;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LifecycleControl:Token"] = "exact-token" })
            .Build();
        const string socketPath = "/tmp/hvo-owner-recovery-test.sock";
        var transport = new OwnerRecoveryTransport(socketPath);
        EndPoint endpoint = localSocket
            ? new UnixDomainSocketEndPoint(socketPath)
            : new IPEndPoint(IPAddress.Loopback, 5130);

        Assert.AreEqual(
            expected,
            IdentityComponentsEndpointRouteBuilderExtensions.IsRecoveryAuthorized(
                context,
                configuration,
                transport,
                endpoint));
    }
}
