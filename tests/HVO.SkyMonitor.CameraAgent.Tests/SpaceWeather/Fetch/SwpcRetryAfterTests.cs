using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcRetryAfterTests
{
    [TestMethod]
    [DataRow("s13-outage-backoff-halts.json", 4)]
    [DataRow("s23-retry-after-forms.json", 14)]
    public void FixtureRateLimitsMatchEveryExpectedDelay(string file, int expectedCases)
    {
        using var scenario = SwpcTestData.Scenario(file);
        var cases = 0;
        foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
        {
            if (!step.TryGetProperty("response", out var response) || !response.TryGetProperty("status", out var status)
                || status.GetInt32() != 429) continue;
            var expected = step.GetProperty("expect");
            var product = expected.GetProperty("product");
            var outcome = expected.GetProperty("outcome").GetString();
            var key = outcome == "rate-limited" ? "nextAttemptNotBeforeUtc" : "retryNotBeforeUtc";
            if (!product.TryGetProperty(key, out var next)) continue;
            var now = SwpcTestData.Time(step.GetProperty("atUtc").GetString()!);
            Assert.AreEqual(SwpcTestData.Time(next.GetString()!) - now,
                SwpcRetryAfter.Resolve(SwpcTestData.Text(response, "retryAfter"), now), $"{file} step {step.GetProperty("step")}");
            cases++;
        }
        Assert.AreEqual(expectedCases, cases);
    }

    [TestMethod]
    [DataRow(null, 1800)]
    [DataRow("", 1800)]
    [DataRow("0", 60)]
    [DataRow("59", 60)]
    [DataRow("60", 60)]
    [DataRow("21600", 21600)]
    [DataRow("21601", 21600)]
    [DataRow("999999999", 21600)]
    [DataRow("1234567890", 1800)]
    [DataRow("+5", 1800)]
    [DataRow("-5", 1800)]
    [DataRow(" 5", 1800)]
    [DataRow("5 ", 1800)]
    [DataRow("5.0", 1800)]
    [DataRow("0x10", 1800)]
    [DataRow("５", 1800)]
    [DataRow("٥", 1800)]
    [DataRow("000000061", 61)]
    public void DeltaSecondsAreOnlyOneToNineAsciiDigits(string? raw, int seconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), SwpcRetryAfter.Resolve(raw, SwpcTestData.Now));

    [TestMethod]
    [DataRow("Tue, 03 Feb 2026 00:00:00 GMT", 60)]
    [DataRow("Tue, 03 Feb 2026 01:00:00 GMT", 60)]
    [DataRow("Tue, 03 Feb 2026 02:00:00 GMT", 3600)]
    [DataRow("Fri, 13 Feb 2026 01:00:00 GMT", 21600)]
    [DataRow("Mon, 01 Jan 0000 00:00:00 GMT", 1800)]
    [DataRow("Mon, 01 Jan 0001 00:00:00 GMT", 60)]
    [DataRow("Fri, 31 Dec 9999 23:59:59 GMT", 21600)]
    [DataRow("Mon, 03 Feb 2026 02:00:00 GMT", 1800)]
    [DataRow("tue, 03 Feb 2026 02:00:00 GMT", 1800)]
    [DataRow("Tue, 03 feb 2026 02:00:00 GMT", 1800)]
    [DataRow("Tue, 03 Feb 2026 02:00:00 UTC", 1800)]
    [DataRow("Tue, 03 Feb 2026 02:00:00 GMT ", 1800)]
    [DataRow("Tuesday, 03-Feb-26 02:00:00 GMT", 1800)]
    [DataRow("Tue Feb  3 02:00:00 2026", 1800)]
    [DataRow("Tue, 03 Feb 2026 24:00:00 GMT", 1800)]
    [DataRow("Tue, 03 Feb 2026 02:60:00 GMT", 1800)]
    [DataRow("Tue, 03 Feb 2026 02:00:60 GMT", 1800)]
    [DataRow("Tue, ０３ Feb 2026 02:00:00 GMT", 1800)]
    public void OnlyExactCalendarDatesWithMatchingWeekdaysAreAccepted(string raw, int seconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), SwpcRetryAfter.Resolve(raw, SwpcTestData.Now));

    [TestMethod]
    public void DateDelaysTruncateSubsecondsAndHandleExtremeClocks()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3599), SwpcRetryAfter.Resolve("Tue, 03 Feb 2026 02:00:00 GMT",
            SwpcTestData.Now.AddTicks(1)));
        Assert.AreEqual(TimeSpan.FromSeconds(3599), SwpcRetryAfter.Resolve("Tue, 03 Feb 2026 02:00:00 GMT",
            SwpcTestData.Now.AddMilliseconds(999)));
        Assert.AreEqual(TimeSpan.FromSeconds(21600), SwpcRetryAfter.Resolve("Fri, 31 Dec 9999 23:59:59 GMT", DateTimeOffset.MinValue));
        Assert.AreEqual(TimeSpan.FromSeconds(60), SwpcRetryAfter.Resolve("Mon, 01 Jan 0001 00:00:00 GMT", DateTimeOffset.MaxValue));
        Assert.AreEqual(TimeSpan.FromSeconds(3600), SwpcRetryAfter.Resolve("Tue, 03 Feb 2026 02:00:00 GMT",
            SwpcTestData.Now.ToOffset(TimeSpan.FromHours(-7))));
    }

    [TestMethod]
    public void LeapDaysAndAbsurdInputsAreBounded()
    {
        var now = SwpcTestData.Time("2028-02-29T00:00:00Z");
        Assert.AreEqual(TimeSpan.FromHours(1), SwpcRetryAfter.Resolve("Tue, 29 Feb 2028 01:00:00 GMT", now));
        Assert.AreEqual(SwpcSource.RateLimitedDefault, SwpcRetryAfter.Resolve("Sun, 29 Feb 2026 01:00:00 GMT", now));
        Assert.AreEqual(SwpcSource.RateLimitedDefault, SwpcRetryAfter.Resolve(new string('9', 10_000), now));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.AreEqual(TimeSpan.FromHours(1), SwpcRetryAfter.Resolve("Tue, 29 Feb 2028 01:00:00 GMT", now));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
