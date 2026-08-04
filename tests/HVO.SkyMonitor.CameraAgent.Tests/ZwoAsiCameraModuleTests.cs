using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Modules.Zwo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class ZwoAsiCameraModuleTests
{
    private const string LibraryEnvironmentVariable = "HVO_ZWO_SDK_LIBRARY";
    private const string SerialEnvironmentVariable = "HVO_ZWO_CAMERA_SERIAL";
    private const string PrivateLibraryPath = "/opt/zwo/private/libASICamera2.so.1.41";
    private static readonly byte[] PrivateSerial = Convert.FromHexString("0011223344556677");
    private static readonly SupportedProfile Asi676Mc = new(
        "asi676mc", "ASI676MC", 3552, 3552, 2.0, 12, 7104, 1, 600,
        "BF774B9E01D4B46BE9441F22AD1EBD276CA86DF19A3B4F9715C1F806E5FABF30");
    private static readonly SupportedProfile Asi178Mc = new(
        "asi178mc", "ASI178MC", 3096, 2080, 2.4, 14, 6192, 10, 510,
        "5E945964BE56D5743484C8FFD4EB7B5019C1D6D746CB3C0922B3B7C73E04C7FD");
    private static readonly SupportedProfile[] SupportedProfiles = [Asi676Mc, Asi178Mc];
    private static readonly JsonSerializerOptions ConfigurationSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string[] ExpectedInitializationCalls =
    [
        "Open:7", "Serial:7", "Close:7", "Open:7", "Init:7", "Caps:7",
        "Set:Exposure:100000", "Get:Exposure", "Set:Gain:0", "Get:Gain",
        "Set:Offset:1", "Get:Offset", "Set:BandwidthOverload:40", "Get:BandwidthOverload",
        "Set:HighSpeedMode:0", "Get:HighSpeedMode", "Set:Flip:0", "Get:Flip",
        "Set:MonoBin:0", "Get:MonoBin", "Roi:3552:3552:1:Raw16", "Position:0:0",
        "Set:HardwareBin:0", "Get:HardwareBin", "GetRoi", "GetPosition"
    ];
    private static readonly string[] Asi178ExpectedInitializationCalls =
    [
        "Open:7", "Serial:7", "Close:7", "Open:7", "Init:7", "Caps:7",
        "Set:Exposure:100000", "Get:Exposure", "Set:Gain:0", "Get:Gain",
        "Set:Offset:10", "Get:Offset", "Set:BandwidthOverload:40", "Get:BandwidthOverload",
        "Set:HighSpeedMode:0", "Get:HighSpeedMode", "Set:Flip:0", "Get:Flip",
        "Set:MonoBin:0", "Get:MonoBin", "Roi:3096:2080:1:Raw16", "Position:0:0",
        "Set:HardwareBin:0", "Get:HardwareBin", "GetRoi", "GetPosition"
    ];

    [TestMethod]
    [TestCategory("Unit")]
    public void AbiLayoutAndAtomicExportBindingAreValidated()
    {
        AsiAbi.Validate();
        Assert.AreEqual(248, Marshal.SizeOf<AsiCameraInfoNative>());
        Assert.AreEqual(264, Marshal.SizeOf<AsiControlCapsNative>());
        Assert.AreEqual(8, Marshal.SizeOf<AsiSerialNumberNative>());
        Assert.AreEqual(72, Marshal.OffsetOf<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.MaxHeight)).ToInt32());
        Assert.AreEqual(192, Marshal.OffsetOf<AsiControlCapsNative>(nameof(AsiControlCapsNative.Maximum)).ToInt32());

        var loader = new FailingLibraryLoader();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => AsiNativeApi.Load(PrivateLibraryPath, loader));
        Assert.IsNull(exception.InnerException);
        Assert.DoesNotContain(PrivateLibraryPath, exception.ToString(), StringComparison.Ordinal);
        Assert.AreEqual(1, loader.LoadCalls);
        Assert.AreEqual(1, loader.FreeCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StrictPreflightIsInertAndRejectsUnknownOptions()
    {
        var factoryCalls = 0;
        var resolverCalls = 0;
        var module = new ZwoAsiCameraModule(
            TimeProvider.System,
            _ =>
            {
                factoryCalls++;
                return new FakeAsiNativeApi();
            },
            _ =>
            {
                resolverCalls++;
                return null;
            });
        var preflight = (ICameraModuleConfigurationPreflight)module;

        preflight.ValidateConfiguration(CreateConfig());
        Assert.AreEqual(0, factoryCalls);
        var invalid = CreateConfig(OptionsJson(extra: ",\"unknown\":true"));
        Assert.ThrowsExactly<JsonException>(() => preflight.ValidateConfiguration(invalid));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => preflight.ValidateConfiguration(
            CreateConfig(OptionsJson(maximumCapturesPerSession: -1))));
        var monoOptions = JsonDocument.Parse(OptionsJson().GetRawText().Replace(
            "\"monoBin\":false",
            "\"monoBin\":true",
            StringComparison.Ordinal)).RootElement.Clone();
        Assert.ThrowsExactly<NotSupportedException>(() => preflight.ValidateConfiguration(CreateConfig(monoOptions)));

        foreach (var profile in SupportedProfiles)
        {
            var sample = LoadSample(profile);
            preflight.ValidateConfiguration(CreateConfig(sample));
            Assert.AreEqual(LibraryEnvironmentVariable,
                sample.Module.Options!.Value.GetProperty("libraryPathEnvironmentVariable").GetString());
            Assert.AreEqual(SerialEnvironmentVariable,
                sample.Module.Options.Value.GetProperty("cameraSerialEnvironmentVariable").GetString());
            Assert.AreEqual(SensorResponseMode.BayerRaw, sample.Rig.Sensor.ResponseMode);
            Assert.IsNull(sample.Rig.Sensor.SimulationResponse);
            Assert.IsNotNull(sample.Pipeline);
            Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, sample.Pipeline.SchemaVersion);
            Assert.AreEqual(CapturePipelineDependencyPolicy.RejectEnabledDependent,
                sample.Pipeline.DependencyPolicy);
            Assert.IsEmpty(sample.Pipeline.Steps);
            if (profile == Asi178Mc)
            {
                Assert.IsFalse(sample.Module.Options.Value.TryGetProperty("maximumCapturesPerSession", out _));
                Assert.IsTrue(sample.Rig.Optics.HorizontalFlip);
                Assert.AreEqual(TimeSpan.FromTicks(320), sample.Rig.Pipeline.Envelope!.MinExposure);
                Assert.AreEqual(TimeSpan.FromSeconds(1000), sample.Rig.Pipeline.Envelope.MaxExposure);
                Assert.AreEqual(0d, sample.Rig.Pipeline.Envelope.MinGain);
                Assert.AreEqual(510d, sample.Rig.Pipeline.Envelope.MaxGain);
                Assert.AreEqual(CameraFeatureDirective.Disabled, sample.Rig.ControlPolicy!.AutoGain);
                Assert.AreEqual(CameraFeatureDirective.Disabled, sample.Rig.ControlPolicy.AutoExposure);
            }
            else
            {
                Assert.AreEqual(50, sample.Module.Options.Value.GetProperty("maximumCapturesPerSession").GetInt32());
            }
        }
        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(0, resolverCalls);
    }

    [TestMethod]
    [DataRow("RenderedRgb")]
    [DataRow("Monochrome")]
    [DataRow("SimulationResponse")]
    [TestCategory("Unit")]
    public void PhysicalProfilesRejectVirtualSensorResponseDeclarations(string mutation)
    {
        foreach (var profile in SupportedProfiles)
        {
            var config = CreateConfig(
                OptionsJson(expectedModel: profile.Model, offset: profile.Offset),
                profile: profile);
            var sensor = mutation switch
            {
                "RenderedRgb" => config.Rig.Sensor with { ResponseMode = SensorResponseMode.RenderedRgb },
                "Monochrome" => config.Rig.Sensor with { ResponseMode = SensorResponseMode.Monochrome },
                "SimulationResponse" => config.Rig.Sensor with
                {
                    SimulationResponse = new ConfiguredSensorResponseProfile(
                        "test-only", profile.SampleDepthBits, 0, profile.MaximumGain, 1, 100,
                        10_000, [new SensorReadNoisePoint(0, 2)], 0, "test", "test-only")
                },
                _ => throw new AssertFailedException($"Unknown mutation {mutation}.")
            };
            var invalid = config with { Rig = config.Rig with { Sensor = sensor } };

            Assert.ThrowsExactly<NotSupportedException>(() =>
                ((ICameraModuleConfigurationPreflight)Module(new FakeAsiNativeApi(profile)))
                    .ValidateConfiguration(invalid));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PreflightRejectsCrossPairedAndUnsupportedProfilesBeforeRuntimeAccess()
    {
        var factoryCalls = 0;
        var resolverCalls = 0;
        var module = new ZwoAsiCameraModule(
            TimeProvider.System,
            _ =>
            {
                factoryCalls++;
                return new FakeAsiNativeApi();
            },
            _ =>
            {
                resolverCalls++;
                return null;
            });
        var preflight = (ICameraModuleConfigurationPreflight)module;

        Assert.ThrowsExactly<NotSupportedException>(() => preflight.ValidateConfiguration(
            CreateConfig(OptionsJson(expectedModel: Asi178Mc.Model))));
        Assert.ThrowsExactly<NotSupportedException>(() => preflight.ValidateConfiguration(
            CreateConfig(OptionsJson(expectedModel: Asi676Mc.Model), profile: Asi178Mc)));
        Assert.ThrowsExactly<NotSupportedException>(() => preflight.ValidateConfiguration(
            CreateConfig(OptionsJson(expectedModel: "ASI120MM Mini"))));

        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(0, resolverCalls);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("ambiguous")]
    [DataRow("model")]
    [TestCategory("Unit")]
    public async Task StableSelectionRejectsMissingAmbiguousAndModelMismatch(string scenario)
    {
        var native = new FakeAsiNativeApi();
        native.Cameras.Clear();
        switch (scenario)
        {
            case "missing":
                native.Cameras.Add(Camera(1, "ZWO ASI676MC", Convert.FromHexString("1011223344556677")));
                break;
            case "ambiguous":
                native.Cameras.Add(Camera(1, "ZWO ASI676MC", PrivateSerial));
                native.Cameras.Add(Camera(2, "ZWO ASI676MC", PrivateSerial));
                break;
            case "model":
                native.Cameras.Add(Camera(1, "ZWO ASI676MM", PrivateSerial, color: false));
                break;
        }
        await using var module = Module(native);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.InitializeAsync(CreateConfig(), CancellationToken.None));

        Assert.DoesNotContain(Convert.ToHexString(PrivateSerial), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateLibraryPath, exception.ToString(), StringComparison.Ordinal);
        Assert.AreEqual(1, native.DisposeCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ModelComparisonRemovesOnlyExactNativeZwoPrefix()
    {
        var accepted = new FakeAsiNativeApi();
        await using (var module = Module(accepted))
        {
            await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        }

        var accepted178 = new FakeAsiNativeApi(Asi178Mc);
        await using (var module = Module(accepted178))
        {
            await module.InitializeAsync(
                CreateConfig(OptionsJson(expectedModel: Asi178Mc.Model, offset: Asi178Mc.Offset), profile: Asi178Mc),
                CancellationToken.None);
        }

        var rejected = new FakeAsiNativeApi();
        rejected.Cameras[0] = rejected.Cameras[0] with
        {
            Info = rejected.Cameras[0].Info with { Model = "Zwo ASI676MC" }
        };
        await using var rejectedModule = Module(rejected);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => rejectedModule.InitializeAsync(CreateConfig(), CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SelectionDoesNotOpenUnrelatedInventoryEntries()
    {
        var native = new FakeAsiNativeApi();
        native.Cameras.Insert(0, Camera(1, "ZWO ASI120MM Mini", Convert.FromHexString("1011223344556677"), color: false));
        native.Cameras.Insert(1, Camera(2, "ZWO ASI676MM", Convert.FromHexString("2011223344556677"), color: false));
        native.Cameras.Insert(2, Camera(3, "ZWO ASI676MC", Convert.FromHexString("3011223344556677")));
        native.FailOpenCameraIds.Add(1);
        native.FailSerialCameraIds.Add(2);
        await using var module = Module(native);

        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        Assert.DoesNotContain("Open:1", native.Calls);
        Assert.DoesNotContain("Open:2", native.Calls);
        Assert.DoesNotContain("Serial:1", native.Calls);
        Assert.DoesNotContain("Serial:2", native.Calls);
        Assert.Contains("Init:7", native.Calls);
        Assert.AreEqual(2, native.CloseCameraCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SelectedCameraCloseFailureIsReportedAsGenericNotConnected()
    {
        var native = new FakeAsiNativeApi();
        native.FailCloseCameraIds.Add(7);
        await using var module = Module(native);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.InitializeAsync(CreateConfig(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "not connected", StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(PrivateSerial), exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SelectedCameraThatBecomesInaccessibleIsReportedGenerically()
    {
        var native = new FakeAsiNativeApi();
        native.FailOpenOnCallByCameraId[7] = 2;
        await using var module = Module(native);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.InitializeAsync(CreateConfig(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "not connected or accessible", StringComparison.Ordinal);
        Assert.IsNull(exception.InnerException);
        Assert.DoesNotContain(Convert.ToHexString(PrivateSerial), exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RuntimeSecretsResolveOnlyDuringInitializeAndLoadFailuresAreSanitized()
    {
        var resolverCalls = 0;
        var module = new ZwoAsiCameraModule(
            TimeProvider.System,
            path => throw new InvalidOperationException(
                $"load failed for {path}",
                new InvalidDataException(Convert.ToHexString(PrivateSerial))),
            name =>
            {
                resolverCalls++;
                return ResolveEnvironmentVariable(name);
            });
        var preflight = (ICameraModuleConfigurationPreflight)module;

        preflight.ValidateConfiguration(CreateConfig());
        Assert.AreEqual(0, resolverCalls);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.InitializeAsync(CreateConfig(), CancellationToken.None));

        Assert.AreEqual(2, resolverCalls);
        Assert.IsNull(exception.InnerException);
        Assert.DoesNotContain(PrivateLibraryPath, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(PrivateSerial), exception.ToString(), StringComparison.OrdinalIgnoreCase);
        await module.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task InitializeOpensConfiguresAndReadsBackInOrder()
    {
        var native = new FakeAsiNativeApi();
        await using var module = Module(native);

        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        Assert.AreEqual("ZWO ASI676MC", native.Cameras[0].Info.Model);
        CollectionAssert.AreEqual(
            ExpectedInitializationCalls,
            InitializationCalls(native));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Asi178InitializationOpensConfiguresAndReadsBackInOrder()
    {
        var native = new FakeAsiNativeApi(Asi178Mc);
        await using var module = Module(native);

        await module.InitializeAsync(
            CreateConfig(OptionsJson(expectedModel: Asi178Mc.Model, offset: Asi178Mc.Offset), profile: Asi178Mc),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            Asi178ExpectedInitializationCalls,
            InitializationCalls(native));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CapturePreservesExactFullFrameBytesLayoutMetadataAndTiming()
    {
        var native = new FakeAsiNativeApi { ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success]) };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        var requested = new CaptureSetpoint(TimeSpan.FromMilliseconds(25), 82, null, null);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still, requested),
            CancellationToken.None);

        var frame = result.Frame!;
        Assert.AreEqual(3552, frame.Width);
        Assert.AreEqual(3552, frame.Height);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, frame.PixelFormat);
        Assert.AreEqual(25_233_408, frame.PixelData.Length);
        Assert.AreEqual(Asi676Mc.ExpectedSha256, Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)));
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, frame.Layout!.CfaPattern);
        Assert.AreEqual(FrameByteOrder.LittleEndian, frame.Layout.ByteOrder);
        Assert.AreEqual(12, frame.Layout.SampleDepthBits);
        Assert.AreEqual(16, frame.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.OpaqueContainerV1, frame.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, frame.Layout.LevelCodeSpace);
        Assert.AreEqual(0d, frame.Layout.BlackLevel);
        Assert.AreEqual(65535d, frame.Layout.WhiteLevel);
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), frame.Metadata.Exposure);
        Assert.AreEqual(82d, frame.Metadata.Gain);
        Assert.AreEqual(21.5d, frame.Metadata.TemperatureC);
        Assert.AreEqual(1d, frame.Metadata.Offset);
        Assert.AreEqual("ZWO ASI676MC", frame.Metadata.SourceId);
        AssertCaptureTiming(result);
        Assert.AreEqual(25_233_408, native.LastBufferSize);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ConfiguredCaptureLimitRestartsSessionBeforeNextCapture()
    {
        var native = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>(Enumerable.Repeat(AsiExposureStatus.Success, 3))
        };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(OptionsJson(maximumCapturesPerSession: 2)), CancellationToken.None);
        var request = new CaptureRequest(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromMilliseconds(25), 82, null, null));

        _ = await module.CaptureAsync(request, CancellationToken.None);
        Assert.AreEqual(2, native.Calls.Count(call => call == "Open:7"));
        Assert.AreEqual(1, native.Calls.Count(call => call == "Init:7"));
        _ = await module.CaptureAsync(request, CancellationToken.None);
        Assert.AreEqual(2, native.Calls.Count(call => call == "Open:7"));
        Assert.AreEqual(1, native.Calls.Count(call => call == "Init:7"));
        _ = await module.CaptureAsync(request, CancellationToken.None);

        Assert.AreEqual(3, native.Calls.Count(call => call == "Open:7"));
        Assert.AreEqual(2, native.Calls.Count(call => call == "Init:7"));
        Assert.AreEqual(2, native.CloseCameraCalls);
        Assert.AreEqual(2, native.Calls.Count(call => call == "Caps:7"));
        Assert.AreEqual(2, native.Calls.Count(call => call.StartsWith("Roi:", StringComparison.Ordinal)));
        Assert.AreEqual(3, native.DataCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FailedCaptureDoesNotAdvanceConfiguredCaptureLimit()
    {
        var native = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>(
                [AsiExposureStatus.Success, AsiExposureStatus.Failed, AsiExposureStatus.Success, AsiExposureStatus.Success])
        };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(OptionsJson(maximumCapturesPerSession: 2)), CancellationToken.None);
        var request = new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still);

        _ = await module.CaptureAsync(request, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.CaptureAsync(request, CancellationToken.None));
        _ = await module.CaptureAsync(request, CancellationToken.None);
        Assert.AreEqual(2, native.Calls.Count(call => call == "Open:7"));

        _ = await module.CaptureAsync(request, CancellationToken.None);

        Assert.AreEqual(3, native.Calls.Count(call => call == "Open:7"));
        Assert.AreEqual(2, native.Calls.Count(call => call == "Init:7"));
        Assert.AreEqual(4, native.Calls.Count(call => call == "StartExposure"));
    }

    [TestMethod]
    [DataRow("open", 2)]
    [DataRow("capabilities", 3)]
    [TestCategory("Unit")]
    public async Task RecycleFailureInvalidatesSessionAndAllowsReinitialize(string failure, int expectedCloseCalls)
    {
        var first = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success]),
            FailCapabilitiesOnCall = failure == "capabilities" ? 2 : 0
        };
        if (failure == "open")
        {
            first.FailOpenOnCallByCameraId[7] = 3;
        }
        var second = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success])
        };
        var natives = new Queue<IAsiNativeApi>([first, second]);
        var module = new ZwoAsiCameraModule(TimeProvider.System, _ => natives.Dequeue(), ResolveEnvironmentVariable);
        var config = CreateConfig(OptionsJson(maximumCapturesPerSession: 1));
        var request = new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still);
        await module.InitializeAsync(config, CancellationToken.None);
        _ = await module.CaptureAsync(request, CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.CaptureAsync(request, CancellationToken.None));

        Assert.IsInstanceOfType<AsiException>(exception.InnerException);
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(3, first.Calls.Count(call => call == "Open:7"));
        Assert.AreEqual(expectedCloseCalls, first.CloseCameraCalls);
        Assert.AreEqual(1, first.Calls.Count(call => call == "StartExposure"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => module.CaptureAsync(request, CancellationToken.None));

        await module.InitializeAsync(config, CancellationToken.None);
        Assert.IsNotNull((await module.CaptureAsync(request, CancellationToken.None)).Frame);
        await module.DisposeAsync();
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, second.DisposeCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Asi178CapturePreservesExactFullFrameBytesLayoutAndMetadata()
    {
        var native = new FakeAsiNativeApi(Asi178Mc)
        {
            ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success])
        };
        await using var module = Module(native);
        await module.InitializeAsync(
            CreateConfig(OptionsJson(expectedModel: Asi178Mc.Model, offset: Asi178Mc.Offset), profile: Asi178Mc),
            CancellationToken.None);

        var result = await module.CaptureAsync(
            new CaptureRequest(
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromMilliseconds(100), 150, null, null)),
            CancellationToken.None);

        var frame = result.Frame!;
        Assert.AreEqual(3096, frame.Width);
        Assert.AreEqual(2080, frame.Height);
        Assert.AreEqual(6192, frame.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, frame.PixelFormat);
        Assert.AreEqual(12_879_360, frame.PixelData.Length);
        Assert.AreEqual(Asi178Mc.ExpectedSha256, Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)));
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, frame.Layout!.CfaPattern);
        Assert.AreEqual(FrameByteOrder.LittleEndian, frame.Layout.ByteOrder);
        Assert.AreEqual(14, frame.Layout.SampleDepthBits);
        Assert.AreEqual(16, frame.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.OpaqueContainerV1, frame.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, frame.Layout.LevelCodeSpace);
        Assert.AreEqual(0d, frame.Layout.BlackLevel);
        Assert.AreEqual(65535d, frame.Layout.WhiteLevel);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), frame.Metadata.Exposure);
        Assert.AreEqual(150d, frame.Metadata.Gain);
        Assert.AreEqual(21.5d, frame.Metadata.TemperatureC);
        Assert.AreEqual(10d, frame.Metadata.Offset);
        Assert.AreEqual("ZWO ASI178MC", frame.Metadata.SourceId);
        AssertCaptureTiming(result);
        Assert.AreEqual(12_879_360, native.LastBufferSize);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CancellationStopsExposureAndReturnsNoFrame()
    {
        var native = new FakeAsiNativeApi { RepeatWorkingStatus = true };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var capture = module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still), cancellation.Token);
        await native.ExposureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => capture);
        Assert.AreEqual(1, native.StopExposureCalls);
        Assert.AreEqual(0, native.DataCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CancellationObservedImmediatelyBeforeSynchronousReadStopsWithoutReading()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success]),
            ExposureStatusObserved = () => cancellation.Cancel()
        };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            cancellation.Token));

        Assert.AreEqual(1, native.StopExposureCalls);
        Assert.AreEqual(0, native.DataCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PollFailureStopsExposureAndPreservesPrimaryException()
    {
        var native = new FakeAsiNativeApi { ThrowOnPoll = true };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<AsiException>(() => module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None));

        Assert.AreEqual(AsiErrorCode.CameraRemoved, exception.ErrorCode);
        Assert.AreEqual(1, native.StopExposureCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task StopFailureInvalidatesSessionPreservesPrimaryAndAllowsReinitialize()
    {
        var first = new FakeAsiNativeApi { ThrowOnPoll = true, ThrowOnStop = true };
        var second = new FakeAsiNativeApi();
        var natives = new Queue<IAsiNativeApi>([first, second]);
        var module = new ZwoAsiCameraModule(TimeProvider.System, _ => natives.Dequeue(), ResolveEnvironmentVariable);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        var primary = await Assert.ThrowsExactlyAsync<AsiException>(() => module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None));

        Assert.AreEqual(AsiErrorCode.CameraRemoved, primary.ErrorCode);
        Assert.AreEqual(1, first.StopExposureCalls);
        Assert.AreEqual(1, first.DisposeCalls);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None));

        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None);
        Assert.IsNotNull(result.Frame);
        await module.DisposeAsync();
        await module.DisposeAsync();
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, second.DisposeCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MonotonicDeadlineStopsTimedOutExposure()
    {
        var native = new FakeAsiNativeApi { RepeatWorkingStatus = true };
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(OptionsJson(timeoutMargin: "00:00:00")), CancellationToken.None);
        var request = new CaptureRequest(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => module.CaptureAsync(request, CancellationToken.None));
        Assert.AreEqual(1, native.StopExposureCalls);
        Assert.AreEqual(0, native.DataCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DisposalCancelsExposureAndPropagatesFinalCloseFailureAfterCleanup()
    {
        var native = new FakeAsiNativeApi { RepeatWorkingStatus = true };
        var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        var capture = module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None);
        await native.ExposureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        native.FailCloseCameraIds.Add(7);

        var exception = await Assert.ThrowsExactlyAsync<AsiException>(() => module.DisposeAsync().AsTask());

        Assert.AreEqual(AsiErrorCode.CameraRemoved, exception.ErrorCode);
        await Assert.ThrowsAsync<OperationCanceledException>(() => capture);
        Assert.AreEqual(1, native.StopExposureCalls);
        Assert.AreEqual(1, native.DisposeCalls);
        await module.DisposeAsync();
        Assert.AreEqual(1, native.DisposeCalls);
    }

    [TestMethod]
    [DataRow("exposure")]
    [DataRow("read")]
    [TestCategory("Unit")]
    public async Task ExposureFailureAndReadErrorReturnNoFrame(string failure)
    {
        var native = new FakeAsiNativeApi
        {
            ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Failed]),
            ThrowOnRead = failure == "read"
        };
        if (failure == "read")
        {
            native.ExposureStatuses = new Queue<AsiExposureStatus>([AsiExposureStatus.Success]);
        }
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None));

        Assert.AreEqual(1, native.StopExposureCalls);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReinitializeAndRepeatedDisposeCleanEachNativeInstanceExactlyOnce()
    {
        var first = new FakeAsiNativeApi();
        var second = new FakeAsiNativeApi();
        var natives = new Queue<IAsiNativeApi>([first, second]);
        var module = new ZwoAsiCameraModule(TimeProvider.System, _ => natives.Dequeue(), ResolveEnvironmentVariable);

        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);
        await module.DisposeAsync();
        await module.DisposeAsync();

        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, second.DisposeCalls);
        Assert.AreEqual(2, first.CloseCameraCalls);
        Assert.AreEqual(2, second.CloseCameraCalls);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => module.InitializeAsync(CreateConfig(), CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SetpointsRejectSdkClampingAndUseReadbackValues()
    {
        var native = new FakeAsiNativeApi();
        await using var module = Module(native);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await module.ApplySetpointAsync(new CaptureSetpoint(TimeSpan.FromTicks(10), 0, null, null), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await module.ApplySetpointAsync(new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 601, null, null), CancellationToken.None));
        native.ReadbackOverrides[AsiControlType.Gain] = 81;
        var applied = await module.ApplySetpointAsync(
            new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 82, null, null), CancellationToken.None);

        Assert.AreEqual(81L, native.GetControlValue(7, AsiControlType.Gain).Value);
        Assert.IsTrue(applied <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Asi178UsesDiscoveredExposureAndGainControlRanges()
    {
        var native = new FakeAsiNativeApi(Asi178Mc);
        await using var module = Module(native);
        var envelope = new ExposureEnvelope(
            TimeSpan.FromTicks(320),
            TimeSpan.FromSeconds(1000),
            0,
            510,
            new ExposureDefaults(TimeSpan.FromMilliseconds(100), 0),
            new ExposureDefaults(TimeSpan.FromSeconds(20), 150),
            0.65);
        var pipeline = new PipelineExposureProfile(
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(20),
            0,
            150,
            envelope);
        await module.InitializeAsync(
            CreateConfig(
                OptionsJson(expectedModel: Asi178Mc.Model, offset: Asi178Mc.Offset),
                pipeline,
                Asi178Mc),
            CancellationToken.None);

        await module.ApplySetpointAsync(
            new CaptureSetpoint(TimeSpan.FromTicks(320), Asi178Mc.MaximumGain, null, null),
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await module.ApplySetpointAsync(
                new CaptureSetpoint(TimeSpan.FromTicks(310), 0, null, null),
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await module.ApplySetpointAsync(
                new CaptureSetpoint(TimeSpan.FromMilliseconds(100), Asi178Mc.MaximumGain + 1, null, null),
                CancellationToken.None));
    }

    [TestMethod]
    [DataRow("dayExposure")]
    [DataRow("dayGain")]
    [DataRow("nightExposure")]
    [DataRow("nightGain")]
    [DataRow("minExposure")]
    [DataRow("minGain")]
    [DataRow("maxExposure")]
    [DataRow("maxGain")]
    [DataRow("dayDefaultExposure")]
    [DataRow("dayDefaultGain")]
    [DataRow("nightDefaultExposure")]
    [DataRow("nightDefaultGain")]
    [DataRow("twilightDefaultExposure")]
    [DataRow("twilightDefaultGain")]
    [TestCategory("Unit")]
    public async Task InitializationRejectsConfiguredSetpointsOutsideDiscoveredControls(string field)
    {
        var envelope = new ExposureEnvelope(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMinutes(30),
            0,
            600,
            new ExposureDefaults(TimeSpan.FromMilliseconds(100), 0),
            new ExposureDefaults(TimeSpan.FromMinutes(20), 82),
            0.65,
            new ExposureDefaults(TimeSpan.FromSeconds(5), 40));
        var pipeline = new PipelineExposureProfile(
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(20),
            0,
            82,
            envelope);
        pipeline = field switch
        {
            "dayExposure" => pipeline with { DayExposure = TimeSpan.FromMinutes(40) },
            "dayGain" => pipeline with { DayGain = 601 },
            "nightExposure" => pipeline with { NightExposure = TimeSpan.FromMinutes(40) },
            "nightGain" => pipeline with { NightGain = 601 },
            "minExposure" => pipeline with { Envelope = envelope with { MinExposure = TimeSpan.FromTicks(10) } },
            "minGain" => pipeline with { Envelope = envelope with { MinGain = 601 } },
            "maxExposure" => pipeline with { Envelope = envelope with { MaxExposure = TimeSpan.FromMinutes(40) } },
            "maxGain" => pipeline with { Envelope = envelope with { MaxGain = 601 } },
            "dayDefaultExposure" => pipeline with
            {
                Envelope = envelope with { DayDefaults = new ExposureDefaults(TimeSpan.FromMinutes(40), 0) }
            },
            "dayDefaultGain" => pipeline with
            {
                Envelope = envelope with { DayDefaults = new ExposureDefaults(TimeSpan.FromMilliseconds(100), 601) }
            },
            "nightDefaultExposure" => pipeline with
            {
                Envelope = envelope with { NightDefaults = new ExposureDefaults(TimeSpan.FromMinutes(40), 82) }
            },
            "nightDefaultGain" => pipeline with
            {
                Envelope = envelope with { NightDefaults = new ExposureDefaults(TimeSpan.FromMinutes(20), 601) }
            },
            "twilightDefaultExposure" => pipeline with
            {
                Envelope = envelope with { TwilightDefaults = new ExposureDefaults(TimeSpan.FromMinutes(40), 40) }
            },
            "twilightDefaultGain" => pipeline with
            {
                Envelope = envelope with { TwilightDefaults = new ExposureDefaults(TimeSpan.FromSeconds(5), 601) }
            },
            _ => pipeline
        };
        var native = new FakeAsiNativeApi();
        await using var module = Module(native);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => module.InitializeAsync(CreateConfig(pipeline: pipeline), CancellationToken.None));

        Assert.DoesNotContain("Set:Exposure", native.Calls);
        Assert.AreEqual(1, native.DisposeCalls);
    }

    [TestMethod]
    [DataRow("mono")]
    [DataRow("dimensions")]
    [DataRow("format")]
    [DataRow("asi120")]
    [TestCategory("Unit")]
    public async Task CameraProfileMismatchesAreRejected(string mismatch)
    {
        var native = new FakeAsiNativeApi();
        var camera = native.Cameras[0];
        native.Cameras[0] = mismatch switch
        {
            "mono" => camera with { Info = camera.Info with { IsColorCamera = false } },
            "dimensions" => camera with { Info = camera.Info with { MaximumWidth = 1280, MaximumHeight = 960 } },
            "format" => camera with { Info = camera.Info with { SupportedImageTypes = [AsiImageType.Raw8] } },
            "asi120" => Camera(7, "ZWO ASI120MM Mini", PrivateSerial, color: false) with
            {
                Info = camera.Info with
                {
                    Model = "ZWO ASI120MM Mini",
                    MaximumWidth = 1280,
                    MaximumHeight = 960,
                    IsColorCamera = false,
                    SupportedBins = [1, 2],
                    SupportedImageTypes = [AsiImageType.Raw8, AsiImageType.Raw16]
                }
            },
            _ => camera
        };
        await using var module = Module(native);
        var config = mismatch == "asi120"
            ? CreateConfig(OptionsJson(expectedModel: "ASI120MM Mini"))
            : CreateConfig();

        if (mismatch is "format" or "asi120")
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(
                () => module.InitializeAsync(config, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => module.InitializeAsync(config, CancellationToken.None));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CameraAgentCompositionRegistersFactoryAliasWithoutLoadingSdk()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
        using var provider = services.BuildServiceProvider();
        var factory = new CameraModuleFactory(
            provider,
            provider.GetServices<CameraModuleRegistration>(),
            NullLogger<CameraModuleFactory>.Instance);

        await using var module = factory.Create("ZwoAsi");

        Assert.IsInstanceOfType<ZwoAsiCameraModule>(module);
        Assert.AreEqual("ZwoAsi", module.ModuleType);
    }

    [TestMethod]
    [TestCategory("Hardware")]
    public async Task OptInHardwareProfileCaptureCloseSmoke()
    {
        var libraryPath = Environment.GetEnvironmentVariable("HVO_ZWO_SDK_LIBRARY");
        var cameraSerial = Environment.GetEnvironmentVariable("HVO_ZWO_CAMERA_SERIAL");
        var expectedModel = Environment.GetEnvironmentVariable("HVO_ZWO_EXPECTED_MODEL");
        if (string.IsNullOrWhiteSpace(libraryPath) || string.IsNullOrWhiteSpace(cameraSerial) ||
            string.IsNullOrWhiteSpace(expectedModel))
        {
            Assert.Inconclusive("Set all HVO_ZWO_* hardware variables to run the physical-camera smoke.");
        }

        await using var module = new ZwoAsiCameraModule(TimeProvider.System);
        var profile = SupportedProfiles.SingleOrDefault(
            candidate => string.Equals(candidate.Model, expectedModel, StringComparison.Ordinal));
        Assert.IsNotNull(profile, $"HVO_ZWO_EXPECTED_MODEL must be one of: {string.Join(", ", SupportedProfiles.Select(candidate => candidate.Model))}.");
        var sample = LoadSample(profile);
        Assert.AreEqual(expectedModel, sample.Module.Options!.Value.GetProperty("expectedModel").GetString());
        await module.InitializeAsync(CreateConfig(sample), CancellationToken.None);
        var result = await module.CaptureAsync(
            new CaptureRequest(
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromMilliseconds(100), 0, null, null)),
            CancellationToken.None);

        var frame = result.Frame!;
        Assert.AreEqual(profile.Width, frame.Width);
        Assert.AreEqual(profile.Height, frame.Height);
        Assert.AreEqual(profile.StrideBytes, frame.StrideBytes);
        Assert.AreEqual(checked(profile.StrideBytes * profile.Height), frame.PixelData.Length);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, frame.PixelFormat);
        Assert.AreEqual(profile.SampleDepthBits, frame.Layout!.SampleDepthBits);
        Assert.AreEqual(16, frame.Layout.ContainerDepthBits);
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, frame.Layout.CfaPattern);
        Assert.AreEqual(FrameByteOrder.LittleEndian, frame.Layout.ByteOrder);
        Assert.AreEqual(FrameStoredCodeTransform.OpaqueContainerV1, frame.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, frame.Layout.LevelCodeSpace);
        Assert.AreEqual(0d, frame.Layout.BlackLevel);
        Assert.AreEqual(65535d, frame.Layout.WhiteLevel);
        Assert.AreEqual($"ZWO {profile.Model}", frame.Metadata.SourceId);
    }

    private static ZwoAsiCameraModule Module(FakeAsiNativeApi native)
        => new(TimeProvider.System, _ => native, ResolveEnvironmentVariable);

    private static CameraModuleDocument LoadSample(SupportedProfile profile)
    {
        var samplePath = Path.Combine(
            AppContext.BaseDirectory,
            $"cameraagent.zwo-{profile.Id}.sample.json");
        return JsonSerializer.Deserialize<CameraModuleDocument>(
            File.ReadAllText(samplePath),
            ConfigurationSerializerOptions)!;
    }

    private static CameraModuleConfig CreateConfig(CameraModuleDocument document)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            document.Module,
            document.Rig,
            document.ProcessingSteps,
            document.Pipeline,
            document.AgentId);

    private static CameraModuleConfig CreateConfig(
        JsonElement? options = null,
        PipelineExposureProfile? pipeline = null,
        SupportedProfile? profile = null)
    {
        profile ??= Asi676Mc;
        var sensor = new SensorProfile(
            $"Physical{profile.Model}",
            profile.Width,
            profile.Height,
            profile.PixelSizeMicrons,
            SensorColorMode.Color,
            CameraPixelFormat.BayerRggb16,
            SensorResponseMode.BayerRaw,
            profile.StrideBytes,
            SampleByteOrder.LittleEndian,
            $"{profile.Id}-physical-provisional-v1");
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, profile.Width, profile.Height),
            1,
            1,
            FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.BayerRggb16,
            profile.SampleDepthBits,
            16,
            FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.OpaqueContainerV1,
            FrameLevelCodeSpace.StoredContainer,
            0,
            65535,
            profile.StrideBytes,
            SampleByteOrder.LittleEndian,
            ColorFilterArrayPattern.Rggb,
            0,
            0);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("ZwoAsi", options ?? OptionsJson()),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 2.5, 170, 0),
                new RigOrientation(90, 0, 0),
                pipeline ?? new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromSeconds(1),
                    0,
                    82),
                ProfileVersion: $"physical-{profile.Id}-sample-v1",
                Readout: readout));
    }

    private static JsonElement OptionsJson(
        string libraryPathEnvironmentVariable = LibraryEnvironmentVariable,
        string cameraSerialEnvironmentVariable = SerialEnvironmentVariable,
        string expectedModel = "ASI676MC",
        long offset = 1,
        string timeoutMargin = "00:00:01",
        int maximumCapturesPerSession = 0,
        string extra = "")
        => JsonDocument.Parse(
            $$"""
              {
                "libraryPathEnvironmentVariable":"{{libraryPathEnvironmentVariable}}",
                "cameraSerialEnvironmentVariable":"{{cameraSerialEnvironmentVariable}}",
                "expectedModel":"{{expectedModel}}",
                "offset":{{offset}},
                "usbBandwidth":40,
                "pollInterval":"00:00:00.001",
                "captureTimeoutMargin":"{{timeoutMargin}}",
                "maximumCapturesPerSession":{{maximumCapturesPerSession}},
                "monoBin":false,
                "hardwareBin":false{{extra}}
              }
              """).RootElement.Clone();

    private static FakeCamera Camera(
        int id,
        string model,
        byte[] serial,
        bool color = true,
        SupportedProfile? profile = null)
    {
        profile ??= Asi676Mc;
        return new(
            new AsiCameraInfo(
                id,
                model,
                profile.Width,
                profile.Height,
                color,
                AsiBayerPattern.Rg,
                [1, 2, 3, 4],
                color ? [AsiImageType.Raw8, AsiImageType.Rgb24, AsiImageType.Y8, AsiImageType.Raw16] : [AsiImageType.Raw8, AsiImageType.Raw16],
                profile.PixelSizeMicrons,
                profile.SampleDepthBits),
            serial);
    }

    private static string[] InitializationCalls(FakeAsiNativeApi native)
        => native.Calls.Where(call =>
            call.StartsWith("Open", StringComparison.Ordinal) || call.StartsWith("Serial", StringComparison.Ordinal) ||
            call.StartsWith("Close", StringComparison.Ordinal) || call.StartsWith("Init", StringComparison.Ordinal) ||
            call.StartsWith("Caps", StringComparison.Ordinal) || call.StartsWith("Set", StringComparison.Ordinal) ||
            call.StartsWith("Get:", StringComparison.Ordinal) || call.StartsWith("Roi", StringComparison.Ordinal) ||
            call.StartsWith("Position", StringComparison.Ordinal) || call.StartsWith("GetRoi", StringComparison.Ordinal) ||
            call.StartsWith("GetPosition", StringComparison.Ordinal)).ToArray();

    private static void AssertCaptureTiming(CaptureResult result)
    {
        var timing = result.AcquisitionTiming!;
        Assert.IsNotNull(timing.SetpointAppliedUtc);
        Assert.IsTrue(timing.SetpointAppliedUtc <= timing.ExposureStartedUtc);
        Assert.IsTrue(timing.ExposureStartedUtc <= timing.ExposureEndedUtc);
        Assert.IsTrue(timing.ExposureEndedUtc <= timing.ReadoutCompletedUtc);
    }

    private static string? ResolveEnvironmentVariable(string name)
        => name switch
        {
            LibraryEnvironmentVariable => PrivateLibraryPath,
            SerialEnvironmentVariable => Convert.ToHexString(PrivateSerial),
            _ => null
        };

    private sealed class FailingLibraryLoader : IAsiLibraryLoader
    {
        internal int LoadCalls { get; private set; }
        internal int FreeCalls { get; private set; }
        public IntPtr Load(string path)
        {
            LoadCalls++;
            return (IntPtr)42;
        }
        public IntPtr GetExport(IntPtr handle, string name) => throw new EntryPointNotFoundException();
        public void Free(IntPtr handle) => FreeCalls++;
    }

    private sealed record FakeCamera(AsiCameraInfo Info, byte[] Serial);

    private sealed record SupportedProfile(
        string Id,
        string Model,
        int Width,
        int Height,
        double PixelSizeMicrons,
        int SampleDepthBits,
        int StrideBytes,
        long Offset,
        long MaximumGain,
        string ExpectedSha256);

    private sealed class FakeAsiNativeApi : IAsiNativeApi
    {
        private readonly Dictionary<AsiControlType, long> _values;
        private readonly SupportedProfile _profile;
        private (int Width, int Height, int Bin, AsiImageType Type) _roi;
        private (int X, int Y) _position;
        private readonly Dictionary<int, int> _openCallsByCameraId = [];

        internal FakeAsiNativeApi(SupportedProfile? profile = null)
        {
            _profile = profile ?? Asi676Mc;
            _values = new Dictionary<AsiControlType, long>
            {
                [AsiControlType.Exposure] = 100_000,
                [AsiControlType.Gain] = 0,
                [AsiControlType.Offset] = _profile.Offset,
                [AsiControlType.BandwidthOverload] = 40,
                [AsiControlType.Temperature] = 215,
                [AsiControlType.HighSpeedMode] = 0,
                [AsiControlType.Flip] = 0,
                [AsiControlType.MonoBin] = 0,
                [AsiControlType.HardwareBin] = 0
            };
            Cameras.Add(Camera(7, $"ZWO {_profile.Model}", PrivateSerial, profile: _profile));
        }

        internal List<FakeCamera> Cameras { get; } = [];
        internal List<string> Calls { get; } = [];
        internal Dictionary<AsiControlType, long> ReadbackOverrides { get; } = [];
        internal Queue<AsiExposureStatus> ExposureStatuses { get; set; } = new([AsiExposureStatus.Success]);
        internal TaskCompletionSource ExposureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<int> FailOpenCameraIds { get; } = [];
        internal Dictionary<int, int> FailOpenOnCallByCameraId { get; } = [];
        internal HashSet<int> FailSerialCameraIds { get; } = [];
        internal HashSet<int> FailCloseCameraIds { get; } = [];
        internal int FailCapabilitiesOnCall { get; init; }
        internal bool RepeatWorkingStatus { get; init; }
        internal bool ThrowOnRead { get; init; }
        internal bool ThrowOnPoll { get; init; }
        internal bool ThrowOnStop { get; init; }
        internal Action? ExposureStatusObserved { get; init; }
        internal int StopExposureCalls { get; private set; }
        internal int DataCalls { get; private set; }
        internal int LastBufferSize { get; private set; }
        internal int CloseCameraCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public string GetSdkVersion() => "1.41";
        public int GetCameraCount() => Cameras.Count;
        public AsiCameraInfo GetCameraProperty(int cameraIndex) => Cameras[cameraIndex].Info;
        public byte[] GetSerialNumber(int cameraId)
        {
            Calls.Add($"Serial:{cameraId}");
            if (FailSerialCameraIds.Contains(cameraId))
            {
                throw new AsiException("ASIGetSerialNumber", AsiErrorCode.CameraRemoved);
            }
            return Cameras.Single(camera => camera.Info.CameraId == cameraId).Serial;
        }
        public void OpenCamera(int cameraId)
        {
            Calls.Add($"Open:{cameraId}");
            var call = _openCallsByCameraId.GetValueOrDefault(cameraId) + 1;
            _openCallsByCameraId[cameraId] = call;
            if (FailOpenCameraIds.Contains(cameraId) || FailOpenOnCallByCameraId.GetValueOrDefault(cameraId) == call)
            {
                throw new AsiException("ASIOpenCamera", AsiErrorCode.CameraClosed);
            }
        }
        public void InitializeCamera(int cameraId) => Calls.Add($"Init:{cameraId}");
        public void CloseCamera(int cameraId)
        {
            Calls.Add($"Close:{cameraId}");
            CloseCameraCalls++;
            if (FailCloseCameraIds.Contains(cameraId))
            {
                throw new AsiException("ASICloseCamera", AsiErrorCode.CameraRemoved);
            }
        }
        public IReadOnlyList<AsiControlCaps> GetControlCapabilities(int cameraId)
        {
            Calls.Add($"Caps:{cameraId}");
            if (Calls.Count(call => call == $"Caps:{cameraId}") == FailCapabilitiesOnCall)
            {
                throw new AsiException("ASIGetNumOfControls", AsiErrorCode.CameraRemoved);
            }
            return
            [
                new(AsiControlType.Exposure, 32, 2_000_000_000, 100_000, false, true),
                new(AsiControlType.Gain, 0, _profile.MaximumGain, 0, false, true),
                new(AsiControlType.Offset, 0, 600, _profile.Offset, false, true),
                new(AsiControlType.BandwidthOverload, 0, 100, 40, false, true),
                new(AsiControlType.Temperature, -1000, 1000, 200, false, false),
                new(AsiControlType.HighSpeedMode, 0, 1, 0, false, true),
                new(AsiControlType.Flip, 0, 3, 0, false, true),
                new(AsiControlType.MonoBin, 0, 1, 0, false, true),
                new(AsiControlType.HardwareBin, 0, 1, 0, false, true)
            ];
        }
        public (long Value, bool Automatic) GetControlValue(int cameraId, AsiControlType type)
        {
            Calls.Add($"Get:{type}");
            return (ReadbackOverrides.GetValueOrDefault(type, _values[type]), false);
        }
        public void SetControlValue(int cameraId, AsiControlType type, long value, bool automatic)
        {
            Calls.Add($"Set:{type}:{value}");
            _values[type] = value;
        }
        public void SetRoiFormat(int cameraId, int width, int height, int bin, AsiImageType imageType)
        {
            Calls.Add($"Roi:{width}:{height}:{bin}:{imageType}");
            _roi = (width, height, bin, imageType);
        }
        public (int Width, int Height, int Bin, AsiImageType ImageType) GetRoiFormat(int cameraId)
        {
            Calls.Add("GetRoi");
            return _roi;
        }
        public void SetStartPosition(int cameraId, int x, int y)
        {
            Calls.Add($"Position:{x}:{y}");
            _position = (x, y);
        }
        public (int X, int Y) GetStartPosition(int cameraId)
        {
            Calls.Add("GetPosition");
            return _position;
        }
        public void StartExposure(int cameraId, bool dark)
        {
            Calls.Add("StartExposure");
            ExposureStarted.TrySetResult();
        }
        public AsiExposureStatus GetExposureStatus(int cameraId)
        {
            if (ThrowOnPoll)
            {
                throw new AsiException("ASIGetExpStatus", AsiErrorCode.CameraRemoved);
            }
            var status = RepeatWorkingStatus ? AsiExposureStatus.Working : ExposureStatuses.Dequeue();
            ExposureStatusObserved?.Invoke();
            return status;
        }
        public void StopExposure(int cameraId)
        {
            Calls.Add("StopExposure");
            StopExposureCalls++;
            if (ThrowOnStop)
            {
                throw new AsiException("ASIStopExposure", AsiErrorCode.CameraRemoved);
            }
        }
        public void GetDataAfterExposure(int cameraId, IntPtr buffer, long bufferSize)
        {
            Calls.Add("GetData");
            DataCalls++;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Injected ASI read error.");
            }
            LastBufferSize = checked((int)bufferSize);
            var bytes = new byte[Math.Min(64 * 1024, LastBufferSize)];
            for (var offset = 0; offset < LastBufferSize; offset += bytes.Length)
            {
                var length = Math.Min(bytes.Length, LastBufferSize - offset);
                for (var index = 0; index < length; index++)
                {
                    bytes[index] = (byte)((offset + index) % 251);
                }
                Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), length);
            }
        }
        public void Dispose() => DisposeCalls++;
    }
}
