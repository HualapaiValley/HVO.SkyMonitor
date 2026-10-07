using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;

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
        VirtualLongExposureVariant Variant, VirtualRenderRefusal? Expected, int? ExpectedTemporalSamples);

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
        List<Probe> probes =
        [
            // The measured boundary: the last whole second that fits 64 slots, and the first that does not.
            new("rectilinear-boundary-fits", rectilinear, rectilinearNative, 56, none, null, 64),
            new("rectilinear-boundary-exceeds", rectilinear, rectilinearNative, 57, none, budget, null),
            .. VirtualAstrometryFixture.Profiles(VirtualAstrometryFixture.Seed(110220, none), rectilinear, none).Select(profile =>
                new Probe($"rectilinear-60s-{profile.Name}", rectilinear, profile, 60, none,
                    VirtualAstrometryFixture.DeclaredRefusal(rectilinear, profile, 60, none), null)),
            new("equidistant-60s-mono-native", equidistant, View(equidistant, none, "mono-native"), 60, none, null, null),
            new("equidistant-60s-cfa-native", equidistant, View(equidistant, none, "cfa-native"), 60, none, null, null),
            new("equidistant-clouds-mono-native", equidistant, View(equidistant, clouds, "mono-native"), 60, clouds,
                VirtualAstrometryFixture.DeclaredRefusal(equidistant, View(equidistant, clouds, "mono-native"), 60, clouds), null),
            new("equidistant-clouds-cfa-native", equidistant, View(equidistant, clouds, "cfa-native"), 60, clouds, null, null)
        ];
        Assert.IsNotNull(probes.Single(p => p.Name == "equidistant-clouds-mono-native").Expected, "Mono clouds must be a declared refusal.");
        Assert.IsTrue(probes.Any(p => p.Name.StartsWith("rectilinear-60s-", StringComparison.Ordinal) && p.Expected is null),
            "The rectilinear ROI views must remain supported at 60 s.");
        var rows = new List<object>(); var failures = new List<string>();
        foreach (var probe in probes)
            rows.Add(await RunProbeAsync(snapshot, probe, failures).ConfigureAwait(false));
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
            probes = rows,
            failures
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Initializes and captures one probe. Only the probe's own declared refusal, at its declared stage, is caught.</summary>
    private static async Task<object> RunProbeAsync(CatalogSnapshotResult snapshot, Probe probe, List<string> failures)
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
        var samples = Value("stellarTemporalSamples") is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : (int?)null;
        if (captured is not null && samples is null) failures.Add($"{probe.Name}: rendered frame reports no stellar temporal samples");
        if (probe.ExpectedTemporalSamples is { } expected && samples != expected)
            failures.Add($"{probe.Name}: expected {expected} temporal samples, rendered {samples?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
        var readout = probe.Profile.Config.Rig.Readout;
        return new
        {
            probe = probe.Name,
            family = probe.Family.Name,
            view = probe.Profile.Name,
            readout = readout is null ? null : new { readout.Roi, readout.BinX, readout.BinY },
            probe.ExposureSeconds,
            variant = probe.Variant.Name,
            expected = probe.Expected,
            probe.ExpectedTemporalSamples,
            outcome = refused is not null ? "refused" : "rendered",
            refusal = refused is null ? null : new { stage, type = refused.GetType().FullName, refused.Message },
            temporalSamples = samples,
            maximumTemporalSamples = Value("stellarMaximumTemporalSamples"),
            maximumSampleMotionPixels = Value("stellarMaximumSampleMotionPixels"),
            admittedSources = Value("stellarAdmittedCount"),
            supportedSources = Value("stellarSupportedCount"),
            elapsedMs
        };
    }
}
