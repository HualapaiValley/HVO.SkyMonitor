using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class SunriseReportingCalendarTests
{
    [TestMethod]
    public void AcceptedStartingSunriseDate_UsesActualEventsAndOwnsExactBoundaryOnce()
    {
        var calendar = new SunriseReportingCalendar(Site());
        var date = new DateOnly(2026, 10, 12);
        var period = calendar.Resolve(date).Period!;
        var next = calendar.Resolve(date.AddDays(1)).Period!;

        Assert.IsTrue(period.IsValid());
        Assert.AreEqual(date, period.ReportDate);
        Assert.AreEqual(period.EndUtc, next.StartUtc);
        Assert.AreNotEqual(TimeSpan.FromDays(1), period.Duration, "Seasonal solar drift must not be replaced by24hours.");
        Assert.AreEqual(date, calendar.Resolve(period.StartUtc).Period!.ReportDate);
        Assert.AreEqual(date.AddDays(-1), calendar.Resolve(period.StartUtc.AddTicks(-1)).Period!.ReportDate);
        Assert.AreEqual(date.AddDays(1), calendar.Resolve(period.EndUtc).Period!.ReportDate);
        Assert.IsFalse(period.Contains(period.EndUtc));
        Assert.AreEqual(date, calendar.Resolve(period.StartUtc.AddHours(2)).Period!.ReportDate,
            "The morning between sunrise and noon belongs to the new period.");
        Assert.AreEqual(date, calendar.Resolve(period.EndUtc.AddHours(-7)).Period!.ReportDate,
            "The following civil morning remains in the starting-sunrise report.");
    }

    [TestMethod]
    [DataRow("America/Denver", 2026, 3, 7)]
    [DataRow("America/Denver", 2026, 10, 31)]
    [DataRow("America/Phoenix", 2026, 12, 31)]
    [DataRow("America/Phoenix", 2026, 1, 31)]
    [DataRow("America/Phoenix", 2028, 2, 28)]
    public void FoldGapMonthAndYear_KeepSolarBoundariesAndUniqueTimestampAssignment(
        string zone, int year, int month, int day)
    {
        var calendar = new SunriseReportingCalendar(Site(zone));
        var date = new DateOnly(year, month, day);
        var period = calendar.Resolve(date).Period!;
        var next = calendar.Resolve(date.AddDays(1)).Period!;
        Assert.AreEqual(period.EndUtc, next.StartUtc);
        Assert.AreEqual(date.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.EndUtc, calendar.TimeZone).DateTime));
        for (var utc = period.StartUtc; utc < next.EndUtc; utc = utc.AddMinutes(15))
        {
            var assigned = calendar.Resolve(utc).Period!;
            Assert.IsTrue(assigned.Contains(utc));
            Assert.AreEqual(1, new[] { period, next }.Count(window => window.Contains(utc)));
        }
    }

    [TestMethod]
    [DataRow(2026, 6, 21)]
    [DataRow(2026, 12, 21)]
    public void PolarNoSunrise_IsExplicitWithoutAFixedOrNoonFallback(int year, int month, int day)
    {
        var calendar = new SunriseReportingCalendar(Site("Arctic/Longyearbyen", latitude: 78.2232, longitude: 15.6469));
        var result = calendar.Resolve(new DateOnly(year, month, day));
        Assert.IsFalse(result.Available);
        Assert.IsNull(result.Period);
        Assert.AreEqual(SunriseReportingCalendar.MissingStartSunrise, result.UnavailableReasonCode);
    }

    [TestMethod]
    public void FractionalSolarEndpoints_QueryBoundsAgreeWithEveryStoredMillisecond()
    {
        var calculator = new FractionalSolar();
        var calendar = new SunriseReportingCalendar(Site("Etc/UTC"), calculator);
        var period = calendar.Resolve(new DateOnly(2026, 10, 12)).Period!;
        var next = calendar.Resolve(period.ReportDate.AddDays(1)).Period!;
        Assert.AreEqual(period.EndUnixMillisecondsExclusive, next.StartUnixMillisecondsInclusive);
        foreach (var boundary in new[] { period.StartUtc, period.EndUtc })
        {
            for (var delta = -2; delta <= 2; delta++)
            {
                var milliseconds = boundary.ToUnixTimeMilliseconds() + delta;
                var source = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                Assert.AreEqual(period.Contains(source), milliseconds >= period.StartUnixMillisecondsInclusive &&
                    milliseconds < period.EndUnixMillisecondsExclusive);
            }
        }
        Assert.IsTrue(period.StartUtc.Ticks % TimeSpan.TicksPerMillisecond != 0,
            "The retained solar boundary must preserve its original precision.");
    }

    [TestMethod]
    public void SavedPeriod_RoundTripsAndRemainsFixedAfterSiteChangesOrLateRetry()
    {
        var date = new DateOnly(2026, 10, 12);
        var original = new SunriseReportingCalendar(Site()).Resolve(date).Period!;
        var retained = JsonSerializer.Deserialize<SunriseReportingPeriod>(JsonSerializer.Serialize(original))!;
        var moved = new SunriseReportingCalendar(Site(latitude: 40, longitude: -105, version: 2)).Resolve(date).Period!;
        Assert.AreEqual(original, retained);
        Assert.IsTrue(retained.IsValid());
        Assert.AreNotEqual(original.IdentitySha256, moved.IdentitySha256);
        Assert.AreNotEqual(original.StartUtc, moved.StartUtc);
        Assert.IsFalse((retained with { EndUtc = retained.EndUtc.AddMinutes(1) }).IsValid());
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(original))!.AsObject();
        malformed.Remove(nameof(SunriseReportingPeriod.IdentitySha256));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SunriseReportingPeriod>(malformed.ToJsonString()));
        malformed[nameof(SunriseReportingPeriod.IdentitySha256)] = original.IdentitySha256;
        malformed["futureWindow"] = true;
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SunriseReportingPeriod>(malformed.ToJsonString()));
        Assert.AreEqual(original.EndUtc.AddMinutes(10), retained.BindFinality(TimeSpan.FromMinutes(10)).EarliestFinalUtc);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => retained.BindFinality(TimeSpan.FromSeconds(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => retained.BindFinality(TimeSpan.FromDays(1) + TimeSpan.FromTicks(1)));
        Assert.ThrowsExactly<ArgumentException>(() => (retained with { EndUtc = retained.EndUtc.AddMinutes(1) })
            .BindFinality(TimeSpan.Zero));
    }

    [TestMethod]
    public void RetainedFinality_KeepsOriginalEligibilityAcrossRetryAndRejectsChangedMetadata()
    {
        var period = new SunriseReportingCalendar(Site()).Resolve(new DateOnly(2026, 10, 12)).Period!;
        var original = period.BindFinality(TimeSpan.FromMinutes(10));
        var retained = JsonSerializer.Deserialize<SunriseReportingFinality>(JsonSerializer.Serialize(original))!;
        var changedConfiguration = period.BindFinality(TimeSpan.Zero);

        Assert.AreEqual(original, retained);
        Assert.IsTrue(retained.IsValid());
        Assert.AreEqual(period.EndUtc.AddMinutes(10), retained.EarliestFinalUtc);
        Assert.IsFalse(retained.IsEligibleForFinal(period.EndUtc));
        Assert.IsFalse(retained.IsEligibleForFinal(retained.EarliestFinalUtc.AddTicks(-1)));
        Assert.IsTrue(retained.IsEligibleForFinal(retained.EarliestFinalUtc));
        Assert.AreNotEqual(retained.IdentitySha256, changedConfiguration.IdentitySha256);
        Assert.IsTrue(changedConfiguration.IsEligibleForFinal(period.EndUtc));
        Assert.IsFalse((retained with { ProcessingSettleAllowance = TimeSpan.Zero, EarliestFinalUtc = period.EndUtc })
            .IsValid(), "Changing current settings cannot reuse the retained finality identity.");
        Assert.IsFalse((retained with { EarliestFinalUtc = retained.EarliestFinalUtc.AddTicks(1) }).IsValid());
        Assert.IsFalse((retained with { Period = period with { EndUtc = period.EndUtc.AddSeconds(1) } }).IsValid());
        Assert.IsFalse((retained with { ContractVersion = "future" }).IsValid());
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(original))!.AsObject();
        malformed.Remove(nameof(SunriseReportingFinality.EarliestFinalUtc));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SunriseReportingFinality>(malformed.ToJsonString()));
        malformed[nameof(SunriseReportingFinality.EarliestFinalUtc)] = original.EarliestFinalUtc;
        malformed["currentSettleAllowance"] = "00:00:00";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SunriseReportingFinality>(malformed.ToJsonString()));
    }

    [TestMethod]
    public void MissingNextSunrise_DoesNotReturnAFullPeriodFromAnAvailableStart()
    {
        var calculator = new FractionalSolar { MissingDate = new DateOnly(2026, 10, 13) };
        var result = new SunriseReportingCalendar(Site("Etc/UTC"), calculator).Resolve(new DateOnly(2026, 10, 12));
        Assert.IsFalse(result.Available);
        Assert.AreEqual(SunriseReportingCalendar.MissingEndSunrise, result.UnavailableReasonCode);
    }

    [TestMethod]
    [DataRow("Asia/Tokyo", 1, 1, 1)]
    [DataRow("Etc/UTC", 9999, 12, 30)]
    [DataRow("Etc/UTC", 9999, 12, 31)]
    public void UnrepresentableCivilBoundaries_AreExplicitlyUnavailable(string zone, int year, int month, int day)
    {
        var result = new SunriseReportingCalendar(Site(zone), new FractionalSolar())
            .Resolve(new DateOnly(year, month, day));
        Assert.IsNull(result.Period);
        Assert.AreEqual(SunriseReportingCalendar.MissingCivilDate, result.UnavailableReasonCode);
    }

    [TestMethod]
    public void SkippedCivilDate_DoesNotInventAStartingSunrise()
    {
        var calendar = new SunriseReportingCalendar(Site("Pacific/Apia", latitude: -13.83, longitude: -171.77));
        var result = calendar.Resolve(new DateOnly(2011, 12, 30));
        Assert.IsNull(result.Period);
        Assert.IsNotNull(result.UnavailableReasonCode);
    }

    [TestMethod]
    public void AdjacentQueries_ReuseTheExactEventAndBoundCalendarCache()
    {
        var calculator = new FractionalSolar();
        var calendar = new SunriseReportingCalendar(Site("Etc/UTC"), calculator);
        var date = new DateOnly(2026, 1, 1);
        for (var day = 0; day < SunriseReportingCalendar.MaximumCachedDates + 3; day++)
        {
            var period = calendar.Resolve(date.AddDays(day)).Period!;
            var again = calendar.Resolve(date.AddDays(day)).Period!;
            Assert.AreSame(period, again, "Immutable resolved periods are reused without rehashing their site and boundaries.");
        }
        Assert.AreEqual(SunriseReportingCalendar.MaximumCachedDates + 4, calculator.Calls);
        calendar.Resolve(date);
        Assert.AreEqual(SunriseReportingCalendar.MaximumCachedDates + 6, calculator.Calls,
            "Old cache entries are evicted rather than retained without bound.");
    }

    private static DeploymentLocationSnapshot Site(string zone = "America/Phoenix", double latitude = 35.347,
        double longitude = -113.878, long version = 1)
        => DeploymentLocationSnapshot.Create("reporting-site", version, "test", null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, latitude, longitude, 1000, zone);

    private sealed class FractionalSolar : ISolarEventCalculator
    {
        public int Calls { get; private set; }

        public DateOnly? MissingDate { get; init; }

        public SolarEventResult Find(SolarEventKind kind, DateTimeOffset start, DateTimeOffset end,
            double latitudeDegrees, double longitudeDegrees, double elevationMeters)
        {
            Calls++;
            return new(kind, DateOnly.FromDateTime(start.UtcDateTime) == MissingDate ? null : start.AddHours(6).AddTicks(1234),
                "fractional-solar-fixture-v1");
        }
    }
}
