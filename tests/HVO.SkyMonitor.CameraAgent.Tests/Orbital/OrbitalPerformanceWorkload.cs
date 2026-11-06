using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// A deterministic, CelesTrak-GP-shaped active group at the #527-measured size (decision record, retained scale
/// sample): 16,754 records, 826 of them six-digit catalog numbers, 64 objects carrying two element sets, two
/// future epochs up to 22.5 hours ahead and 25 epochs older than seven days. Every value is computed from the record
/// index, so the bytes are identical on every run. The changed body differs only in one record's mean anomaly and
/// element-set number.
/// </summary>
internal sealed class OrbitalPerformanceWorkload
{
    internal const int RecordCount = 16_754;
    internal const int RepeatedObjects = 64;
    internal const int ObjectCount = RecordCount - RepeatedObjects;
    internal const int SixDigitObjects = 826;
    internal const int FutureRecords = 2;
    internal const int StaleRecords = 25;
    internal const long Issue527RawBytes = 7_057_239;

    private static readonly DateTimeOffset Start = OrbitalElementsStoreTestData.Start;

    private const int FiveDigitObjects = ObjectCount - SixDigitObjects;
    private const int RepeatedObjectStride = 241;
    private const int StaleFirstIndex = 100;

    private static readonly Lazy<OrbitalPerformanceWorkload> Shared = new(static () => new OrbitalPerformanceWorkload());

    private OrbitalPerformanceWorkload()
    {
        Raw = Build(changed: false);
        ChangedRaw = Build(changed: true);
        GzipWire = CelesTrakGpTestData.Gzip(Raw);
    }

    internal static OrbitalPerformanceWorkload Instance => Shared.Value;

    internal byte[] Raw { get; }

    internal byte[] ChangedRaw { get; }

    internal byte[] GzipWire { get; }

    private static byte[] Build(bool changed)
    {
        var builder = new StringBuilder(RecordCount * 400);
        builder.Append('[');
        for (var index = 0; index < RecordCount; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            AppendRecord(builder, index, changed && index == 0);
        }
        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendRecord(StringBuilder builder, int index, bool changed)
    {
        var repeat = index >= ObjectCount;
        var objectIndex = repeat ? (index - ObjectCount) * RepeatedObjectStride : index;
        var catalog = objectIndex < FiveDigitObjects
            ? 10_000 + (objectIndex * 5)
            : 100_000 + ((objectIndex - FiveDigitObjects) * 7);
        var epoch = Epoch(objectIndex);
        if (repeat)
        {
            epoch = epoch.AddHours(-12);
        }
        var elementSet = 999 - (repeat ? 1 : 0) + (changed ? 1 : 0);
        var meanAnomaly = ((index * 19 % 36_000) / 100.0) + (changed ? 0.5 : 0);
        var designator = string.Create(CultureInfo.InvariantCulture,
            $"{1990 + (objectIndex % 36)}-{(objectIndex % 999) + 1:000}{(char)('A' + (objectIndex / 999 % 26))}");
        builder.Append(CultureInfo.InvariantCulture,
            $"{{\"OBJECT_NAME\":\"HVO SYNTHETIC {catalog}\",\"OBJECT_ID\":\"{designator}\",")
            .Append(CultureInfo.InvariantCulture,
                $"\"EPOCH\":\"{epoch.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}\",")
            .Append(CultureInfo.InvariantCulture, $"\"MEAN_MOTION\":{1.0027 + ((objectIndex % 16_000) / 1000.0):F8},")
            .Append(CultureInfo.InvariantCulture, $"\"ECCENTRICITY\":{(objectIndex * 37 % 99_991) / 1_000_000.0:F7},")
            .Append(CultureInfo.InvariantCulture, $"\"INCLINATION\":{(objectIndex * 7 % 18_001) / 100.0:F4},")
            .Append(CultureInfo.InvariantCulture, $"\"RA_OF_ASC_NODE\":{(objectIndex * 13 % 36_000) / 100.0:F4},")
            .Append(CultureInfo.InvariantCulture, $"\"ARG_OF_PERICENTER\":{(objectIndex * 17 % 36_000) / 100.0:F4},")
            .Append(CultureInfo.InvariantCulture, $"\"MEAN_ANOMALY\":{meanAnomaly:F4},")
            .Append("\"EPHEMERIS_TYPE\":0,\"CLASSIFICATION_TYPE\":\"U\",")
            .Append(CultureInfo.InvariantCulture, $"\"NORAD_CAT_ID\":{catalog},\"ELEMENT_SET_NO\":{elementSet},")
            .Append(CultureInfo.InvariantCulture, $"\"REV_AT_EPOCH\":{(objectIndex * 31 % 99_999) + (repeat ? 0 : 1)},")
            .Append(CultureInfo.InvariantCulture, $"\"BSTAR\":{((index % 2_001) - 1_000) / 10_000_000.0:0.##########},")
            .Append(CultureInfo.InvariantCulture,
                $"\"MEAN_MOTION_DOT\":{((index % 1_001) - 500) / 100_000_000.0:0.##########},")
            .Append("\"MEAN_MOTION_DDOT\":0}");
    }

    /// <summary>Most epochs fall in the three days before retrieval; two are ahead of it and 25 are over a week old.</summary>
    private static DateTimeOffset Epoch(int objectIndex) => objectIndex switch
    {
        1 => Start.AddHours(22.5),
        2 => Start.AddHours(3),
        >= StaleFirstIndex and < StaleFirstIndex + StaleRecords => Start.AddDays(-(8 + objectIndex - StaleFirstIndex)),
        _ => Start.AddSeconds(-((objectIndex * 7_919L) % (72 * 3_600))).AddTicks(objectIndex % 1_000_000 * 10L)
    };
}
