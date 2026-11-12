using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class GpsTimeReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ScriptedNmea_ReportsUtcFixAndDiagnosticOffsetWithoutClockAuthority()
    {
        var time = new FakeTimeProvider(Now.AddMilliseconds(100));
        using var reader = new GpsTimeReader(time);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        var status = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.Available, status.State);
        Assert.AreEqual(Now, status.Utc);
        Assert.AreEqual(TimeSpan.FromMilliseconds(-100), status.ArrivalOffset);
        Assert.AreEqual(8, status.Satellites);
        Assert.AreEqual(1, status.FixQuality);
        Assert.IsFalse(status.CanVerifyClock);
        var clock = new ClockSyncSnapshot(Now, true, null, [], 0, KernelClockState.Unknown);
        var projected = reader.ReportInto(clock);
        Assert.AreEqual(status, projected.Gps);
        Assert.AreEqual(ClockSyncStatus.Unverified, ClockAssessment.Evaluate(TimeSyncSettings.Default, projected).Status);
    }

    [TestMethod]
    public async Task MissingStaleNoFixAndRecovery_AreExplicitAndDoNotReuseOffset()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = new GpsTimeReader(time);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().PpsState);
        time.Advance(TimeSpan.FromSeconds(6));
        var stale = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.Stale, stale.State);
        Assert.IsNull(stale.ArrivalOffset);
        Assert.IsNull(stale.Satellites);
        Assert.AreEqual(GpsSignalState.Stale, stale.PpsState);
        await reader.ReadAsync(Source(Rmc("V")), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    [DataRow("$GPRMC,120000,A,,,,,,,121126,,,A*00")]
    [DataRow("$GPRMC,120000,A,,,,,,,121126,,,A")]
    [DataRow("noise")]
    [DataRow("$GNRMC,120000.000,A,,,,,,,121126,,,AO* 0")]
    [DataRow("GPRMC,246000,A,,,,,,,121126,,,A")]
    [DataRow("GPRMC,120000,A,,,,,,,311126,,,A")]
    [DataRow("GPRMC,120000,X,,,,,,,121126,,,A")]
    [DataRow("GPRMC,120060,A,,,,,,,121126,,,A")]
    [DataRow("GPXXX,120000,A,,,,,,,121126,,,A")]
    public async Task InvalidSentences_DoNotCreateTimeEvidence(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(input.StartsWith('$') || input == "noise" ? input : Sentence(input)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().Utc);
    }

    [TestMethod]
    public async Task OverlengthSentence_IsDiscardedAndResynchronizesAtNextLine()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(new string('x', 200), Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task RoundHasFiniteByteBudget_AndDoesNotAcceptAnUnterminatedSentence()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        using var source = Source(new string('x', 10000));
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsTimeReader.MaximumBytesPerRead, source.Position);
        using var partial = new MemoryStream(Encoding.ASCII.GetBytes(Rmc()));
        await reader.ReadAsync(partial, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task CancellationAndTimeout_CancelPendingFakeRead()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = new GpsTimeReader(time);
        using var stream = new WaitingStream();
        using var cancellation = new CancellationTokenSource();
        var pending = reader.ReadAsync(stream, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        var timed = reader.ReadAsync(stream, CancellationToken.None);
        time.Advance(GpsTimeReader.ReadTimeout);
        await timed.ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task Disconnect_ClearsEvidenceAndNextReadRecovers()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        using var broken = new BrokenStream();
        await reader.ReadAsync(broken, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Unavailable, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task GgaAndPpsWithoutDatedRmc_CannotCreateUtcOrOffset()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().Utc);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        Assert.AreEqual(Now, reader.GetStatus().LastPpsUtc);
    }

    [TestMethod]
    public async Task FreshGgaLossOfFix_SuppressesDiagnosticOffsetUntilRecovery()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Rmc(), Sentence("GPGGA,120000.000,4807.038,N,01131.000,E,0,00,0.9,545.4,M,46.9,M,,")), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task InvalidTrafficAndPps_DoNotRefreshDatedTimeOrGgaEvidence()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = new GpsTimeReader(time);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(GpsTimeReader.StaleAfter);
        await reader.ReadAsync(Source("bad", Sentence("GPGGA,120000.000,,,,,9,100,")), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.Stale, reader.GetStatus().State);
        Assert.AreEqual(Now, reader.GetStatus().LastSentenceUtc);
        Assert.IsNull(reader.GetStatus().FixQuality);
    }

    [TestMethod]
    public async Task NewGgaCannotRefreshStaleRmc_AndHostClockJumpDoesNotExpireEvidence()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = new GpsTimeReader(time);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        time.AdjustTime(Now.AddHours(1));
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        time.Advance(GpsTimeReader.StaleAfter);
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Stale, reader.GetStatus().State);
        Assert.AreEqual(8, reader.GetStatus().Satellites);
    }

    [TestMethod]
    public async Task QueuedRead_CancellationDoesNotConsumeItsSource_AndReleasesGate()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = new GpsTimeReader(time);
        using var blocked = new WaitingStream();
        using var queued = Source(Rmc());
        var first = reader.ReadAsync(blocked, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var second = reader.ReadAsync(queued, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => second).ConfigureAwait(false);
        Assert.AreEqual(0L, queued.Position);
        time.Advance(GpsTimeReader.ReadTimeout);
        await first.ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        await reader.ReadAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    [DataRow("GPRMC,235959.1234567,A,,,,,,,311226,,,A", 2026, 12, 31)]
    [DataRow("GNRMC,000000,A,,,,,,,010127,,,A", 2027, 1, 1)]
    public async Task DatedUtc_IsParsedWithoutHostDateOrCultureAssumptions(string body, int year, int month, int day)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Sentence(body)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.AreEqual(new DateOnly(year, month, day), DateOnly.FromDateTime(reader.GetStatus().Utc!.Value.UtcDateTime));
        Assert.AreEqual(TimeSpan.Zero, reader.GetStatus().Utc!.Value.Offset);
        if (body.StartsWith("GPRMC", StringComparison.Ordinal))
        {
            Assert.AreEqual(1234567L, reader.GetStatus().Utc!.Value.Ticks % TimeSpan.TicksPerSecond);
        }
    }

    internal static string Rmc(string fix = "A") => Sentence($"GNRMC,120000.000,{fix},,,,,,,121126,,,A");
    internal static string Gga() => Sentence("GPGGA,120000.000,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,");
    internal static MemoryStream Source(params string[] lines) => new(Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n"));
    internal static string Sentence(string body)
    {
        var checksum = body.Aggregate(0, (sum, character) => sum ^ character);
        return $"${body}*{checksum:X2}";
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly ExposureTestTimeProvider _inner = new(start);
        private TimeSpan _wallOffset;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + _wallOffset;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _inner.CreateTimer(callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);
        internal void AdjustTime(DateTimeOffset utc) => _wallOffset = utc - _inner.GetUtcNow();
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("fake disconnect"));
    }
}
