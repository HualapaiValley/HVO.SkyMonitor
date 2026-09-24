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
    public string? UnavailableReason { get; init; }
    public int? UnavailableIssue { get; init; }

    public string IconPath => Slug switch
    {
        "overview" => "M3 3h5v5H3zM12 3h5v5h-5zM3 12h5v5H3zM12 12h5v5h-5z",
        "site" => "M10 17V8m0 0 4 4m-4-4-4 4M4 17h12M12 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0",
        "camera" => "M5 6h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2m1 0 1-2h6l1 2M13 11a3 3 0 1 0-6 0 3 3 0 0 0 6 0",
        "registration" => "M3 4h14v12H3zM7 8h6m-6 4h3",
        "schedule" => "M3 4h14v13H3zM6 2v4m8-4v4M3 8h14",
        "focus" => "M13 10a3 3 0 1 0-6 0 3 3 0 0 0 6 0M7 3H3v4m10-4h4v4M7 17H3v-4m10 4h4v-4",
        "calibration" => "M6 3h8v4l3 6a3 3 0 0 1-3 4H6a3 3 0 0 1-3-4l3-6V3ZM6 10h8",
        "pipeline" => "M6 10a2 2 0 1 0-4 0 2 2 0 0 0 4 0M12 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0M18 10a2 2 0 1 0-4 0 2 2 0 0 0 4 0M12 15a2 2 0 1 0-4 0 2 2 0 0 0 4 0M6 9l2.5-2.5m3 0L14 9m-8 2 2.5 2.5m3-1L14 11",
        "environment" => "M7 12a4 4 0 1 1 6-3 3 3 0 1 1 1 6H7a3 3 0 1 1 0-6",
        "transients" => "m12 2-1 6 5-2-7 12 1-7-5 2 7-11Z",
        "automations" => "M4 5h8m-8 5h12M4 15h1m4 0h1M17 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0M9 15a2 2 0 1 0-4 0 2 2 0 0 0 4 0",
        "delivery" => "M3 10h11m-4-4 4 4-4 4M17 4v12",
        "storage" or "data" => "M16 5a6 3 0 1 0-12 0 6 3 0 0 0 12 0M4 5v5c0 4 12 4 12 0V5M4 10v5c0 4 12 4 12 0v-5",
        "health" => "M3 10h3l2-5 4 10 2-5h3",
        "control" => "M10 2v7M6 4a7 7 0 1 0 8 0",
        "software" or "sky-map" => "m10 2 7 4-7 4-7-4 7-4ZM3 10l7 4 7-4M3 14l7 4 7-4",
        _ => "M3 5h14M3 10h14M3 15h14M6 3v4m8 1v4m-6 1v4"
    };

    /// <summary>True when the path is this section's route, an alias, or a child of either.</summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (UnavailableReason is not null) return false;
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
/// The fixed section map of the Operations workspace. Supported routes keep their existing
/// protected services; unavailable prototype destinations name their owning issue instead.
/// </summary>
public static class OperationsSectionCatalog
{
    public const string OverviewPath = "/operations";

    public static IReadOnlyList<string> Groups { get; } = ["Overview", "Setup", "Capture", "Processing", "Automation", "Data", "System"];

    public static IReadOnlyList<OperationsSection> Sections { get; } =
    [
        new("overview", "Overview", "Overview", OverviewPath,
            "Acquisition control, current health, and the state of every local queue.", []),
        new("site", "Observatory & location", "Setup", "", "", [])
        {
            UnavailableIssue = 1010,
            UnavailableReason = "Unavailable: the Observatory & location workspace is tracked in issue #1010. Existing observer facts remain in Sky map & catalog."
        },
        new("camera", "Camera & rig", "Setup", "/operations/camera",
            "Sensor, optics, orientation, exposure envelope, and control policy of the active profile.", []),
        new("sky-map", "Sky map & catalog", "Setup", "/operations/sky-map",
            "Installed catalog identity, observer location, and the visible scene for this rig.", []),
        new("registration", "Device registration", "Setup", "/devices/bootstrap",
            "Optional central registration and bootstrap state.", ["/devices"]),
        new("schedule", "Capture schedule", "Capture", "/operations/schedule",
            "Immutable profile revisions, effective UTC windows, overrides, and admission state.", ["/schedule"]),
        new("focus", "Focus Assistant", "Capture", "",
            "Manual optical-focus preview and measured sharpness sessions.", [])
        {
            UnavailableReason = "Not available: manual focus sessions and image-derived sharpness measurements are not implemented (issue #1017).",
            UnavailableIssue = 1017
        },
        new("calibration", "Calibration", "Capture", "/operations/calibration",
            "Deterministic software references with exact lineage and safe-boundary activation.", ["/calibration"]),
        new("executions", "Processing executions", "Processing", "/operations/pipeline/executions",
            "Live and replay executions from the durable journal, with node status, attempts, inputs, and outputs.", ["/operations/pipeline/replays"]),
        new("graphs", "Named graphs", "Processing", "/operations/pipeline/graphs",
            "Immutable graph revisions, their lifecycle, drafts, and activation.", []),
        new("pipeline", "Pipeline summary", "Processing", "/operations/pipeline",
            "Desired and effective processing graph of the active and pending revisions.", []),
        new("environment", "Environment", "Processing", "/operations/environment",
            "Environmental source health, acquisition attempts, and local observation history.", ["/environmental"]),
        new("transients", "Transients", "Processing", "", "", [])
        {
            UnavailableIssue = 1026,
            UnavailableReason = "Unavailable: detector operations are tracked in issue #1026. Existing candidate/event views remain under Events."
        },
        new("automations", "Automations", "Automation", "/operations/automations",
            "Registered local tasks and triggers with their next scheduled activity.", []),
        new("delivery", "Delivery", "Data", "", "", [])
        {
            UnavailableIssue = 1027,
            UnavailableReason = "Unavailable: the Delivery workspace is tracked in issue #1027. Existing outbox facts remain in Data & storage."
        },
        new("storage", "Storage & retention", "Data", "", "", [])
        {
            UnavailableIssue = 1029,
            UnavailableReason = "Unavailable: the Storage & retention workspace is tracked in issue #1029. Existing storage facts remain in Data & storage."
        },
        new("data", "Data & storage", "Data", "/operations/data",
            "Raw ingress, capture lanes, processing, delivery outbox, and storage pressure.", []),
        new("quarantine", "Quarantine & recovery", "Data", "/operations/quarantine",
            "Bounded quarantine history with audited replay and abandonment decisions.", []),
        new("health", "Health & diagnostics", "System", "", "", [])
        {
            UnavailableIssue = 1030,
            UnavailableReason = "Unavailable: the Health & diagnostics workspace is tracked in issue #1030. Existing status remains in Overview and System."
        },
        new("control", "System control", "System", "", "", [])
        {
            UnavailableIssue = 1031,
            UnavailableReason = "Unavailable: system lifecycle capabilities are tracked in issue #1031. No restart, shutdown, or reset action is exposed here."
        },
        new("software", "Software & catalog", "System", "", "", [])
        {
            UnavailableIssue = 1032,
            UnavailableReason = "Unavailable: the Software & catalog workspace is tracked in issue #1032. Existing catalog facts remain in Sky map & catalog."
        },
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
