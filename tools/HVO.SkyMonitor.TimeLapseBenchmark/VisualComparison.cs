using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private static async Task GenerateDisplayMotion(string root, string catalogRoot)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Use a fresh evidence directory.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        const int size = 3552;
        const int count = 180;
        var start = DateTimeOffset.Parse("2026-10-13T07:00:00Z", CultureInfo.InvariantCulture);
        var variants = new[] { "color-single-stretched", "color-stack3-stretched" };
        var rows = variants.ToDictionary(v => v, _ => new List<object>());
        foreach (var variant in variants) Directory.CreateDirectory(Path.Combine(root, variant));
        var combiner = new RollingMono16Combiner(3);
        var pending = new Queue<Task<CameraFrame>>();
        for (var index = 0; index < 4; index++) pending.Enqueue(Capture(index));
        for (var index = 0; index < count; index++)
        {
            var frame = await pending.Dequeue().ConfigureAwait(false);
            if (index + 4 < count) pending.Enqueue(Capture(index + 4));
            var combined = combiner.Add(new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame));
            foreach (var variant in variants)
            {
                var stacked = variant.Contains("stack3", StringComparison.Ordinal);
                var raw = stacked ? combined.PixelData : frame.PixelData;
                var preview = BayerRggb16Demosaicer.DemosaicToRgb24(size, size, raw);
                var jpeg = Encode(preview, size, size, true);
                var file = $"{index:D4}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(root, variant, file), jpeg).ConfigureAwait(false);
                rows[variant].Add(new
                {
                    index, file, sourceUtc = start.AddSeconds(index * 20), sourceOffsetSeconds = index * 20,
                    sourceDurationSeconds = 20, exposureSeconds = 20, celestialExposure = frame.Metadata.Scene!.VirtualExposure,
                    rawSha256 = Hash(frame.PixelData.Span), combinedSha256 = stacked ? Hash(raw.Span) : null,
                    jpegSha256 = Hash(jpeg), jpegBytes = jpeg.Length,
                    stackCount = stacked ? combiner.BufferedFrameCount : 1,
                    totalIntegrationSeconds = stacked ? combined.TotalIntegration.TotalSeconds : 20,
                    stackSourceIndices = stacked ? Enumerable.Range(Math.Max(0, index - 2), Math.Min(index + 1, 3)).ToArray() : [index]
                });
            }
            if (index % 15 == 0) Console.WriteLine($"DISPLAY_MOTION {index + 1}/{count}");
        }
        foreach (var variant in variants)
            Write(Path.Combine(root, variant, "sequence.json"), new
            {
                width = size, height = size, color = true, phase = "night", start, end = start.AddHours(1),
                sourceSpanSeconds = 3600, playbackDurationSeconds = 60, compression = 60, count, rows = rows[variant],
                stretch = new Mono16DisplayStretchOptions(),
                gapPolicy = "Contiguous 20-second observations, no source replay. Rolling stack initially warms up with one/two frames; subsequent stacks contain the last three unaligned exposures."
            });
        Write(Path.Combine(root, "identity.json"), new
        {
            snapshot.DatabaseSha256, snapshot.RowCount, generationConcurrency = 4,
            boundary = "Same native color exposure sequence as the plain-image hour; actual raw hashes must match. Existing production percentile/asinh stretch, with and without existing three-frame rolling mean. No registration or artificial star amplification.",
            config = Config(size, size, CameraPixelFormat.BayerRggb16, start, 0)
        });
        Console.WriteLine($"DISPLAY_MOTION_COMPLETE {root}");

        Task<CameraFrame> Capture(int index) => Task.Run(async () =>
        {
            var config = Config(size, size, CameraPixelFormat.BayerRggb16, start.AddSeconds(index * 20), index);
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            return (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 0, null, null)), CancellationToken.None).ConfigureAwait(false)).Frame
                ?? throw new InvalidOperationException("No frame.");
        });
    }

    private static async Task GenerateVisualComparison(string root, string catalogRoot)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Use a fresh evidence directory.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        const int size = 3552;
        var start = DateTimeOffset.Parse("2026-10-13T07:00:00Z", CultureInfo.InvariantCulture);
        var products = new List<object>();
        foreach (var color in new[] { false, true })
        {
            var label = color ? "color" : "mono";
            var combiner = new RollingMono16Combiner(3);
            foreach (var index in Enumerable.Range(0, 3))
            {
                var config = Config(size, size, color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16,
                    start.AddSeconds(index * 20), index);
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
                var frame = (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(20), 0, null, null)), CancellationToken.None).ConfigureAwait(false)).Frame
                    ?? throw new InvalidOperationException("No frame.");
                var combined = combiner.Add(new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame));
                if (index == 2)
                {
                    await Save(label + "-single-fixed", FixedDisplayTransfer.Apply(frame.Layout!, frame.PixelData, Transfer)).ConfigureAwait(false);
                    await Save(label + "-single-stretched", Stretch(frame.PixelData)).ConfigureAwait(false);
                    await Save(label + "-stack3-stretched", Stretch(combined.PixelData)).ConfigureAwait(false);
                }
                products.Add(new { label, index, rawSha256 = Hash(frame.PixelData.Span), frame.Metadata.Extra,
                    frame.Metadata.Scene, config, integrationSeconds = combined.TotalIntegration.TotalSeconds });
                Console.WriteLine($"VISUAL {label} {index + 1}/3");
            }
            // A deeper catalog request leaves the physical signal-to-noise admission rule intact.
            var deepConfig = Config(size, size, color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16,
                start, 0, 16, 100000);
            var deepModule = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var deepLifetime = deepModule.ConfigureAwait(false);
            await deepModule.InitializeAsync(deepConfig, CancellationToken.None).ConfigureAwait(false);
            var deep = (await deepModule.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 0, null, null)), CancellationToken.None).ConfigureAwait(false)).Frame
                ?? throw new InvalidOperationException("No deep-catalog frame.");
            await Save(label + "-deep-catalog-stretched", Stretch(deep.PixelData)).ConfigureAwait(false);
            products.Add(new { label, index = 0, deepCatalog = true, rawSha256 = Hash(deep.PixelData.Span),
                deep.Metadata.Extra, deep.Metadata.Scene, config = deepConfig });

            byte[] Stretch(ReadOnlyMemory<byte> pixels) => color
                ? BayerRggb16Demosaicer.DemosaicToRgb24(size, size, pixels)
                : Mono16DisplayStretch.Apply(size, size, pixels);
            async Task Save(string name, byte[] pixels)
            {
                var jpeg = Encode(pixels, size, size, color);
                await File.WriteAllBytesAsync(Path.Combine(root, name + ".jpg"), jpeg).ConfigureAwait(false);
                products.Add(new { name, jpegSha256 = Hash(jpeg), jpegBytes = jpeg.Length });
            }
        }
        Write(Path.Combine(root, "visual-comparison.json"), new
        {
            snapshot.DatabaseSha256, snapshot.RowCount, width = size, height = size, start,
            boundary = "Display-only still comparison. Existing production percentile/asinh stretch and three-frame unaligned arithmetic mean, native raw data. These are not timed video trials or a new production recipe.",
            fixedTransfer = Transfer, stretch = new Mono16DisplayStretchOptions(), products
        });
        Console.WriteLine($"VISUAL_COMPLETE {root}");
    }
}
