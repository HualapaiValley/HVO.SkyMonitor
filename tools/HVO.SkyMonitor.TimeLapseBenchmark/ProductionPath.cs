using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private sealed record ProductionSource(string File, string Sha256, ProcessingArtifact Description);
    private sealed record ProductionInputs(string Version, string CatalogSha256, long CatalogRows,
        CameraRigConfig Rig, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Daytime, IReadOnlyList<ProductionSource> Sources);

    private static async Task GenerateProductionInputs(string root, string catalogRoot, string caseName)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Use fresh production input storage.");
        Directory.CreateDirectory(root);
        var (width, height, color, daytime) = caseName switch
        {
            "mono3552" => (3552, 3552, false, false), "color3552" => (3552, 3552, true, false),
            "mono174" => (1936, 1216, false, false), "day3552" => (3552, 3552, true, true),
            _ => throw new ArgumentException("Unknown production case.", nameof(caseName))
        };
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production") { ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1" });
        var start = DateTimeOffset.Parse(daytime ? "2026-10-12T19:00:00Z" : "2026-10-13T07:00:00Z", CultureInfo.InvariantCulture);
        var interval = daytime ? 5 : 20;
        var count = 300 / interval;
        var exposure = TimeSpan.FromSeconds(daytime ? .037513541 : 20);
        var sources = new List<ProductionSource>();
        CameraRigConfig? rig = null;
        for (var index = 0; index < count; index++)
        {
            var utc = start.AddSeconds(index * interval);
            var config = caseName == "mono174" ? Asi174Config(utc, index)
                : Config(width, height, color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, utc, index);
            rig = config.Rig;
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var frame = (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(interval), CaptureMode.Still,
                new CaptureSetpoint(exposure, 0, null, null)), CancellationToken.None).ConfigureAwait(false)).Frame
                ?? throw new InvalidOperationException("No virtual frame.");
            if (frame.Metadata.Scene?.VirtualExposure?.CelestialStartUtc != utc) throw new InvalidDataException("Virtual sky clock did not advance.");
            var sha = Hash(frame.PixelData.Span);
            var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new { sha, utc, exposure, caseName });
            var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(rig);
            var file = $"{index:D4}.raw";
            var description = new ProcessingArtifact(ProcessingIdentity.CreateArtifactId(identity), FrameArtifactRole.Raw,
                "VirtualSky", identity, "application/x-hvo-frame", frame.Layout, ReadOnlyMemory<byte>.Empty, utc + exposure, exposure,
                new(rigHash, rigHash, "none", "none", "virtual-benchmark", exposure.Ticks.ToString(CultureInfo.InvariantCulture), "linear16"),
                ObservationStartedUtc: utc);
            await File.WriteAllBytesAsync(Path.Combine(root, file), frame.PixelData.ToArray()).ConfigureAwait(false);
            sources.Add(new(file, sha, description));
            Console.WriteLine($"{caseName}: {index + 1}/{count} {sha}");
        }
        Write(Path.Combine(root, "production.json"), new ProductionInputs("hvo-timelapse-production-input-v1", snapshot.DatabaseSha256,
            snapshot.RowCount, rig!, start, start.AddSeconds(300), daytime, sources));
    }

    private static async Task BenchmarkProduction(string inputRoot, string outputRoot, int maximumDimension,
        TimeLapseEncoderProfile profile, int compression, int stackCount)
    {
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new ArgumentException("Use fresh production evidence storage.");
        Directory.CreateDirectory(outputRoot);
        var inputs = JsonSerializer.Deserialize<ProductionInputs>(await File.ReadAllTextAsync(Path.Combine(inputRoot, "production.json")).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Missing production inputs.");
        var options = new TimeLapseFrameOptions(StackCount: stackCount);
        var timeline = TimeLapseTimelinePlanner.Create(inputs.StartUtc, inputs.EndUtc, inputs.Sources.Select(source => new TimeLapseSource(
            source.Description.ArtifactId, CaptureContractJson.ComputeCanonicalJsonSha256(source.Description), source.Description.ObservationStartedUtc!.Value,
            source.Description.Integration)), new(Compression: compression));
        var size = TimeLapseFrameRenderer.Fit(inputs.Rig.Sensor.WidthPixels, inputs.Rig.Sensor.HeightPixels, maximumDimension);
        using var encoder = new FFmpegTimeLapseEncoder(new()
        {
            ScratchDirectory = Path.Combine(outputRoot, "scratch"), Timeout = TimeSpan.FromMinutes(30),
            MaximumAddressSpaceBytes = 8L * 1024 * 1024 * 1024,
            MaximumResidentBytes = 2L * 1024 * 1024 * 1024
        });
        var clock = Stopwatch.StartNew();
        var capability = await encoder.QualifyAsync(profile, CancellationToken.None).ConfigureAwait(false);
        var qualificationSeconds = clock.Elapsed.TotalSeconds;
        if (!capability.Available) throw new InvalidOperationException(capability.ReasonCode);
        var sourceById = inputs.Sources.ToDictionary(static source => source.Description.ArtifactId);
        var readSeconds = 0d;
        var renderSeconds = 0d;
        var sourceReads = 0;
        var lineage = new List<object>();
        async ValueTask<ProcessingArtifact> Restore(Guid id, CancellationToken token)
        {
            var read = Stopwatch.StartNew();
            var source = sourceById[id];
            var payload = await File.ReadAllBytesAsync(Path.Combine(inputRoot, source.File), token).ConfigureAwait(false);
            if (Hash(payload) != source.Sha256) throw new InvalidDataException("Raw input checksum changed.");
            readSeconds += read.Elapsed.TotalSeconds;
            sourceReads++;
            return source.Description with { Payload = payload };
        }
        var sequence = new TimeLapseFrameSequence(inputs.Sources.Select(static source => source.Description).ToArray(), inputs.Rig,
            options, size.Width, size.Height, Restore, _ => inputs.Daytime);
        async ValueTask<ReadOnlyMemory<byte>> Render(int? ordinal, CancellationToken token)
        {
            if (ordinal is null) return TimeLapseFrameRenderer.GapImage(size.Width, size.Height);
            var render = Stopwatch.StartNew();
            var result = await sequence.RenderAsync(timeline.Sources[ordinal.Value].ArtifactId, token).ConfigureAwait(false);
            renderSeconds += render.Elapsed.TotalSeconds;
            lineage.Add(new { result.RenderingIdentitySha256, result.StackSourceIds, result.StackPayloadSha256, result.TotalIntegration });
            return result.Jpeg;
        }
        var allocations = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        clock.Restart();
        var encoded = await encoder.EncodeAsync(timeline, size.Width, size.Height, profile, Render, CancellationToken.None).ConfigureAwait(false);
        await using var encodedLifetime = encoded.ConfigureAwait(false);
        var encodeAndRenderSeconds = clock.Elapsed.TotalSeconds;
        await AtomicPublisher.PublishAsync(PhysicalRoot.Open(outputRoot), "video.mp4", PublishMode.CreateNew, async (destination, token) =>
        {
            using var source = encoded.OpenRead();
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        using (var published = File.OpenRead(Path.Combine(outputRoot, "video.mp4")))
            if (Convert.ToHexString(await SHA256.HashDataAsync(published).ConfigureAwait(false)) != encoded.Evidence.PayloadSha256)
                throw new InvalidDataException("Published benchmark video checksum changed.");
        var totalSeconds = clock.Elapsed.TotalSeconds;
        process.Refresh();
        Write(Path.Combine(outputRoot, "result.json"), new
        {
            boundary = "Production shared rolling stack, source SHA verification, display/overlays, bounded encoder, packet/decode verification, durable MP4 publication and output SHA. Excludes capture, host job DB and central object publication.",
            inputs.CatalogSha256, inputs.CatalogRows, inputs.StartUtc, inputs.EndUtc, inputs.Daytime, sourceCount = inputs.Sources.Count,
            maximumDimension, profile, compression, stackCount, qualificationSeconds, totalSeconds, encodeAndRenderSeconds,
            sourceReads, readSeconds, renderSeconds, managedCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
            managedPeakWorkingSetBytes = process.PeakWorkingSet64, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocations,
            capability, evidence = encoded.Evidence, timeline, lineage
        });
        Console.WriteLine($"Production {profile} {size.Width}x{size.Height} {compression}x: {totalSeconds:F3}s; {encoded.Evidence.PayloadBytes} bytes; verified.");
    }
}
