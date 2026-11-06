using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ArtifactIngestService
{
    // Only v4 references need the two-hop retained facts -> projected-scene lineage.
    // Keep legacy products and their persisted/serialized identities unchanged.
    internal static async Task LoadRichPresentationLineageAsync(
        ApplicationDbContext db, CentralArtifact artifact, CancellationToken cancellationToken)
    {
        if (!string.Equals(artifact.MediaType, PresentationProcessingProducts.ManifestMediaType, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var layer in artifact.Sources.Select(static source => source.ResolvedArtifact)
                     .Where(static resolved => resolved?.StructuredProduct?.ProductSchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion))
        {
            await db.Entry(layer!).Collection(static item => item.Sources).Query()
                .Include(static source => source.ResolvedArtifact)!
                    .ThenInclude(static facts => facts!.StructuredProduct)
                .Include(static source => source.ResolvedArtifact)!
                    .ThenInclude(static facts => facts!.Sources)
                    .ThenInclude(static source => source.ResolvedArtifact)!
                    .ThenInclude(static scene => scene!.StructuredProduct)
                .AsSplitQuery()
                .LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool MatchesRichPresentationCoordinates(CentralArtifact layer, string? coordinateIdentity)
    {
        if (layer.Sources.Count != 1 || layer.Sources.Single().ResolvedArtifact is not { } facts ||
            !IsUsableLineageSource(facts) || facts.CentralFrameId != layer.CentralFrameId ||
            facts.DevicePublicId != layer.DevicePublicId ||
            !string.Equals(facts.MediaType, PresentationMetadataFactsProductV1.MediaType, StringComparison.OrdinalIgnoreCase) ||
            facts.StructuredProduct is not { ProductSchemaVersion: PresentationMetadataFactsProductV1.RichTextSchemaVersion } product ||
            !string.Equals(product.ContentIdentitySha256, layer.StructuredProduct!.SourceIdentitySha256, StringComparison.Ordinal))
            return false;

        // F names the facts product; S names its exact projected-scene source. Both
        // must match independently, even though every layer shares the same S.
        return facts.Sources.Count(source => source.Ordinal == 0 && source.ResolvedArtifact is { } scene &&
            IsUsableLineageSource(scene) && scene.CentralFrameId == layer.CentralFrameId &&
            scene.DevicePublicId == layer.DevicePublicId &&
            string.Equals(scene.MediaType, StructuredProcessingProductContracts.ProjectedSceneMediaType, StringComparison.OrdinalIgnoreCase) &&
            scene.StructuredProduct is { } projected && ProjectedSceneV1.IsSupportedSchemaVersion(projected.ProductSchemaVersion) &&
            string.Equals(projected.ContentIdentitySha256, coordinateIdentity, StringComparison.Ordinal)) == 1;
    }
}
