using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Edits only exposed equipment fields; camera options are restored by the store from a basis revision.</summary>
internal sealed class EquipmentFormModel
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public string ModuleType { get; set; } = string.Empty;
    public string SensorName { get; set; } = string.Empty;
    public string WidthPixels { get; set; } = string.Empty;
    public string HeightPixels { get; set; } = string.Empty;
    public string SensorStrideBytes { get; set; } = string.Empty;
    public string PixelSizeMicrons { get; set; } = string.Empty;
    public string ColorMode { get; set; } = nameof(SensorColorMode.Mono);
    public string SensorPixelFormat { get; set; } = nameof(CameraPixelFormat.Mono16);
    public bool HasReadout { get; set; }
    public string RoiX { get; set; } = string.Empty;
    public string RoiY { get; set; } = string.Empty;
    public string RoiWidth { get; set; } = string.Empty;
    public string RoiHeight { get; set; } = string.Empty;
    public string BinX { get; set; } = string.Empty;
    public string BinY { get; set; } = string.Empty;
    public string BinningAlgorithm { get; set; } = nameof(FrameBinningAlgorithm.IdentityV1);
    public string ReadoutPixelFormat { get; set; } = nameof(CameraPixelFormat.Mono16);
    public string SampleDepthBits { get; set; } = string.Empty;
    public string ContainerDepthBits { get; set; } = string.Empty;
    public string Packing { get; set; } = nameof(FrameSamplePacking.ByteAligned);
    public string StoredCodeTransform { get; set; } = nameof(FrameStoredCodeTransform.IdentityV1);
    public string LevelCodeSpace { get; set; } = nameof(FrameLevelCodeSpace.NativeSample);
    public string BlackLevel { get; set; } = string.Empty;
    public string WhiteLevel { get; set; } = string.Empty;
    public string StrideBytes { get; set; } = string.Empty;
    public string ByteOrder { get; set; } = nameof(SampleByteOrder.LittleEndian);
    public string CfaPattern { get; set; } = nameof(ColorFilterArrayPattern.None);
    public string CfaOriginX { get; set; } = string.Empty;
    public string CfaOriginY { get; set; } = string.Empty;

    public string ProjectionModel { get; set; } = string.Empty;
    public string FocalLengthMillimeters { get; set; } = string.Empty;
    public string FieldOfViewDegrees { get; set; } = string.Empty;
    public string RollDegrees { get; set; } = string.Empty;
    public string LensKind { get; set; } = nameof(AgentCore.LensKind.Unspecified);
    public bool HorizontalFlip { get; set; }
    public string BoresightAltitudeDegrees { get; set; } = string.Empty;
    public string BoresightAzimuthDegrees { get; set; } = string.Empty;
    public string RollAdjustmentDegrees { get; set; } = string.Empty;

    private static string Text(double value) => value.ToString("R", Invariant);
    private static string Text(int value) => value.ToString(Invariant);
    private static string Text(double? value) => value is { } number ? Text(number) : string.Empty;
    private static string Text(int? value) => value is { } number ? Text(number) : string.Empty;

    public static EquipmentFormModel FromCamera(NamedCameraEquipment camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var sensor = camera.Sensor;
        var readout = camera.Readout;
        return new EquipmentFormModel
        {
            ModuleType = camera.Module.Type,
            SensorName = sensor.Name,
            WidthPixels = Text(sensor.WidthPixels),
            HeightPixels = Text(sensor.HeightPixels),
            SensorStrideBytes = Text(sensor.StrideBytes),
            PixelSizeMicrons = Text(sensor.PixelSizeMicrons),
            ColorMode = sensor.ColorMode.ToString(),
            SensorPixelFormat = sensor.PixelFormat.ToString(),
            HasReadout = readout is not null,
            RoiX = readout is null ? string.Empty : Text(readout.Roi.X),
            RoiY = readout is null ? string.Empty : Text(readout.Roi.Y),
            RoiWidth = readout is null ? string.Empty : Text(readout.Roi.Width),
            RoiHeight = readout is null ? string.Empty : Text(readout.Roi.Height),
            BinX = readout is null ? string.Empty : Text(readout.BinX),
            BinY = readout is null ? string.Empty : Text(readout.BinY),
            BinningAlgorithm = readout?.BinningAlgorithm.ToString() ?? nameof(FrameBinningAlgorithm.IdentityV1),
            ReadoutPixelFormat = readout?.PixelFormat.ToString() ?? sensor.PixelFormat.ToString(),
            SampleDepthBits = readout is null ? string.Empty : Text(readout.SampleDepthBits),
            ContainerDepthBits = readout is null ? string.Empty : Text(readout.ContainerDepthBits),
            Packing = readout?.Packing.ToString() ?? nameof(FrameSamplePacking.ByteAligned),
            StoredCodeTransform = readout?.StoredCodeTransform.ToString() ?? nameof(FrameStoredCodeTransform.IdentityV1),
            LevelCodeSpace = readout?.LevelCodeSpace.ToString() ?? nameof(FrameLevelCodeSpace.NativeSample),
            BlackLevel = Text(readout?.BlackLevel),
            WhiteLevel = Text(readout?.WhiteLevel),
            StrideBytes = Text(readout?.StrideBytes),
            ByteOrder = readout?.ByteOrder.ToString() ?? nameof(SampleByteOrder.LittleEndian),
            CfaPattern = readout?.CfaPattern.ToString() ?? nameof(ColorFilterArrayPattern.None),
            CfaOriginX = Text(readout?.CfaOriginX),
            CfaOriginY = Text(readout?.CfaOriginY)
        };
    }

    public static EquipmentFormModel FromOptics(OpticsProfile optics) => new()
    {
        ProjectionModel = optics.ProjectionModel,
        FocalLengthMillimeters = Text(optics.FocalLengthMillimeters),
        FieldOfViewDegrees = Text(optics.FieldOfViewDegrees),
        RollDegrees = Text(optics.RollDegrees),
        LensKind = optics.LensKind.ToString(),
        HorizontalFlip = optics.HorizontalFlip
    };

    public static EquipmentFormModel FromMount(RigOrientation mount) => new()
    {
        BoresightAltitudeDegrees = Text(mount.BoresightAltitudeDegrees),
        BoresightAzimuthDegrees = Text(mount.BoresightAzimuthDegrees),
        RollAdjustmentDegrees = Text(mount.RollAdjustmentDegrees)
    };

    public bool TryApplyCamera(NamedCameraEquipment basis, out JsonElement definition, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ModuleType) || ModuleType != basis.Module.Type)
            problems.Add("Camera module type must match the basis revision.");
        if (string.IsNullOrWhiteSpace(SensorName)) problems.Add("Sensor name is required.");
        var colorMode = Choice<SensorColorMode>(ColorMode, "Sensor color mode", problems);
        var pixelFormat = Choice<CameraPixelFormat>(SensorPixelFormat, "Sensor pixel format", problems);
        if ((colorMode == SensorColorMode.Mono && pixelFormat != CameraPixelFormat.Mono16 && pixelFormat != CameraPixelFormat.Mono8) ||
            (colorMode == SensorColorMode.Color && pixelFormat != CameraPixelFormat.BayerRggb16))
            problems.Add("Sensor color mode and pixel format must agree.");
        if (basis.Module.Type != "VirtualSky" && basis.Sensor.SimulationResponse is not null)
            problems.Add("Physical cameras cannot use a simulation response.");
        var sensor = basis.Sensor with
        {
            Name = SensorName.Trim(),
            WidthPixels = Integer(WidthPixels, "Sensor width (px)", 1, int.MaxValue, problems),
            HeightPixels = Integer(HeightPixels, "Sensor height (px)", 1, int.MaxValue, problems),
            StrideBytes = OptionalInteger(SensorStrideBytes, "Sensor stride (bytes)", 1, int.MaxValue, problems),
            PixelSizeMicrons = Number(PixelSizeMicrons, "Pixel size (um)", double.Epsilon, double.MaxValue, problems),
            ColorMode = colorMode,
            PixelFormat = pixelFormat,
            ResponseMode = colorMode == SensorColorMode.Mono ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw
        };
        SensorReadoutProfile? readout = null;
        if (HasReadout)
        {
            readout = new SensorReadoutProfile(
                new SensorCrop(Integer(RoiX, "ROI X (px)", 0, int.MaxValue, problems), Integer(RoiY, "ROI Y (px)", 0, int.MaxValue, problems),
                    Integer(RoiWidth, "ROI width (px)", 1, int.MaxValue, problems), Integer(RoiHeight, "ROI height (px)", 1, int.MaxValue, problems)),
                Integer(BinX, "Bin X", 1, int.MaxValue, problems), Integer(BinY, "Bin Y", 1, int.MaxValue, problems),
                Choice<FrameBinningAlgorithm>(BinningAlgorithm, "Binning algorithm", problems),
                Choice<CameraPixelFormat>(ReadoutPixelFormat, "Readout pixel format", problems),
                Integer(SampleDepthBits, "Sample depth (bits)", 1, 32, problems),
                Integer(ContainerDepthBits, "Container depth (bits)", 1, 32, problems),
                Choice<FrameSamplePacking>(Packing, "Sample packing", problems),
                Choice<FrameStoredCodeTransform>(StoredCodeTransform, "Stored code transform", problems),
                Choice<FrameLevelCodeSpace>(LevelCodeSpace, "Level code space", problems),
                OptionalNumber(BlackLevel, "Black level", problems), OptionalNumber(WhiteLevel, "White level", problems),
                OptionalInteger(StrideBytes, "Stride (bytes)", 1, int.MaxValue, problems),
                Choice<SampleByteOrder>(ByteOrder, "Byte order", problems),
                Choice<ColorFilterArrayPattern>(CfaPattern, "CFA pattern", problems),
                OptionalInteger(CfaOriginX, "CFA origin X (px)", 0, int.MaxValue, problems),
                OptionalInteger(CfaOriginY, "CFA origin Y (px)", 0, int.MaxValue, problems));
            if (problems.Count == 0)
            {
                if (readout.PixelFormat != sensor.PixelFormat ||
                    (colorMode == SensorColorMode.Mono && readout.CfaPattern != ColorFilterArrayPattern.None) ||
                    (colorMode == SensorColorMode.Color && readout.CfaPattern != ColorFilterArrayPattern.Rggb) ||
                    (colorMode == SensorColorMode.Mono && (readout.CfaOriginX is not null || readout.CfaOriginY is not null)))
                    problems.Add("Readout pixel format and CFA must agree with sensor color mode.");
            }
            if (problems.Count == 0)
            {
                try { SensorReadoutResolver.Resolve(sensor, readout); }
                catch (ArgumentException) { problems.Add("Sensor readout ROI, binning, and sample layout must be valid for the sensor."); }
                catch (OverflowException) { problems.Add("Sensor readout dimensions are out of range."); }
            }
        }
        errors = problems;
        // Never serialize provider options into the browser's submission, even when the caller holds a full basis.
        definition = problems.Count == 0
            ? JsonSerializer.SerializeToElement(new { module = new { type = basis.Module.Type }, sensor, readout }, WebJson)
            : default;
        return problems.Count == 0;
    }

    public bool TryApplyOptics(OpticsProfile basis, out OpticsProfile optics, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectionModel)) problems.Add("Projection model is required.");
        var candidate = basis with
        {
            ProjectionModel = ProjectionModel.Trim(),
            LensKind = Choice<LensKind>(LensKind, "Lens kind", problems),
            FocalLengthMillimeters = Number(FocalLengthMillimeters, "Focal length (mm)", double.Epsilon, double.MaxValue, problems),
            FieldOfViewDegrees = Number(FieldOfViewDegrees, "Field of view (degrees)", double.Epsilon, 360, problems),
            RollDegrees = Number(RollDegrees, "Optics roll (degrees)", -360, 360, problems),
            HorizontalFlip = HorizontalFlip
        };
        errors = problems;
        optics = problems.Count == 0 ? candidate : basis;
        return problems.Count == 0;
    }

    public bool TryApplyMount(RigOrientation basis, out RigOrientation mount, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var candidate = basis with
        {
            BoresightAltitudeDegrees = Number(BoresightAltitudeDegrees, "Boresight altitude (degrees)", -90, 90, problems),
            BoresightAzimuthDegrees = Number(BoresightAzimuthDegrees, "Boresight azimuth (degrees)", 0, 360, problems),
            RollAdjustmentDegrees = Number(RollAdjustmentDegrees, "Roll adjustment (degrees)", -360, 360, problems)
        };
        errors = problems;
        mount = problems.Count == 0 ? candidate : basis;
        return problems.Count == 0;
    }

    private static int Integer(string value, string label, int min, int max, List<string> errors)
    {
        if (int.TryParse(value, NumberStyles.Integer, Invariant, out var number) && number >= min && number <= max) return number;
        errors.Add($"{label} must be a whole number from {min} to {max}.");
        return 0;
    }

    private static int? OptionalInteger(string value, string label, int min, int max, List<string> errors)
        => string.IsNullOrWhiteSpace(value) ? null : Integer(value, label, min, max, errors);

    private static double Number(string value, string label, double min, double max, List<string> errors)
    {
        if (double.TryParse(value, NumberStyles.Float, Invariant, out var number) && double.IsFinite(number) && number >= min && number <= max) return number;
        errors.Add($"{label} must be a number from {min.ToString(Invariant)} to {max.ToString(Invariant)}.");
        return 0;
    }

    private static double? OptionalNumber(string value, string label, List<string> errors)
        => string.IsNullOrWhiteSpace(value) ? null : Number(value, label, -double.MaxValue, double.MaxValue, errors);

    private static T Choice<T>(string value, string label, List<string> errors) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, false, out var choice) && Enum.IsDefined(choice)) return choice;
        errors.Add($"{label} is not a supported value.");
        return default;
    }
}
