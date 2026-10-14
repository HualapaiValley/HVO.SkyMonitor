using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>Host-verified immutable segment and its retained wall-clock window; streams never carry host paths.</summary>
public sealed record TimeLapseAssemblySegment(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimeLapseEncodingEvidence Evidence);

public sealed partial class FFmpegTimeLapseEncoder
{
    /// <summary>
    /// Joins at most 50 adjacent hourly pieces. Callers explicitly supply gap slates for missing hours.
    /// A different codec configuration, encoder profile or discontinuous window is rejected, never silently mixed.
    /// Complete packet hash/timing equality proves the daily output reused the encoded segments.
    /// </summary>
    public async Task<EncodedTimeLapse> AssembleAsync(IReadOnlyList<TimeLapseAssemblySegment> segments,
        int compression, Func<int, CancellationToken, ValueTask<Stream>> openSegment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(openSegment);
        if (segments.Count is < 1 or > 50 || compression is not (180 or 300))
            throw new ArgumentException("Invalid daily time-lapse assembly.");
        var first = segments[0].Evidence;
        if (segments.Any(segment => segment.StartUtc.Offset != TimeSpan.Zero || segment.EndUtc.Offset != TimeSpan.Zero ||
                segment.EndUtc <= segment.StartUtc || segment.EndUtc - segment.StartUtc > TimeSpan.FromHours(2) ||
                segment.Evidence.ProfileVersion != ProfileVersion || segment.Evidence.Profile != first.Profile ||
                segment.Evidence.Capability != first.Capability ||
                segment.Evidence.Media.StreamIdentitySha256 != first.Media.StreamIdentitySha256 ||
                segment.Evidence.Media.Width != first.Media.Width || segment.Evidence.Media.Height != first.Media.Height ||
                segment.Evidence.PayloadBytes <= 0 || segment.Evidence.PayloadBytes > _options.MaximumOutputBytes ||
                segment.Evidence.Media.DurationTicks != TimeLapseTimeline.QuantizeDuration(segment.EndUtc - segment.StartUtc, compression)) ||
            segments.Zip(segments.Skip(1)).Any(pair => pair.First.EndUtc != pair.Second.StartUtc) ||
            segments[^1].EndUtc - segments[0].StartUtc > TimeSpan.FromHours(48))
            throw new TimeLapseEncodingException("timelapse.incompatible-segments", "Daily segments are discontinuous or have incompatible retained profiles.");
        var totalBytes = segments.Sum(static segment => segment.Evidence.PayloadBytes);
        if (totalBytes > _options.MaximumOutputBytes || totalBytes * 2 > _options.MaximumScratchBytes)
            throw new TimeLapseEncodingException("timelapse.scratch-bound", "Daily segment assembly exceeds its reserved storage bound.");
        var capability = await QualifyAsync(first.Profile, cancellationToken).ConfigureAwait(false);
        if (!capability.Available || capability != first.Capability)
            throw new TimeLapseEncodingException("timelapse.encoder-changed", "The daily assembler is not the qualified segment encoder.");
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        FileStream? lease = null;
        try
        {
            (directory, lease) = OpenWorkspace();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(_options.Timeout);
            await RequireBinariesAsync(capability, lifetime.Token).ConfigureAwait(false);
            var expected = new List<TimeLapseInterval>();
            var packetHashes = new List<string>();
            var lines = new StringBuilder("ffconcat version 1.0\n");
            long cursor = 0;
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var name = index.ToString("D2", CultureInfo.InvariantCulture) + ".mp4";
                await CopyVerifiedAsync(await openSegment(index, lifetime.Token).ConfigureAwait(false),
                    Path.Combine(directory, name), segment.Evidence, lifetime.Token).ConfigureAwait(false);
                foreach (var packet in segment.Evidence.Media.Packets)
                {
                    expected.Add(new(null, checked(cursor + packet.PresentationTick), packet.DurationTicks));
                    packetHashes.Add(packet.Sha256);
                }
                cursor = checked(cursor + segment.Evidence.Media.DurationTicks);
                lines.Append("file ").Append(name).Append("\nduration ")
                    .Append((segment.Evidence.Media.DurationTicks / 1_000_000m).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
            }
            if (expected.Count > TimeLapseTimeline.MaximumSources * 2 + 100)
                throw new TimeLapseEncodingException("timelapse.frame-bound", "Daily packet lineage exceeds its bound.");
            await File.WriteAllTextAsync(Path.Combine(directory, "segments.ffconcat"), lines.ToString(), lifetime.Token).ConfigureAwait(false);
            await _process.RunAsync(_options.ExecutablePath,
                ["-hide_banner", "-v", "error", "-xerror", "-nostdin", "-n", "-threads", "1", "-f", "concat", "-safe", "1",
                 "-auto_convert", "0", "-i", "segments.ffconcat", "-map", "0:v:0", "-an", "-c:v", "copy",
                 "-video_track_timescale", "1000000", "-movie_timescale", "1000000", "-movflags", "+faststart", "video.mp4"], directory, lifetime.Token)
                .ConfigureAwait(false);
            var media = await VerifyAsync(directory, expected, first.Media.Width, first.Media.Height, lifetime.Token).ConfigureAwait(false);
            if (!media.Packets.Select(static packet => packet.Sha256).SequenceEqual(packetHashes, StringComparer.Ordinal))
                throw new TimeLapseEncodingException("timelapse.assembly-packets-changed", "Daily assembly changed encoded packet payloads.");
            var path = Path.Combine(directory, "video.mp4");
            var timelineIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                schema = "hvo-timelapse-hourly-assembly-v1",
                compression,
                segments = segments.Select(segment => new
                {
                    segment.StartUtc,
                    segment.EndUtc,
                    segment.Evidence.TimelineIdentitySha256,
                    segment.Evidence.EncodingIdentitySha256,
                    segment.Evidence.PayloadSha256
                })
            });
            var evidence = new TimeLapseEncodingEvidence(ProfileVersion, first.Profile, capability, timelineIdentity,
                CaptureContractJson.ComputeCanonicalJsonSha256(new { ProfileVersion, capability, timelineIdentity, streamCopy = true }),
                new Dictionary<int, string>(), await HashFileAsync(path, lifetime.Token).ConfigureAwait(false), new FileInfo(path).Length, media);
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

    private static async Task CopyVerifiedAsync(Stream input, string path, TimeLapseEncodingEvidence evidence, CancellationToken token)
    {
        using (input)
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total = checked(total + count);
                if (total > evidence.PayloadBytes)
                    throw new TimeLapseEncodingException("timelapse.segment-integrity", "A segment exceeds its published length.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (total != evidence.PayloadBytes || Convert.ToHexString(hash.GetHashAndReset()) != evidence.PayloadSha256)
                throw new TimeLapseEncodingException("timelapse.segment-integrity", "A segment differs from its published checksum or length.");
        }
    }
}
