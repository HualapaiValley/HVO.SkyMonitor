using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcSourceTests
{
    [TestMethod]
    [DataRow(SwpcProduct.Alerts, "/products/alerts.json", "application/json", 120, 3600, 1048576)]
    [DataRow(SwpcProduct.Scales, "/products/noaa-scales.json", "application/json", 300, 10800, 65536)]
    [DataRow(SwpcProduct.KpForecast, "/products/noaa-planetary-k-index-forecast.json", "application/json", 900, 21600, 262144)]
    [DataRow(SwpcProduct.ThreeDayForecast, "/text/3-day-forecast.txt", "text/plain", 1800, 93600, 65536)]
    public void ProductConstantsMatchTheContract(SwpcProduct product, string path, string media, int interval, int outdated, int cap)
    {
        Assert.AreEqual("https://services.swpc.noaa.gov" + path, SwpcSource.RequestUri(product).AbsoluteUri);
        Assert.AreEqual(media, SwpcSource.MediaType(product));
        Assert.AreEqual(TimeSpan.FromSeconds(interval), SwpcSource.BaseInterval(product));
        Assert.AreEqual(TimeSpan.FromSeconds(outdated), SwpcSource.OutdatedAfter(product));
        Assert.AreEqual(cap, SwpcSource.MaximumDecodedBytes(product));
        Assert.AreEqual(SwpcLimits.BodyBytes(product), SwpcSource.MaximumDecodedBytes(product));
    }

    [TestMethod]
    [DataRow(4)] // S1 has no OVATION enum member: every non-core value is unapproved.
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    public void NonCoreProductsAreRejected(int value)
    {
        var product = (SwpcProduct)value;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcSource.RequestUri(product));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcSource.MediaType(product));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcSource.BaseInterval(product));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcSource.OutdatedAfter(product));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcSource.MaximumDecodedBytes(product));
    }

    [TestMethod]
    public void PolicyConstantsMatchTheModel()
    {
        Assert.AreEqual("hvo-swpc", typeof(SwpcSource).GetField(nameof(SwpcSource.HttpClientName))!.GetRawConstantValue());
        Assert.AreEqual("https://services.swpc.noaa.gov/", SwpcSource.BaseUri.AbsoluteUri);
        Assert.AreEqual(TimeSpan.FromSeconds(60), SwpcSource.MinimumAttemptSpacing);
        Assert.AreEqual(TimeSpan.FromSeconds(1800), SwpcSource.TransientBackoffCap);
        Assert.AreEqual(TimeSpan.FromSeconds(1800), SwpcSource.RateLimitedDefault);
        Assert.AreEqual(TimeSpan.FromSeconds(60), SwpcSource.RetryAfterMinimum);
        Assert.AreEqual(TimeSpan.FromSeconds(21600), SwpcSource.RetryAfterMaximum);
        Assert.AreEqual(3, typeof(SwpcSource).GetField(nameof(SwpcSource.RateLimitedHaltCount))!.GetRawConstantValue());
        Assert.AreEqual(3, typeof(SwpcSource).GetField(nameof(SwpcSource.RejectedAttentionCount))!.GetRawConstantValue());
        Assert.AreEqual(3, typeof(SwpcSource).GetField(nameof(SwpcSource.FreshMultiplier))!.GetRawConstantValue());
        Assert.AreEqual(TimeSpan.FromSeconds(30), SwpcSource.RequestTimeout);
    }

    [TestMethod]
    public void PrimaryHandlerHasEveryRequiredBoundary()
    {
        using var handler = SwpcSource.CreatePrimaryHandler();
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsFalse(handler.UseCookies);
        Assert.AreEqual(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.IsFalse(handler.PreAuthenticate);
        Assert.IsNull(handler.Credentials);
        Assert.IsNull(handler.ActivityHeadersPropagator);
        Assert.AreEqual(32, handler.MaxResponseHeadersLength);
        Assert.AreEqual(0, handler.MaxResponseDrainSize);
        Assert.AreEqual(1, handler.MaxConnectionsPerServer);
        Assert.AreEqual(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(1), handler.PooledConnectionIdleTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(128)]
    public void ValidatorBoundsKeepExactValuesAndDropInvalidOnes(int bound)
    {
        var exact = new string('x', bound);
        Assert.AreEqual(exact, SwpcSource.SanitizeValidator(exact, bound));
        foreach (var invalid in new[] { null, "", " ", new string('x', bound + 1), "x\r\ny", "x\ty", "x\0y", "café", "x\u007fy" })
            Assert.IsNull(SwpcSource.SanitizeValidator(invalid, bound));
        Assert.AreEqual(" W/\"etag\" ", SwpcSource.SanitizeValidator(" W/\"etag\" ", bound));
    }

    [TestMethod]
    public void SingleHeaderValuePreservesCommaAndRejectsRepeatedFields()
    {
        using var response = new HttpResponseMessage();
        Assert.IsNull(SwpcSource.SingleHeaderValue(response.Headers, "Retry-After"));
        response.Headers.TryAddWithoutValidation("Retry-After", "Tue, 03 Feb 2026 01:02:03 GMT");
        Assert.AreEqual("Tue, 03 Feb 2026 01:02:03 GMT", SwpcSource.SingleHeaderValue(response.Headers, "Retry-After"));
        response.Headers.TryAddWithoutValidation("Retry-After", "60");
        Assert.IsNull(SwpcSource.SingleHeaderValue(response.Headers, "Retry-After"));
    }

    [TestMethod]
    [DataRow("1.2.3+build", "1.2.3")]
    [DataRow("1.2.3-dev", "1.2.3-dev")]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("+metadata", null)]
    [DataRow("1.2 3", null)]
    [DataRow("1.2\t3", null)]
    [DataRow("1.2/3", null)]
    [DataRow("1.2(comment)", null)]
    [DataRow("café", null)]
    public void UserAgentUsesProjectUrlAndAValidVersion(string? version, string? expectedVersion)
    {
        var expected = "HVO.SkyMonitor" + (expectedVersion is null ? "" : "/" + expectedVersion)
            + " (+https://github.com/HualapaiValley/HVO.SkyMonitor)";
        Assert.AreEqual(expected, SwpcSource.CreateUserAgent(version));
        Assert.IsTrue(SwpcClient.IsValidUserAgent(SwpcSource.UserAgent));
        Assert.AreEqual(SwpcSource.CreateUserAgent(null), SwpcSource.CreateUserAgent(new string('1', 300)));
    }
}
