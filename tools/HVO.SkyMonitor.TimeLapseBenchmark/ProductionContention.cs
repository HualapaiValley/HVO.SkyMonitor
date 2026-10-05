using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private sealed record ForegroundOperation(int Index, double ScheduledSeconds, double StartedSeconds,
        double FinishedSeconds, double ServiceSeconds, double ResponseSeconds, bool MissedDeadline);

    private static async Task BenchmarkProductionContention(string inputRoot, string outputRoot, bool withVideo)
    {
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new ArgumentException("Use fresh contention evidence storage.");
        Directory.CreateDirectory(outputRoot);
        var inputs = JsonSerializer.Deserialize<ProductionInputs>(await File.ReadAllTextAsync(Path.Combine(inputRoot, "production.json")).ConfigureAwait(false), Json)!;
        var source = inputs.Sources[0];
        var executor = new ProcessingRecipeExecutor();
        var recipeOptions = JsonSerializer.SerializeToElement(new EncodedPreviewOptions(JpegQuality: 92));
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var warmup = 0; warmup < 5; warmup++) hashes.Add(await ProcessAsync().ConfigureAwait(false));
        if (hashes.Count != 1) throw new InvalidDataException("Foreground warmup is not deterministic.");
        var active = 1;
        var videoJobs = 0;
        var video = withVideo ? Task.Run(async () =>
        {
            while (Volatile.Read(ref active) == 1 && videoJobs < 50)
            {
                await BenchmarkProduction(inputRoot, Path.Combine(outputRoot, $"video-{videoJobs:D2}"), 1280,
                    TimeLapseEncoderProfile.Software, 180, 3).ConfigureAwait(false);
                videoJobs++;
            }
        }) : Task.CompletedTask;
        var operations = new List<ForegroundOperation>();
        const int count = 30;
        const double interval = 5;
        var allocated = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"PRODUCTION_FOREGROUND_READY video={withVideo}");
        try
        {
            for (var index = 0; index < count; index++)
            {
                if (video.IsFaulted) await video.ConfigureAwait(false);
                var scheduled = index * interval;
                var delay = scheduled - clock.Elapsed.TotalSeconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                var began = clock.Elapsed.TotalSeconds;
                hashes.Add(await ProcessAsync().ConfigureAwait(false));
                var finished = clock.Elapsed.TotalSeconds;
                operations.Add(new(index, scheduled, began, finished, finished - began, finished - scheduled, finished > scheduled + interval));
                Console.WriteLine($"foreground {index + 1}/{count}: {finished - began:F3}s");
            }
        }
        finally
        {
            Volatile.Write(ref active, 0);
            await video.ConfigureAwait(false);
        }
        process.Refresh();
        Write(Path.Combine(outputRoot, "foreground.json"), new
        {
            boundary = "Same process: foreground actual ProcessingRecipeExecutor encoded-preview recipe (native stretch/demosaic/JPEG, verified raw file read) every 5s, with optional continuous serial production video jobs. Excludes CameraAgent ingress/SQL/outbox and physical acquisition.",
            warmupCount = 5, count, interval, withVideo, videoJobs, inputs.CatalogSha256, source.Sha256,
            hashes, outputsMatch = hashes.Count == 1, operations, cpuSecondsIncludingBackground = (process.TotalProcessorTime - cpu).TotalSeconds,
            allocatedBytesIncludingBackground = GC.GetTotalAllocatedBytes(true) - allocated, process.PeakWorkingSet64
        });
        if (hashes.Count != 1) throw new InvalidDataException("Foreground output changed under contention.");

        async Task<string> ProcessAsync()
        {
            var pixels = await File.ReadAllBytesAsync(Path.Combine(inputRoot, source.File)).ConfigureAwait(false);
            if (Hash(pixels) != source.Sha256) throw new InvalidDataException("Foreground raw source changed.");
            var result = await executor.ExecuteAsync(new(BuiltInProcessingRecipes.EncodedPreview, recipeOptions,
                ProcessingInputSelector.Raw(), [source.Description with { Payload = pixels }], "foreground-preview")).ConfigureAwait(false);
            if (result.Status != ProcessingOutcomeStatus.Produced || result.Products.Count != 1)
                throw new InvalidOperationException("Foreground recipe failed: " + result.ReasonCode);
            return result.Products[0].ChecksumSha256;
        }
    }
}
