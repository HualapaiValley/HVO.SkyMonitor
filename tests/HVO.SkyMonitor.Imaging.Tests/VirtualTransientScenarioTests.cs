using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class VirtualTransientScenarioTests
{
    private static readonly DateTimeOffset Epoch = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public void Definition_ValidatesBoundsAndCanonicalIdentityExcludesCallerLabel()
    {
        var definition = CreateDefinition();
        definition.ValidateSensorBounds(16, 16);

        var first = definition.ComputeCanonicalScenarioId();
        var renamed = definition with { ScenarioId = "operator-label" };

        Assert.AreEqual(first, renamed.ComputeCanonicalScenarioId());
        Assert.AreNotEqual(first, (definition with { Seed = definition.Seed + 1 }).ComputeCanonicalScenarioId());
        Assert.AreEqual(64, definition.ComputeParametersSha256().Length);
        Assert.ThrowsExactly<ArgumentException>(() => (definition with
        {
            SkyTracks = [definition.SkyTracks[0], definition.SkyTracks[0]]
        }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (definition with
        {
            SkyTracks = [definition.SkyTracks[0] with
            {
                Keyframes = [
                    definition.SkyTracks[0].Keyframes[0],
                    definition.SkyTracks[0].Keyframes[1] with { OffsetSeconds = 0 }
                ]
            }]
        }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (definition with
        {
            SensorTracks =
            [
                definition.SensorTracks[0] with
                {
                    Keyframes = definition.SensorTracks[0].Keyframes.Select(
                        static keyframe => keyframe with { PixelX = 16 }).ToArray()
                }
            ]
        }).ValidateSensorBounds(16, 16));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (definition with
        {
            SensorTracks = Enumerable.Range(0, VirtualTransientScenarioDefinition.MaximumPrimitiveCount)
                .Select(index => definition.SensorTracks[0] with { PrimitiveId = $"s-{index:D3}" })
                .ToArray(),
            SkyTracks = definition.SkyTracks
        }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (definition with
        {
            SkyTracks = Enumerable.Range(0, 9).Select(index => definition.SkyTracks[0] with
            {
                PrimitiveId = $"p-{index:D3}",
                Keyframes = Enumerable.Range(0, 128).Select(keyframe =>
                    definition.SkyTracks[0].Keyframes[0] with
                    {
                        OffsetSeconds = keyframe,
                        AzimuthDegrees = keyframe
                    }).ToArray()
            }).ToArray(),
            SensorTracks = []
        }).Validate());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Recurrence_ProducesRestartStableBoundedScheduleAndOpaqueIdentities()
    {
        var definition = CreateRecurringDefinition();
        var first = new VirtualTransientScenario(definition);
        var second = new VirtualTransientScenario(definition with { ScenarioId = "renamed-by-operator" });

        var firstEvents = first.EnumerateEvents(Epoch, TimeSpan.FromMinutes(10));
        var secondEvents = second.EnumerateEvents(Epoch, TimeSpan.FromMinutes(10));

        CollectionAssert.AreEqual(firstEvents.ToArray(), secondEvents.ToArray());
        Assert.HasCount(16, firstEvents);
        Assert.IsTrue(firstEvents.All(static item => item.EventId.StartsWith("evt-", StringComparison.Ordinal)));
        Assert.IsFalse(firstEvents.Any(static item =>
            item.EventId.Contains("meteor", StringComparison.OrdinalIgnoreCase) ||
            item.EventId.Contains("fireball", StringComparison.OrdinalIgnoreCase)));
        for (var index = 1; index < firstEvents.Count; index++)
        {
            var interval = firstEvents[index].AnchorUtc - firstEvents[index - 1].AnchorUtc;
            Assert.IsTrue(interval >= TimeSpan.FromSeconds(20));
            Assert.IsTrue(interval <= TimeSpan.FromSeconds(40));
        }
        Assert.AreNotEqual(
            firstEvents[0].EventId,
            new VirtualTransientScenario(definition with { Seed = definition.Seed + 1 })
                .EnumerateEvents(Epoch, TimeSpan.FromMinutes(10))[0].EventId);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SignalRenderer_RecurringPixelsAreStableAcrossRestartAndQueryOrder()
    {
        var definition = CreateRecurringDefinition();
        var firstScenario = new VirtualTransientScenario(definition);
        var target = firstScenario.EnumerateEvents(Epoch, TimeSpan.FromMinutes(10))[3];
        var scene = await SceneTestFactory.CreateEmptyAsync(32, 32, 5).ConfigureAwait(false);
        var layout = new ImageLayout(32, 32, CameraPixelFormat.Mono16, 64);

        _ = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(firstScenario, target.AnchorUtc.AddMinutes(1), TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        var first = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(firstScenario, target.AnchorUtc, TimeSpan.FromSeconds(2)),
            10_000, 0.6, 3);
        var second = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(new VirtualTransientScenario(definition), target.AnchorUtc, TimeSpan.FromSeconds(2)),
            10_000, 0.6, 3);

        CollectionAssert.AreEqual(first.Pixels.ToArray(), second.Pixels.ToArray());
        CollectionAssert.AreEqual(first.Geometry.ToArray(), second.Geometry.ToArray());
        Assert.IsTrue(first.ActivePixelCount > 0);
        Assert.StartsWith("evt-", first.Geometry[0].PrimitiveId, StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Recurrence_RejectsUnboundedAndMalformedDefinitions()
    {
        var definition = CreateRecurringDefinition();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (definition with
        {
            Recurrence = definition.Recurrence! with { MinimumIntervalSeconds = 19 }
        }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (definition with
        {
            Recurrence = definition.Recurrence! with
            {
                Profiles = [definition.Recurrence.Profiles[0] with
                {
                    SkyTracks = [definition.Recurrence.Profiles[0].SkyTracks[0] with
                    {
                        Keyframes =
                        [
                            definition.Recurrence.Profiles[0].SkyTracks[0].Keyframes[0] with { OffsetSeconds = 1 },
                            definition.Recurrence.Profiles[0].SkyTracks[0].Keyframes[1]
                        ]
                    }]
                }]
            }
        }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (definition with
        {
            Recurrence = definition.Recurrence! with
            {
                MinimumIntervalSeconds = 20,
                MaximumIntervalSeconds = 20,
                EventCount = 100,
                MaximumLookaheadSeconds = 604_800,
                Profiles = [definition.Recurrence.Profiles[0] with
                {
                    SkyTracks = Enumerable.Range(0, 4).Select(index =>
                        definition.Recurrence.Profiles[0].SkyTracks[0] with { PrimitiveId = $"p-{index}" }).ToArray()
                }]
            }
        }).Validate());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SignalRenderer_ClipsExposureAndSeparatesOpticalFromSensorSignal()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(16, 16, 5).ConfigureAwait(false);
        var layout = new ImageLayout(16, 16, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 32);
        var scenario = new VirtualTransientScenario(CreateDefinition());

        var first = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        var second = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch.AddSeconds(1), TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        var absent = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch.AddSeconds(2), TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        Assert.IsTrue(first.ActivePixelCount > 0);
        Assert.IsTrue(second.ActivePixelCount > 0);
        Assert.AreEqual(0, absent.ActivePixelCount);
        Assert.IsTrue(first.Geometry[0].DepositedCentroid!.Value.X < second.Geometry[0].DepositedCentroid!.Value.X);
        Assert.IsTrue(first.Pixels[0].SensorElectrons > 0, "Sensor-stage charge must bypass the optical aperture.");
        Assert.AreEqual(0, first.Pixels[0].RedSkyElectrons);
        Assert.ThrowsExactly<OperationCanceledException>(() => VirtualTransientSignalRenderer.Render(
            scene,
            layout,
            new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(1)),
            10_000,
            0.6,
            3,
            cancellationToken: cancellation.Token));
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task Renderers_ApplyCloudToSkyButNotSensorChargeAcrossFormats(CameraPixelFormat format)
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(16, 16, 5).ConfigureAwait(false);
        var scenario = new VirtualTransientScenario(CreateDefinition());
        var clear = new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(1));
        var cloudDefinition = new VirtualCloudScenarioDefinition
        {
            EpochUtc = Epoch,
            HorizonFadeDegrees = 0,
            TemporalSampleCount = 1,
            Keyframes = [new VirtualCloudKeyframe { Coverage = 1, MaximumOpacity = 1 }]
        };
        var cloud = new VirtualCloudRenderContext(new VirtualCloudField(cloudDefinition), Epoch, TimeSpan.FromSeconds(1));
        var layout = new ImageLayout(16, 16, format, 16 * ImageLayout.BytesPerPixel(format));

        var clearResult = Render(scene, layout, clear, null);
        var obscuredResult = Render(scene, layout, clear, cloud);

        Assert.Contains(VirtualTransientScenarioDefinition.CurrentAlgorithmVersion, clearResult.AlgorithmVersion);
        Assert.IsTrue(clearResult.Statistics.Mean > obscuredResult.Statistics.Mean);
        var sensorPixelBytes = ImageLayout.BytesPerPixel(format);
        CollectionAssert.AreEqual(
            clearResult.Pixels[..sensorPixelBytes].ToArray(),
            obscuredResult.Pixels[..sensorPixelBytes].ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SignalRenderer_UsesHalfOpenIntervalsAndConservesAdjacentExposureEnergy()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(16, 16, 5).ConfigureAwait(false);
        var layout = new ImageLayout(16, 16, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 32);
        var definition = CreateDefinition() with { SensorTracks = [] };
        var scenario = new VirtualTransientScenario(definition);
        var first = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        var second = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch.AddSeconds(1), TimeSpan.FromSeconds(1)),
            10_000, 0.6, 3);
        var whole = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(2)),
            10_000, 0.6, 3);
        var atEnd = VirtualTransientSignalRenderer.Render(
            scene, layout, new VirtualTransientRenderContext(scenario, Epoch.AddSeconds(2), TimeSpan.FromTicks(1)),
            10_000, 0.6, 3);

        Assert.AreEqual(0, atEnd.ActivePixelCount);
        Assert.IsTrue(first.Geometry[0].DepositedCentroid!.Value.X < second.Geometry[0].DepositedCentroid!.Value.X);
        Assert.AreEqual(
            whole.Geometry[0].ExpectedElectrons,
            first.Geometry[0].ExpectedElectrons + second.Geometry[0].ExpectedElectrons,
            1e-9);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SignalRenderer_ReportsTheInvalidTransientRenderParameter()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(16, 16, 5).ConfigureAwait(false);
        var transient = new VirtualTransientRenderContext(new VirtualTransientScenario(CreateDefinition()), Epoch, TimeSpan.FromSeconds(1));
        var layout = new ImageLayout(16, 16, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 32);
        var invalidSigma = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualTransientSignalRenderer.Render(scene, layout, transient, 10_000, 0, 3));
        var invalidRadius = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualTransientSignalRenderer.Render(scene, layout, transient, 10_000, 0.6, 0));
        var mismatchedLayout = Assert.ThrowsExactly<ArgumentException>(() =>
            VirtualTransientSignalRenderer.Render(
                scene,
                new ImageLayout(8, 16, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 16),
                transient,
                10_000,
                0.6,
                3));

        Assert.AreEqual("minimumPsfSigmaPixels", invalidSigma.ParamName);
        Assert.AreEqual("minimumPsfRadiusPixels", invalidRadius.ParamName);
        Assert.AreEqual("layout", mismatchedLayout.ParamName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Renderers_RejectMismatchedScenarioIntervals()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(16, 16, 5).ConfigureAwait(false);
        var scenario = new VirtualTransientScenario(CreateDefinition());
        var layout = new ImageLayout(16, 16, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 32);
        var options = Options(new VirtualTransientRenderContext(scenario, Epoch, TimeSpan.FromSeconds(2)));

        Assert.ThrowsExactly<ArgumentException>(() => Mono16SceneRenderer.Render(scene, layout, options));
    }

    private static Mono16SceneRenderOptions Options(VirtualTransientRenderContext transient) => new()
    {
        ExposureSeconds = 1,
        MagnitudeZeroElectronsPerSecond = 10_000,
        PsfSigmaPixels = 0.6,
        PsfRadiusPixels = 3,
        Transient = transient
    };

    private static SceneRenderResult Render(
        VisibleScene scene,
        ImageLayout layout,
        VirtualTransientRenderContext transient,
        VirtualCloudRenderContext? cloud)
        => layout.PixelFormat switch
        {
            CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(
                scene, layout, Options(transient) with { Cloud = cloud }),
            CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(
                scene, layout, new Rgb24CompatibilityRenderOptions
                {
                    ExposureSeconds = 1,
                    MagnitudeZeroElectronsPerSecond = 10_000,
                    PsfSigmaPixels = 0.6,
                    PsfRadiusPixels = 3,
                    Transient = transient,
                    Cloud = cloud
                }),
            CameraPixelFormat.BayerRggb16 => BayerRggb16Renderer.Render(
                scene, layout, new BayerRggb16RenderOptions
                {
                    ExposureSeconds = 1,
                    MagnitudeZeroElectronsPerSecond = 10_000,
                    PsfSigmaPixels = 0.6,
                    PsfRadiusPixels = 3,
                    Transient = transient,
                    Cloud = cloud,
                    SensorResponse = new MonoSensorResponse
                    {
                        AdcBitDepth = 16,
                        FullWellElectrons = 100_000,
                        ElectronsPerAdu = 1,
                        ReadNoiseElectrons = 0,
                        BlackLevelAdu = 0
                    }
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };

    private static VirtualTransientScenarioDefinition CreateDefinition() => new()
    {
        ScenarioId = "fixture-scenario",
        EpochUtc = Epoch,
        TemporalSampleCount = 8,
        SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "p-001",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 0,
                        AltitudeDegrees = 80,
                        AzimuthDegrees = 270,
                        Magnitude = -2,
                        AngularWidthDegrees = 0.2
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 2,
                        AltitudeDegrees = 80,
                        AzimuthDegrees = 90,
                        Magnitude = -2,
                        AngularWidthDegrees = 0.2
                    }
                ]
            }
        ],
        SensorTracks =
        [
            new VirtualTransientSensorTrack
            {
                PrimitiveId = "s-001",
                Keyframes =
                [
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 0,
                        PixelX = 0.5,
                        PixelY = 0.5,
                        ElectronsPerSecond = 10_000
                    },
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 2,
                        PixelX = 0.5,
                        PixelY = 0.5,
                        ElectronsPerSecond = 10_000
                    }
                ]
            }
        ]
    };

    private static VirtualTransientScenarioDefinition CreateRecurringDefinition() => new()
    {
        ScenarioId = "recurring-fixture",
        Seed = 331,
        EpochUtc = Epoch,
        TemporalSampleCount = 4,
        Recurrence = new VirtualTransientRecurrenceDefinition
        {
            MinimumIntervalSeconds = 20,
            MaximumIntervalSeconds = 40,
            EventCount = 16,
            MaximumLookaheadSeconds = 600,
            Profiles =
            [
                new VirtualTransientRecurringProfile
                {
                    Weight = 3,
                    SkyTracks =
                    [
                        new VirtualTransientSkyTrack
                        {
                            PrimitiveId = "p-001",
                            Keyframes =
                            [
                                new VirtualTransientSkyKeyframe
                                {
                                    OffsetSeconds = 0,
                                    AltitudeDegrees = 80,
                                    AzimuthDegrees = 340,
                                    Magnitude = -2,
                                    AngularWidthDegrees = 0.2
                                },
                                new VirtualTransientSkyKeyframe
                                {
                                    OffsetSeconds = 2,
                                    AltitudeDegrees = 80,
                                    AzimuthDegrees = 20,
                                    Magnitude = -2,
                                    AngularWidthDegrees = 0.2
                                }
                            ]
                        }
                    ]
                },
                new VirtualTransientRecurringProfile
                {
                    Weight = 1,
                    SkyTracks =
                    [
                        new VirtualTransientSkyTrack
                        {
                            PrimitiveId = "p-002",
                            Keyframes =
                            [
                                new VirtualTransientSkyKeyframe
                                {
                                    OffsetSeconds = 0,
                                    AltitudeDegrees = 75,
                                    AzimuthDegrees = 250,
                                    Magnitude = -8,
                                    AngularWidthDegrees = 0.35
                                },
                                new VirtualTransientSkyKeyframe
                                {
                                    OffsetSeconds = 3,
                                    AltitudeDegrees = 78,
                                    AzimuthDegrees = 290,
                                    Magnitude = -6,
                                    AngularWidthDegrees = 0.3
                                }
                            ]
                        }
                    ]
                }
            ]
        }
    };
}
