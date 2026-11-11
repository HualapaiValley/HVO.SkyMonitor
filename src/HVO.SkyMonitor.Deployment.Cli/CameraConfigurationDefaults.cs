using System.Globalization;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// One generation of the installer's camera configuration defaults: what the installer writes into
/// <c>camera-module.json</c> for a set of answers (<see cref="Generate"/>). <see cref="Change"/> says, for an operator, what
/// it changed from the generation before it; a re-run that adopts it lists that in its plan (Refs #1779).
/// <see cref="RequiresImageSupport"/> marks a change the CameraAgent image must support before an instance adopts it,
/// which the image declares with <see cref="CameraConfigurationDefaults.ImageLabel"/>.
/// </summary>
internal sealed record CameraConfigurationGeneration(
    int Number,
    string? Change,
    bool RequiresImageSupport,
    Func<InstallRequest, Guid, GeneratedConfiguration> Generate)
{
    /// <summary>The generation's stable name, which the image label declares.</summary>
    public string Id => CameraConfigurationDefaults.IdPrefix + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The ordered registry of the installer's configuration-default generations (Refs #1779). An installation's records hold
/// the canonical identity of its <c>camera-module.json</c>, not the generation that wrote it, so a re-run recognises the
/// generation by generating the installed answers under each generation it knows and comparing identities
/// (<see cref="Recognize"/>). A completed instance written by an older generation then adopts the current one, and the
/// re-run's plan names each generation's <see cref="CameraConfigurationGeneration.Change"/>; one whose identity no
/// generation produces is refused, because the installer cannot explain it.
/// <para>
/// The manifest records no generation: recognition needs none, and a field older installers do not know would need a
/// compatibility path in every record reader. An older installer that meets a configuration a newer installer adopted
/// recognises no generation for it and refuses the re-run, changing nothing; the newer installer re-runs it.
/// </para>
/// </summary>
internal static class CameraConfigurationDefaults
{
    /// <summary>What every generation's <see cref="CameraConfigurationGeneration.Id"/> starts with.</summary>
    public const string IdPrefix = "installer-defaults-";

    /// <summary>
    /// The CameraAgent image label that declares the newest configuration-default generation the image runs, such as
    /// <c>installer-defaults-2</c>. An image without it declares none.
    /// </summary>
    public const string ImageLabel = "io.hvo.skymonitor.configuration-defaults";

    /// <summary>The lighting a request can name, omitted lighting first (<see cref="InstallRequest.IlluminationMode"/>).</summary>
    private static readonly string?[] IlluminationModes = [null, .. InstallRequest.IlluminationModes];

    /// <summary>
    /// Every generation this installer ships, oldest first. A change to what <see cref="CameraConfiguration.Generate(InstallRequest, Guid)"/>
    /// writes is a new entry at the end, never an edit of an existing one: a shipped generation's output must stay as it
    /// is, or the instances it installed are no longer recognised.
    /// </summary>
    public static IReadOnlyList<CameraConfigurationGeneration> Shipped { get; } =
    [
        // What every release up to and including v0.9.1-dev.11 installed: the eleven-step pipeline.
        new(1, null, false, static (request, identity) => CameraConfiguration.Generate(request, identity, 1)),
        // Refs #1722. Turning the cloud node on for an instance needs an image that records a missing rain environment
        // instead of failing the step (Refs #1777), so the running image must declare this generation first.
        new(2, "pipeline: 11 → 13 steps (adds CloudAssessment, CloudPresentation)", true,
            static (request, identity) => CameraConfiguration.Generate(request, identity, 2))
    ];

    private static readonly AsyncLocal<IReadOnlyList<CameraConfigurationGeneration>?> Substitute = new();

    /// <summary>The generations this installer knows, oldest first.</summary>
    public static IReadOnlyList<CameraConfigurationGeneration> Known => Substitute.Value ?? Shipped;

    /// <summary>The generation this installer writes.</summary>
    public static CameraConfigurationGeneration Current => Known[^1];

    /// <summary>
    /// Replaces the registry for the calling flow until the result is disposed, so a test can install under an earlier
    /// generation, or register a generation of its own on top of the shipped ones. Production never calls it.
    /// </summary>
    internal static IDisposable Use(IReadOnlyList<CameraConfigurationGeneration> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        Validate(generations);
        var previous = Substitute.Value;
        Substitute.Value = generations;
        return new Restore(previous);
    }

    /// <summary>
    /// Requires a registry to be numbered 1, 2, 3 … in order, with a description of what each generation after the first
    /// changed.
    /// </summary>
    internal static void Validate(IReadOnlyList<CameraConfigurationGeneration> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        if (generations.Count == 0)
        {
            throw new ArgumentException("The configuration-default registry is empty.", nameof(generations));
        }
        for (var index = 0; index < generations.Count; index++)
        {
            var generation = generations[index];
            if (generation.Number != index + 1 ||
                (index == 0 ? generation.Change is not null : string.IsNullOrWhiteSpace(generation.Change)))
            {
                throw new ArgumentException(
                    $"Configuration-default generation {index + 1} must be numbered {index + 1} and, after the first, describe its change.",
                    nameof(generations));
            }
        }
    }

    /// <summary>
    /// The generation, and the lighting, whose configuration for <paramref name="installed"/> has the canonical identity
    /// <paramref name="configurationSha256"/> and the given rig profile and schedule identities, or <see langword="null"/>
    /// when no known generation produces it. <paramref name="installed"/> carries the installed answers; its lighting is
    /// ignored, because the configuration itself is what records which lighting was installed. The newest match wins.
    /// </summary>
    public static RecognizedConfiguration? Recognize(
        InstallRequest installed,
        Guid applicationIdentity,
        string configurationSha256,
        string rigProfileSha256,
        string scheduleSha256)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var generations = Known;
        for (var index = generations.Count - 1; index >= 0; index--)
        {
            foreach (var mode in IlluminationModes)
            {
                var generated = generations[index].Generate(installed with { IlluminationMode = mode }, applicationIdentity);
                if (string.Equals(generated.Sha256, configurationSha256, StringComparison.Ordinal) &&
                    string.Equals(generated.RigProfileSha256, rigProfileSha256, StringComparison.Ordinal) &&
                    string.Equals(generated.ScheduleSha256, scheduleSha256, StringComparison.Ordinal))
                {
                    return new RecognizedConfiguration(generations[index], mode, generated);
                }
            }
        }
        return null;
    }

    /// <summary>The generations an instance on <paramref name="retained"/> adopts to reach the current one, oldest first.</summary>
    public static IReadOnlyList<CameraConfigurationGeneration> AdoptedAfter(CameraConfigurationGeneration retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        return [.. Known.Where(generation => generation.Number > retained.Number)];
    }

    /// <summary>
    /// The newest generation an image declares it runs (<see cref="ImageLabel"/>), or 0 when it declares none or a value
    /// that is not a generation's name.
    /// </summary>
    public static int DeclaredByImage(string? label)
        => label is not null && label.StartsWith(IdPrefix, StringComparison.Ordinal) &&
           int.TryParse(label.AsSpan(IdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
           number > 0 && label.Length == IdPrefix.Length + number.ToString(CultureInfo.InvariantCulture).Length
            ? number
            : 0;

    /// <summary>
    /// The generation the running image must declare before an instance adopts <paramref name="adopted"/>, or 0 when
    /// none of them needs the image's support.
    /// </summary>
    public static int RequiredImageGeneration(IReadOnlyList<CameraConfigurationGeneration> adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);
        return adopted.Where(static generation => generation.RequiresImageSupport)
            .Select(static generation => generation.Number)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>What adopting <paramref name="adopted"/> changes, as a re-run's plan lists it.</summary>
    public static string Describe(IReadOnlyList<CameraConfigurationGeneration> adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);
        return string.Join("; ", adopted.Select(static generation => generation.Change));
    }

    private sealed class Restore(IReadOnlyList<CameraConfigurationGeneration>? previous) : IDisposable
    {
        public void Dispose() => Substitute.Value = previous;
    }
}

/// <summary>
/// The configuration-default generation and lighting that produced a recorded configuration identity, with the
/// configuration they generate for the installed answers (<see cref="CameraConfigurationDefaults.Recognize"/>).
/// </summary>
internal sealed record RecognizedConfiguration(
    CameraConfigurationGeneration Generation,
    string? IlluminationMode,
    GeneratedConfiguration Configuration);
