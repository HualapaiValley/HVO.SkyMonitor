using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest discovers public test classes.")]
public sealed class FFmpegTimeLapseTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task MissingEncoderIsExplicitlyUnavailableWithoutCreatingScratch()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-video-" + Guid.NewGuid().ToString("N"));
        using var encoder = new FFmpegTimeLapseEncoder(new()
        {
            ScratchDirectory = root,
            ExecutablePath = Path.Combine(root, "missing-ffmpeg")
        });
        var result = await encoder.QualifyAsync(TimeLapseEncoderProfile.Software, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(result.Available);
        Assert.AreEqual("timelapse.encoder-unavailable", result.ReasonCode);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task RealSoftwareEncodeVerifiesCadenceGapsAndFinalHold()
    {
        var root = NewDirectory();
        try
        {
            using var encoder = new FFmpegTimeLapseEncoder(new() { ScratchDirectory = root });
            var start = DateTimeOffset.UnixEpoch;
            var plan = TimeLapseTimelinePlanner.Create(start, start.AddSeconds(120.123),
            [
                new(Guid.NewGuid(), new string('A', 64), start.AddSeconds(5), TimeSpan.FromMilliseconds(1)),
                new(Guid.NewGuid(), new string('B', 64), start.AddSeconds(25), TimeSpan.FromSeconds(20)),
                new(Guid.NewGuid(), new string('C', 64), start.AddSeconds(30), TimeSpan.FromMilliseconds(5))
            ]);
            var restored = new List<int?>();
            var output = await encoder.EncodeAsync(plan, 512, 512, TimeLapseEncoderProfile.Software,
                (ordinal, _) =>
                {
                    restored.Add(ordinal);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>>(Image(ordinal));
                }, CancellationToken.None).ConfigureAwait(false);
            await using (output.ConfigureAwait(false))
            {
                Assert.AreEqual(plan.Intervals.Length, output.Evidence.Media.Packets.Count);
                Assert.AreEqual(plan.DurationTicks, output.Evidence.Media.DurationTicks);
                Assert.IsGreaterThan(100L, output.Evidence.PayloadBytes);
                Assert.AreEqual(plan.IdentitySha256, output.Evidence.TimelineIdentitySha256);
                Assert.HasCount(4, restored);
                using var stream = output.OpenRead();
                Assert.AreEqual(output.Evidence.PayloadBytes, stream.Length);
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "work")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task EncoderSurvivesItsAsyncCallingThreadRetiring()
    {
        var root = NewDirectory();
        try
        {
            var boundary = new BoundedMediaProcess(new() { ScratchDirectory = root, Timeout = TimeSpan.FromSeconds(10) });
            Task<string>? execution = null;
            var caller = new Thread(() =>
            {
                execution = boundary.RunAsync("/bin/sh", ["-c", "touch ready; sleep 1; printf completed"], root, CancellationToken.None);
                _ = SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "ready")), TimeSpan.FromSeconds(5));
            });
            caller.Start();
            Assert.IsTrue(caller.Join(TimeSpan.FromSeconds(6)));
            Assert.IsNotNull(execution);
            Assert.AreEqual("completed", await execution.ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task CanceledOrOververboseProcessIsReapedAndSlotCanBeReused()
    {
        var root = NewDirectory();
        try
        {
            var process = new BoundedMediaProcess(new() { ScratchDirectory = root, Timeout = TimeSpan.FromSeconds(20) });
            Assert.AreEqual("0", (await process.RunAsync("/bin/sh", ["-c", "ulimit -c"], root, CancellationToken.None)
                .ConfigureAwait(false)).Trim(), "An encoder failure must not create an unbounded core dump.");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var started = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OperationCanceledException>(() => process.RunAsync("/bin/sleep", ["10"], root, cancel.Token))
                .ConfigureAwait(false);
            Assert.IsLessThan(TimeSpan.FromSeconds(5), started.Elapsed);
            var error = await Assert.ThrowsExactlyAsync<TimeLapseEncodingException>(() =>
                process.RunAsync("/usr/bin/yes", [], root, CancellationToken.None, outputLimit: 128)).ConfigureAwait(false);
            Assert.AreEqual("timelapse.diagnostic-bound", error.ReasonCode);
            // Repeated short exits also exercise process-exit races in RSS monitoring.
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var output = await process.RunAsync("/bin/echo", ["reaped"], root, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("reaped", output.Trim());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task DailyAssemblyPreservesPacketsAndRejectsCorruptOrIncompatibleSegments()
    {
        var root = NewDirectory();
        try
        {
            using var encoder = new FFmpegTimeLapseEncoder(new() { ScratchDirectory = root });
            var segments = new List<TimeLapseAssemblySegment>();
            var payloads = new List<byte[]>();
            for (var index = 0; index < 2; index++)
            {
                var start = DateTimeOffset.UnixEpoch.AddMinutes(index);
                var plan = TimeLapseTimelinePlanner.Create(start, start.AddMinutes(1),
                    Enumerable.Range(0, 3).Select(ordinal => new TimeLapseSource(Guid.NewGuid(), new string('A', 64),
                        start.AddSeconds(ordinal * 20), TimeSpan.FromSeconds(20))));
                var output = await encoder.EncodeAsync(plan, 512, 512, TimeLapseEncoderProfile.Software,
                    (ordinal, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Image(ordinal)), CancellationToken.None).ConfigureAwait(false);
                await using (output.ConfigureAwait(false))
                {
                    using var stream = output.OpenRead();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer).ConfigureAwait(false);
                    payloads.Add(buffer.ToArray());
                    segments.Add(new(start, start.AddMinutes(1), output.Evidence));
                }
            }
            ValueTask<Stream> Open(int index, CancellationToken _) => ValueTask.FromResult<Stream>(new MemoryStream(payloads[index], writable: false));
            var daily = await encoder.AssembleAsync(segments, 180, Open, CancellationToken.None).ConfigureAwait(false);
            await using (daily.ConfigureAwait(false))
            {
                CollectionAssert.AreEqual(segments.SelectMany(static segment => segment.Evidence.Media.Packets)
                    .Select(static packet => packet.Sha256).ToArray(), daily.Evidence.Media.Packets.Select(static packet => packet.Sha256).ToArray());
                Assert.AreEqual(666666L, daily.Evidence.Media.DurationTicks);
            }
            payloads[1][100] ^= 1;
            var corrupt = await Assert.ThrowsExactlyAsync<TimeLapseEncodingException>(() =>
                encoder.AssembleAsync(segments, 180, Open, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("timelapse.segment-integrity", corrupt.ReasonCode);
            segments[1] = segments[1] with { Evidence = segments[1].Evidence with { Profile = TimeLapseEncoderProfile.Nvidia } };
            var incompatible = await Assert.ThrowsExactlyAsync<TimeLapseEncodingException>(() =>
                encoder.AssembleAsync(segments, 180, Open, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("timelapse.incompatible-segments", incompatible.ReasonCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Manual")]
    [DataRow(180)]
    [DataRow(300)]
    public async Task SubFrameSunriseSegmentsEncodeAndAssembleWithExactPacketTiming(int compression)
    {
        var root = NewDirectory();
        try
        {
            using var encoder = new FFmpegTimeLapseEncoder(new() { ScratchDirectory = root });
            var segments = new List<TimeLapseAssemblySegment>();
            var payloads = new List<byte[]>();
            var start = DateTimeOffset.UnixEpoch;
            foreach (var seconds in new[] { .0000001, .0183828, .5323656, 2.5524336, 3600 })
            {
                var end = start.AddSeconds(seconds);
                var timeline = TimeLapseTimelinePlanner.Create(start, end, [], new(compression));
                var encoded = await encoder.EncodeAsync(timeline, 512, 512, TimeLapseEncoderProfile.Software,
                    (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Image(null)), CancellationToken.None).ConfigureAwait(false);
                await using (encoded.ConfigureAwait(false))
                {
                    Assert.AreEqual(timeline.DurationTicks, encoded.Evidence.Media.DurationTicks);
                    Assert.HasCount(1, encoded.Evidence.Media.Packets);
                    using var input = encoded.OpenRead();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer).ConfigureAwait(false);
                    payloads.Add(buffer.ToArray());
                    segments.Add(new(start, end, encoded.Evidence));
                }
                start = end;
            }
            var daily = await encoder.AssembleAsync(segments, compression,
                (index, _) => ValueTask.FromResult<Stream>(new MemoryStream(payloads[index], writable: false)), CancellationToken.None).ConfigureAwait(false);
            await using (daily.ConfigureAwait(false))
            {
                Assert.AreEqual(segments.Sum(segment => segment.Evidence.Media.DurationTicks), daily.Evidence.Media.DurationTicks);
                CollectionAssert.AreEqual(segments.SelectMany(segment => segment.Evidence.Media.Packets).Select(packet => packet.Sha256).ToArray(),
                    daily.Evidence.Media.Packets.Select(packet => packet.Sha256).ToArray());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Image(int? ordinal)
    {
        var header = Encoding.ASCII.GetBytes("P6\n512 512\n255\n");
        var bytes = new byte[header.Length + 512 * 512 * 3];
        header.CopyTo(bytes, 0);
        if (ordinal is { } index)
            for (var offset = header.Length; offset < bytes.Length; offset += 3) bytes[offset + index % 3] = 220;
        return bytes;
    }
}
