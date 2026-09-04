namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>One routeable section of the Operations workspace.</summary>
public sealed record OperationsSection(
    string Slug,
    string Label,
    string Group,
    string Href,
    string Summary,
    IReadOnlyList<string> AliasPaths)
{
    /// <summary>True when the path is this section's route, an alias, or a child of either.</summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.Equals(Href, OperationsSectionCatalog.OverviewPath, StringComparison.Ordinal))
        {
            return string.Equals(TrimSlash(path), Href, StringComparison.OrdinalIgnoreCase);
        }
        return IsPathOrChild(path, Href) || AliasPaths.Any(alias => IsPathOrChild(path, alias));
    }

    private static bool IsPathOrChild(string path, string candidate)
    {
        var trimmed = TrimSlash(path);
        return string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith($"{candidate}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSlash(string path)
        => path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;
}

/// <summary>
/// The fixed section map of the Operations workspace. Every section routes to a page that
/// reads durable local state through the existing protected services; the catalog carries
/// no runtime state of its own.
/// </summary>
public static class OperationsSectionCatalog
{
    public const string OverviewPath = "/operations";

    public static IReadOnlyList<string> Groups { get; } = ["Overview", "Setup", "Capture", "Processing", "Data", "System"];

    public static IReadOnlyList<OperationsSection> Sections { get; } =
    [
        new("overview", "Overview", "Overview", OverviewPath,
            "Acquisition control, current health, and the state of every local queue.", []),
        new("camera", "Camera & rig", "Setup", "/operations/camera",
            "Sensor, optics, orientation, exposure envelope, and control policy of the active profile.", []),
        new("sky-map", "Sky map & catalog", "Setup", "/operations/sky-map",
            "Installed catalog identity, observer location, and the visible scene for this rig.", []),
        new("registration", "Device registration", "Setup", "/devices/bootstrap",
            "Optional central registration and bootstrap state.", ["/devices"]),
        new("schedule", "Capture schedule", "Capture", "/operations/schedule",
            "Immutable profile revisions, effective UTC windows, overrides, and admission state.", ["/schedule"]),
        new("calibration", "Calibration", "Capture", "/operations/calibration",
            "Deterministic software references with exact lineage and safe-boundary activation.", ["/calibration"]),
        new("environment", "Environment", "Capture", "/operations/environment",
            "Environmental source health, acquisition attempts, and local observation history.", ["/environmental"]),
        new("pipeline", "Pipeline summary", "Processing", "/operations/pipeline",
            "Desired and effective processing graph of the active and pending revisions.", []),
        new("automations", "Automations", "Processing", "/operations/automations",
            "Registered local tasks and triggers with their next scheduled activity.", []),
        new("data", "Data & storage", "Data", "/operations/data",
            "Raw ingress, capture lanes, processing, delivery outbox, and storage pressure.", []),
        new("quarantine", "Quarantine & recovery", "Data", "/operations/quarantine",
            "Bounded quarantine history with audited replay and abandonment decisions.", []),
        new("system", "System", "System", "/operations/system",
            "Allowlisted startup configuration, continuity, and process facts.", ["/system"])
    ];

    /// <summary>Resolves the section that owns an absolute path, or null outside the workspace.</summary>
    public static OperationsSection? Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Sections.FirstOrDefault(section => section.Matches(path));
    }

    public static OperationsSection Get(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return Sections.First(section => string.Equals(section.Slug, slug, StringComparison.Ordinal));
    }
}
