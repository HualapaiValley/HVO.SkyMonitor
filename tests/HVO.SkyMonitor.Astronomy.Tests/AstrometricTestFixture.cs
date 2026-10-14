#pragma warning disable CA5394 // Fixed seeds define reproducible test images, never security material.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Astronomy.Tests;

internal static class AstrometricTestFixture
{
    internal static readonly DateTimeOffset Utc = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
    internal static readonly ObserverLocation Observer = new(35.347, -113.878, 1200);
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static AstrometricFrameContext Frame(DateTimeOffset utc, double seconds = 20) => new(
        new Guid(Convert.FromHexString(Hash("capture" + utc.ToString("O", CultureInfo.InvariantCulture)))[..16]), new Guid(Convert.FromHexString(Hash("artifact" + utc.ToString("O", CultureInfo.InvariantCulture)))[..16]),
        Hash("descriptor" + utc.ToString("O", CultureInfo.InvariantCulture)), Hash("pixels" + utc.ToString("O", CultureInfo.InvariantCulture)), Hash("observer"), Observer,
        utc.AddSeconds(-seconds / 2), utc.AddSeconds(seconds / 2), "stellar-detector-v1", Hash("test-detector-settings"));
    internal static AstrometricCatalogData Catalog()
    {
        var random = new Random(934817);
        var stars = Enumerable.Range(0, 560).Select(i => new CelestialCatalogObject($"SYN{i:0000}", $"Artificial {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1.8 + 3.4 * random.NextDouble())).ToArray();
        return new(new("Generated uniform test catalog", "1", new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))), "test-generated", "1"), stars, true, 7);
    }
    internal static ProjectionContext Truth(double fullField = 185) => new(ProjectionModel.EquidistantFisheye, 256, 256,
        232 / (fullField * Math.PI / 360), 232 / (fullField * Math.PI / 360), 512, 512, ProjectionAperture.Circular, 232, 72, 243, 17, true);
    internal static AstrometricCalibration Calibration(ProjectionContext truth) => new(truth with
    {
        FocalLengthXPixels = truth.FocalLengthXPixels / 1.037,
        FocalLengthYPixels = truth.FocalLengthYPixels / 1.037,
        BoresightAltitudeDegrees = 0,
        BoresightAzimuthDegrees = 0,
        RollDegrees = 0
    }, "known-numeric-intrinsics", Hash("readout"));
    internal static AltAzPoint Horizontal(CelestialCatalogObject star, DateTimeOffset utc) => CoordinateTransforms.EquatorialToHorizontal(
        EquatorialPrecession.PrecessJ2000(new(star.RightAscensionHours, star.DeclinationDegrees), utc), utc, Observer.LatitudeDegrees, Observer.LongitudeDegrees);
    internal static AstrometricDetection[] Centroids(AstrometricCatalogData catalog, ProjectionContext truth, DateTimeOffset utc)
    {
        var projector = ProjectorFactory.Create(truth); var random = new Random(812);
        var all = catalog.Stars.Select(s => (Star: s, Horizontal: Horizontal(s, utc))).Where(s => s.Horizontal.AltitudeDegrees > 5)
            .Select(s => (s.Star, Pixel: projector.Project(s.Horizontal))).Where(s => s.Pixel is { } p && p.X > 10 && p.Y > 10 && p.X < truth.WidthPixels - 10 && p.Y < truth.HeightPixels - 10).ToArray();
        return all.Where(s => all.All(t => t.Star.Id == s.Star.Id || Distance(t.Pixel!.Value, s.Pixel!.Value) > 13)).Select((s, i) =>
            new AstrometricDetection(i, new(s.Pixel!.Value.X + (random.NextDouble() - .5) * .04, s.Pixel.Value.Y + (random.NextDouble() - .5) * .04), 100000 * Math.Pow(10, -.4 * s.Star.Magnitude))).ToArray();
    }
    internal static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    internal static double RotationError(ProjectionContext a, ProjectionContext b)
    {
        var x = CameraBasis.Create(a.BoresightAltitudeDegrees, a.BoresightAzimuthDegrees, a.RollDegrees);
        var y = CameraBasis.Create(b.BoresightAltitudeDegrees, b.BoresightAzimuthDegrees, b.RollDegrees);
        return Math.Acos(Math.Clamp((EnuVector.Dot(x.Right, y.Right) + EnuVector.Dot(x.ImageUp, y.ImageUp) + EnuVector.Dot(x.Boresight, y.Boresight) - 1) / 2, -1, 1)) * 180 / Math.PI;
    }
    internal sealed record Image(double[] Pixels, double[] Ideal, bool[] Mask);
    // Test-only floating flux integration. Independent read noise is applied once after temporal accumulation.
    // The fixture deliberately omits photon noise; the separate preserved POC matrix covers Poisson/CFA noise.
    internal static Image Render(AstrometricCatalogData catalog, ProjectionContext truth, DateTimeOffset midpoint, double seconds, bool bayer, int seed)
    {
        var size = truth.WidthPixels; var height = truth.HeightPixels; var projector = ProjectorFactory.Create(truth); var mask = new bool[size * height]; var ideal = new double[mask.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < size; x++) { var i = y * size + x; mask[i] = truth.ContainsSample(x + .5, y + .5); if (mask[i]) ideal[i] = 300; }
        var samples = (int)Math.Ceiling(seconds / 2);
        for (var sample = 0; sample < samples; sample++)
        {
            var utc = midpoint.AddSeconds(seconds * ((sample + .5) / samples - .5));
            foreach (var star in catalog.Stars)
            {
                var h = Horizontal(star, utc); if (h.AltitudeDegrees < 0 || projector.Project(h) is not { } p) continue;
                var flux = seconds / samples * 18000 * Math.Pow(10, -.4 * star.Magnitude); var weights = new List<(int X, int Y, double W)>(); double total = 0;
                for (var y = (int)Math.Floor(p.Y) - 5; y <= (int)Math.Floor(p.Y) + 5; y++) for (var x = (int)Math.Floor(p.X) - 5; x <= (int)Math.Floor(p.X) + 5; x++)
                { var r2 = Math.Pow(x + .5 - p.X, 2) + Math.Pow(y + .5 - p.Y, 2); if (r2 > 25) continue; var w = Math.Exp(-r2 / (2 * 1.2 * 1.2)); weights.Add((x, y, w)); total += w; }
                foreach (var (x, y, w) in weights)
                    if (x >= 0 && x < size && y >= 0 && y < height && mask[y * size + x])
                    { var color = bayer ? (x % 2, y % 2) switch { (0, 0) => 1.15, (1, 1) => .8, _ => 1 } : 1; ideal[y * size + x] += flux * w / total * color; }
            }
        }
        var random = new Random(seed); var pixels = ideal.Select((value, i) => mask[i] ? Math.Clamp(Math.Round(value + 4 * Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble())), 0, 65535) : 0).ToArray();
        if (!bayer) return new(pixels, ideal, mask);
        var linear = LinearBayerReconstruction.Reconstruct(pixels, mask, size, height, BayerPattern.Rggb).ToLuminance();
        var noiseless = LinearBayerReconstruction.Reconstruct(ideal, mask, size, height, BayerPattern.Rggb).ToLuminance();
        return new(linear.Pixels.ToArray(), noiseless.Pixels.ToArray(), linear.ValidMask.ToArray());
    }
    internal static AstrometricDetection[] Detect(Image image, int size, bool color) => StellarDetector.Detect(image.Pixels, image.Mask, size, size,
        new(MaximumMajorSigma: color ? 2.15 : 1.65)).Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
}
