using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

internal static class CentralTimeLapseProductIdentity
{
    internal static Guid Compute(CentralTimeLapseProduct product)
        => ProcessingIdentity.CreateArtifactId(CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            request = product.RequestIdentity, product.Encoding.EncodingIdentitySha256, product.Encoding.PayloadSha256,
            timeline = product.Timeline, frames = product.Frames, segments = product.SegmentProductIds,
            exclusions = product.Exclusions, gaps = product.HasGaps, gap = product.IsGapFiller
        }));

    internal static void Validate(CentralTimeLapseProduct product)
    {
        CentralTimeLapseObjects.Validate(product);
        TimeLapseEvidenceValidation.Validate(product.Encoding);
        if (product.ProductId != Compute(product) || product.JobId != ProcessingIdentity.CreateArtifactId(product.RequestIdentity) ||
            !product.Period.IsValid() || product.StartUtc.Offset != TimeSpan.Zero || product.EndUtc.Offset != TimeSpan.Zero ||
            product.StartUtc < product.Period.StartUtc || product.EndUtc > product.Period.EndUtc || product.EndUtc <= product.StartUtc ||
            product.IsDaily != (product.Timeline is null) || product.SegmentProductIds.Count > 50 || product.Frames.Count > 8192 ||
            product.IsGapFiller != (product.Timeline is { HasSources: false }) ||
            product.IsDaily && (product.SegmentProductIds.Count == 0 || product.Frames.Count != 0 || product.StartUtc != product.Period.StartUtc || product.EndUtc != product.Period.EndUtc) ||
            !product.IsDaily && (product.SegmentProductIds.Count != 0 || product.EndUtc - product.StartUtc > TimeSpan.FromHours(2)) ||
            product.Exclusions.Count > 32 || product.Exclusions.Any(static pair => pair.Key.Length is < 1 or > 128 || pair.Value < 0))
            throw new InvalidDataException("The central video identity is invalid.");
        if (product.Timeline is { } timeline)
        {
            var expected = TimeLapseTimelinePlanner.Create(product.StartUtc, product.EndUtc, timeline.Sources, timeline.Options);
            if (timeline.IdentitySha256 != expected.IdentitySha256 || !timeline.Intervals.SequenceEqual(expected.Intervals) ||
                product.Encoding.TimelineIdentitySha256 != expected.IdentitySha256 || product.Encoding.Media.DurationTicks != expected.DurationTicks ||
                product.Encoding.Media.Packets.Count != expected.Intervals.Length ||
                product.HasGaps != expected.Intervals.Any(static interval => interval.SourceOrdinal is null) ||
                !product.Frames.Select(static frame => frame.ArtifactId).SequenceEqual(expected.Intervals.Where(static interval => interval.SourceOrdinal.HasValue)
                    .Select(interval => expected.Sources[interval.SourceOrdinal!.Value].ArtifactId)))
                throw new InvalidDataException("The central video does not match its retained source timeline.");
        }
    }
}
