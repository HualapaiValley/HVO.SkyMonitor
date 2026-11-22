using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// The document serialization, stream identity and content-derived revision identity this store already uses. The Silo
/// catalog (#1967) publishes a view revision through these same functions, so the typed view has exactly one published
/// representation and its revision ID can be checked rather than trusted.
/// </summary>
internal sealed partial class SqliteCenteredViews
{
    internal static JsonElement SerializeDocument<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    internal static string StreamIdentity(CenteredViewStream stream) => StreamId(stream);

    internal static string RevisionIdentity(string baseId, string? previousRevisionId, IReadOnlyList<CenteredViewLayer> layers) =>
        RevisionId(baseId, previousRevisionId, layers);
}
