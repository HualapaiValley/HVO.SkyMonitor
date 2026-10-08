using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>Synthetic projected star fields, linear frames rendered from them and measured-association products.</summary>
internal static class MeasuredStellarAssociationFixtures
{
    public const int Width = 192, Height = 128;
    public const double Background = 1000;
    public static readonly Guid CaptureId = Guid.Parse("52600000-0000-0000-0000-000000000001");
    public static readonly Guid ArtifactId = Guid.Parse("52600000-0000-0000-0000-000000000002");
    public static readonly string DescriptorIdentity = new('D', 64);

    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

    /// <summary>
    /// A valid product in which every in-image star prediction is associated at its expected pixel with a
    /// label-eligible measurement, as a perfectly measured frame would report.
    /// </summary>
    public static MeasuredStellarAssociationsV1 AllEligible(ProjectedSceneV1 scene)
    {
        var aperture = new ProjectedSceneAperture(scene);
        var associations = new List<MeasuredStellarAssociationV1>();
        var unmatched = new List<MeasuredStellarUnmatchedPredictionV1>();
        foreach (var star in scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star)
                     .OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            var x = Math.Round(star.Pixel.X, 4);
            var y = Math.Round(star.Pixel.Y, 4);
            if (aperture.Contains(star.Pixel))
                associations.Add(new(star.Id, associations.Count, Math.Round(star.Magnitude, 6), x, y, x, y, 0, 0,
                    1000, 100, null, null, null, false, 0, false, null, null, true, null));
            else
                unmatched.Add(new(star.Id, Math.Round(star.Magnitude, 6), x, y, MeasuredStellarAssociationReasonCodes.OutsideAperture));
        }
        return new(
            MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            new(scene.Source.CaptureId, scene.Source.ArtifactId, scene.Source.ArtifactIdentitySha256.ToUpperInvariant(),
                FrameArtifactRole.Raw, "source", new string('0', 64), CameraPixelFormat.Mono16,
                scene.ImageTransform.OutputWidthPixels, scene.ImageTransform.OutputHeightPixels),
            new(scene.SceneIdentitySha256.ToUpperInvariant(), scene.Kind, scene.Catalog.Name, scene.Catalog.Version,
                scene.Catalog.ChecksumSha256.ToUpperInvariant(), scene.Projection.CalibrationVersion,
                scene.Projection.AlgorithmVersion, scene.AstronomyAlgorithmVersion),
            new("fixture-measurement-v1", new string('A', 64), "mono-linear-v1",
                StellarMeasurementStatus.Completed, Background, 1, associations.Count, associations.Count, 0),
            new(MeasuredStellarAssociator.AlgorithmVersion, new(), StellarLabelPolicy.Version, new()),
            associations,
            unmatched,
            []);
    }

    /// <summary>Stars offset from the zenith by (hour-angle degrees, declination degrees) for an equatorial observer.</summary>
    public static async Task<ProjectedSceneV1> StarFieldAsync(params (string Id, double EastDegrees, double NorthDegrees, double Magnitude)[] stars)
    {
        var utc = DateTimeOffset.Parse("2026-08-25T00:00:00Z", CultureInfo.InvariantCulture);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(stars.Select(star =>
            new CelestialCatalogObject(star.Id, star.Id.ToUpperInvariant(),
                (siderealHours + star.EastDegrees / 15 + 24) % 24, star.NorthDegrees, star.Magnitude)).ToArray()))
            .BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
                new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 400, 400, Width, Height,
                    ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
                new CatalogQuery(8, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1")).ConfigureAwait(false);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity),
            "calibration-v1", visible.Request.ProjectionVersion);
    }

    public static PixelPoint Pixel(ProjectedSceneV1 scene, string id) => scene.Objects.Single(item => item.Id == id).Pixel;

    public static double[] Field(int width = Width, int height = Height) =>
        Enumerable.Repeat(Background, width * height).ToArray();

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded, reproducible synthetic noise.")]
    public static void AddNoise(double[] pixels, int seed, double sigma)
    {
        var random = new Random(seed);
        for (var index = 0; index < pixels.Length; index++)
        {
            var u = 1 - random.NextDouble();
            var v = random.NextDouble();
            pixels[index] += sigma * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }
    }

    public static void AddGaussian(double[] pixels, PixelPoint center, double sigma, double peak, int width = Width)
    {
        for (var index = 0; index < pixels.Length; index++)
        {
            var dx = (index % width + 0.5 - center.X) / sigma;
            var dy = (index / width + 0.5 - center.Y) / sigma;
            var exponent = 0.5 * (dx * dx + dy * dy);
            if (exponent < 40)
                pixels[index] += peak * Math.Exp(-exponent);
        }
    }

    public static void AddTrail(double[] pixels, PixelPoint center, double length, double angleDegrees, double sigma, double flux)
    {
        const int steps = 400;
        var angle = angleDegrees * Math.PI / 180;
        for (var step = 0; step < steps; step++)
        {
            var offset = ((step + 0.5) / steps - 0.5) * length;
            AddGaussian(pixels, new(center.X + offset * Math.Cos(angle), center.Y + offset * Math.Sin(angle)), sigma,
                flux / steps / (2 * Math.PI * sigma * sigma));
        }
    }

    /// <summary>Encodes linear samples as a little-endian 16-bit raw artifact bound to the scene's capture.</summary>
    public static ProcessingArtifact RawArtifact(double[] pixels, CameraPixelFormat format = CameraPixelFormat.Mono16,
        int width = Width, int height = Height)
    {
        var bytes = new byte[pixels.Length * 2];
        for (var index = 0; index < pixels.Length; index++)
        {
            var value = (ushort)Math.Clamp(Math.Round(pixels[index]), 0, ushort.MaxValue);
            bytes[index * 2] = (byte)value;
            bytes[index * 2 + 1] = (byte)(value >> 8);
        }
        var layout = new FrameLayoutDescriptor(width, height, width * 2, format, FrameByteOrder.LittleEndian, 16, 16,
            FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            null, ushort.MaxValue, bytes.Length);
        return new ProcessingArtifact(ArtifactId, FrameArtifactRole.Raw, "source", new string('0', 64),
            "application/x-hvo-frame", layout, bytes,
            DateTimeOffset.Parse("2026-08-25T00:00:00Z", CultureInfo.InvariantCulture), TimeSpan.FromSeconds(1),
            Compatibility)
        {
            CaptureId = CaptureId,
            DescriptorIdentitySha256 = DescriptorIdentity
        };
    }

    public static ProcessingAuxiliaryInput SceneAuxiliary(ProjectedSceneV1 scene)
    {
        var payload = ProjectedSceneJson.Serialize(scene);
        return new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: payload)
        {
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
        };
    }

    public static ProcessingExecutionRequest Request(ProcessingArtifact source, ProjectedSceneV1 scene, string options = "{}")
    {
        using var document = System.Text.Json.JsonDocument.Parse(options);
        return new ProcessingExecutionRequest(BuiltInProcessingRecipes.MeasuredStellarAssociations,
            document.RootElement.Clone(), ProcessingInputSelector.Raw("source"), [source], "measured",
            AuxiliaryInputs: [SceneAuxiliary(scene)]);
    }
}
