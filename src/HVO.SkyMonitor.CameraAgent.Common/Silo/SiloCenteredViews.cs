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
    /// Requires a view manifest to be the one canonical projection of its embedded typed documents, at staging and at every
    /// readback. Its group and revision IDs must be the store-issued base and revision IDs, its projection the section the
    /// documents derive under its predecessor, and the reopen rows it gives the reader (every member and every reopen
    /// dependency) exactly their reopen proof: each artifact revision of the proof once, as a member or as a reopen
    /// dependency, with its digest and requirement, and nothing outside it that reopen would require; a member outside it
    /// may only be optional data. A Ready layer, the preview or a stack's closed window therefore cannot be dropped,
    /// aliased or contradicted while the embedded revision ID stays the same. Whether science is displayed is the
    /// producer's fact, so its row may also be required.
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
        if (!string.Equals(manifest.GroupId, view.Base.BaseId, StringComparison.Ordinal) ||
            !string.Equals(manifest.RevisionId, view.Revision.RevisionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A view revision is published only under its store-issued base and revision IDs.", nameof(manifest));
        }
        if (!string.Equals(derived.StreamId, section.StreamId, StringComparison.Ordinal) ||
            !string.Equals(derived.ReferenceCaptureId, section.ReferenceCaptureId, StringComparison.Ordinal) ||
            derived.RevisionOrder != section.RevisionOrder || derived.Preview != section.Preview || !derived.Layers.SequenceEqual(section.Layers))
        {
            throw new ArgumentException("The view projection is not the section its embedded documents derive.", nameof(section));
        }
        // The rows reopen reads: every member and every reopen dependency, each artifact revision once.
        var rows = new Dictionary<(string ArtifactId, string Revision), (string Sha256, bool Required, bool Member)>();
        foreach (var (artifactId, revision, sha, required, member) in manifest.Members
                     .Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Required, true))
                     .Concat(manifest.Dependencies.Where(static dependency => dependency.Kind == SiloDependencyKind.Reopen)
                         .Select(static dependency => (dependency.ArtifactId, dependency.ArtifactRevision, dependency.Sha256, dependency.Required, false))))
        {
            if (!rows.TryAdd((artifactId, revision), (sha, required, member)))
            {
                throw new ArgumentException($"Artifact revision {artifactId}/{revision} has more than one reopen row.", nameof(manifest));
            }
        }
        var definition = view.Base.Definition;
        var science = Evidence(definition.Science);
        var proof = ReopenProof(derived.Preview, derived.Layers, science, scienceDisplayed: false,
            definition.ClosedWindow is { } window ? Evidence(window) : null);
        foreach (var ((artifactId, revision), (sha, required)) in proof)
        {
            if (!rows.TryGetValue((artifactId, revision), out var row))
            {
                throw new ArgumentException($"View binding {artifactId}/{revision} is not in the manifest's reopen set.", nameof(manifest));
            }
            var displayedScience = row.Required && artifactId == science.ArtifactId && revision == science.IdentitySha256;
            if (!string.Equals(row.Sha256, sha, StringComparison.Ordinal) || row.Required != required && !displayedScience)
            {
                throw new ArgumentException(
                    $"View binding {artifactId}/{revision} must be a reopen entry with its digest and required={required}.", nameof(manifest));
            }
        }
        // Outside the proof, a member may carry optional data, but nothing the typed view does not need may block reopen.
        foreach (var (key, row) in rows)
        {
            if (!proof.ContainsKey(key) && (!row.Member || row.Required))
            {
                throw new ArgumentException(row.Member
                    ? $"Required member {key.ArtifactId}/{key.Revision} is not in the view's reopen set."
                    : $"Reopen dependency {key.ArtifactId}/{key.Revision} is not in the view's reopen set.", nameof(manifest));
            }
        }
    }

    private static List<SiloManifestDependency> Dependencies(
        SiloViewEvidence preview, CenteredViewArtifact science, bool scienceDisplayed, CenteredViewArtifact? closedWindow,
        IReadOnlyList<SiloViewLayerBinding> layers, IReadOnlySet<(string ArtifactId, string Revision)>? members) =>
        ReopenProof(preview, layers, Evidence(science), scienceDisplayed, closedWindow is null ? null : Evidence(closedWindow))
            .Where(pair => members is null || !members.Contains(pair.Key))
            .Select(static pair => new SiloManifestDependency(pair.Key.ArtifactId, pair.Key.Revision, pair.Value.Sha256, SiloDependencyKind.Reopen,
                pair.Value.Required))
            .ToList();

    /// <summary>
    /// The exact reopen proof (#1965 6.1), one row per artifact revision: the preview and the closed window, required;
    /// science, required where displayed; and every evidence binding, required when any layer that uses it is Ready or
    /// Partial. Bindings on other layers are kept but do not block reopen. The producer's dependencies and the validator
    /// both use this one derivation.
    /// </summary>
    /// <exception cref="ArgumentException">The view binds one artifact revision to two payload digests.</exception>
    private static Dictionary<(string ArtifactId, string Revision), (string Sha256, bool Required)> ReopenProof(
        SiloViewEvidence preview, IReadOnlyList<SiloViewLayerBinding> layers, SiloViewEvidence science, bool scienceDisplayed,
        SiloViewEvidence? closedWindow)
    {
        var result = new Dictionary<(string ArtifactId, string Revision), (string Sha256, bool Required)>();
        void Add(SiloViewEvidence evidence, bool needed)
        {
            var key = (evidence.ArtifactId, evidence.IdentitySha256);
            var sha = LowerHex(evidence.PayloadSha256);
            if (!result.TryGetValue(key, out var existing))
            {
                result[key] = (sha, needed);
                return;
            }
            if (!string.Equals(existing.Sha256, sha, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The view binds artifact revision {key.ArtifactId}/{key.IdentitySha256} to two payload digests.",
                    nameof(layers));
            }
            result[key] = (sha, existing.Required || needed);
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
