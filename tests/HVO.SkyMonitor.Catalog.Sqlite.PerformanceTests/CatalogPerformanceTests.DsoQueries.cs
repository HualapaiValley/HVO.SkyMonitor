using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

public sealed partial class CatalogPerformanceTests
{
    private const string ExpectedDsoQueryPinsSha256 = "ACB6F7600850307AAC42A5B5FE021885F4B26C4507F1F4DFED6F333E806C30F6";

    private static async Task<object> MeasurePinnedDeepSkyQueriesAsync(CatalogSnapshotResult snapshot, DeepSkyCatalog catalog)
    {
        var pinsBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "issue-525-query-pins.json"));
        Assert.AreEqual(ExpectedDsoQueryPinsSha256, ComputeBytesChecksum(pinsBytes));
        var pins = JsonSerializer.Deserialize<DsoQueryPins>(pinsBytes, JsonOptions)!;
        Assert.AreEqual("hvo-issue525-query-pins-v1", pins.Schema);
        Assert.AreEqual(pins.DatabaseSha256.ToUpperInvariant(), snapshot.DatabaseSha256.ToUpperInvariant());
        Assert.AreEqual(15, pins.Workloads.Count);
        CollectionAssert.AreEquivalent(new[] { "spatial", "type", "brightness", "alias", "outline" },
            pins.Workloads.Select(static pin => pin.Kind).Distinct(StringComparer.Ordinal).ToArray());

        // These reference collections are setup, after the recorded startup. They are not another catalog resolve.
        // The baseline is a full linear scan on this same candidate, never an invented HYG-only DSO operation.
        var setupStarted = Stopwatch.GetTimestamp();
        var setupAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var setupIoBefore = ReadProcessIo();
        var aliases = ReadBaselineAliases(snapshot.DatabasePath);
        var outlines = catalog.Objects.SelectMany(item => catalog.GetOutlines(item.Id)).ToArray();
        var spatial = catalog.Objects.Select(item => CreateBaselineSpatialEntry(item, catalog.GetOutlines(item.Id)))
            .ToArray();
        var setup = new
        {
            milliseconds = Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - setupAllocatedBefore,
            logicalReadBytes = IoDelta(setupIoBefore, ReadProcessIo(), "rchar"),
            aliasRows = aliases.Length,
            outlineRows = outlines.Length,
            spatialRows = spatial.Length,
            excludedFromStartupAndOperationTiming = true,
            retainedForAllDsoBaselineMeasurements = true
        };
        Assert.AreEqual(catalog.AliasCount, aliases.Length);
        var rows = new List<object>();
        foreach (var pin in pins.Workloads)
        {
            Assert.IsGreaterThan(0, pin.ExpectedMatchCount, pin.Name);
            Assert.IsNotEmpty(pin.ExpectedIds, pin.Name);
            LatencyMeasurement measurement;
            LatencyMeasurement baseline;
            if (pin.Kind == "alias")
            {
                (string, string) Identify(int _, IReadOnlyList<DeepSkyAlias> result)
                {
                    Assert.AreEqual(pin.ExpectedMatchCount, result.Count, pin.Name);
                    CollectionAssert.AreEqual(pin.ExpectedIds, result.Select(static item => item.ObjectId).ToArray());
                    var checksum = ComputeTextChecksum(string.Join('\n', result.Select(static item =>
                        $"{item.Alias}\t{item.ObjectId}\t{item.Kind}")));
                    Assert.AreEqual(pin.ExpectedContentSha256, checksum, pin.Name);
                    return (string.Empty, checksum);
                }
                measurement = await MeasureLatencyAsync(pin.Name, MeasuredOperations,
                    _ => ValueTask.FromResult(catalog.FindByAlias(pin.Alias!)), Identify).ConfigureAwait(false);
                baseline = await MeasureLatencyAsync($"{pin.Name}-linear-baseline", MeasuredOperations,
                    _ => ValueTask.FromResult<IReadOnlyList<DeepSkyAlias>>(aliases.Where(item =>
                        string.Equals(item.Alias, pin.Alias, StringComparison.OrdinalIgnoreCase)).ToArray()), Identify)
                    .ConfigureAwait(false);
            }
            else if (pin.Kind == "outline")
            {
                (string, string) Identify(int _, IReadOnlyList<DeepSkyOutline> result)
                {
                    Assert.AreEqual(pin.ExpectedMatchCount, result.Count, pin.Name);
                    Assert.AreEqual(pin.ExpectedPointCount,
                        result.Sum(static outline => outline.Rings.Sum(static ring => ring.Points.Count)), pin.Name);
                    CollectionAssert.AreEqual(pin.ExpectedIds,
                        result.Select(static item => item.ObjectId).Distinct(StringComparer.Ordinal).ToArray());
                    var checksum = ComputeOutlineContentChecksum(result);
                    Assert.AreEqual(pin.ExpectedContentSha256, checksum, pin.Name);
                    return (string.Empty, checksum);
                }
                measurement = await MeasureLatencyAsync(pin.Name, MeasuredOperations,
                    _ => ValueTask.FromResult(catalog.GetOutlines(pin.ObjectId!)), Identify).ConfigureAwait(false);
                baseline = await MeasureLatencyAsync($"{pin.Name}-linear-baseline", MeasuredOperations,
                    _ => ValueTask.FromResult<IReadOnlyList<DeepSkyOutline>>(outlines.Where(item =>
                        string.Equals(item.ObjectId, pin.ObjectId, StringComparison.Ordinal)).ToArray()), Identify)
                    .ConfigureAwait(false);
            }
            else
            {
                var query = new DeepSkyQuery(pin.MaximumResults,
                    pin.Kind == "spatial" ? new J2000SphericalCap(pin.RightAscensionHours, pin.DeclinationDegrees,
                        pin.RadiusDegrees) : null,
                    pin.Kind == "type" ? new HashSet<string>(pin.ObjectTypes!, StringComparer.Ordinal) : null,
                    pin.Kind == "brightness" ? new DeepSkyBrightnessLimit(
                        Enum.Parse<DeepSkyMagnitudeBand>(pin.Band!), pin.MaximumMagnitude, pin.IncludeUnknown) : null);
                query.Validate();
                (string, string) Identify(int _, DeepSkyQueryResult result)
                {
                    Assert.AreEqual(pin.ExpectedMatchCount, result.MatchCount, pin.Name);
                    CollectionAssert.AreEqual(pin.ExpectedIds, result.Objects.Select(static item => item.Id).ToArray());
                    var checksum = ComputeDeepSkyResultChecksum(result);
                    Assert.AreEqual(pin.ExpectedContentSha256, checksum, pin.Name);
                    return (string.Empty, checksum);
                }
                measurement = await MeasureLatencyAsync(pin.Name, MeasuredOperations,
                    _ => ValueTask.FromResult(catalog.Query(query)), Identify).ConfigureAwait(false);
                baseline = await MeasureLatencyAsync($"{pin.Name}-linear-baseline", MeasuredOperations,
                    _ => ValueTask.FromResult(LinearBaselineQuery(spatial, query)), Identify).ConfigureAwait(false);
            }
            Assert.AreEqual(measurement.ResultChecksumSha256, baseline.ResultChecksumSha256, pin.Name);
            rows.Add(new
            {
                criteria = pin,
                measurement,
                baseline = new
                {
                    kind = "within-candidate-linear-snapshot-scan",
                    measurement = baseline,
                    equivalentPinnedOutput = true,
                    ratioToHyg = (double?)null
                }
            });
        }
        return new { pinsSha256 = ExpectedDsoQueryPinsSha256, baselineSetup = setup, workloads = rows };
    }

    private static DeepSkyAlias[] ReadBaselineAliases(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias, object_id, kind FROM deep_sky_aliases " +
            "ORDER BY object_id COLLATE BINARY, alias COLLATE BINARY";
        using var reader = command.ExecuteReader();
        var rows = new List<DeepSkyAlias>();
        while (reader.Read()) rows.Add(new DeepSkyAlias(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows.ToArray();
    }

    private static BaselineSpatialEntry CreateBaselineSpatialEntry(DeepSkyObject item, IReadOnlyList<DeepSkyOutline> outlines)
    {
        var direction = BaselineDirection(item.RightAscensionHours, item.DeclinationDegrees);
        var reach = (item.MajorAxisArcminutes ?? 0) / 120;
        foreach (var point in outlines.SelectMany(static outline => outline.Rings).SelectMany(static ring => ring.Points))
        {
            reach = Math.Max(reach, BaselineSeparation(direction,
                BaselineDirection(point.RightAscensionDegrees / 15, point.DeclinationDegrees)));
        }
        return new BaselineSpatialEntry(item, direction, reach);
    }

    private static DeepSkyQueryResult LinearBaselineQuery(BaselineSpatialEntry[] entries, DeepSkyQuery query)
    {
        var matches = 0;
        var selected = new List<DeepSkyObject>();
        var centre = query.Region is { } cap
            ? BaselineDirection(cap.CenterRightAscensionHours, cap.CenterDeclinationDegrees) : default;
        foreach (var entry in entries)
        {
            var item = entry.Object;
            if (query.ObjectTypes is { } types && !types.Contains(item.ObjectType) ||
                query.Brightness is { } brightness && !brightness.Matches(item) ||
                query.Region is { } region && BaselineSeparation(centre, entry.Direction) >
                    region.RadiusDegrees + entry.ReachDegrees + DeepSkyCatalog.RegionToleranceDegrees)
            {
                continue;
            }
            if (++matches <= query.MaximumResults) selected.Add(item);
        }
        return new DeepSkyQueryResult(selected.AsReadOnly(), matches);
    }

    private static (double X, double Y, double Z) BaselineDirection(double raHours, double decDegrees)
    {
        var ra = raHours * 15 * Math.PI / 180;
        var dec = decDegrees * Math.PI / 180;
        return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
    }

    private static double BaselineSeparation((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1)) * 180 / Math.PI;

    private static string ComputeOutlineContentChecksum(IReadOnlyList<DeepSkyOutline> outlines)
    {
        var lines = new List<string>();
        foreach (var outline in outlines)
        {
            for (var ring = 0; ring < outline.Rings.Count; ring++)
            {
                for (var index = 0; index < outline.Rings[ring].Points.Count; index++)
                {
                    var point = outline.Rings[ring].Points[index];
                    lines.Add(FormattableString.Invariant($"{outline.ObjectId}:{outline.Level}:{ring}:{index}:{unchecked((ulong)BitConverter.DoubleToInt64Bits(point.RightAscensionDegrees)):X16}:{unchecked((ulong)BitConverter.DoubleToInt64Bits(point.DeclinationDegrees)):X16}"));
                }
            }
        }
        return ComputeTextChecksum(string.Join('\n', lines));
    }

    private sealed record BaselineSpatialEntry(DeepSkyObject Object, (double X, double Y, double Z) Direction,
        double ReachDegrees);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json constructs the pinned contract through its public record constructor.")]
    private sealed record DsoQueryPins(string Schema, string DatabaseSha256, List<DsoQueryPin> Workloads);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "System.Text.Json constructs each pinned workload through its public record constructor.")]
    private sealed record DsoQueryPin(string Name, string Kind, int ExpectedMatchCount, string[] ExpectedIds,
        string ExpectedContentSha256, int MaximumResults = 64, double RightAscensionHours = 0,
        double DeclinationDegrees = 0, double RadiusDegrees = 0, string[]? ObjectTypes = null,
        string? Band = null, double MaximumMagnitude = 0, bool IncludeUnknown = false,
        string? Alias = null, string? ObjectId = null, int ExpectedPointCount = 0);
}
