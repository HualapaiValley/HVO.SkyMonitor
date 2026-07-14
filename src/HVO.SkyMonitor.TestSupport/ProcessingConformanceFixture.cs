using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.TestSupport;

public static class ProcessingConformanceFixture
{
    public static readonly Guid CaptureId = Guid.Parse("93000000-0000-0000-0000-000000000001");
    public static readonly Guid ArtifactId = Guid.Parse("93000000-0000-0000-0000-000000000002");
    public static readonly DateTimeOffset CapturedUtc = DateTimeOffset.Parse(
        "2025-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    public static readonly ReadOnlyMemory<byte> Payload = new byte[]
    {
        0, 0,
        0, 64,
        0, 128,
        255, 255
    };

    public const string ExpectedPayloadHex = "000000FF";
    public const string ExpectedChecksumSha256 = "E3820096CB82366B860B8A4E668453A7AAAF423AF03BDF289FA308EA03A79332";
    public const string ExpectedRecipeIdentitySha256 = "8EBC03FA468DE991D5B80040359752A5232D9C278B91045B180EA64C2CACAE6E";

    public static RecipeIdentityDescriptor SourceRecipe { get; } = RecipeIdentityDescriptor.Create(
        "virtual-raw",
        "1.0.0",
        "virtual-raw-v1",
        JsonSerializer.SerializeToElement(new { seed = 2025 }));

    public static string ProcessingProfileSha256 { get; } =
        CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(Array.Empty<CaptureProcessingStepConfig>()));

    public static CameraModuleConfig CameraConfig { get; } = new(
        new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
        new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(
            new SensorProfile("fixture", 2, 2, 5.86, SensorColorMode.Mono,
                CameraPixelFormat.Mono16, SensorRecipeVersion: "sensor-v1"),
            new OpticsProfile("equidistant", 1, 180, 0, CalibrationVersion: "none-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 150, 150),
            ProfileVersion: "rig-v1"));

    public static string RigProfileSha256 { get; } =
        RigProjectionContextFactory.CreateProfileHashSha256(CameraConfig.Rig);

    public static string CalibrationProfileSha256 { get; } = HashText("calibration:none-v1");

    public static string MaskProfileSha256 { get; } = HashText("mask:none");

    public static string SensorProfileSha256 { get; } =
        CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(CameraConfig.Rig.Sensor));

    public static ProcessingCompatibilityIdentity Compatibility { get; } = new(
        RigProfileSha256,
        RigProfileSha256,
        CalibrationProfileSha256,
        MaskProfileSha256,
        SensorProfileSha256,
        "exposure=20000;gain=150;offset=;temperatureSetpoint=",
        ProcessingProfileSha256);

    public static FrameLayoutDescriptor Layout { get; } = new(
        2,
        2,
        4,
        CameraPixelFormat.Mono16,
        FrameByteOrder.LittleEndian,
        16,
        16,
        FrameSamplePacking.ByteAligned,
        ColorFilterArrayPattern.None,
        0,
        ushort.MaxValue,
        8);

    public static ProcessingExecutionRequest CreateRequest(ProcessingArtifact artifact) => new(
        BuiltInProcessingRecipes.EncodedPreview,
        JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
        ProcessingInputSelector.Raw("source"),
        [artifact],
        "conformance");

    public static ProcessingArtifact CreateProcessingArtifact() => new(
        ArtifactId,
        FrameArtifactRole.Raw,
        "source",
        ProcessingIdentity.CreateRecipeIdentity(SourceRecipe).IdentitySha256,
        "application/x-hvo-frame",
        Layout,
        Payload,
        CapturedUtc,
        TimeSpan.FromSeconds(20),
        Compatibility);

    public static ReconstructionDescriptor CreateDescriptor() => new(
        new CaptureIdentityDescriptor("agent-93", "rig-93", 1, CaptureId),
        new CaptureTimingDescriptor(
            CapturedUtc,
            CapturedUtc,
            CapturedUtc.AddSeconds(20),
            CapturedUtc.AddSeconds(20.1),
            CapturedUtc.AddSeconds(20.2)),
        new CaptureControlDescriptor(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            150,
            150,
            null,
            null,
            null,
            null),
        new CaptureProfileSet(
            new ProfileIdentityDescriptor("rig", "rig-v1", RigProfileSha256),
            new ProfileIdentityDescriptor("calibration", "none-v1", CalibrationProfileSha256),
            new ProfileIdentityDescriptor("mask", "none", MaskProfileSha256),
            new ProfileIdentityDescriptor("sensor", "sensor-v1", SensorProfileSha256),
            new ProfileIdentityDescriptor("processing", "pipeline-v1", ProcessingProfileSha256)),
        Layout,
        new ArtifactDescriptor(
            ArtifactId,
            FrameArtifactRole.Raw,
            "VirtualSky",
            "source",
            CapturedUtc.AddSeconds(20.1),
            [],
            SourceRecipe,
            "application/x-hvo-frame",
            PayloadChecksum.ComputeSha256(Payload.Span)));

    public static void AssertProduct(ProcessingProduct product)
    {
        AssertEqual(ExpectedPayloadHex, Convert.ToHexString(product.Payload.Span), nameof(ExpectedPayloadHex));
        AssertEqual(ExpectedChecksumSha256, product.ChecksumSha256, nameof(ExpectedChecksumSha256));
        AssertEqual(ExpectedRecipeIdentitySha256, product.Recipe.IdentitySha256, nameof(ExpectedRecipeIdentitySha256));
        AssertEqual("conformance", product.Variant, nameof(product.Variant));
        AssertEqual(ArtifactId, product.SourceArtifactIds.Single(), nameof(product.SourceArtifactIds));
        AssertEqual(FrameArtifactRole.Preview, product.Role, nameof(product.Role));
        AssertEqual("application/x-hvo-packed-image", product.MediaType, nameof(product.MediaType));
        AssertEqual(CameraPixelFormat.Mono8, product.Layout?.PixelFormat, nameof(product.Layout));
        AssertEqual(Compatibility, product.Compatibility, nameof(product.Compatibility));
        AssertEqual(TimeSpan.FromSeconds(20), product.TotalIntegration, nameof(product.TotalIntegration));
        AssertEqual(
            ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Preview,
                "conformance",
                ExpectedRecipeIdentitySha256,
                [ArtifactId]),
            product.OutputIdentitySha256,
            nameof(product.OutputIdentitySha256));
        AssertEqual(1, product.Algorithms.Count, nameof(product.Algorithms));
        AssertEqual("display-stretch", product.Algorithms[0].Name, nameof(product.Algorithms));
        AssertEqual(Mono16DisplayStretch.AlgorithmVersion, product.Algorithms[0].Version, nameof(product.Algorithms));
        AssertEqual(2, product.Layout?.Width, nameof(product.Layout.Width));
        AssertEqual(2, product.Layout?.Height, nameof(product.Layout.Height));
        AssertEqual(2, product.Layout?.StrideBytes, nameof(product.Layout.StrideBytes));
        AssertEqual(4L, product.Layout?.ByteLength, nameof(product.Layout.ByteLength));
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertEqual<T>(T expected, T actual, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Processing conformance mismatch for {field}: expected '{expected}', actual '{actual}'.");
        }
    }
}
