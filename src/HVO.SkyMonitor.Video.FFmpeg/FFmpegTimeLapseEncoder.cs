using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>
/// Optional shared infrastructure adapter. The hosts own scheduling, source restoration, recipes and publication.
/// A single leased workspace serializes preparation/encoding/publication and is recoverable after a host restart.
/// </summary>
public sealed partial class FFmpegTimeLapseEncoder : IDisposable
{
    public const string ProfileVersion = "hvo-h264-vfr-microseconds-v1";
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private readonly FFmpegOptions _options;
    private readonly BoundedMediaProcess _process;
    private readonly BoundedMediaProcess _nvidiaProcess;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Dictionary<TimeLapseEncoderProfile, (FFmpegCapability Capability, DateTimeOffset Until)> _capabilities = [];

    public FFmpegTimeLapseEncoder(FFmpegOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _process = new(options);
        _nvidiaProcess = new(options with { MaximumAddressSpaceBytes = options.MaximumNvidiaAddressSpaceBytes });
    }

    public void Dispose() => _slot.Dispose();

    public async Task<FFmpegCapability> QualifyAsync(TimeLapseEncoderProfile profile, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capabilities.TryGetValue(profile, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Capability;
            if (!BoundedMediaProcess.Supported || !File.Exists(_options.ExecutablePath) || !File.Exists(_options.ProbePath))
                return Cache(profile, new(false, "timelapse.encoder-unavailable", null, null));
            var (directory, lease) = OpenWorkspace();
            using (lease)
            {
                try
                {
                    var version = await _process.RunAsync(_options.ExecutablePath, ["-version"], directory, cancellationToken)
                        .ConfigureAwait(false);
                    var firstLine = version.Split('\n')[0];
                    var probeVersion = await _process.RunAsync(_options.ProbePath, ["-version"], directory, cancellationToken)
                        .ConfigureAwait(false);
                    if (!firstLine.StartsWith("ffmpeg version 6.1.", StringComparison.Ordinal) ||
                        !probeVersion.StartsWith("ffprobe version 6.1.", StringComparison.Ordinal))
                        return Cache(profile, new(false, "timelapse.encoder-version-unsupported", firstLine, null));
                    var capability = new FFmpegCapability(true, null, firstLine,
                        await HashFileAsync(_options.ExecutablePath, cancellationToken).ConfigureAwait(false),
                        await HashFileAsync(_options.ProbePath, cancellationToken).ConfigureAwait(false));
                    var start = DateTimeOffset.UnixEpoch;
                    var timeline = TimeLapseTimelinePlanner.Create(start, start.AddSeconds(40),
                    [
                        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), new string('A', 64), start, TimeSpan.FromMilliseconds(1)),
                        new(Guid.Parse("00000000-0000-0000-0000-000000000002"), new string('B', 64), start.AddSeconds(20), TimeSpan.FromSeconds(20))
                    ]);
                    // The canary exercises the actual device/codec, timing filter, MP4 muxer, probe and full decode.
                    // Listing encoders alone does not prove that a GPU, driver or required filter works.
                    await EncodeCoreAsync(directory, timeline, 512, 512, profile, capability,
                        (ordinal, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(CanaryImage(ordinal ?? 0)), cancellationToken)
                        .ConfigureAwait(false);
                    await VerifyCanaryOrderAsync(directory, cancellationToken).ConfigureAwait(false);
                    return Cache(profile, capability);
                }
                catch (TimeLapseEncodingException exception)
                {
                    return Cache(profile, new(false, exception.ReasonCode, null, null));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    return Cache(profile, new(false, "timelapse.capability-invalid", null, null));
                }
                finally { TimeLapseWorkspace.Delete(directory); }
            }
        }
        finally { _slot.Release(); }
    }

    public async Task<EncodedTimeLapse> EncodeAsync(TimeLapseTimeline timeline, int width, int height,
        TimeLapseEncoderProfile profile, Func<int?, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> restoreImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(restoreImage);
        var validated = TimeLapseTimelinePlanner.Create(timeline.StartUtc, timeline.EndUtc, timeline.Sources, timeline.Options);
        if (timeline.IdentitySha256 != validated.IdentitySha256 ||
            !timeline.Sources.SequenceEqual(validated.Sources) || !timeline.SampledOutOrdinals.SequenceEqual(validated.SampledOutOrdinals) ||
            !timeline.Intervals.SequenceEqual(validated.Intervals) || timeline.DurationTicks != validated.DurationTicks ||
            width is < 2 or > 4096 || height is < 2 or > 4096 ||
            width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Invalid time-lapse plan or dimensions.");
        var capability = await QualifyAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
            throw new TimeLapseEncodingException(capability.ReasonCode!, "The requested encoder profile is unavailable.");
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        FileStream? lease = null;
        try
        {
            (directory, lease) = OpenWorkspace();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(_options.Timeout);
            await RequireBinariesAsync(capability, lifetime.Token).ConfigureAwait(false);
            var evidence = await EncodeCoreAsync(directory, timeline, width, height, profile, capability, restoreImage, lifetime.Token)
                .ConfigureAwait(false);
            return new(directory, evidence, lease, _slot);
        }
        catch
        {
            try { if (directory is not null) TimeLapseWorkspace.Delete(directory); }
            finally
            {
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
                _slot.Release();
            }
            throw;
        }
    }

    private async Task<TimeLapseEncodingEvidence> EncodeCoreAsync(string directory, TimeLapseTimeline timeline,
        int width, int height, TimeLapseEncoderProfile profile, FFmpegCapability capability,
        Func<int?, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> restoreImage, CancellationToken token)
    {
        var images = new SortedDictionary<int, string>();
        var lines = new StringBuilder("ffconcat version 1.0\n");
        long bytes = 0;
        foreach (var interval in timeline.Intervals)
        {
            token.ThrowIfCancellationRequested();
            var ordinal = interval.SourceOrdinal ?? -1;
            var name = ordinal < 0 ? "gap.jpg" : ordinal.ToString("D5", CultureInfo.InvariantCulture) + ".jpg";
            if (!images.ContainsKey(ordinal))
            {
                var image = await restoreImage(interval.SourceOrdinal, token).ConfigureAwait(false);
                if (image.Length is <= 0 or > MaximumImageBytes)
                    throw new TimeLapseEncodingException("timelapse.image-bound", "A prepared image exceeded its byte bound.");
                bytes = checked(bytes + image.Length);
                if (bytes + _options.MaximumOutputBytes > _options.MaximumScratchBytes)
                    throw new TimeLapseEncodingException("timelapse.scratch-bound", "Prepared images exceed the reserved scratch allowance.");
                await File.WriteAllBytesAsync(Path.Combine(directory, name), image.ToArray(), token).ConfigureAwait(false);
                images.Add(ordinal, Convert.ToHexString(SHA256.HashData(image.Span)));
            }
            lines.Append("file ").Append(name).Append("\noption framerate 1000000\nduration ")
                .Append((interval.DurationTicks / 1_000_000m).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "frames.ffconcat"), lines.ToString(), token).ConfigureAwait(false);
        var arguments = EncodingArguments(timeline, width, height, profile);
        await (profile == TimeLapseEncoderProfile.Nvidia ? _nvidiaProcess : _process)
            .RunAsync(_options.ExecutablePath, arguments, directory, token).ConfigureAwait(false);
        var media = await VerifyAsync(directory, timeline.Intervals, width, height, token).ConfigureAwait(false);
        var file = Path.Combine(directory, "video.mp4");
        var length = new FileInfo(file).Length;
        if (length <= 0 || length > _options.MaximumOutputBytes)
            throw new TimeLapseEncodingException("timelapse.output-bound", "The encoded video exceeds its bound.");
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            ProfileVersion,
            profile,
            capability,
            timeline.IdentitySha256,
            width,
            height,
            images,
            arguments
        });
        return new(ProfileVersion, profile, capability, timeline.IdentitySha256, identity, images,
            await HashFileAsync(file, token).ConfigureAwait(false), length, media);
    }

    internal static string[] EncodingArguments(TimeLapseTimeline timeline, int width, int height, TimeLapseEncoderProfile profile)
    {
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-xerror", "-nostdin", "-n", "-threads", "1", "-filter_threads", "1",
            "-filter_complex_threads", "1", "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "0", "-i", "frames.ffconcat",
            "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1",
            "-vf", FormattableString.Invariant($"scale={width}:{height}:flags=lanczos,setsar=1")
        };
        args.AddRange(profile == TimeLapseEncoderProfile.Software
            ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-x264-params", "lookahead_threads=1:threads=1:fps=60/1"]
            : ["-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "constqp", "-qp", "18"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-threads", "1", "-bf", "0", "-fps_mode", "vfr", "-enc_time_base", "1:1000000",
            "-video_track_timescale", "1000000", "-movie_timescale", "1000000", "-movflags", "+faststart",
            // Concat's final duration does not control the final encoded packet. Set its duration explicitly,
            // without re-encoding or extending a short tail to one nominal frame interval.
            "-bsf:v", FormattableString.Invariant($"setts=duration=if(eq(N\\,{timeline.Intervals.Length - 1})\\,{timeline.Intervals[^1].DurationTicks}\\,DURATION)"),
            "video.mp4"]);
        return args.ToArray();
    }

    private async Task<TimeLapseMediaProof> VerifyAsync(string directory, IReadOnlyList<TimeLapseInterval> expected,
        int width, int height, CancellationToken token)
    {
        var json = await _process.RunAsync(_options.ProbePath,
            ["-v", "error", "-threads", "1", "-show_streams", "-show_format", "-show_packets", "-show_data_hash", "sha256",
             "-show_entries", "packet=pts,dts,duration,data_hash:stream=codec_name,codec_type,width,height,pix_fmt,time_base,profile,level,extradata_hash:format=format_name,duration",
             "-of", "json", "video.mp4"], directory, token, 32 * 1024 * 1024).ConfigureAwait(false);
        TimeLapseMediaProof proof;
        try { proof = ParseProof(json, expected, width, height); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new TimeLapseEncodingException("timelapse.media-invalid", "The encoder probe returned invalid media evidence.");
        }
        await _process.RunAsync(_options.ExecutablePath,
            ["-v", "error", "-xerror", "-nostdin", "-threads", "1", "-i", "video.mp4", "-map", "0:v:0", "-f", "null", "-"], directory, token)
            .ConfigureAwait(false);
        return proof;
    }

    private static TimeLapseMediaProof ParseProof(string json, IReadOnlyList<TimeLapseInterval> expected, int width, int height)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams");
        if (streams.GetArrayLength() != 1)
            throw new TimeLapseEncodingException("timelapse.media-invalid", "A time-lapse must contain one video stream.");
        var stream = streams[0];
        if (stream.GetProperty("codec_name").GetString() != "h264" ||
            stream.GetProperty("codec_type").GetString() != "video" || stream.GetProperty("width").GetInt32() != width ||
            stream.GetProperty("height").GetInt32() != height || stream.GetProperty("pix_fmt").GetString() != "yuv420p" ||
            stream.GetProperty("time_base").GetString() != "1/1000000" ||
            !root.GetProperty("format").GetProperty("format_name").GetString()!.Split(',').Contains("mp4", StringComparer.Ordinal))
            throw new TimeLapseEncodingException("timelapse.media-invalid", "Encoded media does not match the requested profile.");
        var packets = root.GetProperty("packets").EnumerateArray().Select(packet => new TimeLapsePacket(
            packet.GetProperty("pts").GetInt64(), packet.GetProperty("dts").GetInt64(),
            packet.GetProperty("duration").GetInt64(), packet.GetProperty("data_hash").GetString()!)).ToImmutableArray();
        if (packets.Length != expected.Count || packets.Zip(expected).Any(pair =>
                pair.First.PresentationTick != pair.Second.StartTick || pair.First.DecodeTick != pair.Second.StartTick ||
                pair.First.DurationTicks != pair.Second.DurationTicks || pair.First.Sha256 is not { Length: 71 } ||
                !pair.First.Sha256.StartsWith("SHA256:", StringComparison.Ordinal) || !pair.First.Sha256[7..].All(char.IsAsciiHexDigit)) ||
            !decimal.TryParse(root.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var duration) || Math.Abs(duration * TimeLapseTimeline.TicksPerSecond - expected[^1].EndTick) > 1)
            throw new TimeLapseEncodingException("timelapse.timing-invalid", "Encoded packet count, order or timing differs from the retained plan.");
        return new(width, height, expected[^1].EndTick, CaptureContractJson.ComputeCanonicalJsonSha256(stream), packets);
    }

    private (string Directory, FileStream Lease) OpenWorkspace()
    {
        Directory.CreateDirectory(_options.ScratchDirectory);
        var root = PhysicalRoot.Open(_options.ScratchDirectory);
        var lease = new FileStream(root.Resolve("encoder.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Only this adapter's fixed workspace, after taking its cross-process exclusive lease. Other host
            // data, and arbitrary sibling directories, are never enumerated or removed.
            var directory = root.Resolve("work");
            if (Directory.Exists(directory))
            {
                TimeLapseWorkspace.Delete(directory);
            }
            Directory.CreateDirectory(directory);
            root.Verify(directory, "create-encoder-workspace");
            return (directory, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        DurableSync.RequireRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private FFmpegCapability Cache(TimeLapseEncoderProfile profile, FFmpegCapability capability)
    {
        _capabilities[profile] = (capability, capability.Available ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddMinutes(1));
        return capability;
    }

    private async Task RequireBinariesAsync(FFmpegCapability capability, CancellationToken token)
    {
        if (await HashFileAsync(_options.ExecutablePath, token).ConfigureAwait(false) != capability.BinarySha256 ||
            await HashFileAsync(_options.ProbePath, token).ConfigureAwait(false) != capability.ProbeBinarySha256)
            throw new TimeLapseEncodingException("timelapse.encoder-changed", "A qualified media binary changed; restart to requalify it.");
    }

    private async Task VerifyCanaryOrderAsync(string directory, CancellationToken token)
    {
        await _process.RunAsync(_options.ExecutablePath,
            ["-v", "error", "-xerror", "-nostdin", "-n", "-threads", "1", "-filter_threads", "1", "-i", "video.mp4",
             "-vf", "scale=1:1:flags=area", "-pix_fmt", "rgb24", "-fps_mode", "passthrough", "-f", "rawvideo", "order.rgb"], directory, token).ConfigureAwait(false);
        var colors = await File.ReadAllBytesAsync(Path.Combine(directory, "order.rgb"), token).ConfigureAwait(false);
        if (colors.Length != 6 || colors[0] < 150 || colors[1] > 40 || colors[2] > 40 || colors[3] > 40 || colors[4] < 150 || colors[5] > 40)
            throw new TimeLapseEncodingException("timelapse.frame-order-invalid", "The encoder canary changed the source frame order or decoded colors.");
    }

    private static byte[] CanaryImage(int ordinal)
    {
        var header = Encoding.ASCII.GetBytes("P6\n512 512\n255\n");
        var image = new byte[header.Length + 512 * 512 * 3];
        header.CopyTo(image, 0);
        for (var offset = header.Length; offset < image.Length; offset += 3) image[offset + ordinal % 3] = 200;
        return image;
    }
}
