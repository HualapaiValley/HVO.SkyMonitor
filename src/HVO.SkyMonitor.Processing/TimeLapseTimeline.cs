using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Captured facts retained even when dense captures are sampled out of the presentation timeline.</summary>
public sealed record TimeLapseSource(
    Guid ArtifactId,
    string IdentitySha256,
    DateTimeOffset ObservationUtc,
    TimeSpan Exposure,
    string? ExclusionReasonCode = null);

/// <summary>A displayed source, or an explicit missing-data interval. Times are relative to the planned window.</summary>
public sealed record TimeLapseInterval(int? SourceOrdinal, long StartTick, long DurationTicks)
{
    public long EndTick => checked(StartTick + DurationTicks);
}

/// <summary>Pure presentation policy. Exposure and stack integration never determine playback duration.</summary>
public sealed record TimeLapseTimingOptions(
    int Compression = 180,
    int MaximumHoldSeconds = 60,
    int FallbackCadenceSeconds = 5);

/// <summary>
/// A complete planned wall-clock axis, including gaps. No paths, processes or host storage identities belong here.
/// Integer cumulative rounding bounds error to half a video tick, rather than accumulating per-frame drift.
/// </summary>
public sealed record TimeLapseTimeline(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimeLapseTimingOptions Options,
    ImmutableArray<TimeLapseSource> Sources,
    ImmutableArray<TimeLapseInterval> Intervals,
    ImmutableArray<int> SampledOutOrdinals,
    long DurationTicks,
    string IdentitySha256)
{
    public const string Version = "hvo-timelapse-timestamp-holds-v1";
    public const int TicksPerSecond = 1_000_000;
    public const int MaximumSources = 40_000;
    public const int MaximumFramesPerSecond = 60;

    public bool HasSources => Intervals.Any(static interval => interval.SourceOrdinal.HasValue);
    public bool HasGaps => Intervals.Any(static interval => interval.SourceOrdinal is null);
}

/// <summary>Bounded timestamp selection, with a measured-cadence tail and visible gaps after the hold limit.</summary>
public static class TimeLapseTimelinePlanner
{
    public static TimeLapseTimeline Create(DateTimeOffset startUtc, DateTimeOffset endUtc,
        IEnumerable<TimeLapseSource> sources, TimeLapseTimingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        options ??= new();
        if (startUtc.Offset != TimeSpan.Zero || endUtc.Offset != TimeSpan.Zero || endUtc <= startUtc ||
            endUtc - startUtc > TimeSpan.FromHours(48) || options.Compression is not (180 or 300) ||
            options.MaximumHoldSeconds is < 1 or > 300 ||
            options.FallbackCadenceSeconds < 1 || options.FallbackCadenceSeconds > options.MaximumHoldSeconds)
            throw new ArgumentException("Invalid time-lapse window or timing policy.");

        var ordered = sources.Take(TimeLapseTimeline.MaximumSources + 1)
            .OrderBy(static source => source.ObservationUtc).ThenBy(static source => source.ArtifactId).ToImmutableArray();
        if (ordered.Length > TimeLapseTimeline.MaximumSources ||
            ordered.Select(static source => source.ArtifactId).Distinct().Count() != ordered.Length ||
            ordered.Any(source => source.ArtifactId == Guid.Empty ||
                source.IdentitySha256 is not { Length: 64 } || !source.IdentitySha256.All(char.IsAsciiHexDigit) ||
                source.ObservationUtc.Offset != TimeSpan.Zero || source.ObservationUtc < startUtc ||
                source.ObservationUtc >= endUtc || source.Exposure < TimeSpan.Zero || source.Exposure > TimeSpan.FromHours(24) ||
                source.ExclusionReasonCode is { } reason && (string.IsNullOrWhiteSpace(reason) || reason.Length > 128)))
            throw new ArgumentException("Time-lapse sources exceed the bound or contain invalid captured facts.", nameof(sources));

        long Tick(DateTimeOffset utc) => checked((long)decimal.Round(
            (decimal)(utc - startUtc).Ticks * TimeLapseTimeline.TicksPerSecond /
            (TimeSpan.TicksPerSecond * options.Compression), 0, MidpointRounding.AwayFromZero));

        var duration = Tick(endUtc);
        if (duration < TimeLapseTimeline.TicksPerSecond / TimeLapseTimeline.MaximumFramesPerSecond)
            throw new ArgumentException("The planned video must span at least one presentation frame.");
        var intervals = ImmutableArray.CreateBuilder<TimeLapseInterval>();
        var sampledOut = ImmutableArray.CreateBuilder<int>();
        var selected = new List<int>();
        // Keep the first capture in each 1/60-second video bin. The complete input lineage and omitted ordinals
        // remain in the plan. Daytime five-second cadence yields 36 unique frames/s at 180x or 60 at 300x.
        long priorBin = -1;
        for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            // Select against the unrounded capture clock. Binning already-rounded microseconds would put
            // every third exact 60-fps capture just below its boundary and incorrectly discard daytime frames.
            var bin = (long)((decimal)(ordered[ordinal].ObservationUtc - startUtc).Ticks * TimeLapseTimeline.MaximumFramesPerSecond /
                (TimeSpan.TicksPerSecond * options.Compression));
            if (bin == priorBin) sampledOut.Add(ordinal);
            else { selected.Add(ordinal); priorBin = bin; }
        }

        var cadence = selected.Zip(selected.Skip(1), (a, b) => ordered[b].ObservationUtc - ordered[a].ObservationUtc)
            .Where(delta => delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(options.MaximumHoldSeconds))
            .TakeLast(31).Order().ToArray();
        var tail = cadence.Length == 0 ? TimeSpan.FromSeconds(options.FallbackCadenceSeconds) : cadence[cadence.Length / 2];
        long cursor = 0;
        for (var index = 0; index < selected.Count; index++)
        {
            var ordinal = selected[index];
            var source = ordered[ordinal];
            var start = Tick(source.ObservationUtc);
            if (start > cursor) intervals.Add(new(null, cursor, start - cursor));
            var remaining = endUtc - source.ObservationUtc;
            var next = index + 1 < selected.Count ? ordered[selected[index + 1]].ObservationUtc - source.ObservationUtc : tail;
            var hold = new[] { remaining, next, TimeSpan.FromSeconds(options.MaximumHoldSeconds) }.Min();
            var end = Tick(source.ObservationUtc + hold);
            if (end > start) intervals.Add(new(source.ExclusionReasonCode is null ? ordinal : null, start, end - start));
            cursor = end;
        }
        if (cursor < duration) intervals.Add(new(null, cursor, duration - cursor));
        var coalesced = ImmutableArray.CreateBuilder<TimeLapseInterval>();
        foreach (var interval in intervals)
        {
            if (coalesced.Count > 0 && interval.SourceOrdinal is null && coalesced[^1].SourceOrdinal is null)
                coalesced[^1] = coalesced[^1] with { DurationTicks = coalesced[^1].DurationTicks + interval.DurationTicks };
            else coalesced.Add(interval);
        }
        var result = new TimeLapseTimeline(startUtc, endUtc, options, ordered, coalesced.ToImmutable(),
            sampledOut.ToImmutable(), duration, string.Empty);
        return result with
        {
            IdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                TimeLapseTimeline.Version,
                result.StartUtc,
                result.EndUtc,
                result.Options,
                result.Sources,
                result.Intervals,
                result.SampledOutOrdinals,
                result.DurationTicks,
                TimeLapseTimeline.TicksPerSecond
            })
        };
    }
}
