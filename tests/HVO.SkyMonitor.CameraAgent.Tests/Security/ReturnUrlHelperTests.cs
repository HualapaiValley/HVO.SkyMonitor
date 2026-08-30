using HVO.SkyMonitor.CameraAgent.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Security;

[TestClass]
[TestCategory("Unit")]
public class ReturnUrlHelperTests
{
    [TestMethod]
    [DataRow(null, "/")]
    [DataRow("", "/")]
    [DataRow("weather", "/weather")]
    [DataRow("/weather", "/weather")]
    [DataRow("weather/today", "/weather/today")]
    [DataRow("/weather/today?unit=metric", "/weather/today?unit=metric")]
    [DataRow("/gallery?recipe=calibration..v2", "/gallery?recipe=calibration..v2")]
    public void NormalizeReturnUrl_WhenLocal_ReturnsSanitizedValue(string? input, string expected)
    {
        var result = ReturnUrlHelper.NormalizeReturnUrl(input);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [DataRow("//malicious")]
    [DataRow("https://evil.example")]
    [DataRow("ftp://evil")]
    [DataRow("../secret")]
    [DataRow("\\\\evil")]
    [DataRow("not a url\u0007")]
    public void NormalizeReturnUrl_WhenInvalid_ReturnsRoot(string input)
    {
        var result = ReturnUrlHelper.NormalizeReturnUrl(input);
        Assert.AreEqual("/", result);
    }

    [TestMethod]
    public void BuildLoginPath_AppendsReturnUrlQuery()
    {
        var result = ReturnUrlHelper.BuildLoginPath("/weather");
        Assert.AreEqual("/Account/Login?returnUrl=%2Fweather", result);
    }

}
