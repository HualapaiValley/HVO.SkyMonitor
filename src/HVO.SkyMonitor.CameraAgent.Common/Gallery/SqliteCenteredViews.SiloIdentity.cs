using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// The document serialization, stream identity and content-derived revision identity this store already uses. The Silo
/// catalog (#1967) publishes a view revision through these same functions, so the typed view has exactly one published
/// representation and its revision ID can be checked rather than trusted.
/// </summary>
internal sealed partial class SqliteCenteredViews
{
    // A published document is read with the options that wrote it. A null where the contract has none is refused, as
    // every Silo format refuses it, rather than reaching the validators below.
    private static readonly JsonSerializerOptions DocumentReadOptions = new(JsonOptions) { RespectNullableAnnotations = true };

    internal static JsonElement SerializeDocument<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <exception cref="JsonException">The element is not the typed document.</exception>
    internal static T DeserializeDocument<T>(JsonElement document) where T : class =>
        document.Deserialize<T>(DocumentReadOptions) ?? throw new JsonException("The centered view document is null.");

    internal static string StreamIdentity(CenteredViewStream stream) => StreamId(stream);

    internal static string RevisionIdentity(string baseId, string? previousRevisionId, IReadOnlyList<CenteredViewLayer> layers) =>
        RevisionId(baseId, previousRevisionId, layers);

    /// <summary>The base ID <see cref="RegisterBaseAsync"/> derives from a definition.</summary>
    internal static string BaseIdentity(CenteredViewBaseDefinition definition) =>
        Identity(new { Schema = CenteredViewBase.SchemaVersion, Definition = definition });

    /// <summary>
    /// Requires a typed view to be one this store writes: its definition and layers pass the store's own validation, its
    /// base ID derives from its definition, its revision ID from that base, the predecessor and its layers, and its
    /// presentation manifest, which the revision ID does not cover, from its preview and drawable layers.
    /// </summary>
    /// <exception cref="ArgumentException">Any part of the view does not derive from the rest.</exception>
    internal static void RequireWritten(CenteredView view, string? previousRevisionId)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(view.Base);
        ArgumentNullException.ThrowIfNull(view.Revision);
        var definition = view.Base.Definition;
        ArgumentNullException.ThrowIfNull(definition);
        StreamId(definition.Stream);
        // Element nullability is not part of a read contract; the validators below index these lists directly.
        if (definition.Contributors.Any(static contributor => contributor is null) ||
            definition.Window?.Slots.Any(static slot => slot is null) == true ||
            view.Revision.Layers.Any(static layer => layer is null))
        {
            throw new ArgumentException("A centered view list holds an empty entry.", nameof(view));
        }
        ValidateBase(definition);
        _ = NormalizeLayers(definition, view.Revision.Layers);
        if (!string.Equals(view.Base.BaseId, BaseIdentity(definition), StringComparison.Ordinal) ||
            !string.Equals(view.Revision.BaseId, view.Base.BaseId, StringComparison.Ordinal) ||
            !string.Equals(view.Revision.RevisionId, RevisionId(view.Base.BaseId, previousRevisionId, view.Revision.Layers), StringComparison.Ordinal))
        {
            throw new ArgumentException("The base ID or revision ID does not derive from the definition, predecessor and layer bindings.", nameof(view));
        }
        if (!string.Equals(Serialize(view.Revision.Manifest), Serialize(CreateManifest(definition, view.Revision.Layers)), StringComparison.Ordinal))
        {
            throw new ArgumentException("The presentation manifest does not derive from the preview and drawable layers.", nameof(view));
        }
    }
}
