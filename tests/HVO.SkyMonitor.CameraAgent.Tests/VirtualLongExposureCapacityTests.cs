using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Issue #1168 renderer capacity boundary evidence. The rectilinear 6 mm full-frame views exhaust the renderer's 64
/// temporal slots between 56 s and 57 s; every frozen refusal is exercised by its exact type and message, and every
/// supported 60 s native view renders. A refusal that is not predeclared, or a predeclared one that does not occur, fails.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualLongExposureCapacityTests
{
    private const string TemporalBudgetExceeded = "stellar-exposure-temporal-budget-exceeded";
    private static readonly DateTimeOffset StartUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    private sealed record Probe(string Name, VirtualProjectionFamily Family, VirtualAstrometryProfile Profile, int ExposureSeconds,
        VirtualLongExposureVariant Variant, VirtualRenderRefusal? Expected);

    [TestMethod]
    public async Task RendererCapacityBoundaries()
    {
        VirtualAstrometryQualificationTests.RequireRevision();
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var none = VirtualAstrometryFixture.Variants[0];
        var clouds = VirtualAstrometryFixture.Variants.Single(v => v.Name == "clouds-partial");
        var equidistant = VirtualAstrometryFixture.Families.Single(f => f.Name == "equidistant");
        var rectilinear = VirtualAstrometryFixture.Families.Single(f => f.Name == "rectilinear");
        VirtualAstrometryProfile View(VirtualProjectionFamily family, VirtualLongExposureVariant variant, string name) =>
            VirtualAstrometryFixture.Profiles(VirtualAstrometryFixture.Seed(110220, variant), family, variant).Single(p => p.Name == name);
        var budget = new VirtualRenderRefusal("capture", typeof(InvalidOperationException).FullName!, TemporalBudgetExceeded);
        var rectilinearNative = View(rectilinear, none, "mono-native");
        var renderer = new VirtualSkyCameraModuleOptions();
        // The renderer's step: its configured bound, tightened only by a PSF too narrow to resolve it.
        var stepPixels = Math.Min(renderer.MaximumStellarStepPixels,
            TemporalPointSpreadRaster.MaximumTemporalStepPixels(VirtualAstrometryFixture.PsfSigmaPixels, VirtualAstrometryFixture.PsfRadiusPixels));
        List<Probe> probes =
        [
            // The measured boundary: the last whole second that fits 64 slots, and the first that does not.
            new("rectilinear-boundary-fits", rectilinear, rectilinearNative, 56, none, null),
            new("rectilinear-boundary-exceeds", rectilinear, rectilinearNative, 57, none, budget),
            .. VirtualAstrometryFixture.Profiles(VirtualAstrometryFixture.Seed(110220, none), rectilinear, none).Select(profile =>
                new Probe($"rectilinear-60s-{profile.Name}", rectilinear, profile, 60, none,
                    VirtualAstrometryFixture.DeclaredRefusal(rectilinear, profile, 60, none))),
            new("equidistant-60s-mono-native", equidistant, View(equidistant, none, "mono-native"), 60, none, null),
            new("equidistant-60s-cfa-native", equidistant, View(equidistant, none, "cfa-native"), 60, none, null),
            new("equidistant-clouds-mono-native", equidistant, View(equidistant, clouds, "mono-native"), 60, clouds,
                VirtualAstrometryFixture.DeclaredRefusal(equidistant, View(equidistant, clouds, "mono-native"), 60, clouds)),
            new("equidistant-clouds-cfa-native", equidistant, View(equidistant, clouds, "cfa-native"), 60, clouds, null)
        ];
        Assert.IsNotNull(probes.Single(p => p.Name == "equidistant-clouds-mono-native").Expected, "Mono clouds must be a declared refusal.");
        Assert.IsTrue(probes.Any(p => p.Name.StartsWith("rectilinear-60s-", StringComparison.Ordinal) && p.Expected is null),
            "The rectilinear ROI views must remain supported at 60 s.");
        var rows = new List<object>(); var failures = new List<string>(); var outcomes = new Dictionary<string, (bool Refused, double? Motion)>();
        foreach (var probe in probes)
        {
            var (row, refused, sampleMotion) = await RunProbeAsync(snapshot, probe, failures).ConfigureAwait(false);
            rows.Add(row); outcomes[probe.Name] = (refused, sampleMotion);
        }
        var fits = outcomes["rectilinear-boundary-fits"]; var exceeds = outcomes["rectilinear-boundary-exceeds"];
        var derived = !fits.Refused && exceeds.Refused && fits.Motion is { } motion
            ? BoundarySlots(TimeSpan.FromSeconds(56), motion, TimeSpan.FromSeconds(57), stepPixels, renderer.MaximumStellarSamples) : null;
        if (derived?.Slots != renderer.MaximumStellarSamples)
            failures.Add($"the 56 s render and 57 s refusal must derive exactly {renderer.MaximumStellarSamples} slots at 56 s");
        // The slot count each rectilinear full-frame exposure needs at the boundary view's derived speed bound.
        int? Required(int seconds) => derived is { } d ? (int)Math.Ceiling(seconds * d.SpeedBoundPixelsPerSecond / stepPixels) : null;
        var boundary = new
        {
            view = rectilinearNative.Name,
            stepPixels,
            maximumSlots = renderer.MaximumStellarSamples,
            derivedSlotsAt56Seconds = derived?.Slots,
            derivedSpeedBoundPixelsPerSecond = derived?.SpeedBoundPixelsPerSecond,
            slotsRequiredAt57Seconds = Required(57),
            slotsRequiredAt60Seconds = Required(60),
            derivedMaximumExposureSeconds = derived is { } b ? renderer.MaximumStellarSamples * stepPixels / b.SpeedBoundPixelsPerSecond : (double?)null,
            measuredLastRenderedSeconds = fits.Refused ? (int?)null : 56,
            measuredFirstRefusedSeconds = exceeds.Refused ? 57 : (int?)null
        };
        var resultRoot = Path.Combine(TestContext.TestRunDirectory!, "capacity"); Directory.CreateDirectory(resultRoot);
        var path = Path.Combine(resultRoot, "virtual-long-exposure-capacity.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-long-exposure-capacity-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            startUtc = StartUtc,
            gain = 150,
            slotDerivation = "The renderer reports stellarMaximumSampleMotionPixels = speedBound * ceil(exposureTicks / slots) / TicksPerSecond, " +
                "not its per-source slot count (stellarTemporalSamples totals every source's samples). The 56 s render and the 57 s " +
                "budget refusal admit exactly one slot count under slots = ceil(exposureSeconds * speedBound / stepPixels).",
            rectilinearBoundary = boundary,
            probes = rows,
            failures
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The renderer reports its per-slot motion bound, <c>speedBound * ceil(exposureTicks / slots) / TicksPerSecond</c>, but not
    /// its per-source slot count, and that bound alone is consistent with every slot count below <c>1 / (1 - motion / step)</c>.
    /// A refusal of a slightly longer exposure removes the ambiguity: this returns the one slot count, and its speed bound,
    /// under which <paramref name="fitted"/> fits <c>slots = ceil(exposure * speedBound / step)</c> and
    /// <paramref name="refused"/> needs more than <paramref name="maximumSlots"/>; null when none or several do.
    /// </summary>
    internal static (int Slots, double SpeedBoundPixelsPerSecond)? BoundarySlots(TimeSpan fitted, double fittedMotionPixels,
        TimeSpan refused, double stepPixels, int maximumSlots)
    {
        // Re-deriving the speed bound from its reported product is exact to a few ulps; a requirement within this band of an
        // integer cannot be resolved and yields two candidates, never a wrong one.
        const double Tolerance = 1e-9;
        var candidates = new List<(int Slots, double SpeedBoundPixelsPerSecond)>();
        for (var slots = 1; slots <= maximumSlots; slots++)
        {
            var speed = fittedMotionPixels * TimeSpan.TicksPerSecond / ((fitted.Ticks + slots - 1) / slots);
            var required = fitted.TotalSeconds * speed / stepPixels;
            if (required > slots + Tolerance || required <= slots - 1 - Tolerance ||
                refused.TotalSeconds * speed / stepPixels <= maximumSlots + Tolerance) continue;
            candidates.Add((slots, speed));
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>Initializes and captures one probe. Only the probe's own declared refusal, at its declared stage, is caught.</summary>
    private static async Task<(object Row, bool Refused, double? MotionPixels)> RunProbeAsync(CatalogSnapshotResult snapshot, Probe probe,
        List<string> failures)
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        var stage = "initialize"; var clock = Stopwatch.StartNew();
        CaptureResult? captured = null; Exception? refused = null;
        try
        {
            await module.InitializeAsync(probe.Profile.Config, CancellationToken.None).ConfigureAwait(false);
            stage = "capture";
            captured = await module.CaptureAsync(VirtualAstrometryFixture.Request(StartUtc, TimeSpan.FromSeconds(probe.ExposureSeconds)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (probe.Expected is { } declared && declared.Stage == stage && declared.Matches(ex))
        {
            refused = ex;
        }
        var elapsedMs = clock.Elapsed.TotalMilliseconds;
        if (probe.Expected is not null && refused is null)
            failures.Add($"{probe.Name}: declared {probe.Expected.Stage} refusal did not occur");
        var extra = captured?.Frame?.Metadata.Extra;
        string? Value(string key) => extra is not null && extra.TryGetValue(key, out var value) ? value : null;
        var motion = Value("stellarMaximumSampleMotionPixels") is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : (double?)null;
        if (captured is not null && motion is null) failures.Add($"{probe.Name}: rendered frame reports no stellar sample motion");
        var readout = probe.Profile.Config.Rig.Readout;
        return (new
        {
            probe = probe.Name,
            family = probe.Family.Name,
            view = probe.Profile.Name,
            readout = readout is null ? null : new { readout.Roi, readout.BinX, readout.BinY },
            probe.ExposureSeconds,
            variant = probe.Variant.Name,
            expected = probe.Expected,
            outcome = refused is not null ? "refused" : "rendered",
            refusal = refused is null ? null : new { stage, type = refused.GetType().FullName, refused.Message },
            maximumTemporalSamples = Value("stellarMaximumTemporalSamples"),
            maximumSampleMotionPixels = Value("stellarMaximumSampleMotionPixels"),
            totalTemporalSamples = Value("stellarTemporalSamples"),
            admittedSources = Value("stellarAdmittedCount"),
            supportedSources = Value("stellarSupportedCount"),
            elapsedMs
        }, refused is not null, motion);
    }
}
