using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

/// <summary>Discovers only closed bounded windows for explicitly selected received devices. No CameraAgent transport is involved.</summary>
internal sealed partial class CentralTimeLapseScheduler(CentralTimeLapseStore store, CentralTimeLapseSources sources,
    FFmpegTimeLapseEncoder encoder, IOptions<CentralTimeLapseOptions> options, TimeProvider clock, ILogger<CentralTimeLapseScheduler> logger)
{
    internal async Task ScanAsync(CentralTimeLapseTarget target, CancellationToken token)
    {
        var settings = options.Value;
        settings.Validate();
        var resolved = await sources.ResolveAsync(target, token).ConfigureAwait(false);
        var calendar = new SunriseReportingCalendar(resolved.Site);
        var now = clock.GetUtcNow();
        var currentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, calendar.TimeZone).DateTime);
        var profile = TimeLapseEncoderProfile.Software;
        var capability = settings.PreferNvidia ? await encoder.QualifyAsync(TimeLapseEncoderProfile.Nvidia, token).ConfigureAwait(false) : null;
        if (capability?.Available == true) profile = TimeLapseEncoderProfile.Nvidia;
        else capability = await encoder.QualifyAsync(TimeLapseEncoderProfile.Software, token).ConfigureAwait(false);
        // Pin a profile for a complete scan. A change of available hardware/binary creates a successor set of hours.
        var preset = new CentralTimeLapsePreset(resolved.Rig, settings.Frames, settings.Timing, settings.MaximumDimension,
            profile, CaptureContractJson.ComputeCanonicalJsonSha256(capability), target.Generation);
        for (var offset = settings.CatchUpDays; offset >= 0; offset--)
        {
            var period = calendar.Resolve(currentDate.AddDays(-offset)).Period;
            if (period is null) continue;
            var hours = SunriseReportingCalendar.PartitionCivilHours(period);
            var children = new List<Guid>();
            foreach (var hour in hours)
            {
                if (now < hour.EndUtc + settings.SettleAllowance) break;
                // Immutable capture metadata survives raw retention. Availability changes alone must not replace a
                // completed hour with a new empty revision, or re-read all historical descriptors every minute.
                try
                {
                    var discovery = await sources.DiscoveryIdentityAsync(target.DevicePublicId, resolved.ObservatoryId, period,
                        hour.StartUtc, hour.EndUtc, settings.SettleAllowance, preset, token).ConfigureAwait(false);
                    if (await store.FindDiscoveredAsync(target.DevicePublicId, period.ReportDate, discovery, token).ConfigureAwait(false) is { } known)
                    {
                        children.Add(known);
                        continue;
                    }
                    children.Add(await store.EnqueueAsync(cancellationToken => sources.FreezeAsync(target.DevicePublicId,
                        resolved.ObservatoryId, period, hour.StartUtc, hour.EndUtc, settings.SettleAllowance, preset, cancellationToken), token).ConfigureAwait(false));
                }
                catch (Exception exception) when (!token.IsCancellationRequested && exception is InvalidDataException or ArgumentException)
                {
                    // One corrupt/overbound window must not prevent later hours or periods from being discovered.
                    // Omitting its child also prevents publication of an incomplete daily request.
                    Log.HourRejected(logger, target.DevicePublicId, hour.StartUtc, hour.EndUtc, exception.GetType().Name);
                }
            }
            if (children.Count == hours.Count && now >= period.EndUtc + settings.SettleAllowance)
                await store.EnqueueAsync(_ => Task.FromResult(new CentralTimeLapseRequest(target.DevicePublicId,
                    resolved.ObservatoryId, period, period.StartUtc, period.EndUtc, period.EndUtc + settings.SettleAllowance,
                    true, preset, [], new Dictionary<string, int>(), children)), token).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(11313, LogLevel.Warning, "Central time-lapse hour rejected for {DeviceId}, {StartUtc} to {EndUtc}: {FailureType}")]
        public static partial void HourRejected(ILogger logger, Guid deviceId, DateTimeOffset startUtc, DateTimeOffset endUtc, string failureType);
    }
}
