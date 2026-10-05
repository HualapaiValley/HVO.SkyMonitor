using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

internal sealed class CentralTimeLapseSources(ApplicationDbContext db, ICentralArtifactObjectReader reader)
{
    private sealed record Inventory(int Count, int ReconstructedCount, DateTimeOffset? LatestReceiptUtc);
    private static readonly JsonSerializerOptions RigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<string> DiscoveryIdentityAsync(Guid deviceId, Guid observatoryId, SunriseReportingPeriod period,
        DateTimeOffset start, DateTimeOffset end, TimeSpan settle, CentralTimeLapsePreset preset, CancellationToken token)
    {
        var historyStart = start.AddMinutes(-1);
        var inventory = await db.CentralArtifacts.AsNoTracking().Where(item => item.DevicePublicId == deviceId &&
                item.Frame!.ObservatoryId == observatoryId && item.Role == FrameArtifactRole.Raw &&
                item.Frame.Timing!.ExposureStartedUtc >= historyStart && item.Frame.Timing.ExposureStartedUtc < end)
            .GroupBy(static _ => 1).Select(group => new Inventory(group.Count(),
                group.Count(item => item.ReconstructionState == CentralReconstructionState.Complete),
                group.Max(item => (DateTimeOffset?)item.ReceivedAtUtc)))
            .SingleOrDefaultAsync(token).ConfigureAwait(false) ?? new(0, 0, null);
        return DiscoveryIdentity(deviceId, observatoryId, period, start, end, settle, preset, inventory);
    }

    private static string DiscoveryIdentity(Guid deviceId, Guid observatoryId, SunriseReportingPeriod period,
        DateTimeOffset start, DateTimeOffset end, TimeSpan settle, CentralTimeLapsePreset preset, Inventory inventory)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new { deviceId, observatoryId, period.IdentitySha256, start, end, settle, preset, inventory });

    internal async Task<(Guid ObservatoryId, CameraRigConfig Rig, DeploymentLocationSnapshot Site)> ResolveAsync(
        CentralTimeLapseTarget target, CancellationToken token)
    {
        var hash = target.RigProfileSha256.ToUpperInvariant();
        var profile = await db.DeviceRigProfiles.AsNoTracking().Where(item => item.DevicePublicId == target.DevicePublicId && item.ProfileSha256 == hash)
            .OrderBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected received rig profile is unavailable.");
        var rig = JsonSerializer.Deserialize<CameraRigConfig>(profile.ConfigJson, RigJson)
            ?? throw new InvalidDataException("The selected received rig profile is invalid.");
        if (!string.Equals(RigProjectionContextFactory.CreateProfileHashSha256(rig), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected rig checksum does not match its received configuration.");
        var location = await db.DeviceDeploymentLocationVersions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.DevicePublicId == target.DevicePublicId && item.ObservatoryId == profile.ObservatoryId &&
            item.LocationId == target.LocationId && item.Version == target.LocationVersion &&
            item.Status == DeploymentLocationResolutionStatus.Acknowledged, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected accepted deployment location is unavailable.");
        var site = new DeploymentLocationSnapshot(location.LocationId, location.Version, location.CanonicalSha256,
            location.Source, location.HorizontalAccuracyMeters, location.EffectiveFromUtc, location.EffectiveUntilUtc,
            location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, location.TimeZoneId);
        if (!site.Validate().IsValid) throw new InvalidDataException("The selected deployment checksum is invalid.");
        return (profile.ObservatoryId, rig, site);
    }

    /// <summary>Called inside the queue's serializable transaction so retention cannot expire selected objects before holds commit.</summary>
    internal async Task<CentralTimeLapseRequest> FreezeAsync(Guid deviceId, Guid observatoryId, SunriseReportingPeriod period,
        DateTimeOffset start, DateTimeOffset end, TimeSpan settle, CentralTimeLapsePreset preset, CancellationToken token)
    {
        var historyStart = start.AddMinutes(-1);
        var rows = await db.CentralArtifacts.AsNoTracking().Where(item => item.DevicePublicId == deviceId && item.Frame!.ObservatoryId == observatoryId &&
                item.Role == FrameArtifactRole.Raw && item.Frame.Timing!.ExposureStartedUtc >= historyStart && item.Frame.Timing.ExposureStartedUtc < end)
            .OrderBy(item => item.Frame!.Timing!.ExposureStartedUtc).ThenBy(item => item.ArtifactId).Take(8193)
            .Include(item => item.Layout).Include(item => item.Recipe).Include(item => item.Sources)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location).AsSplitQuery().ToArrayAsync(token).ConfigureAwait(false);
        if (rows.Length > 8192) throw new InvalidDataException("The central video source window exceeds its 8192-capture limit.");
        var sources = new List<CentralTimeLapseSource>();
        var exclusions = new Dictionary<string, int>(StringComparer.Ordinal);
        var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(preset.Rig);
        foreach (var row in rows)
        {
            string? reason = null;
            if (row.ObjectState != CentralArtifactObjectState.Available || row.ReconstructionState != CentralReconstructionState.Complete || row.RetentionDeletionToken.HasValue)
                reason = "timelapse.source-unavailable";
            else if (row.Layout is not { Width: >= 2 and <= 4096, Height: >= 2 and <= 4096 } layout || row.ByteLength > 64L * 1024 * 1024 ||
                     (layout.PixelFormat == nameof(CameraPixelFormat.BayerRggb16) && (long)layout.Width * layout.Height > TimeLapseFrameRenderer.MaximumColorSourcePixels) ||
                     layout.Width != preset.Rig.Sensor.WidthPixels || layout.Height != preset.Rig.Sensor.HeightPixels ||
                     layout.PixelFormat is not (nameof(CameraPixelFormat.Mono16) or nameof(CameraPixelFormat.BayerRggb16)))
                reason = "timelapse.source-layout";
            else if (row.Frame!.LocationEvidenceState != CentralCaptureLocationEvidenceState.ReportedResolved ||
                     row.Frame.Location?.LocationId != period.Site.LocationId || row.Frame.Location.Version != period.Site.Version ||
                     !row.Frame.Profiles.Any(profile => profile.Kind == CentralProfileKind.Rig && string.Equals(profile.Sha256, rigHash, StringComparison.OrdinalIgnoreCase)))
                reason = "timelapse.source-profile";
            if (reason is not null)
            {
                if (row.Frame!.Timing!.ExposureStartedUtc >= start) exclusions[reason] = exclusions.GetValueOrDefault(reason) + 1;
                continue;
            }
            var descriptor = CentralReconstructionDescriptorFactory.Create(row.Frame!, row);
            if (!descriptor.Validate().IsValid) throw new InvalidDataException("A retained central descriptor is invalid.");
            sources.Add(new(row.Id, descriptor, row.StorageReference, row.ByteLength));
        }
        var retained = sources.Where(source => source.Descriptor.Timing.ExposureStartedUtc < start).TakeLast(2)
            .Concat(sources.Where(source => source.Descriptor.Timing.ExposureStartedUtc >= start)).ToArray();
        return new(deviceId, observatoryId, period, start, end, end + settle, false, preset, retained, exclusions, [])
        {
            DiscoveryIdentity = DiscoveryIdentity(deviceId, observatoryId, period, start, end, settle, preset,
                new(rows.Length, rows.Count(static row => row.ReconstructionState == CentralReconstructionState.Complete),
                    rows.Length == 0 ? null : rows.Max(static row => row.ReceivedAtUtc)))
        };
    }

    internal async ValueTask<ProcessingArtifact> RestoreAsync(CentralTimeLapseSource source, CancellationToken token)
    {
        if (source.ByteLength is < 1 or > 64L * 1024 * 1024 || source.ByteLength != source.Descriptor.Layout.ByteLength)
            throw new InvalidDataException("Invalid bounded central video source.");
        var artifact = new CentralArtifact
        {
            Id = source.CentralArtifactId,
            StorageReference = source.StorageReference,
            ByteLength = source.ByteLength,
            ChecksumSha256 = source.Descriptor.Artifact.ChecksumSha256
        };
        var snapshot = await reader.VerifyAsync(artifact, token).ConfigureAwait(false);
        var payload = GC.AllocateUninitializedArray<byte>((int)source.ByteLength);
        await using var destination = new MemoryStream(payload, writable: true);
        await reader.CopyToAsync(snapshot, destination, null, token).ConfigureAwait(false);
        if (destination.Position != source.ByteLength || !FrameReconstructor.TryReconstruct(source.Descriptor, payload, out _).IsValid)
            throw new InvalidDataException("The received video source failed reconstruction.");
        return LogicHostRecipeExecutionAdapter.CreateArtifact(source.Descriptor) with { Payload = payload };
    }
}
