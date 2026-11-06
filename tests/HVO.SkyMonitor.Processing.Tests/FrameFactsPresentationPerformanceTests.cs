using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class FrameFactsPresentationPerformanceTests
{
    [TestMethod]
    [DataRow(1936, 1216)]
    [DataRow(3552, 3552)]
    public void CompareLegacyAndOptInFullResolution(int width, int height)
    {
        var descriptor = FrameFactsRetainedContextTests.Descriptor();
        var source = new string('A', 64);
        var layout = new ImageLayout(width, height, CameraPixelFormat.Mono8, width);
        var raw = new byte[layout.RequiredByteLength];
        var checksum = SHA256.HashData(raw);
        var facts = new PresentationMetadataFactsV1(source, ["CAPTURE 42"], ["GAIN 7.25"], ["UNAVAILABLE"], ["2026-10-22"]);
        var options = new FrameFactsPresentationOptions
        {
            WidthPixels = width,
            HeightPixels = height,
            TopLeftTemplate = "**CAPTURE {{capture.sequence}}**",
            TopRightTemplate = "*GAIN {{acquisition.gain|fixed2}}*",
            BottomLeftTemplate = "__{{environment.airTemperature}}__",
            BottomRightTemplate = "{{time.captureUtc|date}}"
        };
        Measure("legacy", () => PresentationLayerProducers.FromMetadataFacts(facts, width, height));
        Measure("rich", () => FrameFactsPresentation.Create(source,
            FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(new(descriptor))), options));
        CollectionAssert.AreEqual(checksum, SHA256.HashData(raw));

        void Measure(string name, Func<PresentationLayerPayloadV1> produce)
        {
            for (var warmup = 0; warmup < 5; warmup++) _ = Run();
            var times = new double[30];
            var producerTimes = new double[30];
            var allocated = 0L;
            var maximumWorkingSet = 0L;
            var payloadBytes = 0;
            string? outputChecksum = null;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            for (var index = 0; index < times.Length; index++)
            {
                var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
                var result = Run();
                allocated += GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
                times[index] = result.TotalMs;
                producerTimes[index] = result.ProducerMs;
                payloadBytes = result.PayloadBytes;
                var actual = Convert.ToHexString(SHA256.HashData(result.Output));
                outputChecksum ??= actual;
                Assert.AreEqual(outputChecksum, actual);
                process.Refresh();
                maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
            }
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            Array.Sort(times);
            Array.Sort(producerTimes);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "frame-facts-cost-v1",
                name,
                width,
                height,
                warmup = 5,
                operations = times.Length,
                concurrency = 1,
                medianMs = times[15],
                p95Ms = times[28],
                producerMedianMs = producerTimes[15],
                producerP95Ms = producerTimes[28],
                framesPerSecond = 1000 / times.Average(),
                cpuMs,
                allocatedBytesPerOperation = allocated / times.Length,
                maximumWorkingSet,
                payloadBytes,
                outputBytes = width * height * 3,
                outputChecksum,
                managedFullFrameOutputs = 1,
                nativeTileMaximumBytes = 4 * 1024 * 1024,
                ioBytes = 0,
                backlog = 0
            }));

            (double TotalMs, double ProducerMs, byte[] Output, int PayloadBytes) Run()
            {
                var started = Stopwatch.GetTimestamp();
                var payload = produce();
                var produced = Stopwatch.GetTimestamp();
                var output = PresentationLayerCompositor.CompositeDisplay(layout, raw,
                    [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
                var finished = Stopwatch.GetTimestamp();
                return (Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(started, produced).TotalMilliseconds, output.Pixels, PresentationLayerPayloadJson.Serialize(payload).Length);
            }
        }
    }
}
