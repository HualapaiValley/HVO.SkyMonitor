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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(input.StartsWith('$') || input == "noise" ? input : Sentence(input)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().Utc);
    }

    [TestMethod]
    public async Task OverlengthSentence_IsDiscardedAndResynchronizesAtNextLine()
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(new string('x', 200), Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task RoundHasFiniteByteBudget_AndDoesNotAcceptAnUnterminatedSentence()
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(new FakeTimeProvider(Now));
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
        using var reader = CreateReader(new FakeTimeProvider(Now));
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
        using var reader = CreateReader(new FakeTimeProvider(Now));
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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(time);
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
        using var reader = CreateReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Sentence(body)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.AreEqual(new DateOnly(year, month, day), DateOnly.FromDateTime(reader.GetStatus().Utc!.Value.UtcDateTime));
        Assert.AreEqual(TimeSpan.Zero, reader.GetStatus().Utc!.Value.Offset);
        if (body.StartsWith("GPRMC", StringComparison.Ordinal))
        {
            Assert.AreEqual(1234567L, reader.GetStatus().Utc!.Value.Ticks % TimeSpan.TicksPerSecond);
        }
    }

    [TestMethod]
    [DataRow(4096)]
    [DataRow(8192)]
    public async Task ByteBudgetBoundary_DiscardsTheSamePhysicalLineUntilLf_ThenRecovers(int prefixLength)
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        using var source = new MemoryStream();
        await source.WriteAsync(Encoding.ASCII.GetBytes(new string('x', prefixLength) + Rmc() + "\n")).ConfigureAwait(false);
        source.Position = 0;
        for (var consumed = 0; consumed < prefixLength; consumed += GpsTimeReader.MaximumBytesPerRead)
        {
            await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        }
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State, "A checksummed tail is still part of the overlong physical line.");
        Assert.IsNull(reader.GetStatus().Utc);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        await AppendAsync(source, Rmc() + "\r\n").ConfigureAwait(false);
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.AreEqual(Now, reader.GetStatus().Utc);
    }

    [TestMethod]
    public async Task PartialLineFraming_RemainsWithItsSourceAcrossEofAndOtherStreams()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        using var partial = new MemoryStream();
        await partial.WriteAsync(Encoding.ASCII.GetBytes("partial")).ConfigureAwait(false);
        partial.Position = 0;
        await reader.ReadAsync(partial, CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(1));
        using var independent = Source(Rmc());
        await reader.ReadAsync(independent, CancellationToken.None).ConfigureAwait(false);
        var accepted = reader.GetStatus();
        time.Advance(TimeSpan.FromSeconds(1));
        await AppendAsync(partial, Rmc() + "\n").ConfigureAwait(false);
        await reader.ReadAsync(partial, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(accepted.LastSentenceUtc, reader.GetStatus().LastSentenceUtc, "Returning to a source cannot turn its partial-line tail into a new sentence.");
        Assert.AreEqual(accepted.ArrivalOffset, reader.GetStatus().ArrivalOffset);
        await AppendAsync(partial, Rmc() + "\r\n").ConfigureAwait(false);
        await reader.ReadAsync(partial, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(time.GetUtcNow(), reader.GetStatus().LastSentenceUtc);
    }

    [TestMethod]
    [DataRow(127)]
    [DataRow(13)]
    public async Task EmbeddedControlCharacter_IsRejectedDespiteValidChecksum_AndNextLineRecovers(int control)
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        var fields = "GNRMC,120000.000,A,,,,,,,121126,,,A".Split(',');
        fields[3] = ((char)control).ToString();
        using var invalid = Source(Sentence(string.Join(',', fields)));
        await reader.ReadAsync(invalid, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().Utc);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        using var valid = Source(Rmc());
        await reader.ReadAsync(valid, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State, "A single terminal CR in CRLF is allowed.");
    }

    [TestMethod]
    [DataRow(82, true)]
    [DataRow(83, false)]
    public async Task LineLengthBoundary_IncludesTerminalCrAndExcludesLf(int length, bool accepted)
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        const string body = "GNRMC,120000.000,A,,,,,,,121126,,,A,";
        var line = Sentence(body + new string('x', length - Sentence(body).Length - 1)) + "\r\n";
        Assert.AreEqual(length + 1, line.Length);
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(line));
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(accepted ? GpsSignalState.Available : GpsSignalState.Missing, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task GgaAndPps_ExpireAtExactlyFiveMonotonicSeconds()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        using var source = Source(Rmc(), Gga());
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        time.Advance(GpsTimeReader.StaleAfter - TimeSpan.FromTicks(1));
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().PpsState);
        Assert.AreEqual(8, reader.GetStatus().Satellites);
        time.Advance(TimeSpan.FromTicks(1));
        var expired = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.Stale, expired.State);
        Assert.AreEqual(GpsSignalState.Stale, expired.PpsState);
        Assert.IsNull(expired.FixQuality);
        Assert.IsNull(expired.Satellites);
        Assert.IsNull(expired.ArrivalOffset);
        Assert.AreEqual(Now, expired.LastPpsUtc);
    }

    [TestMethod]
    public async Task ReportInto_PreservesPopulatedInToleranceNetworkAndKernelEvidence()
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        using var source = Source(Rmc());
        await reader.ReadAsync(source, CancellationToken.None).ConfigureAwait(false);
        var selected = new TimeServerResult("fake", null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1), 1);
        var kernel = new KernelClockState(KernelClockStatus.Synchronized, TimeSpan.Zero, TimeSpan.Zero);
        var snapshot = new ClockSyncSnapshot(Now, true, selected, [selected], 1, kernel);
        var projected = reader.ReportInto(snapshot);
        Assert.IsNull(snapshot.Gps);
        Assert.AreSame(selected, projected.Selected);
        Assert.AreSame(snapshot.Servers, projected.Servers);
        Assert.AreSame(kernel, projected.Kernel);
        Assert.AreEqual(snapshot.MeasuredUtc, projected.MeasuredUtc);
        Assert.AreEqual(snapshot.IgnoredEntries, projected.IgnoredEntries);
        Assert.AreEqual(GpsSignalState.Available, projected.Gps!.State);
        Assert.IsFalse(projected.Gps.CanVerifyClock);
        Assert.AreEqual(ClockSyncStatus.InTolerance, ClockAssessment.Evaluate(TimeSyncSettings.Default, projected).Status);
        Assert.AreEqual(ClockAssessment.Evaluate(TimeSyncSettings.Default, snapshot), ClockAssessment.Evaluate(TimeSyncSettings.Default, projected));
    }

    [TestMethod]
    [DataRow(-3600)]
    [DataRow(3600)]
    [DataRow(-1)]
    [DataRow(1)]
    public async Task WallClockStep_RecomputesCurrentOffsetFromMonotonicRmcAge(int seconds)
    {
        var time = new FakeTimeProvider(Now.AddMilliseconds(100));
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(1));
        time.AdjustTime(time.GetUtcNow().AddSeconds(seconds));
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(1));
        var status = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.Available, status.State);
        Assert.AreEqual(TimeSpan.FromMilliseconds(-100) - TimeSpan.FromSeconds(seconds), status.ArrivalOffset);
        Assert.AreEqual(Now, status.Utc, "The original receiver UTC remains diagnostic history.");
        Assert.IsFalse(status.CanVerifyClock);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(GpsSignalState.Stale, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
    }

    [TestMethod]
    public async Task MonotonicAgingWithoutWallStep_DoesNotChangeDiagnosticOffset()
    {
        var time = new FakeTimeProvider(Now.AddMilliseconds(100));
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(GpsTimeReader.StaleAfter - TimeSpan.FromTicks(1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(-100), reader.GetStatus().ArrivalOffset);
    }

    [TestMethod]
    [DataRow("N")]
    [DataRow("E")]
    [DataRow("M")]
    [DataRow("S")]
    [DataRow("X")]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("AA")]
    public async Task InvalidRmcMode_RevokesPriorFixUntilValidRmcRecovers(string mode)
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        await reader.ReadAsync(Source(Sentence($"GNRMC,120000.000,A,,,,,,,121126,,,{mode}")), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        Assert.IsFalse(reader.GetStatus().CanVerifyClock);
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State, "GGA and PPS cannot restore a rejected RMC fix.");
        time.Advance(GpsTimeReader.StaleAfter);
        Assert.AreEqual(GpsSignalState.Stale, reader.GetStatus().State);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    [DataRow("GPRMC,120000.000,A,,,,,,,121126")]
    [DataRow("GNRMC,120000.000,A,,,,,,,121126,,,A")]
    [DataRow("GNRMC,120000.000,A,,,,,,,121126,,,D")]
    [DataRow("GNRMC,120000.000,A,,,,,,,121126,,,P")]
    [DataRow("GNRMC,120000.000,A,,,,,,,121126,,,R")]
    [DataRow("GNRMC,120000.000,A,,,,,,,121126,,,F")]
    public async Task SupportedMeasuredRmcModeOrLegacySentence_RemainsDiagnosticOnly(string body)
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Sentence(body)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.AreEqual(TimeSpan.Zero, reader.GetStatus().ArrivalOffset);
        Assert.IsFalse(reader.GetStatus().CanVerifyClock);
    }

    [TestMethod]
    [DataRow("io")]
    [DataRow("timeout")]
    [DataRow("access")]
    [DataRow("disposed")]
    [DataRow("canceled")]
    public async Task ExpectedTransportFailure_ClearsEveryEvidenceFieldAndReadGateRecovers(string failure)
    {
        using var reader = CreateReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        using var broken = new FaultingStream(TransportFailure(failure));
        await reader.ReadAsync(broken, CancellationToken.None).ConfigureAwait(false);
        var status = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.Unavailable, status.State);
        Assert.IsNull(status.Utc);
        Assert.IsNull(status.LastSentenceUtc);
        Assert.IsNull(status.ArrivalOffset);
        Assert.IsNull(status.FixQuality);
        Assert.IsNull(status.Satellites);
        Assert.IsNull(status.LastPpsUtc);
        Assert.AreEqual(GpsSignalState.Missing, status.PpsState);
        Assert.IsFalse(status.CanVerifyClock);
        Assert.IsTrue(broken.CanRead, "The caller still owns the stream.");
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Missing, reader.GetStatus().State);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.AreEqual(TimeSpan.Zero, reader.GetStatus().ArrivalOffset);
    }

    [TestMethod]
    [DataRow("canceled", false)]
    [DataRow("canceled", true)]
    [DataRow("io", false)]
    [DataRow("timeout", false)]
    [DataRow("access", false)]
    [DataRow("disposed", false)]
    public async Task CallerCancellation_WinsOverTransportCancellationAndDeadline(string failure, bool deadlineAlsoExpires)
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        using var broken = new FaultingStream(TransportFailure(failure), () =>
        {
            cancellation.Cancel();
            if (deadlineAlsoExpires)
            {
                time.Advance(GpsTimeReader.ReadTimeout);
            }
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(broken, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    [TestMethod]
    public async Task ReaderDeadline_RetainsCompletedEvidence_AndUnexpectedFailurePropagates()
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        using var pendingSource = new WaitingStream();
        var pending = reader.ReadAsync(pendingSource, CancellationToken.None);
        time.Advance(GpsTimeReader.ReadTimeout);
        await pending.ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        using var unexpected = new FaultingStream(new InvalidOperationException("programming defect"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(unexpected, CancellationToken.None)).ConfigureAwait(false);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
    }

    private static Exception TransportFailure(string failure) => failure switch
    {
        "io" => new IOException("fake disconnect"),
        "timeout" => new TimeoutException("fake transport timeout"),
        "access" => new UnauthorizedAccessException("fake access refusal"),
        "disposed" => new ObjectDisposedException("fake transport"),
        "canceled" => new OperationCanceledException("fake internal cancellation"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private sealed class FaultingStream(Exception failure, Action? beforeThrow = null) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeThrow?.Invoke();
            return ValueTask.FromException<int>(failure);
        }
    }

    [TestMethod]
    [DataRow("120699")]
    [DataRow("290307")]
    [DataRow("280646")]
    public async Task OutOfWindowRmc_RevokesPriorFixRegardlessOfHostClock(string date)
    {
        var time = new FakeTimeProvider(Now);
        using var reader = CreateReader(time);
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        await reader.ReadAsync(Source(Sentence($"GNRMC,120000.000,A,,,,,,,{date},,,A")), CancellationToken.None).ConfigureAwait(false);
        var rejected = reader.GetStatus();
        Assert.AreEqual(GpsSignalState.NoFix, rejected.State);
        Assert.IsNull(rejected.ArrivalOffset);
        Assert.IsFalse(rejected.CanVerifyClock);
        time.AdjustTime(rejected.Utc!.Value);
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State, "Matching untrusted host UTC cannot make rejected receiver evidence valid.");
        await reader.ReadAsync(Source(Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State);
        await reader.ReadAsync(Source(Rmc()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(GpsSignalState.Available, reader.GetStatus().State);
        Assert.IsFalse(reader.GetStatus().CanVerifyClock);
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    public async Task ScriptedUtcWindow_EnforcesInclusiveBoundsAtTickPrecision(int ticks, bool valid)
    {
        var window = new GpsUtcWindow(Now, Now.AddTicks(2));
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now.AddYears(50)), window);
        var stamp = Now.AddTicks(ticks).ToString("HHmmss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var date = Now.AddTicks(ticks).ToString("ddMMyy", System.Globalization.CultureInfo.InvariantCulture);
        await reader.ReadAsync(Source(Sentence($"GNRMC,{stamp},A,,,,,,,{date},,,A")), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(valid ? GpsSignalState.Available : GpsSignalState.NoFix, reader.GetStatus().State);
        Assert.AreEqual(valid, reader.GetStatus().ArrivalOffset.HasValue);
        Assert.IsFalse(reader.GetStatus().CanVerifyClock);
    }

    [TestMethod]
    public async Task MissingUtcReference_IsNoFixAndCannotTrustHostClockOrGgaPps()
    {
        using var reader = new GpsTimeReader(new FakeTimeProvider(Now));
        await reader.ReadAsync(Source(Rmc(), Gga()), CancellationToken.None).ConfigureAwait(false);
        reader.ObserveSimulatedPps();
        Assert.AreEqual(GpsSignalState.NoFix, reader.GetStatus().State);
        Assert.IsNull(reader.GetStatus().ArrivalOffset);
        Assert.IsFalse(reader.GetStatus().CanVerifyClock);
    }

    [TestMethod]
    public void ScriptedUtcWindow_RefusesReversedBounds()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GpsUtcWindow(Now, Now.AddTicks(-1)));

    private static GpsTimeReader CreateReader(TimeProvider time)
        => new(time, new GpsUtcWindow(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 12, 31, 23, 59, 59, TimeSpan.Zero)));

    private static async Task AppendAsync(MemoryStream source, string text)
    {
        var position = source.Position;
        source.Position = source.Length;
        await source.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);
        source.Position = position;
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
