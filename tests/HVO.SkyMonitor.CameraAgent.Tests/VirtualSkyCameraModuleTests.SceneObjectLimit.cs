using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] SceneLimitConstellationIds = ["TST"];
    private static readonly string[] SceneLimitSolarSystemBodies = ["Moon", "Jupiter"];
    private static readonly string[] SceneLimitAlwaysPresentIds =
        ["hip-901", "hip-902", "solar-system:Jupiter", "solar-system:Moon", "star-000"];

    [TestMethod]
    public async Task SceneObjectLimit_CatalogLargerThanTheRigBudgetCapturesTheBrightestSupportedStars()
    {
        // The installer rig declares maximumResults 2000 while a night query selects 8,920 HYG stars; this is the
        // same shape at fixture scale. The exhaustive opt-out keeps refusing, which is how every capture failed.
        var catalog = CreateSceneLimitCatalog(40);
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10), CancellationToken.None).ConfigureAwait(false);

        var frame = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;

        var extra = frame.Metadata.Extra!;
        Assert.AreEqual("10", extra["stellarMaximumCandidates"]);
        Assert.AreEqual(nameof(StellarCandidateSelection.BrightestSupported), extra["stellarCandidateSelection"]);
        Assert.AreEqual("40", extra["stellarCandidateCount"]);
        Assert.AreEqual("10", extra["stellarSupportedCount"]);
        Assert.AreEqual("10", extra["stellarAdmittedCount"]);
        CollectionAssert.AreEquivalent(SceneLimitStarIds(10), frame.Metadata.Scene!.Objects!.Select(static item => item.Id).ToArray());

        var exhaustive = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await using var exhaustiveLifetime = exhaustive.ConfigureAwait(false);
        await exhaustive.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10, exhaustive: true), CancellationToken.None)
            .ConfigureAwait(false);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await exhaustive.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(refusal.Message, "candidate-budget-exceeded", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SceneObjectLimit_OperatorChangeAppliesToTheNextCaptureInEitherDirection()
    {
        var bound = new MutableSceneObjectBound(3);
        var module = new VirtualSkyCameraModule(TimeProvider.System, CreateSceneLimitCatalog(40), new ProjectedSceneStore(),
            sceneObjectBound: bound);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10), CancellationToken.None).ConfigureAwait(false);

        var lowered = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;
        // Raised above the rig's own maximumResults: the operator's value governs.
        bound.Value = 20;
        var raised = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;
        bound.Value = 3;
        var restored = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual(3, bound.Reads, "The bound is read once per capture.");
        Assert.AreEqual("3", lowered.Metadata.Extra!["stellarMaximumCandidates"]);
        Assert.AreEqual("3", lowered.Metadata.Extra["stellarSupportedCount"]);
        CollectionAssert.AreEquivalent(SceneLimitStarIds(3), lowered.Metadata.Scene!.Objects!.Select(static item => item.Id).ToArray());
        Assert.AreEqual("20", raised.Metadata.Extra!["stellarMaximumCandidates"]);
        Assert.AreEqual("20", raised.Metadata.Extra["stellarSupportedCount"]);
        CollectionAssert.AreEquivalent(SceneLimitStarIds(20), raised.Metadata.Scene!.Objects!.Select(static item => item.Id).ToArray());
        // The limit is scene content, bound through the catalog query: equal limits give equal scene identities.
        Assert.AreNotEqual(lowered.Metadata.Scene.SceneId, raised.Metadata.Scene.SceneId);
        Assert.AreEqual(lowered.Metadata.Scene.SceneId, restored.Metadata.Scene!.SceneId);
        Assert.AreEqual(lowered.Metadata.Extra["stellarPredictionsSha256"], restored.Metadata.Extra!["stellarPredictionsSha256"]);
    }

    [TestMethod]
    public async Task SceneObjectLimit_AlwaysAddsConstellationFigureStarsAndSolarSystemBodies()
    {
        var zenith = SceneLimitZenith();
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Moon] = new(new(zenith.RightAscensionHours + .3, zenith.DeclinationDegrees - 3), -12),
            [SolarSystemBody.Jupiter] = new(new(zenith.RightAscensionHours - .3, zenith.DeclinationDegrees + 3), -2)
        });
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "901", "902")]);
        var module = new VirtualSkyCameraModule(TimeProvider.System, CreateSceneLimitCatalog(40, figureStars: true),
            new ProjectedSceneStore(), topology, ephemeris, sceneObjectBound: new MutableSceneObjectBound(1));
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10, figures: true), CancellationToken.None)
            .ConfigureAwait(false);

        var frame = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;

        Assert.AreEqual("1", frame.Metadata.Extra!["stellarMaximumCandidates"]);
        Assert.AreEqual("3", frame.Metadata.Extra["stellarSupportedCount"], "One ranked star plus both figure endpoints.");
        CollectionAssert.AreEquivalent(SceneLimitAlwaysPresentIds,
            frame.Metadata.Scene!.Objects!.Select(static item => item.Id).ToArray());
        Assert.IsNotEmpty(frame.Metadata.Scene.Segments!);
        Assert.IsTrue(frame.Metadata.Scene.Segments!.All(static segment =>
            segment.ConstellationId == "TST" && segment.FromObjectId == "hip-901" && segment.ToObjectId == "hip-902"));
    }

    [TestMethod]
    public async Task SceneObjectLimit_WithinTheLimitMatchesTheExhaustiveCaptureAndIdentity()
    {
        var catalog = CreateSceneLimitCatalog(8);
        async Task<CameraFrame> CaptureAsync(bool exhaustive, ISceneObjectBound? bound)
        {
            var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore(),
                sceneObjectBound: bound);
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10, exhaustive), CancellationToken.None)
                .ConfigureAwait(false);
            return (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;
        }

        var ranked = await CaptureAsync(exhaustive: false, bound: null).ConfigureAwait(false);
        var exhaustive = await CaptureAsync(exhaustive: true, bound: null).ConfigureAwait(false);
        var exhaustiveIgnoringBound = await CaptureAsync(exhaustive: true, new MutableSceneObjectBound(3)).ConfigureAwait(false);
        var operatorAtQuery = await CaptureAsync(exhaustive: false, new MutableSceneObjectBound(8)).ConfigureAwait(false);

        Assert.AreEqual(nameof(StellarCandidateSelection.BrightestSupported), ranked.Metadata.Extra!["stellarCandidateSelection"]);
        Assert.AreEqual(nameof(StellarCandidateSelection.Exhaustive), exhaustive.Metadata.Extra!["stellarCandidateSelection"]);
        foreach (var frame in new[] { exhaustive, exhaustiveIgnoringBound })
        {
            // The mode is not scene content, so it leaves the identity and every rendered sample unchanged.
            Assert.AreEqual("10", frame.Metadata.Extra!["stellarMaximumCandidates"]);
            Assert.AreEqual(ranked.Metadata.Scene!.SceneId, frame.Metadata.Scene!.SceneId);
            Assert.AreEqual(ranked.Metadata.Extra["stellarPredictionsSha256"], frame.Metadata.Extra["stellarPredictionsSha256"]);
            CollectionAssert.AreEqual(ranked.PixelData.ToArray(), frame.PixelData.ToArray());
        }
        Assert.AreEqual("8", operatorAtQuery.Metadata.Extra!["stellarMaximumCandidates"]);
        Assert.AreEqual(ranked.Metadata.Extra["stellarPredictionsSha256"], operatorAtQuery.Metadata.Extra["stellarPredictionsSha256"]);
        CollectionAssert.AreEqual(ranked.PixelData.ToArray(), operatorAtQuery.PixelData.ToArray());
        Assert.AreNotEqual(ranked.Metadata.Scene!.SceneId, operatorAtQuery.Metadata.Scene!.SceneId);
    }

    [TestMethod]
    public async Task SceneObjectLimit_FactoryResolvesTheBoundOnlyWhenTheHostRegistersIt()
    {
        var catalog = CreateSceneLimitCatalog(40);
        foreach (var (bound, expected) in new (ISceneObjectBound?, string)[] { (null, "10"), (new MutableSceneObjectBound(4), "4") })
        {
            var services = new ServiceCollection()
                .AddSingleton(TimeProvider.System)
                .AddSingleton<ICelestialCatalog>(catalog)
                .AddSingleton<IProjectedSceneStore, ProjectedSceneStore>();
            if (bound is not null) services.AddSingleton(bound);
            using var provider = services.BuildServiceProvider();
            var factory = new CameraModuleFactory(provider,
                [new CameraModuleRegistration("VirtualSky", typeof(VirtualSkyCameraModule))],
                NullLogger<CameraModuleFactory>.Instance);
            var module = factory.Create("VirtualSky");
            try
            {
                await module.InitializeAsync(CreateSceneLimitConfig(maximumResults: 10), CancellationToken.None).ConfigureAwait(false);
                var frame = (await module.CaptureAsync(SceneLimitRequest(), CancellationToken.None).ConfigureAwait(false)).Frame!;
                Assert.AreEqual(expected, frame.Metadata.Extra!["stellarMaximumCandidates"]);
            }
            finally
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    public void SceneObjectLimit_InfrastructureRegistersTheLiveOperatorSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<ISceneObjectBound>();

        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, bound.Current);
        configuration[SkyMapOptions.MaximumObjectsKey] = "900";
        Assert.AreEqual(900, bound.Current);
        configuration[SkyMapOptions.MaximumObjectsKey] = "7";
        Assert.AreEqual(7, bound.Current);
    }

    private static EquatorialPoint SceneLimitZenith()
        => EquatorialPrecession.PrecessToJ2000(
            new EquatorialPoint(AstronomyTime.LocalMeanSiderealDegrees(FixtureUtc, -113.878) / 15d, 35.347), FixtureUtc);

    // Distinct magnitudes near the zenith, brightest first by ID; HIP 901/902 are optional fainter figure stars.
    private static InMemoryCelestialCatalog CreateSceneLimitCatalog(int count, bool figureStars = false)
    {
        var zenith = SceneLimitZenith();
        var stars = Enumerable.Range(0, count).Select(index => new CelestialCatalogObject(
            $"star-{index:000}", $"Star {index}",
            zenith.RightAscensionHours + ((index / 8) - 2) * .3,
            zenith.DeclinationDegrees + ((index % 8) - 3.5) * 4,
            1 + index * .05)).ToList();
        if (figureStars)
        {
            stars.Add(new("hip-901", "Figure A", zenith.RightAscensionHours + .1, zenith.DeclinationDegrees + 2, 4.5, null, "901"));
            stars.Add(new("hip-902", "Figure B", zenith.RightAscensionHours - .1, zenith.DeclinationDegrees - 2, 4.8, null, "902"));
        }
        return new InMemoryCelestialCatalog(stars);
    }

    private static string[] SceneLimitStarIds(int count)
        => Enumerable.Range(0, count).Select(static index => $"star-{index:000}").ToArray();

    private static CaptureRequest SceneLimitRequest()
        => new(FixtureUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));

    private static CameraModuleConfig CreateSceneLimitConfig(int maximumResults, bool exhaustive = false, bool figures = false)
    {
        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["magnitudeZeroElectronsPerSecond"] = 1000000,
            ["maximumMagnitude"] = 6.5,
            ["maximumResults"] = maximumResults,
            ["backgroundElectronsPerSecond"] = 0,
            ["readNoiseStandardDeviation"] = 3
        };
        if (exhaustive) options["stellarCandidateSelection"] = "Exhaustive";
        if (figures)
        {
            options["constellationIds"] = SceneLimitConstellationIds;
            options["includeConstellationEndpointStars"] = true;
            options["solarSystemBodies"] = SceneLimitSolarSystemBodies;
        }
        return CreateConfig(CameraPixelFormat.Mono16, 128, 128) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(options))
        };
    }

    private sealed class MutableSceneObjectBound(int value) : ISceneObjectBound
    {
        public int Value { get; set; } = value;

        public int Reads { get; private set; }

        public int Current
        {
            get
            {
                Reads++;
                return Value;
            }
        }
    }
}
