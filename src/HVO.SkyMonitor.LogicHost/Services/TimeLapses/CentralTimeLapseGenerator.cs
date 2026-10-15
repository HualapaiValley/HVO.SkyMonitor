using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

internal sealed class CentralTimeLapseGenerator(CentralTimeLapseStore store, CentralTimeLapseSources sources,
    CentralTimeLapseObjects objects, FFmpegTimeLapseEncoder encoder, IPlanetEphemeris ephemeris, TimeProvider clock)
{
    internal async Task RunAsync(CentralTimeLapseLease lease, CancellationToken token)
    {
        var request = lease.Request;
        if (clock.GetUtcNow() < request.EligibleUtc) throw new InvalidDataException("The video source window has not settled.");
        if (await store.FindForJobAsync(lease.JobId, token).ConfigureAwait(false) is { } retained)
        {
            await objects.VerifyAsync(retained, token).ConfigureAwait(false);
            await store.CompleteAsync(lease, retained.IsGapFiller ? CentralTimeLapseState.NoSources : CentralTimeLapseState.Produced,
                null, retained.ProductId, token).ConfigureAwait(false);
            return;
        }
        // Only unpublished keys under this request's private prefix are recoverable temporaries.
        await objects.RemoveUnpublishedAsync(lease, await store.RetainedChunkKeysAsync(lease, token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (request.IsDaily)
        {
            await AssembleAsync(lease, token).ConfigureAwait(false);
            return;
        }
        var excluded = new Dictionary<string, int>(request.Exclusions, StringComparer.Ordinal);
        var rejected = new HashSet<Guid>();
        var admitted = new List<CentralTimeLapseSource>();
        foreach (var source in request.Sources)
        {
            if (request.Preset.Frames.MaximumSaturatedMillionths.HasValue &&
                !TimeLapseFrameRenderer.AcceptsQuality(await sources.RestoreAsync(source, token).ConfigureAwait(false), request.Preset.Frames, token))
            {
                rejected.Add(source.Descriptor.Artifact.ArtifactId);
                if (source.Descriptor.Timing.ExposureStartedUtc >= request.StartUtc)
                    excluded["timelapse.quality-saturation"] = excluded.GetValueOrDefault("timelapse.quality-saturation") + 1;
            }
            else admitted.Add(source);
        }
        var timeline = TimeLapseTimelinePlanner.Create(request.StartUtc, request.EndUtc,
            request.Sources.Where(source => source.Descriptor.Timing.ExposureStartedUtc >= request.StartUtc).Select(source => new TimeLapseSource(
                source.Descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeCanonicalJsonSha256(source.Descriptor),
                source.Descriptor.Timing.ExposureStartedUtc, source.Descriptor.Controls.EffectiveExposure,
                rejected.Contains(source.Descriptor.Artifact.ArtifactId) ? "timelapse.quality-saturation" : null)), request.Preset.Timing);
        if (!timeline.HasSources)
        {
            await store.CompleteAsync(lease, CentralTimeLapseState.NoSources,
                rejected.Count > 0 ? "timelapse.quality-excluded-all" : "timelapse.no-admitted-sources", null, token, excluded).ConfigureAwait(false);
            return;
        }
        await RequireProfileAsync(request, token).ConfigureAwait(false);
        var layout = admitted[0].Descriptor.Layout;
        var size = TimeLapseFrameRenderer.Fit(layout.Width, layout.Height, request.Preset.MaximumDimension);
        var byId = admitted.ToDictionary(static source => source.Descriptor.Artifact.ArtifactId);
        var sequence = new TimeLapseFrameSequence(admitted.Select(source => LogicHostRecipeExecutionAdapter.CreateArtifact(source.Descriptor)).ToArray(),
            request.Preset.Rig, request.Preset.Frames, size.Width, size.Height, (id, cancellationToken) => sources.RestoreAsync(byId[id], cancellationToken),
            utc => SolarAltitudeClassifier.DirectionAt(ephemeris, utc, request.Period.Site.LatitudeDegrees, request.Period.Site.LongitudeDegrees).AltitudeDegrees >= -6);
        var frames = new List<CentralTimeLapseFrameLineage>();
        async ValueTask<ReadOnlyMemory<byte>> Render(int? ordinal, CancellationToken cancellationToken)
        {
            if (ordinal is null) return TimeLapseFrameRenderer.GapImage(size.Width, size.Height);
            var id = timeline.Sources[ordinal.Value].ArtifactId;
            var rendered = await sequence.RenderAsync(id, cancellationToken).ConfigureAwait(false);
            frames.Add(new(id, rendered.RenderingIdentitySha256, rendered.StackSourceIds, rendered.StackPayloadSha256, rendered.TotalIntegration));
            return rendered.Jpeg;
        }
        var encoded = await encoder.EncodeAsync(timeline, size.Width, size.Height, request.Preset.EncoderProfile, Render, token).ConfigureAwait(false);
        await using var encodedLifetime = encoded.ConfigureAwait(false);
        var product = await PublishAsync(lease, request, encoded, timeline, frames, [], excluded,
            timeline.Intervals.Any(static interval => interval.SourceOrdinal is null), token).ConfigureAwait(false);
        await store.CompleteAsync(lease, CentralTimeLapseState.Produced, null, product.ProductId, token, excluded).ConfigureAwait(false);
    }

    private async Task AssembleAsync(CentralTimeLapseLease lease, CancellationToken token)
    {
        var request = lease.Request;
        var children = await store.ChildrenAsync(lease, token).ConfigureAwait(false);
        if (children.Count != request.HourlyJobIds.Count || children[0].StartUtc != request.StartUtc || children[^1].EndUtc != request.EndUtc ||
            children.Zip(children.Skip(1)).Any(pair => pair.First.EndUtc != pair.Second.StartUtc))
            throw new InvalidDataException("The immutable daily hour partition is incomplete.");
        var exclusions = children.SelectMany(static child => CentralTimeLapseStore.ReadExclusions(child)).GroupBy(static pair => pair.Key)
            .ToDictionary(static group => group.Key, static group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        if (children.Any(static child => child.State is not (CentralTimeLapseState.Produced or CentralTimeLapseState.NoSources)))
        {
            await store.CompleteAsync(lease, CentralTimeLapseState.Failed, "timelapse.hourly-generation-incomplete", null, token, exclusions).ConfigureAwait(false);
            return;
        }
        if (children.All(static child => child.State == CentralTimeLapseState.NoSources))
        {
            await store.CompleteAsync(lease, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, token, exclusions).ConfigureAwait(false);
            return;
        }
        await RequireProfileAsync(request, token).ConfigureAwait(false);
        var size = TimeLapseFrameRenderer.Fit(request.Preset.Rig.Sensor.WidthPixels, request.Preset.Rig.Sensor.HeightPixels, request.Preset.MaximumDimension);
        var products = new List<CentralTimeLapseProduct>();
        foreach (var child in children)
        {
            var product = await store.FindForJobAsync(child.Id, token).ConfigureAwait(false);
            if (product is null && child.State == CentralTimeLapseState.NoSources)
            {
                var childRequest = CentralTimeLapseStore.ReadRequest(child);
                var timeline = TimeLapseTimelinePlanner.Create(child.StartUtc, child.EndUtc, [], request.Preset.Timing);
                var gap = await encoder.EncodeAsync(timeline, size.Width, size.Height, request.Preset.EncoderProfile,
                    (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(TimeLapseFrameRenderer.GapImage(size.Width, size.Height)), token).ConfigureAwait(false);
                await using var gapLifetime = gap.ConfigureAwait(false);
                product = await PublishAsync(lease, childRequest, gap, timeline, [], [], CentralTimeLapseStore.ReadExclusions(child), true, token).ConfigureAwait(false);
            }
            products.Add(product ?? throw new InvalidDataException("A retained hourly video is missing."));
        }
        var assembled = await encoder.AssembleAsync(products.Select(product => new TimeLapseAssemblySegment(product.StartUtc, product.EndUtc, product.Encoding)).ToArray(),
            request.Preset.Timing.Compression, (index, cancellationToken) => new ValueTask<Stream>(objects.OpenAsync(products[index], cancellationToken)), token).ConfigureAwait(false);
        await using var assembledLifetime = assembled.ConfigureAwait(false);
        var daily = await PublishAsync(lease, request, assembled, null, [], products.Select(static product => product.ProductId).ToArray(),
            exclusions, products.Any(static product => product.HasGaps), token).ConfigureAwait(false);
        await store.CompleteAsync(lease, CentralTimeLapseState.Produced, null, daily.ProductId, token, exclusions).ConfigureAwait(false);
    }

    private async Task<CentralTimeLapseProduct> PublishAsync(CentralTimeLapseLease authority, CentralTimeLapseRequest request,
        EncodedTimeLapse encoded, TimeLapseTimeline? timeline, IReadOnlyList<CentralTimeLapseFrameLineage> frames,
        IReadOnlyList<Guid> segments, IReadOnlyDictionary<string, int> exclusions, bool gaps, CancellationToken token)
    {
        var gap = timeline is { HasSources: false };
        var chunks = await objects.PutAsync(authority, request.JobId, encoded, token).ConfigureAwait(false);
        var product = new CentralTimeLapseProduct(Guid.Empty, request.JobId, request.DevicePublicId,
            request.ObservatoryId, request.Period, request.StartUtc, request.EndUtc, request.IsDaily, gap, gaps, request.Identity,
            timeline, frames, segments, exclusions, encoded.Evidence, chunks, clock.GetUtcNow(), authority.JobId, authority.Attempt);
        return await store.CommitAsync(authority, product with { ProductId = CentralTimeLapseProductIdentity.Compute(product) }, token).ConfigureAwait(false);
    }

    private async Task RequireProfileAsync(CentralTimeLapseRequest request, CancellationToken token)
    {
        var capability = await encoder.QualifyAsync(request.Preset.EncoderProfile, token).ConfigureAwait(false);
        if (!capability.Available) throw new TimeLapseEncodingException(capability.ReasonCode ?? "timelapse.encoder-unavailable", "The selected central encoder is unavailable.");
        if (CaptureContractJson.ComputeCanonicalJsonSha256(capability) != request.Preset.CapabilityIdentity)
            throw new TimeLapseEncodingException("timelapse.encoder-profile-changed", "The retained central encoder capability changed.");
    }
}
