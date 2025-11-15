using HVO.SkyMonitor.CameraAgent.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public class SkyMonitorClientOptionsTests
{
    [TestMethod]
    public void ResolveBaseUri_ReturnsParsedUri_WhenValid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = "https://example.com:5174/api"
        };

        var uri = options.ResolveBaseUri();

        Assert.AreEqual("https://example.com:5174/api", uri.ToString());
    }

    [TestMethod]
    public void TryResolveBaseUri_ReturnsFalse_WhenInvalid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = "not-a-url"
        };

        var success = options.TryResolveBaseUri(out var uri);

        Assert.IsFalse(success);
        Assert.IsNull(uri);
    }
}