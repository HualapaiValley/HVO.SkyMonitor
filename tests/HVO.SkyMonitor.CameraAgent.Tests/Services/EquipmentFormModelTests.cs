using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class EquipmentFormModelTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SampleJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static NamedCameraEquipment Camera(bool color = false) => new(
        new CameraModuleDescriptor("zwo-asi", JsonSerializer.SerializeToElement(new { secret = "serial", useUnvalidatedCameraAtOwnRisk = true })),
        new SensorProfile("ASI120MM", 1280, 960, 3.75, color ? SensorColorMode.Color : SensorColorMode.Mono,
            color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16),
        new SensorReadoutProfile(new SensorCrop(0, 0, 1280, 960), 1, 1, FrameBinningAlgorithm.IdentityV1,
            color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, 12, 16,
            FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            0, 4095, CfaPattern: color ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            CfaOriginX: color ? 0 : null, CfaOriginY: color ? 0 : null));

    [TestMethod]
    public void Camera_RoundTripsMonoAndRggbReadoutWithoutExposingOptions()
    {
        foreach (var color in new[] { false, true })
        {
            var basis = Camera(color);
            var model = EquipmentFormModel.FromCamera(basis);
            Assert.IsTrue(model.TryApplyCamera(basis, out var definition, out var errors), string.Join(" ", errors));
            Assert.IsFalse(definition.GetRawText().Contains("secret", StringComparison.Ordinal));
            Assert.IsFalse(definition.GetProperty("module").TryGetProperty("options", out _));
            var saved = definition.Deserialize<NamedCameraEquipment>(WebJson)!;
            Assert.AreEqual(basis.Sensor with
            {
                ResponseMode = color ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome
            }, saved.Sensor);
            Assert.AreEqual(basis.Readout, saved.Readout);
            Assert.AreEqual(color ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None, saved.Readout!.CfaPattern);
        }
    }

    [TestMethod]
    public void UnsupportedCustomCameraDraft_CanBeSubmittedThroughBasisWithoutProviderOptions()
    {
        var basis = Camera();
        var model = EquipmentFormModel.FromCamera(basis);
        model.SensorName = "custom ASI120MM Mini";
        model.PixelSizeMicrons = "3.8";
        Assert.IsTrue(model.TryApplyCamera(basis, out var definition, out var errors), string.Join(" ", errors));
        Assert.AreEqual("custom ASI120MM Mini", definition.GetProperty("sensor").GetProperty("name").GetString());
        Assert.AreEqual("zwo-asi", definition.GetProperty("module").GetProperty("type").GetString());
        Assert.IsFalse(definition.GetProperty("module").TryGetProperty("options", out _));
        Assert.AreEqual("serial", basis.Module.Options!.Value.GetProperty("secret").GetString());
    }

    [TestMethod]
    public void DifferentZwoBasis_CreatesMonoAsi120GeometryAndSensorStride_WithoutChangingReadoutStride()
    {
        var basis = Camera(color: true) with
        {
            Sensor = Camera(color: true).Sensor with { Name = "ASI294MC", WidthPixels = 4144, HeightPixels = 2822, StrideBytes = 8288 },
            Readout = Camera(color: true).Readout! with { StrideBytes = 3000 }
        };
        var model = EquipmentFormModel.FromCamera(basis);
        model.SensorName = "ASI120MM";
        model.WidthPixels = "1280";
        model.HeightPixels = "960";
        model.SensorStrideBytes = "2560";
        model.ColorMode = nameof(SensorColorMode.Mono);
        model.SensorPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.ReadoutPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.CfaPattern = nameof(ColorFilterArrayPattern.None);
        model.CfaOriginX = string.Empty;
        model.CfaOriginY = string.Empty;
        Assert.IsTrue(model.TryApplyCamera(basis, out var definition, out var errors), string.Join(" ", errors));
        var saved = definition.Deserialize<NamedCameraEquipment>(WebJson)!;
        Assert.AreEqual("ASI120MM", saved.Sensor.Name);
        Assert.AreEqual(1280, saved.Sensor.WidthPixels);
        Assert.AreEqual(960, saved.Sensor.HeightPixels);
        Assert.AreEqual(2560, saved.Sensor.StrideBytes);
        Assert.AreEqual(SensorColorMode.Mono, saved.Sensor.ColorMode);
        Assert.AreEqual(SensorResponseMode.Monochrome, saved.Sensor.ResponseMode);
        Assert.AreEqual(CameraPixelFormat.Mono16, saved.Sensor.PixelFormat);
        Assert.AreEqual(3000, saved.Readout!.StrideBytes);
        Assert.AreEqual(ColorFilterArrayPattern.None, saved.Readout.CfaPattern);
        Assert.IsFalse(definition.GetProperty("module").TryGetProperty("options", out _));

        model.SensorStrideBytes = "invalid";
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Sensor stride (bytes)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ColorStarter_CustomMonoAsi120_RequiresCoherentReadoutAndRejectsPhysicalSimulation()
    {
        var basis = ZwoCameraStarter.Create("asi676mc");
        var model = EquipmentFormModel.FromCamera(basis);
        model.SensorName = "custom ASI120MM Mini";
        model.WidthPixels = "1280";
        model.HeightPixels = "960";
        model.SensorStrideBytes = "2560";
        model.ColorMode = nameof(SensorColorMode.Mono);
        model.SensorPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.RoiX = "0";
        model.RoiY = "0";
        model.RoiWidth = "1280";
        model.RoiHeight = "960";
        model.ReadoutPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.StrideBytes = "2560";
        model.CfaPattern = nameof(ColorFilterArrayPattern.None);
        model.CfaOriginX = string.Empty;
        model.CfaOriginY = string.Empty;

        Assert.IsTrue(model.TryApplyCamera(basis, out var definition, out var errors), string.Join(" ", errors));
        var saved = definition.Deserialize<NamedCameraEquipment>(WebJson)!;
        Assert.AreEqual(1280, saved.Sensor.WidthPixels);
        Assert.AreEqual(960, saved.Sensor.HeightPixels);
        Assert.AreEqual(2560, saved.Sensor.StrideBytes);
        Assert.AreEqual(SensorResponseMode.Monochrome, saved.Sensor.ResponseMode);
        Assert.IsNull(saved.Sensor.SimulationResponse);
        Assert.AreEqual(new SensorCrop(0, 0, 1280, 960), saved.Readout!.Roi);
        Assert.AreEqual(2560, saved.Readout.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.Mono16, saved.Readout.PixelFormat);
        Assert.AreEqual(ColorFilterArrayPattern.None, saved.Readout.CfaPattern);

        model.SensorPixelFormat = nameof(CameraPixelFormat.BayerRggb16);
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("color mode and pixel format", StringComparison.Ordinal)));
        model.SensorPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.ReadoutPixelFormat = nameof(CameraPixelFormat.BayerRggb16);
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Readout pixel format", StringComparison.Ordinal)));
        model.ReadoutPixelFormat = nameof(CameraPixelFormat.Mono16);
        model.CfaPattern = nameof(ColorFilterArrayPattern.Rggb);
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));

        var simulatedPhysical = basis with
        {
            Sensor = basis.Sensor with
            {
                SimulationResponse = new ConfiguredSensorResponseProfile(
                    "v1", 12, 0, 100, 1, 1, 1000, [], 0, "gain", "test")
            }
        };
        Assert.IsFalse(model.TryApplyCamera(simulatedPhysical, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Physical cameras", StringComparison.Ordinal)));

        model.CfaPattern = nameof(ColorFilterArrayPattern.None);
        var virtualBasis = simulatedPhysical with
        {
            Module = new CameraModuleDescriptor("VirtualSky")
        };
        model.ModuleType = "VirtualSky";
        Assert.IsTrue(model.TryApplyCamera(virtualBasis, out definition, out errors), string.Join(" ", errors));
        Assert.IsNotNull(definition.Deserialize<NamedCameraEquipment>(WebJson)!.Sensor.SimulationResponse);
    }

    [TestMethod]
    public void InvalidNumbersEnumsAndReadoutGeometry_ReturnLabelledErrorsWithoutPayload()
    {
        var basis = Camera();
        var model = EquipmentFormModel.FromCamera(basis);
        model.WidthPixels = "1.5";
        model.PixelSizeMicrons = "NaN";
        model.ColorMode = "Other";
        model.BinX = "0";
        Assert.IsFalse(model.TryApplyCamera(basis, out var payload, out var errors));
        Assert.AreEqual(JsonValueKind.Undefined, payload.ValueKind);
        Assert.IsTrue(errors.Any(e => e.Contains("Sensor width (px)", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("Pixel size (um)", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("Sensor color mode", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("Bin X", StringComparison.Ordinal)));

        model = EquipmentFormModel.FromCamera(basis);
        model.RoiWidth = "1281";
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("readout ROI", StringComparison.Ordinal)));
        model = EquipmentFormModel.FromCamera(basis);
        model.CfaPattern = "999";
        Assert.IsFalse(model.TryApplyCamera(basis, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("CFA pattern", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ShippedVirtualOptics_AllowUnchangedZeroFocalDuplicateButRejectInvalidFocalLengths()
    {
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json")));
        var basis = sample.RootElement.GetProperty("rig").GetProperty("optics")
            .Deserialize<OpticsProfile>(SampleJson)!;
        Assert.AreEqual("EquidistantFisheye", basis.ProjectionModel);
        Assert.AreEqual(0d, basis.FocalLengthMillimeters);

        var model = EquipmentFormModel.FromOptics(basis);
        Assert.IsTrue(model.TryApplyOptics(basis, out var duplicate, out var errors), string.Join(" ", errors));
        Assert.AreEqual(basis, duplicate);

        model.FocalLengthMillimeters = "-1";
        Assert.IsFalse(model.TryApplyOptics(basis, out var rejected, out errors));
        Assert.AreSame(basis, rejected);
        Assert.IsTrue(errors.Any(e => e.Contains("Focal length (mm)", StringComparison.Ordinal)));

        model.FocalLengthMillimeters = "0";
        model.ProjectionModel = "Perspective";
        Assert.IsFalse(model.TryApplyOptics(basis, out rejected, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Focal length (mm)", StringComparison.Ordinal)));

        model = EquipmentFormModel.FromOptics(basis with { FocalLengthMillimeters = 2.5 });
        model.FocalLengthMillimeters = "0";
        Assert.IsFalse(model.TryApplyOptics(basis with { FocalLengthMillimeters = 2.5 }, out _, out errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Focal length (mm)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OpticsAndMount_EditsKeepUnexposedCalibrationAndValidateRanges()
    {
        var basis = new OpticsProfile("equidistant", 2.5, 180, 1, LensKind.Fisheye,
            PrincipalPointX: 641.5, PrincipalPointY: 482.5, ImageCircleRadiusPixels: 470,
            FocalLengthXPixels: 540, VerticalFieldOfViewDegrees: 130,
            Crop: new SensorCrop(4, 8, 1200, 900), CalibrationVersion: "cal-7");
        var model = EquipmentFormModel.FromOptics(basis);
        model.RollDegrees = "-2.5";
        Assert.IsTrue(model.TryApplyOptics(basis, out var optics, out var errors), string.Join(" ", errors));
        Assert.AreEqual(-2.5, optics.RollDegrees);
        Assert.AreEqual(basis.PrincipalPointX, optics.PrincipalPointX);
        Assert.AreEqual(basis.Crop, optics.Crop);
        Assert.AreEqual(basis.CalibrationVersion, optics.CalibrationVersion);
        model.FieldOfViewDegrees = "361";
        Assert.IsFalse(model.TryApplyOptics(basis, out optics, out errors));
        Assert.AreSame(basis, optics);
        Assert.IsTrue(errors.Any(e => e.Contains("Field of view (degrees)", StringComparison.Ordinal)));

        var mountBasis = new RigOrientation(85, 270, 0.5);
        var mountModel = EquipmentFormModel.FromMount(mountBasis);
        mountModel.BoresightAzimuthDegrees = "180.25";
        Assert.IsTrue(mountModel.TryApplyMount(mountBasis, out var mount, out errors), string.Join(" ", errors));
        Assert.AreEqual(180.25, mount.BoresightAzimuthDegrees);
        Assert.AreEqual(mountBasis.RollAdjustmentDegrees, mount.RollAdjustmentDegrees);
        mountModel.BoresightAltitudeDegrees = "91";
        Assert.IsFalse(mountModel.TryApplyMount(mountBasis, out mount, out errors));
        Assert.AreSame(mountBasis, mount);
        Assert.IsTrue(errors.Any(e => e.Contains("Boresight altitude (degrees)", StringComparison.Ordinal)));
    }
}
