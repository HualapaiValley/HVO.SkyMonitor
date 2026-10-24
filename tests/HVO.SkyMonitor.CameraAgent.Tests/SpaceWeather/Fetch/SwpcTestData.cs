using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

internal static class SwpcTestData
{
    public static DateTimeOffset Now => Time("2026-02-03T01:00:00Z");
    public static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    public static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.GetString() : null;
    public static JsonDocument Scenario(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1", "scenarios", name)));
    public static byte[] EmptyGzipMember => [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 255, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    public static byte[] Gzip(byte[] bytes)
    {
        // The pinned GZipStream emits no complete member for empty input; use the canonical empty member.
        if (bytes.Length == 0) return EmptyGzipMember;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
}

internal sealed class SwpcStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri? Uri, (string, string)[] Headers)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri, request.Headers.NonValidated.Select(h => (h.Key, string.Join(", ", h.Value))).ToArray()));
        Assert.IsNull(request.Content);
        return respond(request, cancellationToken);
    }
}

internal sealed class SwpcContent(byte[] bytes, bool unbounded = false, bool pending = false,
    Exception? failure = null, int chunk = int.MaxValue, int[]? chunks = null, int afterBytes = 0,
    bool faultOnce = false, bool limitAtOutcome = false) : HttpContent
{
    private int readIndex;
    public bool Opened { get; private set; }
    public bool Disposed { get; private set; }
    public int BytesRead { get; private set; }
    public int LargestRead { get; private set; }
    public int ReadCalls { get; private set; }
    public int LastReadRequest { get; private set; }
    public int ZeroReadCalls { get; private set; }
    public int FaultCount { get; private set; }
    public int ReadsAfterThrow { get; private set; }
    public int? BytesAtThrow { get; private set; }
    public bool ReaderDisposed { get; private set; }
    public TaskCompletionSource BlockedReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new AssertFailedException("No buffering.");
    protected override Task<Stream> CreateContentReadStreamAsync() { Opened = true; return Task.FromResult<Stream>(new Reader(this)); }
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(ReaderDisposed, this);
        if (FaultCount > 0) ReadsAfterThrow++;
        ReadCalls++;
        LastReadRequest = buffer.Length;
        ReadStarted.TrySetResult();
        LargestRead = Math.Max(LargestRead, buffer.Length);
        if (BytesRead >= afterBytes)
        {
            if (pending)
            {
                BlockedReadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            if (failure is not null && (!faultOnce || FaultCount == 0))
            {
                BytesAtThrow ??= BytesRead;
                FaultCount++;
                throw failure;
            }
        }
        var count = Math.Min(buffer.Length, chunks is null ? chunk : chunks[readIndex++ % chunks.Length]);
        if (limitAtOutcome && BytesRead < afterBytes) count = Math.Min(count, afterBytes - BytesRead);
        if (unbounded) buffer.Span[..count].Clear();
        else
        {
            count = Math.Min(count, bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
        }
        if (count == 0) ZeroReadCalls++;
        BytesRead += count;
        return count;
    }
    private sealed class Reader(SwpcContent owner) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            owner.ReaderDisposed = true;
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => owner.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => owner.ReadAsync(buffer, cancellationToken);
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
