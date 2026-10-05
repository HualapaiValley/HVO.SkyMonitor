using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal static class CameraAgentTimeLapseProductIdentity
{
    internal static string Compute(CameraAgentTimeLapseProduct product)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            version = "hvo-cameraagent-timelapse-product-v1",
            product.JobId,
            window = product.WindowIdentitySha256,
            preset = product.PresetIdentitySha256,
            timeline = product.Timeline,
            frames = product.Frames,
            segments = product.SegmentProductIds,
            exclusions = product.Exclusions,
            product.Encoding.EncodingIdentitySha256,
            product.Encoding.PayloadSha256,
            gaps = product.HasGaps,
            gapFiller = product.IsGapFiller
        });

    internal static void Validate(CameraAgentTimeLapseProduct product)
    {
        TimeLapseEvidenceValidation.Validate(product.Encoding);
        if (product.OutputIdentitySha256 != Compute(product) || product.ProductId != ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256) ||
            product.StartUtc.Offset != TimeSpan.Zero || product.EndUtc.Offset != TimeSpan.Zero || product.EndUtc <= product.StartUtc ||
            product.Encoding.ProfileVersion != FFmpegTimeLapseEncoder.ProfileVersion || product.Encoding.Profile != TimeLapseEncoderProfile.Software ||
            !product.Encoding.Capability.Available || product.Encoding.PayloadBytes <= 0 ||
            product.IsDaily != (product.Timeline is null) || product.SegmentProductIds.Count > 50 || product.Frames.Count > 8192 ||
            product.IsGapFiller != (product.Timeline is { HasSources: false }) ||
            product.IsDaily && (product.SegmentProductIds.Count == 0 || product.Frames.Count != 0) ||
            !product.IsDaily && product.SegmentProductIds.Count != 0)
            throw new InvalidDataException("The retained local video identity is invalid.");
        if (product.Timeline is { } timeline)
        {
            var expected = TimeLapseTimelinePlanner.Create(product.StartUtc, product.EndUtc, timeline.Sources, timeline.Options);
            if (timeline.IdentitySha256 != expected.IdentitySha256 || !timeline.Intervals.SequenceEqual(expected.Intervals) ||
                product.Encoding.TimelineIdentitySha256 != expected.IdentitySha256 || product.Encoding.Media.DurationTicks != expected.DurationTicks ||
                product.Encoding.Media.Packets.Count != expected.Intervals.Length ||
                !product.Frames.Select(static frame => frame.ArtifactId).SequenceEqual(expected.Intervals.Where(static interval => interval.SourceOrdinal.HasValue)
                    .Select(interval => expected.Sources[interval.SourceOrdinal!.Value].ArtifactId)))
                throw new InvalidDataException("The local video does not match its retained source timeline.");
        }
    }
}
