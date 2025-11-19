using System;
using System.Collections.Generic;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.NoOp;

namespace HVO.SkyMonitor.CameraAgent.Tests;

internal static class TestCameraModuleConfigFactory
{
    private static readonly ObservatoryLocation DefaultObservatory = new(21.3, -157.8, 400, "Pacific/Honolulu");

    public static CameraModuleConfig Create(
        CameraModuleDescriptor? descriptor = null,
        CameraRigConfig? rig = null,
        IReadOnlyList<CaptureProcessingStepConfig>? processingSteps = null,
        CapturePipelineConfig? pipeline = null)
    {
        descriptor ??= CreateDescriptor();
        rig ??= CreateRig();
        return new CameraModuleConfig(DefaultObservatory, descriptor, rig, processingSteps, pipeline);
    }

    public static CameraModuleDescriptor CreateDescriptor(Type? moduleType = null, JsonElement? options = null)
    {
        moduleType ??= typeof(NoOpCameraModule);
        return new CameraModuleDescriptor(moduleType.FullName ?? moduleType.Name, options);
    }

    public static CameraRigConfig CreateRig(
        int widthPixels = 32,
        int heightPixels = 32,
        double pixelSizeMicrons = 3.2,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono8,
        ExposureEnvelope? envelope = null,
        CameraControlPolicy? controlPolicy = null)
    {
        return new CameraRigConfig(
            new SensorProfile("Sensor", widthPixels, heightPixels, pixelSizeMicrons, SensorColorMode.Mono, pixelFormat),
            new OpticsProfile("Equidistant", 2.8, 180, 0),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2),
                5.0d,
                25.0d,
                envelope),
            controlPolicy);
    }
}
