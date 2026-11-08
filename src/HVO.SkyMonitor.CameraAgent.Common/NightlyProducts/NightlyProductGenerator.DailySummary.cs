using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

internal sealed partial class NightlyProductGenerator
{
    private async ValueTask EvaluateDailySummaryAsync(RunContext context, NightlyProductWindow window, int candidateCount,
        NightlyProductAdmissionResult admission, CancellationToken cancellationToken)
    {
        var period = context.Occurrence.SourceWindow!.ReportingPeriod;
        var day = await _store.GetDayAsync(window.ObservingDate, cancellationToken).ConfigureAwait(false);
        var links = new List<DailySummaryProductV1>();
        foreach (var product in day.Products.Where(product => product.Kind != NightlyProductKind.DailySummary &&
            product.Scope == NightlyProductScope.Final && product.IsCurrent && product.WindowStartUtc == window.StartUtc &&
            product.WindowEndUtc == window.EndUtc))
        {
            var detail = await _store.GetAsync(product.ProductId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A selected summary product is no longer published.");
            if (detail.Occurrence.SourceWindow!.ReportingPeriod.IdentitySha256 != period.IdentitySha256) continue;
            await _store.VerifyPublicationAsync(product.ProductId, cancellationToken).ConfigureAwait(false);
            links.Add(new(product.ProductId, NightlyProductContract.TargetFor(product.Kind), detail.OutputIdentitySha256,
                product.FirstObservationUtc));
        }
        var summary = DailySummaryProducer.Summarize(window.ObservingDate, period.IdentitySha256,
            window.StartUtc, window.EndUtc, candidateCount,
            [.. admission.Admitted.Select(static source => new DailySummaryCaptureV1(source.ArtifactId, source.CaptureId,
                source.OutputIdentitySha256, source.ExposureStartedUtc))], links, admission.Exclusions);
        var selected = admission.Admitted.Single(source => source.ArtifactId == summary.RepresentativeArtifactId);
        var selector = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, selected.Variant, selected.RecipeIdentitySha256);
        var auxiliary = new[] { NightlyProductPreset.BindOccurrence(context.Occurrence) };
        var recipe = DailySummaryProducer.CreateIdentity(summary, selector, auxiliary);
        Guid[] ids = [.. summary.Captures.Select(static source => source.ArtifactId), .. summary.Products.Select(static product => product.ProductId)];
        var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, DailySummaryProducer.Variant,
            recipe.IdentitySha256, ids);
        var fingerprint = identity;
        if (await IsUnchangedAsync(context, NightlyProductScope.Final, window.StartUtc, fingerprint, cancellationToken).ConfigureAwait(false))
            return;
        var existing = await _store.FindByOutputIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await _store.VerifyPublicationAsync(existing.Summary.ProductId, cancellationToken).ConfigureAwait(false);
            context.Reused++;
        }
        else
        {
            var restored = await _sources.RestoreAsync([selected], cancellationToken).ConfigureAwait(false);
            var product = DailySummaryProducer.Produce(summary, restored.Single(), selector, auxiliary, cancellationToken);
            NightlyProductSource[] lineage = [
                .. summary.Captures.Select((source, index) => new NightlyProductSource(index, NightlyProductSourceKind.PreviewFrame,
                    source.ArtifactId, source.OutputIdentitySha256, source.CaptureId, source.ObservationStartedUtc)),
                .. summary.Products.Select((source, index) => new NightlyProductSource(summary.Captures.Count + index,
                    NightlyProductSourceKind.NightlyProduct, source.ProductId, source.OutputIdentitySha256, null, source.ObservationStartedUtc))];
            existing = await _store.PublishAsync(new NightlyProductPublication(NightlyProductKind.DailySummary,
                NightlyProductScope.Final, window.ObservingDate, window.StartUtc, window.EndUtc, 0, DailySummaryProducer.RecipeName,
                product, lineage, context.Options.RenditionJpegQuality)
            { Occurrence = context.Occurrence }, cancellationToken).ConfigureAwait(false);
            context.Published++;
        }
        await RecordAsync(context, NightlyProductScope.Final, window, NightlyProductWindowDisposition.Produced,
            summary.Partial ? NightlyProductContract.PartialSummaryReasonCode : null, candidateCount, admission.Admitted.Count,
            admission.Exclusions, fingerprint, [existing.Summary.ProductId], cancellationToken).ConfigureAwait(false);
        context.FinalsRecorded++;
    }
}
