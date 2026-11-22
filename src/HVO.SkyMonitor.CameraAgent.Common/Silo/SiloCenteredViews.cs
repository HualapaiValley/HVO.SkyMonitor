using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// One normalized representation of a centered view revision (#1965 4.4, 6.1, 6.3). The typed view is serialized
/// through the store's own functions, its base and revision IDs are recomputed rather than trusted, and the exact reopen
/// set is derived from its preview, science, closed window and layers: every supplied binding is kept, and an evidence
/// payload is required wherever any layer using it is Ready or Partial. The embedded typed documents are the durable
/// view, so staging and every manifest readback derive the section and reopen set from them again.
/// </summary>
internal static class SiloCenteredViews
{
    /// <summary>The view section for a typed revision, with its documents, bindings, preview and reference capture.</summary>
    /// <exception cref="ArgumentException">The view is not one the store writes under this predecessor.</exception>
    public static SiloManifestView Section(CenteredView view, string? previousRevisionId)
    {
        SqliteCenteredViews.RequireWritten(view, previousRevisionId);
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

    /// <summary>
    /// Requires a view manifest to agree with its embedded typed documents, at staging and at every readback. The
    /// projection must be exactly the section the documents derive under the manifest's predecessor, and each entry of
    /// their exact reopen set must be a member or reopen dependency with its digest and requirement. A Ready layer, the
    /// preview or a stack's closed window therefore cannot be dropped while the embedded revision ID stays the same.
    /// Whether science is displayed is the producer's fact, so its entry may also be required.
    /// </summary>
    /// <exception cref="ArgumentException">The manifest disagrees with its documents, or they are not a view the store writes.</exception>
    public static void RequireConformance(SiloGroupManifest manifest, SiloManifestView section)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(section);
        CenteredView view;
        try
        {
            view = new(SqliteCenteredViews.DeserializeDocument<CenteredViewBase>(section.Base),
                SqliteCenteredViews.DeserializeDocument<CenteredViewRevision>(section.Revision));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The embedded view documents are not typed centered view documents.", nameof(section), exception);
        }
        var derived = Section(view, manifest.PreviousRevisionId);
        if (!string.Equals(derived.StreamId, section.StreamId, StringComparison.Ordinal) ||
            !string.Equals(derived.ReferenceCaptureId, section.ReferenceCaptureId, StringComparison.Ordinal) ||
            derived.RevisionOrder != section.RevisionOrder || derived.Preview != section.Preview || !derived.Layers.SequenceEqual(section.Layers))
        {
            throw new ArgumentException("The view projection is not the section its embedded documents derive.", nameof(section));
        }
        var definition = view.Base.Definition;
        var science = Evidence(definition.Science);
        var closedWindow = definition.ClosedWindow is { } window ? Evidence(window) : null;
        foreach (var ((artifactId, revision, sha), required) in ReopenSet(derived.Preview, derived.Layers, science, scienceDisplayed: false, closedWindow))
        {
            var member = manifest.Members.FirstOrDefault(member =>
                member.ArtifactId == artifactId && member.ArtifactRevision == revision);
            var dependency = manifest.Dependencies.FirstOrDefault(dependency => dependency.Kind == SiloDependencyKind.Reopen &&
                dependency.ArtifactId == artifactId && dependency.ArtifactRevision == revision);
            var (declaredSha, declaredRequired) = member is not null ? (member.Sha256, member.Required)
                : dependency is not null ? (dependency.Sha256, dependency.Required)
                : throw new ArgumentException($"View binding {artifactId}/{revision} is not in the manifest's reopen set.", nameof(manifest));
            var displayedScience = declaredRequired && artifactId == science.ArtifactId && revision == science.IdentitySha256;
            if (!string.Equals(declaredSha, sha, StringComparison.Ordinal) || declaredRequired != required && !displayedScience)
            {
                throw new ArgumentException(
                    $"View binding {artifactId}/{revision} must be a reopen entry with its digest and required={required}.", nameof(manifest));
            }
        }
    }

    private static List<SiloManifestDependency> Dependencies(
        SiloViewEvidence preview, CenteredViewArtifact science, bool scienceDisplayed, CenteredViewArtifact? closedWindow,
        IReadOnlyList<SiloViewLayerBinding> layers, IReadOnlySet<(string ArtifactId, string Revision)>? members) =>
        ReopenSet(preview, layers, Evidence(science), scienceDisplayed, closedWindow is null ? null : Evidence(closedWindow))
            .Where(pair => members is null || !members.Contains((pair.Key.ArtifactId, pair.Key.Revision)))
            .Select(static pair => new SiloManifestDependency(pair.Key.ArtifactId, pair.Key.Revision, pair.Key.Sha256, SiloDependencyKind.Reopen,
                pair.Value))
            .ToList();

    /// <summary>
    /// The exact reopen set (#1965 6.1): the preview and the closed window, required; science, required where displayed;
    /// and every evidence binding, required when any layer that uses it is Ready or Partial. Bindings on other layers are
    /// kept but do not block reopen.
    /// </summary>
    private static Dictionary<(string ArtifactId, string Revision, string Sha256), bool> ReopenSet(
        SiloViewEvidence preview, IReadOnlyList<SiloViewLayerBinding> layers, SiloViewEvidence science, bool scienceDisplayed,
        SiloViewEvidence? closedWindow)
    {
        var result = new Dictionary<(string, string, string), bool>();
        void Add(SiloViewEvidence evidence, bool needed)
        {
            var key = (evidence.ArtifactId, evidence.IdentitySha256, LowerHex(evidence.PayloadSha256));
            result[key] = result.TryGetValue(key, out var existing) ? existing || needed : needed;
        }
        Add(preview, true);
        foreach (var layer in layers)
        {
            if (layer.Evidence is { } evidence)
            {
                Add(evidence, EvidenceBlocksReopen(layer.Readiness));
            }
        }
        Add(science, scienceDisplayed);
        if (closedWindow is not null)
        {
            Add(closedWindow, true);
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
