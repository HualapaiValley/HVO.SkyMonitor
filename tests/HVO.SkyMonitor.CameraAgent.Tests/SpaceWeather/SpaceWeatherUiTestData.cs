using System.Collections.Immutable;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

/// <summary>Synthetic SWPC records, store views and statuses for the operator projection; no clock, file or network.</summary>
internal static class SpaceWeatherUiTestData
{
    internal static DateTimeOffset Now => new(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The Hualapai Valley Observatory site, whose geomagnetic latitude is about 42.2°.</summary>
    internal static DeploymentLocationSnapshot Site(string timeZoneId = "America/Phoenix", double latitude = 35.5599378,
        double longitude = -113.9119818)
        => DeploymentLocationSnapshot.Create("hvo", 1, "test", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null,
            latitude, longitude, 1100, timeZoneId);

    internal static SwpcMessageRecord Message(
        string code,
        int serial,
        DateTimeOffset issued,
        SpaceWeatherFamily family,
        string? level = null,
        double? stated = null,
        IReadOnlyDictionary<string, DateTimeOffset?>? times = null,
        string? modifier = null,
        int? link = null,
        bool corrected = false,
        ImmutableArray<SwpcWatchDay>? days = null)
    {
        var header = new SwpcMessageHeader("synthetic", null, modifier, corrected,
            link is { } target ? new SwpcMessageLink("Serial", target) : null, family, level, stated, days,
            (times ?? new Dictionary<string, DateTimeOffset?>()).ToImmutableDictionary(StringComparer.Ordinal), null, null);
        return new SwpcMessageRecord(code + "/" + serial.ToString(CultureInfo.InvariantCulture), code, serial, issued,
            issued.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), new string('A', 64), "{}", header, "TEST",
            "synthetic");
    }

    /// <summary>A warning valid over [<paramref name="from"/>, <paramref name="until"/>), issued at its start by default.</summary>
    internal static SwpcMessageRecord Warning(
        int serial,
        DateTimeOffset from,
        DateTimeOffset until,
        string code = "WARK05",
        string? level = "G1",
        double? stated = null,
        DateTimeOffset? issued = null,
        int? link = null,
        string? modifier = null)
        => Message(code, serial, issued ?? from, SpaceWeatherFamily.Warning, level, stated,
            new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal) { ["Valid From"] = from, ["Valid To"] = until },
            modifier, link);

    /// <summary>
    /// A store view over <paramref name="messages"/> in ingest order. Each version is retrieved one minute after it was
    /// issued unless <paramref name="retrieved"/> says otherwise; a null retrieval time leaves the version unjoinable.
    /// </summary>
    internal static SwpcStoreView View(
        IReadOnlyList<SwpcMessageRecord> messages,
        Func<SwpcMessageRecord, DateTimeOffset?>? retrieved = null,
        IReadOnlyDictionary<SwpcMessageNode, SwpcAcknowledgement>? acknowledgements = null,
        SwpcFreshness alerts = SwpcFreshness.Fresh,
        long revision = 7,
        DateTimeOffset? recordDrift = null)
    {
        var versions = messages.Select(static (message, index) => new SwpcMessageVersion(message, index + 1)).ToArray();
        var stored = versions
            .Select(version => (version, at: retrieved is null ? version.Message.IssueMinuteUtc.AddMinutes(1) : retrieved(version.Message)))
            .Where(static pair => pair.at is not null)
            .Select(static pair => new SwpcStoredMessage(pair.version, new string('B', 64), pair.at!.Value))
            .ToImmutableArray();
        var groups = SwpcGroupBuilder.Build(versions);
        var notices = new SwpcNoticeData(stored, groups,
            groups.Where(static g => g.Orphan).Select(static g => g.Identity).ToImmutableHashSet(StringComparer.Ordinal),
            ImmutableDictionary<SwpcMessageNode, int>.Empty,
            (acknowledgements ?? new Dictionary<SwpcMessageNode, SwpcAcknowledgement>()).ToImmutableDictionary());
        var product = new SwpcStoredProduct(null, new SwpcValidators(null, null), Now.AddMinutes(-1), Now.AddMinutes(-1),
            SwpcFetchOutcome.Updated, new SwpcFetchCheckpoint(null, null, false, 0, 0, 0, recordDrift), null, alerts);
        return new SwpcStoreView(revision, ImmutableDictionary<SwpcProduct, SwpcStoredProduct>.Empty.Add(SwpcProduct.Alerts, product),
            null, notices);
    }

    internal static SpaceWeatherOperations.ProductStatus Product(
        string product = "alerts",
        bool configured = true,
        bool succeeded = true,
        string freshness = "fresh",
        int rejected = 0,
        SpaceWeatherOperations.HaltStatus? halt = null)
        => new(product, configured, succeeded ? "updated" : null, freshness, succeeded ? Now.AddMinutes(-1) : null, null,
            Now.AddMinutes(2), null, 0, 0, rejected, halt);

    internal static SpaceWeatherOperations.Status Status(
        bool enabled = true,
        long revision = 7,
        SpaceWeatherOperations.HaltStatus? providerHalt = null,
        bool definitionExists = true,
        bool definitionEnabled = true,
        IReadOnlyList<SpaceWeatherOperations.ProductStatus>? products = null)
        => new(Now, enabled, true, revision, products ?? [Product()], providerHalt, definitionExists, definitionEnabled,
            SpaceWeatherOperations.Credit);

    internal static SpaceWeatherUiProjection Project(
        SwpcStoreView view,
        DateTimeOffset? now = null,
        DeploymentLocationSnapshot? location = null,
        bool noSite = false,
        SpaceWeatherOperations.Status? status = null,
        ISolarEventCalculator? sun = null,
        SpaceWeatherDarknessCache? cache = null)
        => SpaceWeatherUiProjectionBuilder.Build(status ?? Status(), view, noSite ? null : location ?? Site(), now ?? Now, false,
            cache ?? new SpaceWeatherDarknessCache(), sun ?? new TestSun());

    internal static SwpcMessageNode Node(string code, int serial) => new(code, serial);
}

/// <summary>A solar calculator returning only the listed events, counting every query.</summary>
internal sealed class TestSun(params (SolarEventKind Kind, DateTimeOffset Utc)[] events) : ISolarEventCalculator
{
    private int calls;

    internal int Calls => Volatile.Read(ref calls);

    public SolarEventResult Find(SolarEventKind kind, DateTimeOffset intervalStartUtc, DateTimeOffset intervalEndUtc,
        double latitudeDegrees, double longitudeDegrees, double elevationMeters)
    {
        Interlocked.Increment(ref calls);
        foreach (var (eventKind, utc) in events)
        {
            if (eventKind == kind && utc >= intervalStartUtc && utc < intervalEndUtc)
            {
                return new SolarEventResult(kind, utc, "test-sun");
            }
        }
        return new SolarEventResult(kind, null, "test-sun");
    }
}
