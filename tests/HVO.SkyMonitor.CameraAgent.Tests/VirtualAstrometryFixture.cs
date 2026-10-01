using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>Qualification input boundary: no projected scene, source IDs, optical truth or truth mask.</summary>
internal sealed record VirtualAstrometryPixels(ReadOnlyMemory<byte> Payload, FrameLayoutDescriptor Layout,
    DateTimeOffset StartUtc, TimeSpan Exposure, double Gain, string? ParentPayloadSha256 = null)
{
    internal static VirtualAstrometryPixels FromCapture(CameraFrame frame) => new(frame.PixelData.ToArray(),
        frame.Layout ?? throw new InvalidOperationException("An authoritative pixel layout is required."),
        frame.TimestampUtc, frame.Metadata.Exposure, frame.Metadata.Gain);
}

internal sealed record VirtualAstrometryProfile(string Name, CameraModuleConfig Config);

internal static class VirtualAstrometryFixture
{
    internal static readonly ObserverLocation Observer = new(35.347, -113.878, 1000);
    internal static readonly AstrometricSolverOptions SolverOptions = new(MaximumCatalogMagnitude: 5);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static IReadOnlyList<VirtualAstrometryProfile> Profiles(int seed)
    {
        var mono = LoadRig("virtual-asi174.full.json") with
        {
            Orientation = new(82, 137, 13),
            Readout = new(new(0, 0, 1936, 1216), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample, 64, 4095)
        };
        var cfa = LoadRig("virtual-asi178mc.full.json") with { Orientation = new(84, 73, 25) };
        var roi = mono.Readout! with { Roi = new(240, 96, 1440, 1024) };
        var bin = mono.Readout! with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 };
        return
        [
            Profile("mono-native", mono, seed),
            Profile("mono-roi", mono with { Readout = roi }, seed),
            Profile("mono-bin2", mono with { Readout = bin }, seed),
            Profile("mono-roi-bin2", mono with { Readout = roi with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 } }, seed),
            Profile("mono-mirror", mono with { Optics = mono.Optics with { HorizontalFlip = !mono.Optics.HorizontalFlip } }, seed),
            Profile("mono-roll", mono with { Orientation = new(82, 137, -57) }, seed),
            Profile("cfa-native", cfa, seed)
        ];
    }

    private static CameraRigConfig LoadRig(string filename)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, filename)));
        return document.RootElement.GetProperty("rig").Deserialize<CameraRigConfig>(JsonOptions)!;
    }

    private static VirtualAstrometryProfile Profile(string name, CameraRigConfig rig, int seed)
    {
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = seed,
            MaximumMagnitude = 5,
            MaximumResults = 2000,
            MagnitudeZeroElectronsPerSecond = 60000,
            BackgroundElectronsPerSecond = 2,
            PsfSigmaPixels = 1,
            PsfRadiusPixels = 4,
            VignettingStrength = .25,
            ShotNoiseEnabled = true,
            Asi174Sensor = new() { Enabled = rig.Sensor.PixelFormat == CameraPixelFormat.Mono16 }
        };
        return new(name, new(new(Observer.LatitudeDegrees, Observer.LongitudeDegrees, Observer.ElevationMeters, "America/Phoenix"),
            new("VirtualSky", JsonSerializer.SerializeToElement(options, JsonOptions)), rig,
            CapturePipelineConfig.Empty, "virtual-astrometry-qualification"));
    }

    internal static AstrometricCalibration NominalCalibration(VirtualAstrometryProfile profile)
    {
        // Nominal intrinsics are declared sensor/optics specifications. Capture pose never enters this model.
        var nominalFocal = profile.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.Mono16
            ? 595.84 / (Math.PI / 2) / 1.037 : 735.553926 / 1.037;
        var nominalRig = profile.Config.Rig with
        {
            Orientation = new(52, 290, -41),
            Optics = profile.Config.Rig.Optics with
            {
                FocalLengthMillimeters = 0,
                FocalLengthXPixels = nominalFocal,
                FocalLengthYPixels = nominalFocal,
                CalibrationVersion = "virtual-astrometry-nominal-v1"
            }
        };
        var readout = SensorReadoutResolver.Resolve(nominalRig.Sensor, nominalRig.Readout!);
        return new(RigProjectionContextFactory.Create(nominalRig), nominalRig.Optics.CalibrationVersion,
            CaptureContractJson.ComputeCanonicalJsonSha256(readout.Layout));
    }

    internal static StellarDetectionOptions DetectionOptions(FrameLayoutDescriptor layout) => new(
        MinimumPeakAboveBackground: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 32 : 8,
        MinimumMinorSigma: .2, MaximumMajorSigma: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 2.3 : 1.8,
        MaximumVarianceRatio: 2.5, IsolationRadiusPixels: 12d / (layout.Readout?.BinX ?? 1));

    internal static (double[] Samples, bool[] Valid) Decode(VirtualAstrometryPixels input)
    {
        var layout = input.Layout;
        var validation = layout.Validate();
        if (!validation.IsValid || input.Payload.Length != layout.ByteLength ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.Packing != FrameSamplePacking.ByteAligned || layout.ContainerDepthBits != 16 ||
            layout.ByteOrder is not (FrameByteOrder.LittleEndian or FrameByteOrder.BigEndian) ||
            layout.WhiteLevel is null || layout.LevelCodeSpace is not (FrameLevelCodeSpace.NativeSample or FrameLevelCodeSpace.StoredContainer))
            throw new ArgumentException("Qualification requires an exact, validated linear 16-bit payload with declared levels.", nameof(input));
        var white = layout.WhiteLevel.Value;
        if (layout.LevelCodeSpace == FrameLevelCodeSpace.NativeSample)
        {
            white = layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.IdentityV1 => white,
                FrameStoredCodeTransform.FullRangeScaledV1 => white * ushort.MaxValue / ((1 << layout.SampleDepthBits) - 1),
                FrameStoredCodeTransform.LeftShiftedV1 => white * (1 << (16 - layout.SampleDepthBits)),
                _ => throw new ArgumentException("Opaque sample codes cannot establish saturation.", nameof(input))
            };
        }
        var samples = new double[checked(layout.Width * layout.Height)];
        var valid = new bool[samples.Length];
        for (var y = 0; y < layout.Height; y++)
            for (var x = 0; x < layout.Width; x++)
            {
                var bytes = input.Payload.Span.Slice(y * layout.StrideBytes + x * 2, 2);
                var value = layout.ByteOrder == FrameByteOrder.LittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
                samples[y * layout.Width + x] = value;
                valid[y * layout.Width + x] = value < white;
            }
        return (samples, valid);
    }

    internal static StellarDetectionResult Measure(VirtualAstrometryPixels input, AstrometricCalibration nominal)
    {
        var (samples, valid) = Decode(input);
        var aperture = nominal.Projection;
        if (aperture.WidthPixels != input.Layout.Width || aperture.HeightPixels != input.Layout.Height)
            throw new ArgumentException("Nominal aperture must describe the measured pixel view.", nameof(nominal));
        // This is the declared optical aperture, independent of pose, stars, or the captured scene.
        for (var y = 0; y < input.Layout.Height; y++)
            for (var x = 0; x < input.Layout.Width; x++)
                valid[y * input.Layout.Width + x] &= aperture.ContainsSample(x + .5, y + .5);
        if (input.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
        {
            var pattern = (input.Layout.Readout?.CfaOriginX ?? 0, input.Layout.Readout?.CfaOriginY ?? 0) switch
            {
                (0, 0) => BayerPattern.Rggb,
                (1, 0) => BayerPattern.Grbg,
                (0, 1) => BayerPattern.Gbrg,
                (1, 1) => BayerPattern.Bggr,
                _ => throw new ArgumentException("Invalid declared CFA origin.", nameof(input))
            };
            var luminance = LinearBayerReconstruction.Reconstruct(samples, valid, input.Layout.Width, input.Layout.Height, pattern).ToLuminance();
            return StellarDetector.Detect(luminance.Pixels.Span, luminance.ValidMask.Span,
                input.Layout.Width, input.Layout.Height, DetectionOptions(input.Layout));
        }
        return StellarDetector.Detect(samples, valid, input.Layout.Width, input.Layout.Height, DetectionOptions(input.Layout));
    }

    internal static AstrometricFrameContext FrameContext(VirtualAstrometryPixels input)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(input.Payload.Span));
        var descriptorHash = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "virtual-astrometry-logical-exposure-v1",
            input.Layout,
            input.StartUtc,
            input.Exposure,
            input.Gain,
            input.ParentPayloadSha256,
            payloadHash
        });
        var sourceId = new Guid(Convert.FromHexString(descriptorHash)[..16]);
        var captureId = new Guid(Convert.FromHexString(payloadHash)[..16]);
        return new(captureId, sourceId, descriptorHash, payloadHash,
            CaptureContractJson.ComputeCanonicalJsonSha256(Observer), Observer, input.StartUtc, input.StartUtc + input.Exposure,
            StellarDetector.AlgorithmVersion, CaptureContractJson.ComputeCanonicalJsonSha256(DetectionOptions(input.Layout)));
    }

    internal static (VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels) DeriveCfaPhase(
        VirtualAstrometryProfile nativeProfile, VirtualAstrometryPixels native, int x, int y)
    {
        if (native.Layout.PixelFormat != CameraPixelFormat.BayerRggb16 || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentException("Only declared one-photosite phase views are supported.");
        var width = native.Layout.Width - x; var height = native.Layout.Height - y;
        var rig = nativeProfile.Config.Rig with
        {
            Readout = nativeProfile.Config.Rig.Readout! with
            {
                Roi = new(x, y, width, height),
                CfaOriginX = x,
                CfaOriginY = y,
                StrideBytes = width * 2
            }
        };
        var layout = SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout).Layout;
        var pixels = new byte[checked((int)layout.ByteLength)];
        for (var row = 0; row < height; row++)
            native.Payload.Span.Slice((row + y) * native.Layout.StrideBytes + x * 2, width * 2)
                .CopyTo(pixels.AsSpan(row * layout.StrideBytes));
        var profile = new VirtualAstrometryProfile($"cfa-derived-phase-{x}{y}", nativeProfile.Config with { Rig = rig });
        return (profile, new(pixels, layout, native.StartUtc, native.Exposure, native.Gain,
            Convert.ToHexString(SHA256.HashData(native.Payload.Span))));
    }

    internal static AstrometricSolveResult Solve(VirtualAstrometryPixels input, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, StellarDetectionResult measured, AstrometricFrameAssessment? prior = null)
    {
        var detections = measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        return prior is null ? AstrometricSolver.Solve(FrameContext(input), nominal, catalog, detections, SolverOptions)
            : AstrometricSolver.Refine(FrameContext(input), nominal, catalog, detections, prior, SolverOptions);
    }
}
