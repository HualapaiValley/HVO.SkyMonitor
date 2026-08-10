using System;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
[TestCategory("Unit")]
public class SkyMonitorClientOptionsTests
{
    [TestMethod]
    public void ResolveBaseUriReturnsParsedUriWhenValid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("https://example.com:5174/api", UriKind.Absolute)
        };

        var uri = options.ResolveBaseUri();

        Assert.AreEqual("https://example.com:5174/api", uri.ToString());
    }

    [TestMethod]
    public void TryResolveBaseUriReturnsFalseWhenInvalid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("relative", UriKind.Relative)
        };

        var success = options.TryResolveBaseUri(out var uri);

        Assert.IsFalse(success);
        Assert.IsNull(uri);
    }

    [TestMethod]
    public void PublicBaseUriIsRequiredAndDoesNotFallBackToTransportUrl()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("http://logichost:8080", UriKind.Absolute)
        };

        Assert.IsFalse(options.TryResolvePublicBaseUri(out var missing));
        Assert.IsNull(missing);

        options.PublicBaseUrl = new Uri("https://logic.example", UriKind.Absolute);
        Assert.AreEqual("https://logic.example/", options.ResolvePublicBaseUri().ToString());
    }
}
