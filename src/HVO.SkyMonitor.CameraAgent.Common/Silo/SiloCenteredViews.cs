using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// One normalized representation of a centered view revision (#1965 4.4, 6.1, 6.3). The typed view is serialized
/// through the store's own functions, its revision ID is recomputed rather than trusted, and the exact reopen set is
/// derived from its preview and layers: every supplied binding is kept, and an evidence payload is required wherever
/// any layer using it is Ready or Partial.
/// </summary>
internal static class SiloCenteredViews
{
    /// <summary>The view section for a typed revision, with its documents, bindings, preview and reference capture.</summary>
    public static SiloManifestView Section(CenteredView view, string? previousRevisionId)
    {
        ArgumentNullException.ThrowIfNull(view);
        var expected = SqliteCenteredViews.RevisionIdentity(view.Base.BaseId, previousRevisionId, view.Revision.Layers);
        if (!string.Equals(expected, view.Revision.RevisionId, StringComparison.Ordinal) ||
            !string.Equals(view.Revision.BaseId, view.Base.BaseId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The revision ID does not derive from its base, predecessor and layer bindings.", nameof(view));
        }
        var definition = view.Base.Definition;
        return new(
            SqliteCenteredViews.StreamIdentity(definition.Stream),
            definition.Reference.Capture.CaptureId.ToString("D", CultureInfo.InvariantCulture),
            view.Revision.Order,
            Preview(definition),
            CenteredViewBase.SchemaVersion,
            SqliteCenteredViews.SerializeDocument(view.Base),
            CenteredViewRevision.SchemaVersion,
            SqliteCenteredViews.SerializeDocument(view.Revision),
            Bindings(view.Revision.Layers));
    }

    /// <summary>
    /// The reopen dependencies of a typed revision: its derived reopen set, plus the base's science and closed window. A
    /// member published in the same group is listed there instead and is excluded here.
    /// </summary>
    public static IReadOnlyList<SiloManifestDependency> ReopenDependencies(
        CenteredView view, bool scienceDisplayed, IReadOnlySet<(string ArtifactId, string Revision)>? members = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var definition = view.Base.Definition;
        return Dependencies(Preview(definition), definition.Science, scienceDisplayed, definition.ClosedWindow, Bindings(view.Revision.Layers),
            members);
    }

    /// <summary>The same derivation from the contract's parts.</summary>
    public static IReadOnlyList<SiloManifestDependency> ReopenDependencies(
        CenteredViewArtifact preview, CenteredViewArtifact science, bool scienceDisplayed, CenteredViewArtifact? closedWindow,
        IReadOnlyList<CenteredViewLayer> layers) =>
        Dependencies(Evidence(preview), science, scienceDisplayed, closedWindow, Bindings(layers), null);

    private static List<SiloManifestDependency> Dependencies(
        SiloViewEvidence preview, CenteredViewArtifact science, bool scienceDisplayed, CenteredViewArtifact? closedWindow,
        IReadOnlyList<SiloViewLayerBinding> layers, IReadOnlySet<(string ArtifactId, string Revision)>? members)
    {
        var required = new Dictionary<(string ArtifactId, string Revision), (string Sha, bool Required)>();
        void Add(SiloViewEvidence evidence, bool needed)
        {
            var key = (evidence.ArtifactId, evidence.IdentitySha256);
            var sha = LowerHex(evidence.PayloadSha256);
            required[key] = required.TryGetValue(key, out var existing) ? (existing.Sha, existing.Required || needed) : (sha, needed);
        }
        foreach (var ((artifactId, revision, sha), needed) in RequiredReopen(preview, layers))
        {
            Add(new(artifactId, revision, sha), needed);
        }
        Add(Evidence(science), scienceDisplayed);
        if (closedWindow is not null)
        {
            Add(Evidence(closedWindow), true);
        }
        return required.Where(pair => members is null || !members.Contains(pair.Key))
            .Select(static pair => new SiloManifestDependency(pair.Key.ArtifactId, pair.Key.Revision, pair.Value.Sha, SiloDependencyKind.Reopen,
                pair.Value.Required))
            .ToList();
    }

    /// <summary>
    /// The exact reopen set a view section implies: its preview, required, and every evidence binding, required when any
    /// layer that uses it is Ready or Partial. Bindings on other layers are kept but do not block reopen.
    /// </summary>
    public static IReadOnlyDictionary<(string ArtifactId, string Revision, string Sha256), bool> RequiredReopen(SiloManifestView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return RequiredReopen(view.Preview, view.Layers);
    }

    private static Dictionary<(string ArtifactId, string Revision, string Sha256), bool> RequiredReopen(
        SiloViewEvidence preview, IReadOnlyList<SiloViewLayerBinding> layers)
    {
        var result = new Dictionary<(string, string, string), bool>
        {
            [(preview.ArtifactId, preview.IdentitySha256, LowerHex(preview.PayloadSha256))] = true
        };
        foreach (var layer in layers)
        {
            if (layer.Evidence is not { } evidence)
            {
                continue;
            }
            var key = (evidence.ArtifactId, evidence.IdentitySha256, LowerHex(evidence.PayloadSha256));
            var needed = EvidenceBlocksReopen(layer.Readiness);
            result[key] = result.TryGetValue(key, out var existing) ? existing || needed : needed;
        }
        return result;
    }

    public static IReadOnlyList<SiloViewLayerBinding> Bindings(IReadOnlyList<CenteredViewLayer> layers) =>
        layers.Select(static layer => new SiloViewLayerBinding(
            layer.Key.Kind,
            layer.Key.Scope.ToString(),
            layer.EvidenceOrder,
            layer.Readiness.ToString(),
            layer.Key.SourceCaptureId?.ToString("D", CultureInfo.InvariantCulture),
            layer.Evidence is { } evidence ? Evidence(evidence) : null,
            layer.ObservedUtc?.ToUniversalTime(),
            layer.Reason)).ToArray();

    /// <summary>Whether a layer in this state needs its evidence bytes to reopen exactly (#1965 6.1 table).</summary>
    public static bool EvidenceBlocksReopen(string readiness) =>
        readiness is nameof(CenteredViewReadiness.Ready) or nameof(CenteredViewReadiness.Partial);

    private static SiloViewEvidence Preview(CenteredViewBaseDefinition definition) =>
        new(definition.Preview.Product.ArtifactId.ToString("D", CultureInfo.InvariantCulture), definition.Preview.Product.ProductIdentitySha256,
            definition.Preview.PayloadSha256);

    private static SiloViewEvidence Evidence(CenteredViewArtifact artifact) =>
        new(artifact.ArtifactId.ToString("D", CultureInfo.InvariantCulture), artifact.IdentitySha256, artifact.PayloadSha256);

    /// <summary>The canonical lowercase form of a hex digest; anything that is not hex is returned unchanged to fail validation.</summary>
    internal static string LowerHex(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(static character => char.IsAsciiHexDigit(character)))
        {
            return sha256;
        }
        return Convert.ToHexStringLower(Convert.FromHexString(sha256));
    }
}
