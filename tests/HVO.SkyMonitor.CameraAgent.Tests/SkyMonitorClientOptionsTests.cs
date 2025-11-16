using System;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
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
}