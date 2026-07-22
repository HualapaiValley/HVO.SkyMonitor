using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralTransientMaskSource(
    TransientTemporalPosition Position,
    Guid CentralArtifactId,
    TransientDetectorInput Input,
    ReconstructionDescriptor Descriptor);

internal interface ICentralTransientMaskFactory
{
    Task<TransientDetectorMask[]?> CreateAsync(
        IReadOnlyList<CentralTransientMaskSource> sources,
        CentralTransientExecutionOptionsV1 options,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientMaskFactory(
    ApplicationDbContext dbContext,
    ICelestialCatalog celestialCatalog,
    ICelestialCatalogMetadataSource catalogMetadata) : ICentralTransientMaskFactory
{
    private static readonly JsonSerializerOptions RigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string EmptyMaskProfileSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(
        JsonSerializer.SerializeToElement(new { mode = "none" }));

    public async Task<TransientDetectorMask[]?> CreateAsync(
        IReadOnlyList<CentralTransientMaskSource> sources,
        CentralTransientExecutionOptionsV1 options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        if (sources.Count == 0 || sources.Select(item => item.Descriptor.Profiles.Mask).Distinct().Count() != 1)
        {
            return null;
        }
        var maskProfile = sources[0].Descriptor.Profiles.Mask;
        if (!string.Equals(maskProfile.Name, "mask", StringComparison.Ordinal) ||
            !string.Equals(maskProfile.Version, "none-v1", StringComparison.Ordinal) ||
            !string.Equals(maskProfile.Sha256, EmptyMaskProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var frame = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(item => item.Id == sources[0].CentralArtifactId)
            .Select(item => new
            {
                item.Frame!.RegistrationId,
                RigProfile = item.Frame.DeviceRigProfile,
                RigIdentity = item.Frame.Profiles.Where(profile => profile.Kind == CentralProfileKind.Rig)
                    .Select(profile => profile.Sha256).Single()
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (frame?.RigProfile is null)
        {
            return null;
        }
        var registration = await dbContext.DeviceRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == frame.RegistrationId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return null;
        }

        CameraRigConfig? rig;
        try
        {
            rig = JsonSerializer.Deserialize<CameraRigConfig>(frame.RigProfile.ConfigJson, RigJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (rig is null)
        {
            return null;
        }
        string rigSha256;
        ProjectionContext projection;
        try
        {
            rigSha256 = RigProjectionContextFactory.CreateProfileHashSha256(rig);
            projection = RigProjectionContextFactory.Create(rig);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
        if (!string.Equals(rigSha256, frame.RigIdentity, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rigSha256, frame.RigProfile.ProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            sources.Any(item => !string.Equals(
                item.Descriptor.Profiles.Rig.Sha256, rigSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var detectorLayout = sources[0].Input.Descriptor.Layout;
        var scaleX = (double)detectorLayout.Width / projection.WidthPixels;
        var scaleY = (double)detectorLayout.Height / projection.HeightPixels;
        if (Math.Abs(scaleX - scaleY) > 1e-12 || scaleX is <= 0 or > 1 ||
            sources.Any(item => item.Input.Descriptor.Layout != detectorLayout))
        {
            return null;
        }

        var skyBits = new byte[Linear16MaskOperations.RequiredByteLength(detectorLayout.Width, detectorLayout.Height)];
        var imageCircleBits = new byte[skyBits.Length];
        var horizonBits = new byte[skyBits.Length];
        var projector = ProjectorFactory.Create(projection);
        for (var y = 0; y < detectorLayout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < detectorLayout.Width; x++)
            {
                var sourcePixel = new PixelPoint((x + 0.5) / scaleX, (y + 0.5) / scaleY);
                var direction = projector.Unproject(sourcePixel);
                var pixelIndex = checked(y * detectorLayout.Width + x);
                if (direction is null)
                {
                    SetMaskBit(skyBits, pixelIndex);
                    SetMaskBit(imageCircleBits, pixelIndex);
                }
                else if (direction.Value.AltitudeDegrees < 0)
                {
                    SetMaskBit(horizonBits, pixelIndex);
                }
            }
        }

        var observer = new ObserverLocation(
            registration.ObservatoryLatitudeDegrees,
            registration.ObservatoryLongitudeDegrees,
            registration.ObservatoryElevationMeters);
        var supports = new List<Linear16CircularMaskRegion>();
        var supportRadius = Math.Ceiling(4 * options.StarSourceSupportRadiusPixels * scaleX) + 1;
        foreach (var source in sources.OrderBy(item => item.Position))
        {
            var scene = await new VisibleSceneBuilder(celestialCatalog).BuildAsync(new VisibleSceneRequest(
                source.Descriptor.Timing.ExposureStartedUtc,
                observer,
                projection,
                new CatalogQuery(options.StarMaximumMagnitude, options.StarMaximumResults),
                catalogMetadata.Metadata,
                horizonPolicy: HorizonPolicy.GeometricHorizon,
                projectionVersion: rig.Optics.CalibrationVersion), cancellationToken).ConfigureAwait(false);
            supports.AddRange(scene.Objects.Select(item => new Linear16CircularMaskRegion(
                item.Pixel.X * scaleX,
                item.Pixel.Y * scaleY,
                supportRadius)));
        }

        var empty = Linear16MaskOperations.Empty(detectorLayout.Width, detectorLayout.Height);
        var profileVersion = maskProfile.Sha256.ToUpperInvariant();
        return
        [
            TransientDetectorMask.Create(TransientDetectorMaskKind.Sky,
                new ProcessingAlgorithmIdentity("central-sky-mask", rigSha256),
                new Linear16PixelMask(detectorLayout.Width, detectorLayout.Height, skyBits)),
            TransientDetectorMask.Create(TransientDetectorMaskKind.ImageCircle,
                new ProcessingAlgorithmIdentity("central-image-circle-mask", RigProjectionContextFactory.AlgorithmVersion),
                new Linear16PixelMask(detectorLayout.Width, detectorLayout.Height, imageCircleBits)),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Horizon,
                new ProcessingAlgorithmIdentity("central-geometric-horizon-mask", RigProjectionContextFactory.AlgorithmVersion),
                new Linear16PixelMask(detectorLayout.Width, detectorLayout.Height, horizonBits)),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Obstruction,
                new ProcessingAlgorithmIdentity("central-obstruction-mask", profileVersion), empty),
            TransientDetectorMask.Create(TransientDetectorMaskKind.BadPixel,
                new ProcessingAlgorithmIdentity("central-bad-pixel-mask", profileVersion), empty),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Star,
                new ProcessingAlgorithmIdentity("catalog-projected-star-mask", catalogMetadata.Metadata.Checksum),
                Linear16MaskOperations.CreateCircularSupportMask(
                    detectorLayout.Width, detectorLayout.Height, supports, cancellationToken))
        ];
    }

    private static void SetMaskBit(byte[] bits, int pixelIndex)
        => bits[pixelIndex >> 3] |= (byte)(1 << (pixelIndex & 7));
}
