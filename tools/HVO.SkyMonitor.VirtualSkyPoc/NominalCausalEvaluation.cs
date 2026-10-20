using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.VirtualSkyPoc;

// Tool-only envelopes. These are deliberately separate from CameraFrame.Metadata and production contracts.
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by strict JSON deserialization.")]
internal sealed record NominalCircle(string Mode, double? CenterX, double? CenterY, double? Radius);
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by strict JSON deserialization.")]
internal sealed record NominalMasks(string Sky, string Horizon, string Obstruction, string BadPixel, string Star,
    NominalCircle ImageCircle);
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by strict JSON deserialization.")]
internal sealed record NominalRawFrame(Guid CaptureId, long CaptureSequence, string Role, string Path,
    string Sha256, string RecipeIdentitySha256, DateTimeOffset ObservationStartedUtc, DateTimeOffset ObservationEndedUtc,
    TransientTimingQuality TimingQuality, FrameLayoutDescriptor Layout, TransientLinearLevelsV1 Levels,
    ProcessingCompatibilityIdentity Compatibility, TransientSensitivityV1 Sensitivity, NominalMasks Masks,
    string? GeometryIdentitySha256, ObserverLocation? Observer);
internal sealed record NominalRawManifest(string Schema, double MaximumAdjacentStartIntervalSeconds,
    IReadOnlyList<NominalRawFrame> Frames);
internal sealed record NominalFileRead(string Path, int Bytes, string Sha256);
internal sealed record NominalFrameOutcome(NominalRawFrame Raw, TransientDetectorInputDescriptorV1 Input,
    TransientTemporalBackgroundOutcomeDescriptorV1 Temporal, Linear16TransientExtractionResult? Extraction);
internal sealed record NominalEvaluationReport(string Schema, string ToolVersion, string ManifestSha256,
    string Status, IReadOnlyList<string> Reasons, Linear16TransientExtractionOptions Options,
    double MaximumAdjacentStartIntervalSeconds, TransientTemporalBackgroundKind TemporalKind,
    IReadOnlyList<NominalFileRead> FilesRead, IReadOnlyList<NominalFrameOutcome> Frames);

internal static class NominalCausalEvaluation
{
    internal const int MaximumFrames = 64;
    internal const int MaximumManifestBytes = 512 * 1024;
    internal const int MaximumReportBytes = 16 * 1024 * 1024;
    internal const string ToolVersion = "nominal-causal-evaluator-v1";
    internal static readonly Linear16TransientExtractionOptions Options = new(20, 3, 80, 32, 8, 64, 20000, 2, .98);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 48,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static NominalEvaluationReport Evaluate(string publicRoot, Action<string>? onRead = null)
    {
        var reads = new List<NominalFileRead>();
        var manifestBytes = Read(publicRoot, "input.json", MaximumManifestBytes, null, reads, onRead);
        var manifest = Parse<NominalRawManifest>(manifestBytes);
        ValidateManifest(manifest);

        // Validate every declared file/hash before any pixel conversion or extraction. Never enumerate a scene.
        foreach (var frame in manifest.Frames)
            _ = Resolve(publicRoot, frame.Path);
        var payloads = manifest.Frames.Select(frame => Read(publicRoot, frame.Path,
            checked((int)frame.Layout.ByteLength), frame.Sha256, reads, onRead)).ToArray();
        for (var index = 0; index < payloads.Length; index++)
            Require(payloads[index].Length == manifest.Frames[index].Layout.ByteLength, "raw-byte-length");

        var outcomes = new List<NominalFrameOutcome>();
        var history = new Queue<TransientTemporalSource>();
        for (var index = 0; index < manifest.Frames.Count; index++)
        {
            var frame = manifest.Frames[index];
            var artifact = new ProcessingArtifact(frame.CaptureId, FrameArtifactRole.Raw, "raw-v1",
                frame.RecipeIdentitySha256, "application/x-hvo-frame", frame.Layout, payloads[index],
                frame.ObservationEndedUtc, frame.ObservationEndedUtc - frame.ObservationStartedUtc, frame.Compatibility,
                CaptureSequence: frame.CaptureSequence, ObservationStartedUtc: frame.ObservationStartedUtc,
                ObservationEndedUtc: frame.ObservationEndedUtc);
            var source = new TransientSourceEvidenceReferenceV1(TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                frame.CaptureId, new(TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new(frame.CaptureId, FrameArtifactRole.Raw, "raw-v1", frame.RecipeIdentitySha256, frame.Sha256)),
                frame.ObservationStartedUtc, frame.ObservationEndedUtc, frame.TimingQuality,
                new("explicit-public-raw-manifest", ToolVersion));
            var created = TransientDetectorInputFactory.Create(artifact, source, frame.Levels);
            Require(created.Validation.IsValid, $"detector-input:{created.Validation.ReasonCode}:{created.Validation.FieldPath}");
            var input = created.Input!;
            var target = new TransientTemporalSource(TransientTemporalPosition.N, frame.CaptureSequence, input,
                frame.Sensitivity, CreateMasks(frame, input));
            // Positions describe the supplied preceding slots; retain the real sequence IDs so the factory can
            // reject dropped captures rather than relabeling nonconsecutive IDs as contiguous history.
            var context = history.Select((previous, slot) => previous with
            {
                Position = (TransientTemporalPosition)(slot - history.Count)
            }).ToArray();
            var temporal = TransientTemporalBackgroundFactory.Create(new(TransientTemporalBackgroundKind.CausalProvisional,
                target, context, [], TimeSpan.FromSeconds(manifest.MaximumAdjacentStartIntervalSeconds)));
            Linear16TransientExtractionResult? extraction = null;
            if (temporal.Product is { } background)
            {
                var layout = input.Descriptor.Layout;
                var hardMask = Linear16TransientExtraction.CreateHardExclusionMask(background.EffectiveMask,
                    input.SaturationMask, background.NoSupportMask, target.Masks.Select(mask => mask.Mask).ToArray());
                extraction = Linear16TransientExtraction.Extract(
                    new(layout.Width, layout.Height, layout.StrideBytes, CameraPixelFormat.Mono16, input.Pixels),
                    new(layout.Width, layout.Height, background.Descriptor.Layout.StrideBytes, CameraPixelFormat.Mono16, background.Pixels),
                    hardMask, input.SaturationMask, Options);
            }
            outcomes.Add(new(frame, input.Descriptor, TransientTemporalBackgroundJson.CreateDescriptor(temporal), extraction));
            history.Enqueue(target);
            if (history.Count > 2) _ = history.Dequeue();
        }
        var reasons = new List<string>();
        if (outcomes.Any(frame => frame.Temporal.Status != TransientTemporalBackgroundStatus.Produced))
            reasons.Add("temporal-context-unavailable");
        if (outcomes.Any(frame => frame.Extraction?.CandidateLimitExceeded == true)) reasons.Add("extraction-work-limit");
        return new("nominal-causal-evaluation-v1", ToolVersion, Hash(manifestBytes),
            reasons.Count == 0 ? "evaluated-all-targets" : "evaluated-all-targets-with-unavailable-or-limited-results",
            reasons, Options, manifest.MaximumAdjacentStartIntervalSeconds, TransientTemporalBackgroundKind.CausalProvisional, reads, outcomes);
    }

    private static TransientDetectorMask[] CreateMasks(NominalRawFrame frame, TransientDetectorInput input)
    {
        var width = input.Descriptor.Layout.Width;
        var height = input.Descriptor.Layout.Height;
        var circle = frame.Masks.ImageCircle;
        var aperture = new byte[Linear16MaskOperations.RequiredByteLength(frame.Layout.Width, frame.Layout.Height)];
        if (circle.Mode == "circle")
        {
            for (var y = 0; y < frame.Layout.Height; y++)
                for (var x = 0; x < frame.Layout.Width; x++)
                    if (double.Hypot(x + .5 - circle.CenterX!.Value, y + .5 - circle.CenterY!.Value) > circle.Radius!.Value)
                    {
                        var pixel = y * frame.Layout.Width + x;
                        aperture[pixel >> 3] |= (byte)(1 << (pixel & 7));
                    }
        }
        var reduced = Linear16MaskOperations.ReduceRggb16ToDetector(new(frame.Layout.Width, frame.Layout.Height, aperture));
        return new[] { TransientDetectorMaskKind.Sky, TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon, TransientDetectorMaskKind.Obstruction, TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star }.Select(kind => TransientDetectorMask.Create(kind,
                new(kind == TransientDetectorMaskKind.ImageCircle ? "declared-aperture" : "explicit-empty-mask", "v1"),
                kind == TransientDetectorMaskKind.ImageCircle ? reduced : Linear16MaskOperations.Empty(width, height))).ToArray();
    }

    internal static void ValidateManifest(NominalRawManifest manifest)
    {
        Require(manifest.Schema == "nominal-causal-raw-v1" &&
            double.IsFinite(manifest.MaximumAdjacentStartIntervalSeconds) &&
            manifest.MaximumAdjacentStartIntervalSeconds is > 0 and <= 3600 &&
            manifest.Frames is { Count: > 0 and <= MaximumFrames }, "manifest-bounds");
        var ids = new HashSet<Guid>();
        long previous = 0;
        foreach (var frame in manifest.Frames)
        {
            Require(frame is not null && frame.CaptureId != Guid.Empty && ids.Add(frame.CaptureId) &&
                frame.CaptureSequence > previous && frame.CaptureSequence < long.MaxValue && frame.Role == "raw" &&
                Sha(frame.Sha256) && Sha(frame.RecipeIdentitySha256) &&
                (frame.GeometryIdentitySha256 is null || Sha(frame.GeometryIdentitySha256)) &&
                frame.Path == "raw/" + frame.Sha256 + ".raw", "raw-identity-or-path");
            previous = frame.CaptureSequence;
            if (frame.Observer is { } observer) observer.Validate();
            Require(frame.ObservationStartedUtc != default && frame.ObservationEndedUtc != default &&
                frame.ObservationStartedUtc.Offset == TimeSpan.Zero && frame.ObservationEndedUtc.Offset == TimeSpan.Zero &&
                frame.ObservationEndedUtc > frame.ObservationStartedUtc &&
                frame.ObservationEndedUtc - frame.ObservationStartedUtc <= TimeSpan.FromSeconds(60) &&
                Enum.IsDefined(frame.TimingQuality), "raw-interval");
            var layout = frame.Layout;
            Require(layout is not null && layout.Width is >= 2 and <= 640 && layout.Height is >= 2 and <= 640 &&
                layout.Width % 2 == 0 && layout.Height % 2 == 0 && layout.StrideBytes == layout.Width * 2 &&
                layout.ByteLength == (long)layout.StrideBytes * layout.Height &&
                layout.PixelFormat == CameraPixelFormat.BayerRggb16 && layout.ByteOrder == FrameByteOrder.LittleEndian &&
                layout.SampleDepthBits == 12 && layout.ContainerDepthBits == 16 &&
                layout.Packing == FrameSamplePacking.ByteAligned && layout.CfaPattern == ColorFilterArrayPattern.Rggb &&
                layout.BlackLevel == 64 && layout.WhiteLevel == 4095 && layout.Readout is null &&
                layout.StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1 &&
                layout.LevelCodeSpace == FrameLevelCodeSpace.NativeSample && frame.Levels == new TransientLinearLevelsV1(64, 4095, 4095),
                "nominal-native12-rggb-layout-only");
            var compatibility = frame.Compatibility;
            Require(compatibility is not null && new[] { compatibility.Rig, compatibility.Orientation, compatibility.Calibration,
                compatibility.Mask, compatibility.Sensor, compatibility.SetpointRegime, compatibility.ProcessingProfile }.All(Text) &&
                (compatibility.LocationIdentitySha256 is null || Sha(compatibility.LocationIdentitySha256)), "raw-compatibility");
            Require(frame.Sensitivity is { Numerator: > 0, Denominator: > 0 } && Text(frame.Sensitivity.ResponseIdentity), "raw-response");
            var masks = frame.Masks;
            Require(masks is not null && new[] { masks.Sky, masks.Horizon, masks.Obstruction, masks.BadPixel, masks.Star }
                .All(value => value == "explicit-empty-v1") && masks.ImageCircle is not null, "explicit-masks-required");
            var circle = masks.ImageCircle;
            Require(circle.Mode == "empty" ? circle.CenterX is null && circle.CenterY is null && circle.Radius is null :
                circle.Mode == "circle" && circle.CenterX is { } x && double.IsFinite(x) && x >= 0 && x <= layout.Width &&
                circle.CenterY is { } y && double.IsFinite(y) && y >= 0 && y <= layout.Height &&
                circle.Radius is > 0 and <= 640, "declared-aperture-only");
        }
    }

    internal static byte[] Read(string root, string relative, int maximumBytes, string? expectedHash,
        List<NominalFileRead> reads, Action<string>? onRead)
    {
        var path = Resolve(root, relative);
        onRead?.Invoke(relative);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length > 0 && stream.Length <= maximumBytes, "input-file-bound:" + relative);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "input-file-changed:" + relative);
        var hash = Hash(bytes);
        Require(expectedHash is null || string.Equals(hash, expectedHash, StringComparison.Ordinal), "input-hash:" + relative);
        reads.Add(new(relative, bytes.Length, hash));
        return bytes;
    }

    internal static string Resolve(string root, string relative)
    {
        Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) &&
            !relative.Contains('\\', StringComparison.Ordinal) && relative.Split('/').All(part => part is not ("" or "." or "..")),
            "relative-public-path-required");
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(rootPath, relative));
        Require(path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal), "outside-public-root");
        CheckPath(path);
        return path;
    }

    private static void CheckPath(string path)
    {
        string[] forbidden = ["truth", "scenario", "expected", "label", "metadata", "diagnostic", "brightness", "seed"];
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var name = Path.GetFileName(current);
            Require(!forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)), "forbidden-private-path");
            var attributes = File.GetAttributes(current);
            Require((attributes & FileAttributes.ReparsePoint) == 0, "symlink-or-reparse-path");
            if (Path.GetDirectoryName(current) == current) break;
        }
    }

    internal static void Publish<T>(string path, T report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
        Require(bytes.Length <= MaximumReportBytes, "report-byte-bound");
        var fullPath = Path.GetFullPath(path);
        CheckPath(Path.GetDirectoryName(fullPath)!);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static T Parse<T>(ReadOnlySpan<byte> bytes) where T : class
        => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new ArgumentException("Null nominal input.");
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool Sha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();
    internal static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new ArgumentException(reason);
    }
}
