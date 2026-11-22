using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// Maps a centered view revision onto a group manifest (#1965 4.4, 6.1). The revision ID hashes the complete layer list,
/// so every supplied binding is kept exactly, in every layer state. Which evidence bytes block exact reopen depends only
/// on the layer state: Ready and Partial need their evidence payload; AssessedEmpty, Incompatible, Waiting and
/// NotConfigured reopen from the binding alone.
/// </summary>
internal static class SiloCenteredViews
{
    public static SiloManifestView Section(CenteredView view, string streamId, JsonElement baseDocument, JsonElement revisionDocument)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Section(view.Base.BaseId, view.Revision.RevisionId, view.Revision.Order, view.Revision.Layers, streamId, baseDocument,
            revisionDocument);
    }

    public static SiloManifestView Section(
        string baseId, string revisionId, long order, IReadOnlyList<CenteredViewLayer> layers, string streamId, JsonElement baseDocument,
        JsonElement revisionDocument)
    {
        RequireDocument(baseDocument, "baseId", baseId, nameof(baseDocument));
        RequireDocument(revisionDocument, "baseId", baseId, nameof(revisionDocument));
        RequireDocument(revisionDocument, "revisionId", revisionId, nameof(revisionDocument));
        return new(streamId, order, CenteredViewBase.SchemaVersion, baseDocument.Clone(), CenteredViewRevision.SchemaVersion,
            revisionDocument.Clone(), Bindings(layers));
    }

    public static IReadOnlyList<SiloViewLayerBinding> Bindings(IReadOnlyList<CenteredViewLayer> layers) =>
        layers.Select(static layer => new SiloViewLayerBinding(
            layer.Key.Kind,
            layer.Key.Scope.ToString(),
            layer.EvidenceOrder,
            layer.Readiness.ToString(),
            layer.Key.SourceCaptureId?.ToString("D", CultureInfo.InvariantCulture),
            layer.Evidence is { } evidence
                ? new SiloViewEvidence(evidence.ArtifactId.ToString("D", CultureInfo.InvariantCulture), evidence.IdentitySha256, evidence.PayloadSha256)
                : null,
            layer.ObservedUtc?.ToUniversalTime(),
            layer.Reason)).ToArray();

    /// <summary>Whether a layer in this state needs its evidence bytes to reopen exactly (#1965 6.1 table).</summary>
    public static bool EvidenceBlocksReopen(string readiness) =>
        readiness is nameof(CenteredViewReadiness.Ready) or nameof(CenteredViewReadiness.Partial);

    /// <summary>
    /// The reopen dependencies of a view revision: the base's exact artifacts are required; each layer's evidence binding
    /// is a reopen dependency, required only where its state needs the bytes. The binding itself is kept regardless.
    /// </summary>
    public static IReadOnlyList<SiloManifestDependency> ReopenDependencies(CenteredView view, bool scienceDisplayed)
    {
        ArgumentNullException.ThrowIfNull(view);
        var definition = view.Base.Definition;
        var preview = new CenteredViewArtifact(definition.Preview.Product.ArtifactId, definition.Preview.Product.ProductIdentitySha256,
            definition.Preview.PayloadSha256);
        return ReopenDependencies(preview, definition.Science, scienceDisplayed, definition.ClosedWindow, view.Revision.Layers);
    }

    public static IReadOnlyList<SiloManifestDependency> ReopenDependencies(
        CenteredViewArtifact preview, CenteredViewArtifact science, bool scienceDisplayed, CenteredViewArtifact? closedWindow,
        IReadOnlyList<CenteredViewLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var result = new List<SiloManifestDependency>();
        var seen = new HashSet<(string, string)>();
        void Add(CenteredViewArtifact artifact, bool required)
        {
            var id = artifact.ArtifactId.ToString("D", CultureInfo.InvariantCulture);
            if (seen.Add((id, artifact.IdentitySha256)))
            {
                result.Add(new(id, artifact.IdentitySha256, LowerHex(artifact.PayloadSha256), SiloDependencyKind.Reopen, required));
            }
        }
        Add(preview, true);
        Add(science, scienceDisplayed);
        if (closedWindow is not null)
        {
            Add(closedWindow, true);
        }
        foreach (var layer in layers)
        {
            if (layer.Evidence is { } evidence)
            {
                Add(evidence, EvidenceBlocksReopen(layer.Readiness.ToString()));
            }
        }
        return result;
    }

    private static string LowerHex(string sha256) => Convert.ToHexStringLower(Convert.FromHexString(sha256));

    private static void RequireDocument(JsonElement document, string property, string expected, string name)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The verbatim view document must name {property} '{expected}'.", name);
        }
    }
}
