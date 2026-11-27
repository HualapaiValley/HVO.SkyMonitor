using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Parametric raw-rendering profile family mapped from catalog object classes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkyProfileClass>))]
public enum DeepSkyProfileClass
{
    Unsupported,
    Galaxy,
    GlobularCluster,
    OpenCluster,
    PlanetaryNebula,
    DiffuseNebula,
    SupernovaRemnant,
    CompositeClusterNebula,
    DarkNebula
}

/// <summary>Explicit provenance of the photometry that normalized a rendered deep-sky source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkyPhotometryBasis>))]
public enum DeepSkyPhotometryBasis
{
    None,
    CatalogVisualMagnitude,
    CatalogSurfaceBrightnessB25,
    SyntheticScenario
}

/// <summary>Explicit provenance of the sky geometry used to rasterize a deep-sky source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkyGeometryBasis>))]
public enum DeepSkyGeometryBasis
{
    None,
    CatalogOutline,
    CatalogOrientedEllipse,
    CatalogCircle,
    UnresolvedPointSource,
    SyntheticScenario
}

/// <summary>Explicit provenance of channel-response ratios for a deep-sky source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkySpectralOrigin>))]
public enum DeepSkySpectralOrigin
{
    CatalogBMinusV,
    SimulationClassDefault,
    SyntheticScenario,
    AttenuationOnly
}

/// <summary>
/// Explicit relative spectral weights for monochrome and RGB/CFA rendering. Heuristic class spectra are always
/// labeled as simulation parameters rather than measured spectroscopy.
/// </summary>
public sealed record DeepSkySpectralFactors(
    double Mono,
    double Red,
    double Green,
    double Blue,
    string SpectralModel,
    DeepSkySpectralOrigin Origin,
    bool IsSimulationParameter)
{
    public double Channel(int channel) => channel switch
    {
        -1 => Mono,
        0 => Red,
        1 => Green,
        2 => Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public void Validate()
    {
        if (!ValidFactor(Mono) || !ValidFactor(Red) || !ValidFactor(Green) || !ValidFactor(Blue) ||
            string.IsNullOrWhiteSpace(SpectralModel) || !Enum.IsDefined(Origin))
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkySpectralFactors));
        }
    }

    private static bool ValidFactor(double value) => double.IsFinite(value) && value is >= 0 and <= 10;
}

/// <summary>
/// Versioned bounded parametric appearance parameters kept strictly separate from immutable catalog facts.
/// Every heuristic morphology and spectral parameter is explicitly marked as a simulation parameter.
/// </summary>
public sealed record DeepSkySimulationParameters(
    string ProfileSchemaVersion,
    DeepSkyProfileClass ProfileClass,
    string MorphologyModel,
    bool IsSimulationParameter,
    DeepSkySpectralFactors Spectral,
    double SersicIndex = 1.5,
    double EffectiveRadiusFraction = 0.35,
    double CoreConcentration = 0.25,
    double GranularityAmplitude = 0.15,
    double InnerShellRadiusFraction = 0.5,
    double SoftEdgeFraction = 0.25,
    double CompositeNebulaFluxFraction = 0.75,
    int MaximumUnresolvedClusterMembers = 24,
    double PeakOpticalDepth = 1.6)
{
    public const string CurrentSchemaVersion = "deep-sky-parametric-profile-v1";

    public void Validate()
    {
        if (!string.Equals(ProfileSchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !Enum.IsDefined(ProfileClass) || string.IsNullOrWhiteSpace(MorphologyModel) ||
            Spectral is null ||
            !double.IsFinite(SersicIndex) || SersicIndex is < 0.25 or > 6 ||
            !double.IsFinite(EffectiveRadiusFraction) || EffectiveRadiusFraction is <= 0.02 or > 1 ||
            !double.IsFinite(CoreConcentration) || CoreConcentration is <= 0.02 or > 1 ||
            !double.IsFinite(GranularityAmplitude) || GranularityAmplitude is < 0 or > 0.35 ||
            !double.IsFinite(InnerShellRadiusFraction) || InnerShellRadiusFraction is < 0 or >= 0.98 ||
            !double.IsFinite(SoftEdgeFraction) || SoftEdgeFraction is <= 0.01 or > 1 ||
            !double.IsFinite(CompositeNebulaFluxFraction) || CompositeNebulaFluxFraction is < 0 or > 1 ||
            MaximumUnresolvedClusterMembers is < 1 or > 128 ||
            !double.IsFinite(PeakOpticalDepth) || PeakOpticalDepth is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkySimulationParameters));
        }
        Spectral.Validate();
    }
}

/// <summary>
/// Explicit declared synthetic appearance parameters for one object in a synthetic scenario. Supplies photometry,
/// geometry, or morphology only when an explicit versioned scenario declares it; never mutates catalog rows.
/// </summary>
public sealed record DeepSkySyntheticSourceOverride(
    string ObjectId,
    double? VisualMagnitude = null,
    double? ColorIndex = null,
    double? MajorAxisArcminutes = null,
    double? MinorAxisArcminutes = null,
    double? PositionAngleDegrees = null,
    DeepSkyProfileClass? ProfileClass = null,
    double? PeakOpticalDepth = null,
    double? CompositeNebulaFluxFraction = null,
    int? MaximumUnresolvedClusterMembers = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ObjectId) ||
            VisualMagnitude is { } v && (!double.IsFinite(v) || v is < -30 or > 30) ||
            ColorIndex is { } bv && (!double.IsFinite(bv) || bv is < -1 or > 4) ||
            MajorAxisArcminutes is { } maj && (!double.IsFinite(maj) || maj is <= 0 or > 1200) ||
            MinorAxisArcminutes is { } min && (!double.IsFinite(min) || min is <= 0 or > 1200) ||
            MajorAxisArcminutes is { } maj2 && MinorAxisArcminutes is { } min2 && min2 > maj2 ||
            PositionAngleDegrees is { } pa && (!double.IsFinite(pa) || pa is < 0 or >= 180) ||
            ProfileClass is { } cls && !Enum.IsDefined(cls) ||
            PeakOpticalDepth is { } tau && (!double.IsFinite(tau) || tau is < 0 or > 10) ||
            CompositeNebulaFluxFraction is { } frac && (!double.IsFinite(frac) || frac is < 0 or > 1) ||
            MaximumUnresolvedClusterMembers is { } members && members is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkySyntheticSourceOverride));
        }
    }
}

/// <summary>
/// Versioned synthetic deep-sky scenario supplying declared sources, overrides, or compatible celestial diffuse
/// background for controlled simulation and qualification without fabricating catalog facts.
/// </summary>
public sealed record DeepSkySyntheticScenario(
    string ScenarioId,
    string SchemaVersion = DeepSkySyntheticScenario.CurrentSchemaVersion,
    IReadOnlyList<DeepSkyObject>? AdditionalObjects = null,
    IReadOnlyList<DeepSkyOutline>? AdditionalOutlines = null,
    IReadOnlyList<DeepSkySyntheticSourceOverride>? Overrides = null,
    double CelestialDiffuseBackgroundElectronsPerSecond = 0)
{
    public const string CurrentSchemaVersion = "deep-sky-synthetic-scenario-v1";
    public const int MaximumOutlineRingsPerOutline = 64;
    public const int MaximumOutlineVerticesPerOutline = 4_096;
    public const int MaximumTotalOutlineVertices = 16_384;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ScenarioId) || ScenarioId.Length > 128 ||
            !string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !double.IsFinite(CelestialDiffuseBackgroundElectronsPerSecond) ||
            CelestialDiffuseBackgroundElectronsPerSecond is < 0 or > 1e12)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkySyntheticScenario));
        }
        if (AdditionalObjects is { Count: > 256 } || AdditionalOutlines is { Count: > 256 } || Overrides is { Count: > 256 })
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkySyntheticScenario), "Synthetic scenario exceeds structural bounds.");
        }
        var seenOverrideIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Overrides ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate();
            if (!seenOverrideIds.Add(item.ObjectId))
            {
                throw new ArgumentException($"Duplicate synthetic override for '{item.ObjectId}'.", nameof(Overrides));
            }
        }
        var seenObjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in AdditionalObjects ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Designation) ||
                string.IsNullOrWhiteSpace(item.DisplayName) ||
                string.IsNullOrWhiteSpace(item.Constellation) ||
                !DeepSkyObjectTypes.IsSupported(item.ObjectType) ||
                !double.IsFinite(item.RightAscensionHours) || item.RightAscensionHours is < 0 or >= 24 ||
                !double.IsFinite(item.DeclinationDegrees) || item.DeclinationDegrees is < -90 or > 90 ||
                item.MajorAxisArcminutes is { } maj && (!double.IsFinite(maj) || maj is <= 0 or > 1200) ||
                item.MinorAxisArcminutes is { } min && (item.MajorAxisArcminutes is not { } maj2 || !double.IsFinite(min) || min <= 0 || min > maj2) ||
                item.PositionAngleDegrees is { } pa && (!double.IsFinite(pa) || pa is < 0 or >= 180) ||
                item.VMagnitude is { } vMag && (!double.IsFinite(vMag) || vMag is < -30 or > 30) ||
                item.BMagnitude is { } bMag && (!double.IsFinite(bMag) || bMag is < -30 or > 30) ||
                item.SurfaceBrightness is { } sb && (!double.IsFinite(sb) || sb is < -30 or > 40) ||
                item.MessierNumber is < 1 or > 110 ||
                item.CaldwellNumber is < 1 or > 109 ||
                !seenObjectIds.Add(item.Id))
            {
                throw new ArgumentException($"Synthetic deep-sky object '{item.Id}' is invalid or duplicated.", nameof(AdditionalObjects));
            }
        }
        var seenOutlineKeys = new HashSet<(string ObjectId, int Level)>();
        var totalOutlineVertices = 0;
        foreach (var outline in AdditionalOutlines ?? [])
        {
            ArgumentNullException.ThrowIfNull(outline);
            if (string.IsNullOrWhiteSpace(outline.ObjectId) ||
                outline.Level is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
                !seenOutlineKeys.Add((outline.ObjectId, outline.Level)) ||
                outline.Rings is not { Count: >= 1 and <= MaximumOutlineRingsPerOutline })
            {
                throw new ArgumentException($"Synthetic deep-sky outline '{outline.ObjectId}' is invalid or duplicated.", nameof(AdditionalOutlines));
            }
            var outlineVertices = 0;
            foreach (var ring in outline.Rings)
            {
                ArgumentNullException.ThrowIfNull(ring);
                if (ring.Points is not { Count: >= DeepSkyCatalog.MinimumRingPointCount } points || points[0] != points[^1])
                {
                    throw new ArgumentException($"Synthetic deep-sky outline ring for '{outline.ObjectId}' must be closed with at least {DeepSkyCatalog.MinimumRingPointCount} points.", nameof(AdditionalOutlines));
                }
                outlineVertices += points.Count;
                totalOutlineVertices += points.Count;
                if (outlineVertices > MaximumOutlineVerticesPerOutline || totalOutlineVertices > MaximumTotalOutlineVertices)
                {
                    throw new ArgumentOutOfRangeException(nameof(AdditionalOutlines), "Synthetic deep-sky outline exceeds vertex structural bounds.");
                }
                for (var idx = 0; idx < points.Count; idx++)
                {
                    var pt = points[idx];
                    if (!double.IsFinite(pt.RightAscensionDegrees) || pt.RightAscensionDegrees is < 0 or >= 360 ||
                        !double.IsFinite(pt.DeclinationDegrees) || pt.DeclinationDegrees is < -90 or > 90)
                    {
                        throw new ArgumentException($"Synthetic deep-sky outline vertex for '{outline.ObjectId}' is out of range.", nameof(AdditionalOutlines));
                    }
                    if (idx > 0 && pt == points[idx - 1])
                    {
                        throw new ArgumentException($"Synthetic deep-sky outline ring for '{outline.ObjectId}' repeats consecutive vertices.", nameof(AdditionalOutlines));
                    }
                }
            }
        }
    }

    public string ComputeCanonicalSha256()
    {
        Validate();
        var canonical = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(this));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}

/// <summary>
/// Resolves explicit versioned bounded parametric profiles from catalog object classes and evaluates smooth,
/// texture-free radial/outline profiles without modifying catalog facts.
/// </summary>
public static class DeepSkySourceProfileResolver
{
    public const string GalaxyMorphologyModel = "galaxy-sersic-smooth-v1";
    public const string GlobularMorphologyModel = "globular-king-granular-v1";
    public const string OpenClusterMorphologyModel = "open-cluster-residual-sparse-v1";
    public const string PlanetaryNebulaMorphologyModel = "planetary-nebula-ring-disc-v1";
    public const string DiffuseNebulaMorphologyModel = "diffuse-nebula-soft-envelope-v1";
    public const string SupernovaRemnantMorphologyModel = "remnant-limb-shell-v1";
    public const string CompositeMorphologyModel = "composite-cluster-nebula-v1";
    public const string DarkNebulaMorphologyModel = "dark-nebula-attenuation-v1";

    public static DeepSkyProfileClass Classify(string objectType) => objectType switch
    {
        "G" or "GPair" or "GTrpl" or "GGroup" => DeepSkyProfileClass.Galaxy,
        "GCl" => DeepSkyProfileClass.GlobularCluster,
        "OCl" or "*Ass" => DeepSkyProfileClass.OpenCluster,
        "PN" => DeepSkyProfileClass.PlanetaryNebula,
        "Neb" or "EmN" or "HII" or "RfN" => DeepSkyProfileClass.DiffuseNebula,
        "SNR" => DeepSkyProfileClass.SupernovaRemnant,
        "Cl+N" => DeepSkyProfileClass.CompositeClusterNebula,
        "DrkN" => DeepSkyProfileClass.DarkNebula,
        _ => DeepSkyProfileClass.Unsupported
    };

    public static DeepSkySimulationParameters Resolve(
        DeepSkyObject item,
        DeepSkySyntheticSourceOverride? syntheticOverride = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        syntheticOverride?.Validate();
        var profileClass = syntheticOverride?.ProfileClass ?? Classify(item.ObjectType);
        if (profileClass == DeepSkyProfileClass.Unsupported)
        {
            throw new ArgumentException($"Object type '{item.ObjectType}' has no parametric extended-source profile.", nameof(item));
        }

        var spectral = ResolveSpectralFactors(item, profileClass, syntheticOverride);
        var (sersicIndex, effectiveRadiusFraction) = ResolveGalaxySersic(item.HubbleType);

        var parameters = profileClass switch
        {
            DeepSkyProfileClass.Galaxy => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                GalaxyMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                SersicIndex: sersicIndex,
                EffectiveRadiusFraction: effectiveRadiusFraction),
            DeepSkyProfileClass.GlobularCluster => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                GlobularMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                CoreConcentration: 0.22,
                GranularityAmplitude: 0.16),
            DeepSkyProfileClass.OpenCluster => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                OpenClusterMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                MaximumUnresolvedClusterMembers: syntheticOverride?.MaximumUnresolvedClusterMembers ?? 24),
            DeepSkyProfileClass.PlanetaryNebula => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                PlanetaryNebulaMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                InnerShellRadiusFraction: 0.52),
            DeepSkyProfileClass.DiffuseNebula => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                DiffuseNebulaMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                SoftEdgeFraction: 0.28),
            DeepSkyProfileClass.SupernovaRemnant => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                SupernovaRemnantMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                InnerShellRadiusFraction: 0.78),
            DeepSkyProfileClass.CompositeClusterNebula => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                CompositeMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                SoftEdgeFraction: 0.25,
                CompositeNebulaFluxFraction: syntheticOverride?.CompositeNebulaFluxFraction ?? 0.75,
                MaximumUnresolvedClusterMembers: syntheticOverride?.MaximumUnresolvedClusterMembers ?? 20),
            DeepSkyProfileClass.DarkNebula => new DeepSkySimulationParameters(
                DeepSkySimulationParameters.CurrentSchemaVersion,
                profileClass,
                DarkNebulaMorphologyModel,
                IsSimulationParameter: true,
                spectral,
                SoftEdgeFraction: 0.30,
                PeakOpticalDepth: syntheticOverride?.PeakOpticalDepth ?? 1.6),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
        parameters.Validate();
        return parameters;
    }

    /// <summary>
    /// Evaluates the dimensionless unnormalized radial profile at normalized elliptical radius <paramref name="q"/>
    /// in <c>[0, 1]</c>. Always bounded, finite, non-negative, and zero at or outside <c>q = 1</c>.
    /// </summary>
    public static double EvaluateRadialDensity(
        DeepSkySimulationParameters parameters,
        double q,
        double angleRadians,
        ulong sourceHash)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(q) || q < 0 || q >= 1) return 0d;

        return parameters.ProfileClass switch
        {
            DeepSkyProfileClass.Galaxy => EvaluateGalaxyDensity(parameters, q),
            DeepSkyProfileClass.GlobularCluster => EvaluateGlobularDensity(parameters, q, angleRadians, sourceHash),
            DeepSkyProfileClass.PlanetaryNebula => EvaluatePlanetaryNebulaDensity(parameters, q),
            DeepSkyProfileClass.DiffuseNebula or DeepSkyProfileClass.CompositeClusterNebula =>
                Math.Pow(Math.Max(0d, 1d - q * q), 1.5),
            DeepSkyProfileClass.SupernovaRemnant => EvaluateRemnantDensity(parameters, q),
            DeepSkyProfileClass.DarkNebula => Math.Max(0d, 1d - q * q),
            _ => 0d
        };
    }

    /// <summary>
    /// Evaluates a smooth, outline-constrained profile at normalized boundary-clearance <paramref name="edgeRatio"/>
    /// in <c>[0, 1]</c> and normalized center distance <paramref name="centerRatio"/> in <c>[0, 1]</c>.
    /// Strictly smooth inside sourced contours with no fabricated filament or survey texture.
    /// </summary>
    public static double EvaluateOutlineDensity(
        DeepSkySimulationParameters parameters,
        double edgeRatio,
        double centerRatio)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(edgeRatio) || edgeRatio <= 0) return 0d;
        var clampedEdge = Math.Clamp(edgeRatio, 0d, 1d);
        var clampedCenter = Math.Clamp(double.IsFinite(centerRatio) ? centerRatio : 0d, 0d, 1d);
        var taper = Smoothstep(Math.Min(1d, clampedEdge / parameters.SoftEdgeFraction));

        return parameters.ProfileClass switch
        {
            DeepSkyProfileClass.SupernovaRemnant =>
                taper * (0.15 + 0.85 * Math.Exp(-Math.Pow((clampedEdge - 0.18) / 0.14, 2))),
            DeepSkyProfileClass.PlanetaryNebula =>
                taper * (0.28 + 0.72 * Math.Exp(-Math.Pow((clampedEdge - 0.30) / 0.22, 2))),
            DeepSkyProfileClass.DarkNebula => taper,
            _ => taper * (0.40 + 0.60 * (1d - 0.65 * clampedCenter * clampedCenter))
        };
    }

    internal static ulong ComputeStableObjectHash(string objectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("hvo-dso-profile-v1:" + objectId));
        return BitConverter.ToUInt64(hash, 0);
    }

    private static double EvaluateGalaxyDensity(DeepSkySimulationParameters parameters, double q)
    {
        var n = parameters.SersicIndex;
        // Standard Ciotti & Bertin (1999) asymptotic approximation for Sersic b_n.
        var bn = Math.Max(0.5, 2d * n - 1d / 3d + 4d / (405d * n));
        var re = parameters.EffectiveRadiusFraction;
        var raw = Math.Exp(-bn * (Math.Pow(Math.Max(1e-4, q) / re, 1d / n) - 1d));
        var edge = Math.Exp(-bn * (Math.Pow(1d / re, 1d / n) - 1d));
        var window = Math.Sqrt(Math.Max(0d, 1d - q * q));
        return Math.Max(0d, (raw - edge) * window);
    }

    private static double EvaluateGlobularDensity(
        DeepSkySimulationParameters parameters,
        double q,
        double angleRadians,
        ulong sourceHash)
    {
        var rc = parameters.CoreConcentration;
        var core = Math.Pow(1d + q * q / (rc * rc), -1.5);
        var tidal = Math.Pow(1d + 1d / (rc * rc), -1.5);
        var smooth = Math.Max(0d, core - tidal);
        if (parameters.GranularityAmplitude <= 0) return smooth;

        // Deterministic bounded statistical granularity (low-amplitude angular/radial hash modulation, never survey stars).
        var radialBin = (int)(q * 12d);
        var angularBin = (int)((((angleRadians % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI)) / (2 * Math.PI) * 24d);
        var mix = Mix64(sourceHash ^ ((ulong)(uint)radialBin << 32) ^ (uint)angularBin);
        var signedUnit = ((mix >> 11) * (1d / (1UL << 53))) * 2d - 1d;
        // Attenuate granularity near tidal boundary so the edge stays smooth.
        var envelope = (1d - q * q) * (0.25 + 0.75 * Math.Min(1d, q / Math.Max(0.05, rc)));
        return smooth * Math.Max(0.1, 1d + parameters.GranularityAmplitude * envelope * signedUnit);
    }

    private static double EvaluatePlanetaryNebulaDensity(DeepSkySimulationParameters parameters, double q)
    {
        var rRing = parameters.InnerShellRadiusFraction;
        const double ringWidth = 0.22;
        var ring = Math.Exp(-0.5 * Math.Pow((q - rRing) / ringWidth, 2));
        var interior = 0.28 * Math.Max(0d, 1d - q * q);
        var outerCutoff = Smoothstep(Math.Min(1d, (1d - q) / 0.18));
        return (ring + interior) * outerCutoff;
    }

    private static double EvaluateRemnantDensity(DeepSkySimulationParameters parameters, double q)
    {
        var rShell = parameters.InnerShellRadiusFraction;
        const double shellWidth = 0.13;
        var shell = Math.Exp(-0.5 * Math.Pow((q - rShell) / shellWidth, 2));
        var interior = 0.10 * Math.Max(0d, 1d - q * q);
        var outerCutoff = Smoothstep(Math.Min(1d, (1d - q) / 0.12));
        return (shell + interior) * outerCutoff;
    }

    private static (double SersicIndex, double EffectiveRadiusFraction) ResolveGalaxySersic(string? hubbleType)
    {
        if (string.IsNullOrWhiteSpace(hubbleType))
        {
            return (1.8, 0.35);
        }
        var trimmed = hubbleType.Trim();
        if (trimmed.Length > 0 && trimmed[0] is 'E' or 'e')
        {
            return (3.5, 0.30);
        }
        if (trimmed.StartsWith("S0", StringComparison.OrdinalIgnoreCase))
        {
            return (2.6, 0.32);
        }
        if (trimmed.StartsWith("Sa", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Sb", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("SBa", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("SBb", StringComparison.OrdinalIgnoreCase))
        {
            return (2.0, 0.35);
        }
        return (1.2, 0.40);
    }

    private static DeepSkySpectralFactors ResolveSpectralFactors(
        DeepSkyObject item,
        DeepSkyProfileClass profileClass,
        DeepSkySyntheticSourceOverride? syntheticOverride)
    {
        if (profileClass == DeepSkyProfileClass.DarkNebula)
        {
            return new DeepSkySpectralFactors(
                0d, 0d, 0d, 0d, "dark-nebula-dust-attenuation-v1", DeepSkySpectralOrigin.AttenuationOnly, true);
        }

        if (syntheticOverride?.ColorIndex is { } syntheticBv)
        {
            var factors = Rgb24CompatibilityRenderer.ColorFactors(syntheticBv);
            return new DeepSkySpectralFactors(
                1d, factors[0], factors[1], factors[2], "synthetic-bv-color-factors-v1",
                DeepSkySpectralOrigin.SyntheticScenario, true);
        }

        if (profileClass is DeepSkyProfileClass.Galaxy or DeepSkyProfileClass.GlobularCluster or DeepSkyProfileClass.OpenCluster)
        {
            if (item.BMagnitude is { } bMag && item.VMagnitude is { } vMag)
            {
                var bv = Math.Clamp(bMag - vMag, -0.4, 2.0);
                var factors = Rgb24CompatibilityRenderer.ColorFactors(bv);
                return new DeepSkySpectralFactors(
                    1d, factors[0], factors[1], factors[2], "catalog-bv-color-factors-v1",
                    DeepSkySpectralOrigin.CatalogBMinusV, true);
            }

            var (defaultBv, defaultLabel) = profileClass switch
            {
                DeepSkyProfileClass.Galaxy => (0.75, "galaxy"),
                DeepSkyProfileClass.GlobularCluster => (0.70, "globular-cluster"),
                _ => (0.15, "open-cluster")
            };
            var fallback = Rgb24CompatibilityRenderer.ColorFactors(defaultBv);
            return new DeepSkySpectralFactors(
                1d, fallback[0], fallback[1], fallback[2], $"simulation-default-bv-{defaultLabel}-v1",
                DeepSkySpectralOrigin.SimulationClassDefault, true);
        }

        if (profileClass == DeepSkyProfileClass.PlanetaryNebula)
        {
            // Heuristic OIII/H-alpha emission ratio normalized to green = 1.0 (simulation parameter).
            return new DeepSkySpectralFactors(
                1d, 0.72, 1.0, 0.88, "simulation-oiii-halpha-pn-v1",
                DeepSkySpectralOrigin.SimulationClassDefault, true);
        }

        if (profileClass == DeepSkyProfileClass.SupernovaRemnant)
        {
            return new DeepSkySpectralFactors(
                1d, 1.18, 1.0, 0.72, "simulation-halpha-oiii-snr-v1",
                DeepSkySpectralOrigin.SimulationClassDefault, true);
        }

        if (string.Equals(item.ObjectType, "RfN", StringComparison.Ordinal))
        {
            return new DeepSkySpectralFactors(
                1d, 0.72, 1.0, 1.38, "simulation-reflection-blue-v1",
                DeepSkySpectralOrigin.SimulationClassDefault, true);
        }

        // Diffuse/emission nebula and composite Cl+N (H-alpha + OIII heuristic simulation ratio).
        return new DeepSkySpectralFactors(
            1d, 1.25, 1.0, 0.78, "simulation-hii-emission-v1",
            DeepSkySpectralOrigin.SimulationClassDefault, true);
    }

    private static double Smoothstep(double x)
    {
        var t = Math.Clamp(x, 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    internal static ulong Mix64(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
