using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpFixtureTests
{
    [TestMethod]
    public void FixtureManifestPinsEveryCopiedFixtureByteForByte()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(CelesTrakGpTestData.FixturePath("fixture-manifest.json")));
        var root = manifest.RootElement;
        Assert.AreEqual("synthetic-provider-shaped-not-downloaded", root.GetProperty("classification").GetString());
        Assert.AreEqual("hvo-orbital-acquisition-fixtures-v1", root.GetProperty("fixtureVersion").GetString());
        Assert.AreEqual("celestrak-gp-json-v1", root.GetProperty("profile").GetString());

        var declared = new List<string>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            declared.Add(path);
            var bytes = File.ReadAllBytes(CelesTrakGpTestData.FixturePath(path));
            Assert.AreEqual(file.GetProperty("lengthBytes").GetInt64(), bytes.LongLength, path);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)), path);
            Assert.IsFalse(bytes.Contains((byte)'\n') || bytes.Contains((byte)'\r'), $"{path} must be one line");
        }
        CollectionAssert.AreEqual(declared.Order(StringComparer.Ordinal).ToArray(), declared.ToArray(),
            "Manifest entries are sorted ordinally.");

        var copied = Directory.GetFiles(CelesTrakGpTestData.FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(static name => name != "fixture-manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(copied, declared.ToArray(), "Every copied fixture is pinned and nothing extra is copied.");
    }

    [TestMethod]
    public void FixturesAreSyntheticAndCarryNoProviderBytes()
    {
        foreach (var path in Directory.GetFiles(CelesTrakGpTestData.FixtureDirectory, "*.json"))
        {
            var text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("ISS (ZARYA)", StringComparison.Ordinal), path);
            Assert.IsFalse(text.Contains("GENERATED VIA SPACE-TRACK", StringComparison.OrdinalIgnoreCase), path);
        }
        var valid = Encoding.UTF8.GetString(CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.ValidFixture));
        Assert.IsTrue(valid.Contains("HVO SYNTHETIC", StringComparison.Ordinal));
    }
}

/// <summary>Shared fixture loading, payload builders, fakes and a manual clock for the CelesTrak GP tests.</summary>
internal static class CelesTrakGpTestData
{
    public const string ValidFixture = "celestrak-gp-active-valid.json";
    public const string UserAgent = "HVO.SkyMonitor.CameraAgent/1.0-test";

    /// <summary>The retrieval instant every fixture outcome in the README is stated against.</summary>
    public static readonly DateTimeOffset RetrievedUtc = new(2026, 10, 19, 17, 28, 18, TimeSpan.Zero);

    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-acquisition");

    public static string FixturePath(string name) => Path.Combine(FixtureDirectory, name);

    public static byte[] ReadFixture(string name) => File.ReadAllBytes(FixturePath(name));

    public static CelesTrakGpClientSettings DefaultSettings { get; } = new(CelesTrakGpSource.MaximumRequestTimeout,
        CelesTrakGpSource.MaximumWireBytes, CelesTrakGpSource.MaximumRawBytes, UserAgent);

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static OrbitalFetchResult Retrieved(byte[] raw, DateTimeOffset? completedUtc = null,
        string? entityTag = "\"synthetic-etag-1\"", string? lastModified = "Mon, 19 Oct 2026 17:00:00 GMT")
    {
        var completed = completedUtc ?? RetrievedUtc;
        return new OrbitalFetchResult(OrbitalFetchDisposition.Retrieved, 200, completed.AddSeconds(-1), completed, raw,
            Sha256(raw), raw.Length, null, entityTag, lastModified, "orbital.retrieved", "status=200");
    }

    private static readonly (string Name, string Value)[] BaseMembers =
    [
        ("OBJECT_NAME", "\"HVO SYNTHETIC T\""),
        ("OBJECT_ID", "\"2024-010A\""),
        ("EPOCH", "\"2026-10-19T06:00:00.000000\""),
        ("MEAN_MOTION", "15.2"),
        ("ECCENTRICITY", "0.0012"),
        ("INCLINATION", "53.2"),
        ("RA_OF_ASC_NODE", "120.5"),
        ("ARG_OF_PERICENTER", "60.25"),
        ("MEAN_ANOMALY", "300.75"),
        ("EPHEMERIS_TYPE", "0"),
        ("CLASSIFICATION_TYPE", "\"U\""),
        ("NORAD_CAT_ID", "60001"),
        ("ELEMENT_SET_NO", "999"),
        ("REV_AT_EPOCH", "1234"),
        ("BSTAR", "0.00012"),
        ("MEAN_MOTION_DOT", "0.00001"),
        ("MEAN_MOTION_DDOT", "0")
    ];

    /// <summary>A valid compact record; each override replaces a member's raw JSON, removes it (null) or appends it.</summary>
    public static string Record(params (string Name, string? Value)[] overrides)
    {
        var members = BaseMembers.Select(static m => (m.Name, Value: (string?)m.Value)).ToList();
        foreach (var (name, value) in overrides)
        {
            var index = members.FindIndex(m => m.Name == name);
            if (index >= 0)
            {
                members[index] = (name, value);
            }
            else if (value is not null)
            {
                members.Add((name, value));
            }
        }
        return "{" + string.Join(',', members.Where(static m => m.Value is not null)
            .Select(static m => $"\"{m.Name}\":{m.Value}")) + "}";
    }

    public static string RecordFor(int catalogNumber, params (string Name, string? Value)[] overrides) =>
        Record([("NORAD_CAT_ID", catalogNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. overrides]);

    public static byte[] Payload(params string[] records) => Encoding.UTF8.GetBytes("[" + string.Join(',', records) + "]");

    public static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            gzip.Write(raw);
        }
        return output.ToArray();
    }

    public static CelesTrakGpClient CreateClient(StubHttpHandler handler, TimeProvider? timeProvider = null,
        CelesTrakGpClientSettings? settings = null) =>
        new(new HttpClient(handler), timeProvider ?? TimeProvider.System, settings ?? DefaultSettings);

    public static HttpResponseMessage Response(HttpStatusCode status, TrackingContent content,
        string? contentType = "application/json", string? contentEncoding = null, long? contentLength = null,
        Action<HttpResponseMessage>? configure = null)
    {
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        if (contentEncoding is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Encoding", contentEncoding);
        }
        if (contentLength is not null)
        {
            content.Headers.ContentLength = contentLength;
        }
        var response = new HttpResponseMessage(status) { Content = content };
        configure?.Invoke(response);
        return response;
    }
}

/// <summary>Records each request and answers from a delegate. It never touches the network.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private readonly List<RecordedRequest> requests = [];

    public StubHttpHandler(Func<HttpResponseMessage> respond)
        : this((_, _) => Task.FromResult(respond()))
    {
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return requests.ToArray();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.NonValidated
            .Select(static h => (h.Key, Values: string.Join(", ", h.Value)))
            .ToArray();
        lock (requests)
        {
            requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, request.Content is not null));
        }
        return respond(request, cancellationToken);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, IReadOnlyList<(string Name, string Values)> Headers,
    bool HasContent);

/// <summary>
/// Response content that records whether anything read the body. Reads can be chunked, held until cancellation, or
/// made to fail, and the length is never computed implicitly, so Content-Length is present only when a test sets it.
/// </summary>
internal sealed class TrackingContent(byte[] body, int chunkBytes = int.MaxValue, bool holdReads = false,
    Exception? readFailure = null) : HttpContent
{
    private int position;

    public bool BodyRead { get; private set; }

    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TrackingContent Of(string text) => new(Encoding.UTF8.GetBytes(text));

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        BodyRead = true;
        return stream.WriteAsync(body).AsTask();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        BodyRead = true;
        return Task.FromResult<Stream>(new ReadStream(this));
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        CreateContentReadStreamAsync();

    private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ReadStarted.TrySetResult();
        if (holdReads)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        if (readFailure is not null)
        {
            throw readFailure;
        }
        var count = Math.Min(Math.Min(buffer.Length, chunkBytes), body.Length - position);
        body.AsMemory(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    private sealed class ReadStream(TrackingContent owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            owner.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            owner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            owner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A clock whose timers fire only when the test advances it.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            now += delta;
            due = timers.Where(t => t.DueUtc is { } at && at <= now).ToList();
            foreach (var timer in due)
            {
                timer.DueUtc = timer.Period is { } period ? now + period : null;
            }
        }
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueUtc { get; set; }
        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                DueUtc = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                if (!owner.timers.Contains(this))
                {
                    owner.timers.Add(this);
                }
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
