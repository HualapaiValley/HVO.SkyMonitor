using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed class CenteredViewSourceLayers(
    SqliteCaptureProcessingStore store, SqliteCenteredViews views, ICenteredViewLayerProducer producer,
    CenteredViewTrailPublication? trails = null)
{
    private const int PageSize = 24;

    internal async ValueTask RebuildForProductAsync(ExposureProductLookup source, string generation,
        DateTimeOffset observedUtc, CancellationToken token)
    {
        var bindings = await store.ReadCenteredLayerBindingsAsync(generation, token).ConfigureAwait(false);
        if (ResolveBinding(bindings, source.Slot) is not { } binding)
            return;

        long after = 0;
        IOException? retryableFailure = null;
        while (true)
        {
            var bases = await views.ReadContributorBasesAsync(source.Exposure, after, PageSize, token).ConfigureAwait(false);
            foreach (var (viewBase, order) in bases)
            {
                try
                {
                    await PublishAsync(viewBase, binding, source, observedUtc, token).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    retryableFailure ??= exception;
                }
                after = order;
            }
            if (bases.Count < PageSize)
                break;
        }
        if (retryableFailure is not null)
            ExceptionDispatchInfo.Throw(retryableFailure);
    }

    internal async ValueTask RebuildForBaseAsync(CenteredViewBase viewBase, CancellationToken token)
    {
        var bindings = await store.ReadCenteredLayerBindingsAsync(viewBase.Definition.Stream.GenerationId, token).ConfigureAwait(false);
        foreach (var contributor in viewBase.Definition.Contributors)
        {
            var exposure = contributor.Exposure;
            if (bindings is not { Count: 0 })
            {
                long after = 0;
                while (true)
                {
                    var sources = await store.ReadCenteredViewSourcesAsync(exposure.Input.Key, exposure.ExecutionId, after, PageSize, token)
                        .ConfigureAwait(false);
                    foreach (var source in sources)
                    {
                        if (ResolveBinding(bindings, source.Slot) is { } binding)
                            await PublishAsync(viewBase, binding, source, exposure.ExposureEndedUtc, token).ConfigureAwait(false);
                        after = source.Revision!.Value;
                    }
                    if (sources.Count < PageSize)
                        break;
                }
            }
            if (trails is not null)
                await trails.RebuildForBaseAsync(viewBase, exposure.Input.Key, token).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishAsync(CenteredViewBase viewBase, CenteredViewLayerBinding binding,
        ExposureProductLookup source, DateTimeOffset observedUtc, CancellationToken token)
    {
        var member = binding.Scope == CenteredViewLayerScope.Reference ? viewBase.Definition.Reference :
            viewBase.Definition.Contributors.Single(contributor => contributor.Exposure.Input.Key == source.Exposure).Exposure;
        if (member.Input.Key != source.Exposure || member.ExecutionId != source.Slot.ExecutionId || source.Revision is not > 0)
            return;

        if (source.Availability == ExposureProductAvailability.Available)
            await producer.PublishAsync(viewBase.BaseId, binding.Scope, source, binding.Style, observedUtc,
                cancellationToken: token).ConfigureAwait(false);
        else if (source.Availability != ExposureProductAvailability.Pending)
            await views.PublishUnavailableProductLayerAsync(viewBase.BaseId, binding.Key(source.Exposure.CaptureId),
                source, observedUtc, token).ConfigureAwait(false);
    }

    private static CenteredViewLayerBinding? ResolveBinding(
        IReadOnlyDictionary<(string Producer, string Schema, string Variant), CenteredViewLayerBinding>? bindings, ExposureProductSlot slot) =>
        bindings is null ? CenteredViewLayerBindings.ReadLegacySlot(slot) :
            bindings.GetValueOrDefault((slot.Producer, CenteredViewLayerBindings.SourceSchema(slot.SchemaVersion), slot.Variant));
}
