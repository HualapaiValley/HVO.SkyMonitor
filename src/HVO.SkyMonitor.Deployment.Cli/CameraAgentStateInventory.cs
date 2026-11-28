namespace HVO.SkyMonitor.Deployment;

internal static class CameraAgentStateInventory
{
    public static IReadOnlyList<string> Stores { get; } =
        [.. CameraAgentStateLayout.SqliteStores, CameraAgentStateSnapshot.RetainedIntegrityStore];

    /// <summary>Whether <paramref name="relative"/> is a store location a snapshot manifest may name.</summary>
    public static bool IsValidStoreLocation(string relative)
        => relative.Length is > 0 and < 512 && relative.EndsWith(".db", StringComparison.Ordinal) &&
           !relative.StartsWith('/') && !relative.Contains('\\', StringComparison.Ordinal) && !relative.Any(char.IsControl) &&
           relative.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");
}
