using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// Runs one managed time-data refresh: three bounded downloads from the approved endpoints, the provider checksums
/// (USNO SHA-512 and the leap file's <c>#h</c>), a parse with the product validators, and then atomic adoption of the
/// pair unless it is unchanged, covers less than the adopted one, or would retire the only adopted pair that covers the
/// current time. Any failure keeps the adopted pair.
/// </summary>
/// <remarks>
/// It runs only as the reserved automation occurrence, never on a capture or planning request. One attempt runs at a
/// time in this process, and an attempt starts at most once per <see cref="MinimumAttemptInterval"/>, measured from the
/// start recorded durably in <c>refresh.json</c> before any request, so neither a restart nor run-now adds attempts.
/// Coverage always comes from the parsed files, never from the download time.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The single-flight gate never materializes a wait handle and lives for the host's lifetime.")]
public sealed partial class CameraAgentTimeDataRefreshJob(
    CameraAgentManagedTimeDataStore store,
    ICameraAgentTimeDataClient client,
    IOptionsMonitor<CameraAgentSatellitePlanningOptions> options,
    TimeProvider timeProvider,
    ILogger<CameraAgentTimeDataRefreshJob> logger)
{
    /// <summary>The application limit: at most one attempt a day. The providers publish no allowance.</summary>
    public static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromDays(1);

    /// <summary>The largest floor shortfall waited out precisely, so a slightly early daily occurrence still runs.</summary>
    public static readonly TimeSpan MaximumPrecisionWait = TimeSpan.FromSeconds(120);

    public const string DisabledReasonCode = "time-data.disabled";
    public const string InFlightReasonCode = "time-data.attempt-in-flight";
    public const string FloorReasonCode = "time-data.floor-not-elapsed";
    public const string InProgressReasonCode = "time-data.in-progress";
    public const string ChecksumUnavailableReasonCode = "time-data.checksum-unavailable";
    public const string ChecksumMismatchReasonCode = "time-data.checksum-mismatch";
    public const string LeapHashMismatchReasonCode = "time-data.leap-hash-mismatch";
    public const string TermsMissingReasonCode = "time-data.terms-missing";
    public const string ParseFailedReasonCode = "time-data.parse-failed";
    public const string StorageFailedReasonCode = "time-data.storage-failed";
    public const string CancelledReasonCode = "time-data.cancelled";
    public const string AdoptedReasonCode = "time-data.adopted";
    public const string UnchangedReasonCode = "time-data.unchanged";
    public const string NotNewerReasonCode = "time-data.not-newer";

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "No wait handle is ever materialized; the job lives for the host's lifetime.")]
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while an attempt runs in this process; an <c>InProgress</c> record without one was interrupted.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Runs one occurrence. Caller cancellation is rethrown after the attempt record is closed.</summary>
    public async ValueTask<LocalAutomationExecution> ExecuteAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.TimeDataRefreshEnabled)
            return Skipped(DisabledReasonCode, "managed time-data refresh is off in this CameraAgent's settings");
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Skipped(InFlightReasonCode, "another time-data refresh is running");
        try
        {
            return await RunAsync(manual, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<LocalAutomationExecution> RunAsync(bool manual, CancellationToken cancellationToken)
    {
        CameraAgentTimeDataRefreshState? previous;
        try
        {
            previous = await store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A damaged record cannot prove an earlier attempt; the new record replaces it.
            StateUnreadable(logger, exception.GetType().Name);
            previous = null;
        }
        if (previous is not null)
        {
            var shortfall = previous.LastAttemptUtc + MinimumAttemptInterval - timeProvider.GetUtcNow();
            if (shortfall > MaximumPrecisionWait)
                return Skipped(FloorReasonCode, string.Create(CultureInfo.InvariantCulture,
                    $"next permitted attempt {(previous.LastAttemptUtc + MinimumAttemptInterval).UtcDateTime:O}"));
            if (shortfall > TimeSpan.Zero)
                await Task.Delay(shortfall, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        var started = timeProvider.GetUtcNow();
        var lastSuccess = previous?.LastSuccessUtc;
        try
        {
            await store.WriteStateAsync(new(started, manual, CameraAgentTimeDataRefreshOutcome.InProgress, InProgressReasonCode,
                string.Empty, null, lastSuccess), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AttemptFailed(logger, StorageFailedReasonCode, "the attempt record could not be written", exception);
            return Failed(StorageFailedReasonCode, "the attempt record could not be written; no request was made");
        }
        AttemptStarted(logger, manual ? "Manual" : "Scheduled");

        (CameraAgentTimeDataRefreshOutcome Outcome, string Code, string Detail) result;
        try
        {
            result = await AttemptAsync(started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(new(started, manual, CameraAgentTimeDataRefreshOutcome.Failed, CancelledReasonCode,
                "the attempt was cancelled before it completed; the adopted pair is unchanged", timeProvider.GetUtcNow(), lastSuccess)).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AttemptFailed(logger, StorageFailedReasonCode, "the pair could not be stored", exception);
            result = (CameraAgentTimeDataRefreshOutcome.Failed, StorageFailedReasonCode,
                string.Concat("the pair could not be stored; the adopted pair is unchanged; exception=", exception.GetType().Name));
        }

        var completed = timeProvider.GetUtcNow();
        var succeeded = result.Outcome != CameraAgentTimeDataRefreshOutcome.Failed;
        await CloseAsync(new(started, manual, result.Outcome, result.Code, result.Detail, completed,
            succeeded ? completed : lastSuccess)).ConfigureAwait(false);
        if (succeeded)
        {
            AttemptSucceeded(logger, result.Outcome.ToString(), result.Code);
            return new(LocalAutomationRunOutcome.Succeeded, Describe(result.Code, result.Detail));
        }
        AttemptRejected(logger, result.Code, result.Detail);
        return Failed(result.Code, result.Detail);
    }

    private async ValueTask<(CameraAgentTimeDataRefreshOutcome, string, string)> AttemptAsync(DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var eop = await client.FetchAsync(CameraAgentTimeDataSources.EarthOrientationUri,
            CameraAgentPinnedOrbitalTimeSource.MaximumEarthOrientationBytes, cancellationToken).ConfigureAwait(false);
        if (eop.Bytes is null) return Rejected(eop.ReasonCode!, "Earth orientation " + eop.Detail);
        var checksums = await client.FetchAsync(CameraAgentTimeDataSources.EarthOrientationChecksumsUri,
            CameraAgentTimeDataSources.MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
        if (checksums.Bytes is null) return Rejected(checksums.ReasonCode!, "checksum list " + checksums.Detail);
        if (CameraAgentTimeDataSources.PublishedEarthOrientationSha512(checksums.Bytes) is not { } sha512)
            return Rejected(ChecksumUnavailableReasonCode, "the checksum list does not name the Earth-orientation file exactly once");
        if (!CameraAgentTimeDataSources.MatchesSha512(eop.Bytes, sha512))
            return Rejected(ChecksumMismatchReasonCode, "the Earth-orientation file does not match its published SHA-512");
        var leap = await client.FetchAsync(CameraAgentTimeDataSources.LeapSecondsUri,
            CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes, cancellationToken).ConfigureAwait(false);
        if (leap.Bytes is null) return Rejected(leap.ReasonCode!, "leap seconds " + leap.Detail);
        if (CameraAgentTimeDataSources.VerifiedLeapSecondsHash(leap.Bytes) is not { } leapHash)
            return Rejected(LeapHashMismatchReasonCode, "the leap-second file does not match its own #h hash");
        if (!CameraAgentTimeDataSources.CarriesLeapSecondsTerms(leap.Bytes))
            return Rejected(TermsMissingReasonCode, "the leap-second file no longer states that it is in the public domain");

        OrbitalTimeData candidateData;
        try
        {
            candidateData = OrbitalTimeData.FromFinals2000A(eop.Bytes, leap.Bytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            return Rejected(ParseFailedReasonCode, string.Concat("the product validators rejected the pair; exception=", exception.GetType().Name));
        }
        if (candidateData.NominalZeroEarthOrientation)
            return Rejected(ParseFailedReasonCode, "the pair carries no Earth-orientation values");
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = new CameraAgentTimeDataCandidate(eop.Bytes, eop.LastModifiedUtc, sha512, leap.Bytes, leap.LastModifiedUtc,
            leapHash, started);
        var candidateEnd = CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(candidateData);
        var adopted = await ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var current = adopted is null ? null : await ReadAdoptedAsync(adopted.Pair, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            if (current.Data.EarthOrientationSha256 == candidateData.EarthOrientationSha256 &&
                current.Data.LeapSecondsSha256 == candidateData.LeapSecondsSha256)
                return (CameraAgentTimeDataRefreshOutcome.Unchanged, UnchangedReasonCode, Coverage("the adopted pair is current", candidateEnd));
            var currentEnd = CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(current.Data);
            if (candidateEnd < currentEnd)
                return (CameraAgentTimeDataRefreshOutcome.NotNewer, NotNewerReasonCode, string.Create(CultureInfo.InvariantCulture,
                    $"the download covers planning to {candidateEnd.UtcDateTime:O}, before the adopted pair's {currentEnd.UtcDateTime:O}; the adopted pair is kept"));
        }
        // A damaged current pair must not bypass protection of the intact predecessor covering now. The store also
        // verifies retention independently, so a direct adoption cannot replace that predecessor with a damaged pair.
        var now = timeProvider.GetUtcNow();
        var instant = new OrbitalUtcInterval(now, now);
        if (!CameraAgentPinnedOrbitalTimeSource.Covers(candidateData, instant) &&
            (current is null || !CameraAgentPinnedOrbitalTimeSource.Covers(current.Data, instant)) &&
            adopted?.Previous is { } previous && await ReadAdoptedAsync(previous, cancellationToken).ConfigureAwait(false) is { } replaced &&
            CameraAgentPinnedOrbitalTimeSource.Covers(replaced.Data, instant))
            return (CameraAgentTimeDataRefreshOutcome.NotNewer, NotNewerReasonCode,
                "neither the download nor an intact adopted current pair covers the current time, and the intact predecessor does; the adopted pairs are kept");
        var pointer = await store.AdoptAsync(candidate, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return (CameraAgentTimeDataRefreshOutcome.Adopted, AdoptedReasonCode, Coverage(
            string.Concat("pair ", pointer.Pair, " eop=", candidate.EarthOrientationSha256, " leap=", candidate.LeapSecondsSha256), candidateEnd));

        static (CameraAgentTimeDataRefreshOutcome, string, string) Rejected(string code, string detail)
            => (CameraAgentTimeDataRefreshOutcome.Failed, code, string.Concat(detail, "; the adopted pair is unchanged"));

        static string Coverage(string text, DateTimeOffset end)
            => string.Create(CultureInfo.InvariantCulture, $"{text}; planning coverage to {end.UtcDateTime:O}");
    }

    /// <summary>The adopted pointer, or null when there is none or it is damaged.</summary>
    private async ValueTask<CameraAgentManagedTimePointer?> ReadPointerAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A verified adopted pair, or null when it no longer verifies.</summary>
    private async ValueTask<CameraAgentPinnedOrbitalTimeSource.PinnedPair?> ReadAdoptedAsync(string pair, CancellationToken cancellationToken)
        => await CameraAgentPinnedOrbitalTimeSource.ReadProvenancedAsync(store.PairDirectory(pair),
            CameraAgentManagedTimeDataStore.ProvenanceSchema, cancellationToken).ConfigureAwait(false);

    /// <summary>Closes the attempt record even after cancellation: one small local write, never a request.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Closing the record is best-effort; the InProgress record then reads as interrupted.")]
    private async ValueTask CloseAsync(CameraAgentTimeDataRefreshState state)
    {
        try
        {
            await store.WriteStateAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CloseFailed(logger, exception.GetType().Name, exception);
        }
    }

    private static LocalAutomationExecution Skipped(string code, string text) => new(LocalAutomationRunOutcome.Skipped, Describe(code, text));

    private static LocalAutomationExecution Failed(string code, string text) => new(LocalAutomationRunOutcome.Failed, Describe(code, text));

    /// <summary><c>{code}: {text}</c>, bounded to the run-detail limit.</summary>
    internal static string Describe(string code, string text)
    {
        var value = string.Concat(code, ": ", text);
        return value.Length <= LocalAutomationContract.MaximumDetailLength ? value : value[..LocalAutomationContract.MaximumDetailLength];
    }

    [LoggerMessage(7770, LogLevel.Information, "Time-data refresh attempt started ({Trigger}).")]
    private static partial void AttemptStarted(ILogger logger, string trigger);

    [LoggerMessage(7771, LogLevel.Information, "Time-data refresh attempt completed as {Outcome} ({ReasonCode}).")]
    private static partial void AttemptSucceeded(ILogger logger, string outcome, string reasonCode);

    [LoggerMessage(7772, LogLevel.Warning, "Time-data refresh attempt failed ({ReasonCode}): {Detail}. The adopted pair is unchanged.")]
    private static partial void AttemptRejected(ILogger logger, string reasonCode, string detail);

    [LoggerMessage(7773, LogLevel.Warning, "Time-data refresh failed ({ReasonCode}): {Detail}.")]
    private static partial void AttemptFailed(ILogger logger, string reasonCode, string detail, Exception exception);

    [LoggerMessage(7774, LogLevel.Warning, "The time-data refresh record could not be read ({ExceptionType}); it is replaced by this attempt.")]
    private static partial void StateUnreadable(ILogger logger, string exceptionType);

    [LoggerMessage(7775, LogLevel.Error, "The time-data refresh record could not be closed ({ExceptionType}); it reads as interrupted.")]
    private static partial void CloseFailed(ILogger logger, string exceptionType, Exception exception);
}
