using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

/// <summary>Freezes captured facts under the raw retention lock, then restores at most three verified payloads at a time.</summary>
internal sealed class TimeLapseSourceReader(IOptions<CameraAgentHostOptions> options, RawCaptureIngress ingress,
    SqliteCaptureProcessingStore processing, SqliteTimeLapseStore store,
    Func<IDeploymentLocationStore?> locations, IPlanetEphemeris ephemeris)
{
    internal async ValueTask<CameraAgentTimeLapseSourcePlan> FreezeAsync(CameraAgentTimeLapseJob job, CancellationToken token)
    {
        if (await store.GetPlanAsync(job.JobId, token).ConfigureAwait(false) is { } frozen) return frozen;
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        await processing.InitializeAsync(token).ConfigureAwait(false);
        var lifecycle = RawIngressLifecycleLock.ForRoot(options.Value.RawIngressRoot);
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var candidates = await processing.ReadTimeLapseRawSourcesAsync(job.Preset.AgentId, job.Window.StartUtc.AddMinutes(-1), job.Window.EndUtc, 8192, token)
                .ConfigureAwait(false);
            if (candidates.Count > 8192) throw new InvalidDataException("The hourly source window exceeds 8192 captures.");
            var accepted = new List<CameraAgentTimeLapseRawSource>();
            var exclusions = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var root = PhysicalRoot.Open(options.Value.RawIngressRoot);
            var rig = RigProjectionContextFactory.CreateProfileHashSha256(job.Preset.Rig);
            foreach (var candidate in candidates)
            {
                var descriptor = candidate.Descriptor;
                var utc = descriptor.Timing.ExposureStartedUtc;
                string? reason = null;
                if (!string.Equals(descriptor.Profiles.Rig.Sha256, rig, StringComparison.OrdinalIgnoreCase)) reason = "timelapse.rig-mismatch";
                else if (descriptor.Artifact.Role != FrameArtifactRole.Raw || descriptor.Layout.Width > 4096 || descriptor.Layout.Height > 4096 ||
                    (descriptor.Layout.PixelFormat == CameraPixelFormat.BayerRggb16 && (long)descriptor.Layout.Width * descriptor.Layout.Height > TimeLapseFrameRenderer.MaximumColorSourcePixels) ||
                    descriptor.Layout.Width != job.Preset.Rig.Sensor.WidthPixels || descriptor.Layout.Height != job.Preset.Rig.Sensor.HeightPixels ||
                    descriptor.Layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
                    descriptor.Layout.ByteLength > 64 * 1024 * 1024) reason = "timelapse.unsupported-source";
                else if (!AtSite(descriptor, job)) reason = "timelapse.location-mismatch";
                else if (job.Window.Policy.Selection == LocalAutomationSourceSelection.DarkNightActualSources &&
                    SolarAltitudeClassifier.DirectionAt(ephemeris, utc, job.Window.ReportingPeriod.Site.LatitudeDegrees,
                        job.Window.ReportingPeriod.Site.LongitudeDegrees).AltitudeDegrees > -18) reason = "timelapse.solar-excluded";
                else if (!File.Exists(root.Resolve(candidate.PayloadRelativePath)) || !File.Exists(root.Resolve(candidate.SidecarRelativePath)))
                    reason = "timelapse.source-unavailable";
                if (reason is null) accepted.Add(candidate);
                else if (utc >= job.Window.StartUtc) exclusions[reason] = exclusions.GetValueOrDefault(reason) + 1;
            }
            // Only the two immediate preceding admitted captures can contribute to the initial rolling stack.
            var sources = accepted.Where(source => source.Descriptor.Timing.ExposureStartedUtc < job.Window.StartUtc).TakeLast(2)
                .Concat(accepted.Where(source => source.Descriptor.Timing.ExposureStartedUtc >= job.Window.StartUtc)).ToArray();
            var plan = new CameraAgentTimeLapseSourcePlan(sources, exclusions);
            await store.FreezePlanAsync(job.JobId, plan, token).ConfigureAwait(false);
            return plan;
        }
        finally { lifecycle.Release(); }
    }

    internal async ValueTask<ProcessingArtifact> RestoreAsync(CameraAgentTimeLapseRawSource source, CancellationToken token)
    {
        var descriptor = source.Descriptor;
        var path = PhysicalRoot.Open(options.Value.RawIngressRoot).Resolve(source.PayloadRelativePath);
        DurableSync.RequireRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length != descriptor.Layout.ByteLength || stream.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("A frozen video source has an invalid payload length.");
        var payload = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        if (!string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A frozen video source differs from its committed checksum.");
        return Describe(descriptor) with { Payload = payload };
    }

    internal static ProcessingArtifact Describe(ReconstructionDescriptor descriptor)
        => new(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
            descriptor.Layout, ReadOnlyMemory<byte>.Empty, descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor), descriptor.Capture.CaptureSequence,
            ObservationStartedUtc: descriptor.Timing.ExposureStartedUtc,
            ObservationEndedUtc: ProcessingArtifact.ResolveObservationEndedUtc(descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure),
            Conditions: new(descriptor.Controls.EffectiveGain, descriptor.Controls.EffectiveOffset, descriptor.Controls.TemperatureSetpointC));

    internal bool IsDaytime(CameraAgentTimeLapseJob job, DateTimeOffset utc) =>
        SolarAltitudeClassifier.DirectionAt(ephemeris, utc, job.Window.ReportingPeriod.Site.LatitudeDegrees,
            job.Window.ReportingPeriod.Site.LongitudeDegrees).AltitudeDegrees >= -6;

    private bool AtSite(ReconstructionDescriptor descriptor, CameraAgentTimeLapseJob job)
    {
        ObservatoryLocation? location;
        try
        {
            location = descriptor.Location is { } provenance
                ? locations()?.Resolve(provenance, descriptor.Timing.ExposureStartedUtc).ToObservatoryLocation()
                : job.Preset.LegacyObservatory;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { return false; }
        var site = job.Window.ReportingPeriod.Site;
        return location is not null && location.LatitudeDegrees == site.LatitudeDegrees &&
            location.LongitudeDegrees == site.LongitudeDegrees && location.ElevationMeters == site.ElevationMeters &&
            location.TimeZoneId == site.TimeZoneId;
    }
}
