namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// One routeable section of the Operations workspace. <see cref="Eyebrow"/> and
/// <see cref="Description"/> are the prototype page heading for the section.
/// </summary>
public sealed record OperationsSection(
    string Slug,
    string Label,
    string Group,
    string Href,
    string Eyebrow,
    string Description,
    IReadOnlyList<string> AliasPaths)
{
    /// <summary>A short neutral sidebar badge, such as "Manual"; null for none.</summary>
    public string? Badge { get; init; }

    /// <summary>
    /// What the page cannot do yet on this CameraAgent; the sidebar describes its link with this note.
    /// </summary>
    public string? CapabilityNote { get; init; }

    public string IconPath => Slug switch
    {
        "overview" => "M3 3h5v5H3zM12 3h5v5h-5zM3 12h5v5H3zM12 12h5v5h-5z",
        "site" => "M10 17V8m0 0 4 4m-4-4-4 4M4 17h12M12 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0",
        "camera" => "M5 6h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2m1 0 1-2h6l1 2M13 11a3 3 0 1 0-6 0 3 3 0 0 0 6 0",
        "registration" => "M5 4h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2m2 4h6m-6 4h3",
        "schedule" => "M5 4h10a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2M6 2v4m8-4v4M3 8h14",
        "focus" => "M13 10a3 3 0 1 0-6 0 3 3 0 0 0 6 0M7 3H3v4m10-4h4v4M7 17H3v-4m10 4h4v-4",
        "calibration" => "M6 3h8v4l3 6a3 3 0 0 1-3 4H6a3 3 0 0 1-3-4l3-6V3ZM6 10h8",
        "pipeline" => "M6 10a2 2 0 1 0-4 0 2 2 0 0 0 4 0M12 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0M18 10a2 2 0 1 0-4 0 2 2 0 0 0 4 0M12 15a2 2 0 1 0-4 0 2 2 0 0 0 4 0M6 9l2.5-2.5m3 0L14 9m-8 2 2.5 2.5m3-1L14 11",
        "environment" => "M7 12a4 4 0 1 1 6-3 3 3 0 1 1 1 6H7a3 3 0 1 1 0-6",
        "transients" => "m12 2-1 6 5-2-7 12 1-7-5 2 7-11Z",
        "automations" => "M4 5h8m-8 5h12M4 15h6M17 5a2 2 0 1 0-4 0 2 2 0 0 0 4 0M9 15a2 2 0 1 0-4 0 2 2 0 0 0 4 0",
        "delivery" => "M3 10h11m-4-4 4 4-4 4M17 4v12",
        "storage" => "M16 5a6 3 0 1 0-12 0 6 3 0 0 0 12 0M4 5v5c0 1.7 2.7 3 6 3s6-1.3 6-3V5M4 10v5c0 1.7 2.7 3 6 3s6-1.3 6-3v-5",
        "health" => "M3 10h3l2-5 4 10 2-5h3",
        "control" => "M10 2v7M6 4a7 7 0 1 0 8 0",
        "software" => "m10 2 7 4-7 4-7-4 7-4ZM3 10l7 4 7-4M3 14l7 4 7-4",
        _ => "M3 5h14M3 10h14M3 15h14"
    };

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
/// The fixed section map of the Operations workspace, in the prototype's order and grouping.
/// Implemented pages that the prototype does not name live under the section that owns them:
/// executions, replays and named graphs under Pipeline, the sky map under Observatory &amp;
/// location, data and quarantine under Storage &amp; retention, and system facts under Health.
/// </summary>
public static class OperationsSectionCatalog
{
    public const string OverviewPath = "/operations";

    public static IReadOnlyList<string> Groups { get; } = ["Overview", "Setup", "Capture", "Processing", "Automation", "Data", "System"];

    public static IReadOnlyList<OperationsSection> Sections { get; } =
    [
        new("overview", "Overview", "Overview", OverviewPath,
            "Local authority",
            "Configure and operate this camera without making local acquisition dependent on LogicHost.", []),
        new("site", "Observatory & location", "Setup", "/operations/site",
            "Setup / geometry authority",
            "Keep local capture geometry available offline while showing the separate LogicHost Observatory assignment and review state.", ["/operations/sky-map"]),
        new("camera", "Camera & rig", "Setup", "/operations/camera",
            "Setup / versioned capture profile",
            "Keep the camera implementation, native readout, and physical optics explicit while activating them as one capture-time profile.", []),
        new("registration", "Registration", "Setup", "/operations/registration",
            "Setup / central integration",
            "Inspect this CameraAgent identity, its LogicHost enrollment, Observatory assignment, and current credential health.", ["/devices/bootstrap", "/devices"]),
        new("schedule", "Schedule", "Capture", "/operations/schedule",
            "Capture / deterministic admission",
            "Define when capture may start, then apply day, twilight, and night exposure policies without catch-up captures.", ["/schedule"]),
        new("focus", "Focus", "Capture", "/operations/focus",
            "Capture / manual optical setup",
            "Adjust the lens by hand at the camera. This page does not imply a motorized focuser or autofocus capability.", [])
        {
            Badge = "Manual",
            CapabilityNote = "Focus sessions, temporary previews and image-derived sharpness measurements are not implemented on this CameraAgent."
        },
        new("calibration", "Calibration", "Capture", "/operations/calibration",
            "Capture / immutable references",
            "Manage compatible bias, dark, flat, and defect evidence without weakening raw-source immutability.", ["/calibration"]),
        new("pipeline", "Pipeline", "Processing", "/operations/pipeline",
            "Processing / configured dependency graph",
            "Configure unique step IDs, registered operation types, and explicit dependencies, then validate the whole CameraAgent graph.", []),
        new("environment", "Environment", "Processing", "/operations/environment",
            "Processing / independent observations",
            "Track weather and camera conditions independently from image processing, with explicit freshness and capture associations.", ["/environmental"]),
        new("transients", "Transients", "Processing", "/operations/transients",
            "Processing / independent detector lane",
            "Inspect local causal candidate extraction and the handoff to authoritative centered validation without conflating it with display processing.", []),
        new("automations", "Automations", "Automation", "/operations/automations",
            "Automation / typed scheduled work",
            "Generate observing-day products and perform bounded maintenance through registered task types, explicit triggers, and durable run history.", []),
        new("delivery", "Delivery", "Data", "/operations/delivery",
            "Data / one-way central integration",
            "Monitor policy-selected exports, durable retries, and LogicHost acknowledgements without treating central availability as capture correctness.", []),
        new("storage", "Storage & retention", "Data", "/operations/storage",
            "Data / local evidence safety",
            "Keep immutable evidence, durable lanes, retention eligibility, and disk pressure visible as one local safety boundary.", ["/operations/data", "/operations/quarantine"]),
        new("health", "Health & diagnostics", "System", "/operations/health",
            "System / bounded diagnostics",
            "Separate capture freshness, durable work pressure, host resources, and central dependencies without exposing sensitive payload data.", ["/operations/system", "/system"]),
        new("control", "System control", "System", "/operations/control",
            "System / authenticated local actions",
            "Perform only bounded CameraAgent controls that preserve accepted evidence and produce an auditable terminal receipt.", []),
        new("software", "Software & catalog", "System", "/operations/software",
            "System / verified local assets",
            "Inspect application, immutable celestial packages, and separately refreshed orbital evidence without implying unattended activation.", [])
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
