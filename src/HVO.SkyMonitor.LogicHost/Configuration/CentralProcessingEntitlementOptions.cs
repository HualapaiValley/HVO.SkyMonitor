using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>Per-observatory allocation: active-job entitlements, weight, priority, and an optional runner pool.</summary>
internal sealed class ObservatoryEntitlementOptions
{
    /// <summary>Maximum unexpired leases for the observatory; 0 means unlimited; null inherits the default.</summary>
    public int? ActiveJobs { get; init; }

    /// <summary>Maximum unexpired leases per camera (device) of the observatory; 0 means unlimited.</summary>
    public int? ActiveJobsPerCamera { get; init; }

    /// <summary>Weighted fair share; larger weights receive proportionally more concurrent capacity.</summary>
    public double? Weight { get; init; }

    /// <summary>Lower values are served first among non-starving work.</summary>
    public int? Priority { get; init; }

    /// <summary>Runner pool that serves this observatory; null means the shared pool.</summary>
    public string? Pool { get; init; }

    /// <summary>Per-resource-class active-job limits for this observatory; 0 means unlimited.</summary>
    public Dictionary<string, int> ResourceClassActiveJobs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Global budget for one resource class across all observatories.</summary>
internal sealed class ResourceClassBudgetOptions
{
    /// <summary>Maximum unexpired leases in the class; 0 means unlimited.</summary>
    public int ActiveJobs { get; init; }

    /// <summary>Maximum summed input bytes of unexpired leases in the class; 0 means unlimited.</summary>
    public long ActiveInputBytes { get; init; }
}

/// <summary>
/// Fair scheduling and entitlement policy for LogicHost-owned central jobs (#429). Disabled by default so a
/// standalone installation needs no plan, license, or metering service; when enabled, the claim engine enforces the
/// limits atomically and orders work by starvation age, priority, and weighted fair share.
/// </summary>
internal sealed class CentralProcessingEntitlementOptions
{
    public const string SectionName = "ProcessingEntitlements";

    public const string StructuredAnalysisClass = "structured-analysis";
    public const string PresentationClass = "presentation";
    public const string CompositionClass = "composition";
    public const string EncodingClass = "encoding";
    public const string ImageClass = "image";

    public const string SharedPool = "shared";

    public bool Enabled { get; init; }

    /// <summary>Default active-job entitlement per observatory; 0 means unlimited.</summary>
    public int DefaultActiveJobs { get; init; }

    /// <summary>Default active-job entitlement per camera; 0 means unlimited.</summary>
    public int DefaultActiveJobsPerCamera { get; init; }

    public double DefaultWeight { get; init; } = 1.0;

    public int DefaultPriority { get; init; }

    /// <summary>Work older than this is served before priority and share ordering (starvation prevention).</summary>
    public TimeSpan StarvationAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Pending jobs per observatory above which the health check reports admission backpressure; 0 disables.</summary>
    public int AdmissionPendingLimit { get; init; }

    /// <summary>Oldest claimable work age at which a saturated observatory degrades health.</summary>
    public TimeSpan BacklogDegradedAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Keyed by observatory id text (the configuration binder does not bind GUID dictionary keys).</summary>
    public Dictionary<string, ObservatoryEntitlementOptions> Observatories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ResourceClassBudgetOptions> ResourceClasses { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Recipe name to resource class; unmapped recipes use the built-in defaults, then <c>image</c>.</summary>
    public Dictionary<string, string> RecipeResourceClasses { get; init; } = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> DefaultRecipeClasses = new(StringComparer.Ordinal)
    {
        [BuiltInProcessingRecipes.EncodedPreview] = EncodingClass,
        [BuiltInProcessingRecipes.JpegEncoding] = EncodingClass,
        [BuiltInProcessingRecipes.Annotation] = PresentationClass,
        [BuiltInProcessingRecipes.ProjectedScene] = PresentationClass,
        [BuiltInProcessingRecipes.WeatherCloudOverlay] = PresentationClass,
        [BuiltInProcessingRecipes.ImageQuality] = StructuredAnalysisClass,
        [BuiltInProcessingRecipes.CloudAssessment] = StructuredAnalysisClass,
        [BuiltInProcessingRecipes.NoOpAnalyzer] = StructuredAnalysisClass,
        ["central-transient-validation"] = StructuredAnalysisClass,
        ["central-transient-derivative"] = StructuredAnalysisClass,
        ["central-transient-reprocessing"] = StructuredAnalysisClass,
        [BuiltInProcessingRecipes.RollingMean] = CompositionClass,
        [BuiltInProcessingRecipes.LinearNormalization] = CompositionClass,
        [BuiltInProcessingRecipes.ReferenceCalibration] = CompositionClass
    };

    public static readonly string[] KnownClasses =
        [StructuredAnalysisClass, PresentationClass, CompositionClass, EncodingClass, ImageClass];

    public string ResolveResourceClass(string recipeName)
        => RecipeResourceClasses.TryGetValue(recipeName, out var configured)
            ? configured
            : DefaultRecipeClasses.TryGetValue(recipeName, out var known) ? known : ImageClass;

    public ObservatoryEntitlementOptions? Find(Guid observatoryId)
        => Observatories.TryGetValue(observatoryId.ToString("D"), out var entitlement) ? entitlement : null;

    private IEnumerable<(Guid ObservatoryId, ObservatoryEntitlementOptions Entitlement)> ParsedObservatories()
        => Observatories.Select(pair => (Guid.TryParse(pair.Key, out var id) ? id : Guid.Empty, pair.Value));

    public int ResolveActiveJobs(Guid observatoryId) => Find(observatoryId)?.ActiveJobs ?? DefaultActiveJobs;

    public int ResolveActiveJobsPerCamera(Guid observatoryId)
        => Find(observatoryId)?.ActiveJobsPerCamera ?? DefaultActiveJobsPerCamera;

    public double ResolveWeight(Guid observatoryId) => Find(observatoryId)?.Weight ?? DefaultWeight;

    public int ResolvePriority(Guid observatoryId) => Find(observatoryId)?.Priority ?? DefaultPriority;

    public string? ResolvePool(Guid observatoryId) => Find(observatoryId)?.Pool;

    /// <summary>
    /// The compact JSON the claim query joins with <c>OPENJSON</c>: one row per configured observatory carrying the
    /// resolved limits, weight, priority, and pool.
    /// </summary>
    public string CreateObservatoryEntitlementsJson()
        => JsonSerializer.Serialize(ParsedObservatories().Select(pair => new
        {
            o = pair.ObservatoryId,
            a = pair.Entitlement.ActiveJobs ?? DefaultActiveJobs,
            c = pair.Entitlement.ActiveJobsPerCamera ?? DefaultActiveJobsPerCamera,
            w = pair.Entitlement.Weight ?? DefaultWeight,
            p = pair.Entitlement.Priority ?? DefaultPriority,
            pool = pair.Entitlement.Pool
        }));

    /// <summary>One row per recipe: its resource class and that class's global limits.</summary>
    public string CreateRecipeClassesJson()
    {
        var recipes = HVO.SkyMonitor.ProcessingRunner.Contracts.ProcessingRunnerCapabilities.BuiltInRecipeNames
            .Concat(DefaultRecipeClasses.Keys)
            .Concat(RecipeResourceClasses.Keys)
            .Distinct(StringComparer.Ordinal);
        return JsonSerializer.Serialize(recipes.Select(recipe =>
        {
            var cls = ResolveResourceClass(recipe);
            var budget = ResourceClasses.TryGetValue(cls, out var configured) ? configured : null;
            return new { r = recipe, cls, a = budget?.ActiveJobs ?? 0, b = budget?.ActiveInputBytes ?? 0L };
        }));
    }

    /// <summary>Per observatory and class limits: rows of observatory id, class, and active-job limit.</summary>
    public string CreateObservatoryClassLimitsJson()
        => JsonSerializer.Serialize(ParsedObservatories().SelectMany(pair => pair.Entitlement.ResourceClassActiveJobs
            .Select(limit => new { o = pair.ObservatoryId, cls = limit.Key, a = limit.Value })));

    public bool Validate(out string? error)
    {
        error = null;
        if (DefaultActiveJobs < 0 || DefaultActiveJobsPerCamera < 0 || DefaultWeight <= 0 || !double.IsFinite(DefaultWeight)
            || StarvationAge <= TimeSpan.Zero || AdmissionPendingLimit < 0 || BacklogDegradedAfter <= TimeSpan.Zero)
        {
            error = "ProcessingEntitlements defaults are invalid.";
            return false;
        }
        foreach (var (key, entitlement) in Observatories)
        {
            if (!Guid.TryParse(key, out var observatoryId) || observatoryId == Guid.Empty || entitlement.ActiveJobs is < 0 || entitlement.ActiveJobsPerCamera is < 0
                || entitlement.Weight is <= 0 || (entitlement.Weight is { } weight && !double.IsFinite(weight))
                || (entitlement.Pool is not null && !IsValidPool(entitlement.Pool))
                || entitlement.ResourceClassActiveJobs.Any(pair => pair.Value < 0 || !IsValidClass(pair.Key)))
            {
                error = $"ProcessingEntitlements:Observatories:{observatoryId} is invalid.";
                return false;
            }
        }
        foreach (var (cls, budget) in ResourceClasses)
        {
            if (!IsValidClass(cls) || budget.ActiveJobs < 0 || budget.ActiveInputBytes < 0)
            {
                error = $"ProcessingEntitlements:ResourceClasses:{cls} is invalid.";
                return false;
            }
        }
        foreach (var (recipe, cls) in RecipeResourceClasses)
        {
            if (string.IsNullOrWhiteSpace(recipe) || !IsValidClass(cls))
            {
                error = $"ProcessingEntitlements:RecipeResourceClasses:{recipe} is invalid.";
                return false;
            }
        }
        return true;
    }

    public static bool IsValidClass(string? cls)
        => !string.IsNullOrWhiteSpace(cls) && cls.Length <= 64
            && cls.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public static bool IsValidPool(string? pool)
        => !string.IsNullOrWhiteSpace(pool) && pool.Length <= 64
            && !string.Equals(pool, SharedPool, StringComparison.Ordinal)
            && pool.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
