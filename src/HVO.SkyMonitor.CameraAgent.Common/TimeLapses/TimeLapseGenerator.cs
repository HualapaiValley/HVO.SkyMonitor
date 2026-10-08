using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

/// <summary>Owns local video lineage and publication. No generated video is passed to ingress or an upload outbox.</summary>
internal sealed class TimeLapseGenerator(SqliteTimeLapseStore store, TimeLapseSourceReader sources,
    FFmpegTimeLapseEncoder encoder, TimeProvider clock)
{
    internal async ValueTask RunAsync(CameraAgentTimeLapseJob job, CancellationToken token)
    {
        if (!job.Occurrence.IsValid() || !job.Window.IsEligibleForFinal(clock.GetUtcNow()) ||
            !Options.TimeLapseOptions.Matches(job.Preset)) throw new InvalidDataException("The retained video request is invalid.");
        var day = await store.GetDayAsync(job.Window.ReportingPeriod.ReportDate, token).ConfigureAwait(false);
        // A crash after immutable publication but before job completion reuses exactly that job's product.
        var previous = day.Products.SingleOrDefault(product => product.JobId == job.JobId && !product.IsGapFiller);
        if (previous is not null)
        {
            await CompleteWithProductAsync(job, previous, token).ConfigureAwait(false);
            return;
        }
        if (job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay)
        {
            await AssembleDayAsync(job, day, token).ConfigureAwait(false);
            return;
        }
        if (job.ParentJobId.HasValue && await store.FindWindowAsync(job, job.Window, token).ConfigureAwait(false) is { IsGapFiller: false } reusable)
        {
            await CompleteWithProductAsync(job, reusable, token).ConfigureAwait(false);
            return;
        }
        var plan = await sources.FreezeAsync(job, token).ConfigureAwait(false);
        var candidates = plan.Sources;
        var qualityExcluded = new HashSet<Guid>();
        if (job.Preset.Frames.MaximumSaturatedMillionths.HasValue)
        {
            var admitted = new List<CameraAgentTimeLapseRawSource>();
            var excluded = new Dictionary<string, int>(plan.Exclusions, StringComparer.Ordinal);
            foreach (var source in plan.Sources)
            {
                var restored = await sources.RestoreAsync(source, token).ConfigureAwait(false);
                if (TimeLapseFrameRenderer.AcceptsQuality(restored, job.Preset.Frames, token)) admitted.Add(source);
                else
                {
                    qualityExcluded.Add(source.Descriptor.Artifact.ArtifactId);
                    if (source.Descriptor.Timing.ExposureStartedUtc >= job.Window.StartUtc)
                        excluded["timelapse.quality-saturation"] = excluded.GetValueOrDefault("timelapse.quality-saturation") + 1;
                }
            }
            // The retained candidate plan is immutable. Admission is reproducible from its checksummed pixels and preset.
            plan = new(admitted, excluded);
        }
        var timeline = TimeLapseTimelinePlanner.Create(job.Window.StartUtc, job.Window.EndUtc,
            candidates.Where(source => source.Descriptor.Timing.ExposureStartedUtc >= job.Window.StartUtc).Select(source => new TimeLapseSource(
                source.Descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeCanonicalJsonSha256(source.Descriptor),
                source.Descriptor.Timing.ExposureStartedUtc, source.Descriptor.Controls.EffectiveExposure,
                qualityExcluded.Contains(source.Descriptor.Artifact.ArtifactId) ? "timelapse.quality-saturation" : null)), job.Preset.Timing);
        await store.RecordExclusionsAsync(job.JobId, plan.Exclusions, token).ConfigureAwait(false);
        if (!timeline.HasSources)
        {
            await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.NoSources,
                plan.Exclusions.ContainsKey("timelapse.quality-saturation") ? "timelapse.quality-excluded-all" : "timelapse.no-admitted-sources",
                null, token).ConfigureAwait(false);
            return;
        }
        var first = plan.Sources[0].Descriptor.Layout;
        var size = TimeLapseFrameRenderer.Fit(first.Width, first.Height, job.Preset.MaximumDimension);
        var lineage = new List<CameraAgentTimeLapseFrameLineage>();
        var byId = plan.Sources.ToDictionary(static source => source.Descriptor.Artifact.ArtifactId);
        var sequence = new TimeLapseFrameSequence(plan.Sources.Select(static source => TimeLapseSourceReader.Describe(source.Descriptor)).ToArray(),
            job.Preset.Rig, job.Preset.Frames, size.Width, size.Height, (id, cancellationToken) => sources.RestoreAsync(byId[id], cancellationToken),
            utc => sources.IsDaytime(job, utc));
        async ValueTask<ReadOnlyMemory<byte>> Restore(int? ordinal, CancellationToken cancellationToken)
        {
            if (ordinal is null) return TimeLapseFrameRenderer.GapImage(size.Width, size.Height);
            var id = timeline.Sources[ordinal.Value].ArtifactId;
            var rendered = await sequence.RenderAsync(id, cancellationToken).ConfigureAwait(false);
            lineage.Add(new(id, rendered.RenderingIdentitySha256, rendered.StackSourceIds,
                rendered.StackPayloadSha256, rendered.TotalIntegration));
            return rendered.Jpeg;
        }
        await store.RequireWorkspaceCapacityAsync(token).ConfigureAwait(false);
        var encoded = await encoder.EncodeAsync(timeline, size.Width, size.Height, TimeLapseEncoderProfile.Software, Restore, token)
            .ConfigureAwait(false);
        await using var encodedLifetime = encoded.ConfigureAwait(false);
        var product = CreateProduct(job, timeline, lineage, [], plan.Exclusions, encoded.Evidence,
            timeline.Intervals.Any(static interval => interval.SourceOrdinal is null));
        await store.PublishAsync(product, encoded, token).ConfigureAwait(false);
        await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Produced, null, product.ProductId, token).ConfigureAwait(false);
    }

    private async ValueTask AssembleDayAsync(CameraAgentTimeLapseJob job, CameraAgentTimeLapseDay day, CancellationToken token)
    {
        var children = day.Jobs.Where(child => child.ParentJobId == job.JobId).OrderBy(static child => child.Window.StartUtc).ToArray();
        if (children.Length is < 1 or > 50 || children[0].Window.StartUtc != job.Window.StartUtc || children[^1].Window.EndUtc != job.Window.EndUtc ||
            children.Zip(children.Skip(1)).Any(pair => pair.First.Window.EndUtc != pair.Second.Window.StartUtc))
            throw new InvalidDataException("The retained daily video partition is incomplete.");
        if (children.Any(static child => child.State is not (CameraAgentTimeLapseState.Produced or CameraAgentTimeLapseState.NoSources)))
        {
            await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Failed, "timelapse.hourly-generation-incomplete", null, token).ConfigureAwait(false);
            return;
        }
        if (children.All(static child => child.State == CameraAgentTimeLapseState.NoSources))
        {
            var emptyExclusions = children.SelectMany(static child => child.Exclusions).GroupBy(static pair => pair.Key)
                .ToDictionary(static group => group.Key, static group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
            await store.RecordExclusionsAsync(job.JobId, emptyExclusions, token).ConfigureAwait(false);
            await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, token).ConfigureAwait(false);
            return;
        }
        var products = new List<CameraAgentTimeLapseProduct>();
        var actual = day.Products.Single(product => product.ProductId == children.First(child => child.ProductId.HasValue).ProductId);
        foreach (var child in children)
        {
            var product = child.ProductId is { } id ? await store.GetAsync(id, token).ConfigureAwait(false)
                : day.Products.SingleOrDefault(candidate => candidate.JobId == child.JobId && candidate.IsGapFiller);
            if (product is null && child.State == CameraAgentTimeLapseState.NoSources)
            {
                var empty = TimeLapseTimelinePlanner.Create(child.Window.StartUtc, child.Window.EndUtc, [], child.Preset.Timing);
                var size = actual.Encoding.Media;
                await store.RequireWorkspaceCapacityAsync(token).ConfigureAwait(false);
                var gap = await encoder.EncodeAsync(empty, size.Width, size.Height, actual.Encoding.Profile,
                    (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(TimeLapseFrameRenderer.GapImage(size.Width, size.Height)), token).ConfigureAwait(false);
                await using var gapLifetime = gap.ConfigureAwait(false);
                product = CreateProduct(child, empty, [], [], child.Exclusions, gap.Evidence, true);
                product = await store.PublishAsync(product, gap, token).ConfigureAwait(false);
            }
            products.Add(product ?? throw new InvalidDataException("A retained hourly product is missing."));
        }
        await store.RequireWorkspaceCapacityAsync(token).ConfigureAwait(false);
        var encoded = await encoder.AssembleAsync(products.Select(product => new TimeLapseAssemblySegment(
                product.StartUtc, product.EndUtc, product.Encoding)).ToArray(), job.Preset.Timing.Compression,
            async (index, cancellationToken) => await store.OpenVideoAsync(products[index].ProductId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A retained hourly video payload is missing."), token).ConfigureAwait(false);
        await using var encodedLifetime = encoded.ConfigureAwait(false);
        var exclusions = products.SelectMany(static product => product.Exclusions).GroupBy(static pair => pair.Key)
            .ToDictionary(static group => group.Key, static group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        var daily = CreateProduct(job, null, [], products.Select(static product => product.ProductId).ToArray(), exclusions,
            encoded.Evidence, products.Any(static product => product.HasGaps));
        await store.RecordExclusionsAsync(job.JobId, exclusions, token).ConfigureAwait(false);
        await store.PublishAsync(daily, encoded, token).ConfigureAwait(false);
        await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Produced, null, daily.ProductId, token).ConfigureAwait(false);
    }

    private async ValueTask CompleteWithProductAsync(CameraAgentTimeLapseJob job, CameraAgentTimeLapseProduct product, CancellationToken token)
    {
        using var stream = await store.OpenVideoAsync(product.ProductId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The retained video payload is unavailable.");
        await store.RecordExclusionsAsync(job.JobId, product.Exclusions, token).ConfigureAwait(false);
        await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Produced, null, product.ProductId, token).ConfigureAwait(false);
    }

    private CameraAgentTimeLapseProduct CreateProduct(CameraAgentTimeLapseJob job, TimeLapseTimeline? timeline,
        IReadOnlyList<CameraAgentTimeLapseFrameLineage> frames, IReadOnlyList<Guid> segments,
        IReadOnlyDictionary<string, int> exclusions, TimeLapseEncodingEvidence encoding, bool gaps)
    {
        var product = new CameraAgentTimeLapseProduct(Guid.Empty, string.Empty, job.JobId, job.Window.ReportingPeriod.ReportDate,
            job.Window.ReportingPeriod.IdentitySha256, job.Window.IdentitySha256, job.Window.StartUtc, job.Window.EndUtc,
            job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay, job.Preset.IdentitySha256,
            timeline, frames, segments, exclusions, encoding, clock.GetUtcNow().ToUniversalTime())
        { HasGaps = gaps, IsGapFiller = timeline is { HasSources: false } };
        var identity = CameraAgentTimeLapseProductIdentity.Compute(product);
        return product with { ProductId = ProcessingIdentity.CreateArtifactId(identity), OutputIdentitySha256 = identity };
    }
}
