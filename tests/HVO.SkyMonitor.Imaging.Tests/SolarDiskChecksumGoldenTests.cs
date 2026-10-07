using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

/// <summary>
/// Versioned Mono/RGB/CFA checksum fixtures for resolved Sun and Moon disks. The hashes belong to
/// <see cref="SolarDiskRenderPlan.AlgorithmVersion"/> <c>solar-lunar-resolved-psf-v4-resolved-footprints</c>; a
/// renderer change that moves them must bump that version and re-pin them in the same change.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SolarDiskChecksumGoldenTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "AA80FD75DF75A43062F89BBF18EDF7BF0D78B1FBD9F5C9ECD1B96F0091A67E87")]
    [DataRow(CameraPixelFormat.Rgb24, "FFB4B44F59E2323D6E69B4CFCD9E7CE729FB655BD210EE20C57AF4F1738F5706")]
    [DataRow(CameraPixelFormat.BayerRggb16, "42D271E9CABD86904643C318EB4823AC4CC3B6E6FD2625D59AFCCA58050FB0BF")]
    public async Task SunAndQuarterMoonRenderTheirPinnedPixels(CameraPixelFormat format, string expectedSha256)
    {
        // About 40 px per degree: the Sun spans roughly 20 px and the Moon 18 px, either side of the zenith.
        var scene = await SceneTestFactory.CreateNarrowEmptyAsync(64, 64, 2300).ConfigureAwait(false);
        var projection = scene.Request.Projection;
        var layout = new ImageLayout(64, 64, format, 64 * ImageLayout.BytesPerPixel(format));
        var disks = new SolarDiskRenderPlan(projection,
        [
            new SolarDiskAppearance(SolarSystemBody.Sun, scene.Request.Utc, new(89.6, 90), 0.25, 0, 1, 0, 149600000),
            new SolarDiskAppearance(SolarSystemBody.Moon, scene.Request.Utc, new(89.6, 270), 0.22, 0.5, 0.5, 60, 384400)
        ], 1000, new SolarDiskRenderSettings(HorizonPolicy: scene.Request.HorizonPolicy,
            Refraction: scene.Request.Refraction));
        var result = format switch
        {
            CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(scene, layout, new()
            { BackgroundElectronsPerSecond = 20, SolarDisks = disks }),
            CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(scene, layout, new()
            { BackgroundElectronsPerSecond = 20, SolarDisks = disks }),
            _ => BayerRggb16Renderer.Render(scene, layout, new()
            { ChannelResponse = new(1, 1, 1), BackgroundElectronsPerSecond = 20, SolarDisks = disks })
        };
        var background = format switch
        {
            CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(scene, layout, new() { BackgroundElectronsPerSecond = 20 }),
            CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(scene, layout, new() { BackgroundElectronsPerSecond = 20 }),
            _ => BayerRggb16Renderer.Render(scene, layout, new()
            { ChannelResponse = new(1, 1, 1), BackgroundElectronsPerSecond = 20 })
        };

        // Both discs land in frame, so the fixture pins disk pixels rather than an empty background.
        var changed = result.Pixels.ToArray().Zip(background.Pixels.ToArray()).Count(static pair => pair.First != pair.Second);
        Assert.IsGreaterThan(40, changed, $"{format}: {changed} bytes differ from the background.");
        Assert.AreEqual(expectedSha256, Convert.ToHexString(SHA256.HashData(result.Pixels.Span)), format.ToString());
    }
}
