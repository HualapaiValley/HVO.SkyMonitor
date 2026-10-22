using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>
    /// The combined output required to interpret one of its three support companions. Reads only bounded identity
    /// and path facts from the same capture/node/recipe group; it never opens a payload or unrelated metadata.
    /// </summary>
    internal async ValueTask<ProcessingRetentionHold?> ReadMixedGainCompanionAnchorAsync(
        string companionOutputIdentitySha256,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT combined.artifact_id, combined.payload_relative_path, combined.sidecar_relative_path,
                   combined.variant, companion.variant
            FROM processing_outputs companion
            JOIN processing_outputs combined
              ON combined.capture_id = companion.capture_id AND combined.node_id = companion.node_id
             AND combined.recipe_identity_sha256 = companion.recipe_identity_sha256
            WHERE companion.output_identity_sha256 = $identity AND companion.role = 'Metadata'
              AND combined.role = 'Combined' AND combined.availability_state = 'Available'
              AND json_extract(combined.descriptor_json, '$.descriptor.artifact.recipe.name') = $recipe
              AND json_extract(combined.descriptor_json, '$.descriptor.artifact.recipe.semanticVersion') = $version
            ORDER BY combined.output_identity_sha256
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$identity", companionOutputIdentitySha256);
        command.Parameters.AddWithValue("$recipe", BuiltInProcessingRecipes.MixedGainNormalizedMean);
        command.Parameters.AddWithValue("$version", GainNormalizationCompatibility.RecipeSemanticVersion);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var combinedVariant = reader.GetString(3);
        var companionVariant = reader.GetString(4);
        var hold = new ProcessingRetentionHold(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetString(2));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("A mixed-gain companion has ambiguous combined output identity.");
        return companionVariant == MixedGainSupportV1.VariantFor(combinedVariant, MixedGainDiagnosticsV1.Variant) ||
               companionVariant == MixedGainSupportV1.VariantFor(combinedVariant, MixedGainSupportV1.ValidCountsEncoding) ||
               companionVariant == MixedGainSupportV1.VariantFor(combinedVariant, MixedGainSupportV1.NoCoverageEncoding)
            ? hold : null;
    }
}
