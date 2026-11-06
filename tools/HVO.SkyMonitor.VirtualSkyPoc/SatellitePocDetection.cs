using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    private static readonly int[] SatelliteCausalContextFrames = [1, 2];
    private static Guid SatelliteEvidenceId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static ProcessingArtifact[] SatelliteArtifacts(CameraFrame[] frames, CameraModuleConfig config)
    {
        var rig = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig);
        return frames.Select((frame, index) => new ProcessingArtifact(
            SatelliteEvidenceId($"raw:{frame.TimestampUtc:O}:{SatelliteHash(frame.PixelData.Span)}"), FrameArtifactRole.Raw, "raw-v1",
            rig, "application/x-hvo-frame", frame.Layout, frame.PixelData,
            frame.TimestampUtc.Add(frame.Metadata.Exposure), frame.Metadata.Exposure,
            new(rig, "configured-optics-v1", "raw-v1", "aperture-only-v1", "synthetic-native12-v1", "fixed-v1", "harness-v2"),
            CaptureSequence: index + 1, ObservationStartedUtc: frame.TimestampUtc,
            ObservationEndedUtc: frame.TimestampUtc.Add(frame.Metadata.Exposure))).ToArray();
    }

    // Only raw artifacts and ordinary geometry enter this method. The caller keeps all simulator and orbital data private.
    private static Linear16TransientExtractionResult SatelliteDetect(ProcessingArtifact[] artifacts,
        ProjectionContext projection, string output, string name)
    {
        var timer = Stopwatch.StartNew();
        var sources = artifacts.Select((artifact, index) =>
        {
            var source = new TransientSourceEvidenceReferenceV1(TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                SatelliteEvidenceId($"evidence:{artifact.ArtifactId}"),
                new(TransientWholeArtifactLocatorV1.CurrentSchemaVersion, TransientSourceLocatorKind.WholeArtifact,
                    new(artifact.ArtifactId, artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256, SatelliteHash(artifact.Payload.Span))),
                artifact.ObservationStartedUtc!.Value, artifact.ObservationEndedUtc!.Value, TransientTimingQuality.Estimated,
                new("virtual-logical-exposure", "v1"));
            var result = TransientDetectorInputFactory.Create(artifact, source, new(64, 4095, 4095));
            if (!result.Validation.IsValid) throw new InvalidOperationException($"Detector input: {result.Validation.ReasonCode} {result.Validation.FieldPath}");
            var input = result.Input!;
            var apertureBits = new byte[Linear16MaskOperations.RequiredByteLength(artifacts[0].Layout!.Width, artifacts[0].Layout!.Height)];
            var width = artifacts[0].Layout!.Width;
            for (var i = 0; i < width * artifacts[0].Layout!.Height; i++)
                if (!projection.ContainsSample(i % width + .5, i / width + .5)) apertureBits[i >> 3] |= (byte)(1 << (i & 7));
            var aperture = Linear16MaskOperations.ReduceRggb16ToDetector(new(width, artifacts[0].Layout!.Height, apertureBits));
            var masks = new[] { TransientDetectorMaskKind.Sky, TransientDetectorMaskKind.ImageCircle,
                TransientDetectorMaskKind.Horizon, TransientDetectorMaskKind.Obstruction, TransientDetectorMaskKind.BadPixel,
                TransientDetectorMaskKind.Star }.Select(kind => TransientDetectorMask.Create(kind,
                    new ProcessingAlgorithmIdentity(kind == TransientDetectorMaskKind.ImageCircle ? "configured-aperture" : "explicit-empty-mask", "v1"),
                    kind == TransientDetectorMaskKind.ImageCircle ? aperture :
                        Linear16MaskOperations.Empty(input.Descriptor.Layout.Width, input.Descriptor.Layout.Height))).ToArray();
            return new TransientTemporalSource(TransientTemporalPosition.N, index + 1, input, new("fixed-response-v1", 1, 1), masks);
        }).ToArray();
        var coldStart = new List<object>();
        TransientTemporalBackgroundOutcome Window(int target) => TransientTemporalBackgroundFactory.Create(new(
            TransientTemporalBackgroundKind.CausalProvisional, sources[target],
            Enumerable.Range(Math.Max(0, target - 2), Math.Min(2, target))
                .Select(i => sources[i] with { Position = (TransientTemporalPosition)(i - target) }).ToArray(),
            [], TimeSpan.FromSeconds(artifacts[target].Integration.TotalSeconds + .001)));
        for (var i = 0; i < 2; i++)
        {
            var outcome = Window(i);
            if (outcome.Status != TransientTemporalBackgroundStatus.Missing)
                throw new InvalidOperationException("Cold start unexpectedly produced a causal background.");
            coldStart.Add(new { frame = i + 1, outcome.Status, outcome.ReasonCode, outcome.Field });
        }
        var backgroundOutcome = Window(2);
        var background = backgroundOutcome.Product ?? throw new InvalidOperationException($"Causal background: {backgroundOutcome.ReasonCode} {backgroundOutcome.Field}");
        var targetInput = sources[2].Input;
        var layout = targetInput.Descriptor.Layout;
        var options = new Linear16TransientExtractionOptions(20, 3, 80, 32, 8, 64, 20000, 2, .98);
        var result = Linear16TransientExtraction.Extract(new(layout.Width, layout.Height, layout.StrideBytes, CameraPixelFormat.Mono16, targetInput.Pixels),
            new(layout.Width, layout.Height, layout.StrideBytes, CameraPixelFormat.Mono16, background.Pixels),
            background.EffectiveMask, targetInput.SaturationMask, options);
        if (result.CandidateLimitExceeded) throw new InvalidOperationException("Detector candidate bound exceeded; result unusable.");
        WriteJson(Path.Combine(output, name + "-detection.json"), new
        {
            temporalMode = TransientTemporalBackgroundKind.CausalProvisional,
            targetFrame = 3,
            contextFrames = SatelliteCausalContextFrames,
            coldStart,
            options,
            result,
            targetInput.Descriptor.SourceToDetectorTransform,
            background = background.Descriptor,
            background.IncludedSamples,
            background.MaskedSamples,
            elapsedSeconds = timer.Elapsed.TotalSeconds,
            limitation = "Unchanged Processing input/temporal factories and Imaging extraction; durable worker/event/association path not exercised. Empty star mask, no event exclusions, same gain/exposure.",
            residualDisplay = "Positive residual fixed 0..500 ADU mapped to 0..255, excluded pixels black."
        });
        File.WriteAllBytes(Path.Combine(output, name + "-detector-target.raw"), targetInput.Pixels.ToArray());
        File.WriteAllBytes(Path.Combine(output, name + "-detector-background.raw"), background.Pixels.ToArray());
        File.WriteAllBytes(Path.Combine(output, name + "-detector-hard-mask.bits"), background.EffectiveMask.Bits.ToArray());
        File.WriteAllBytes(Path.Combine(output, name + "-detector-saturation-mask.bits"), targetInput.SaturationMask.Bits.ToArray());
        var display = new byte[layout.Width * layout.Height * 3];
        for (var i = 0; i < display.Length / 3; i++)
        {
            if (Linear16MaskOperations.IsExcluded(background.EffectiveMask, i % layout.Width, i / layout.Width)) continue;
            var value = Math.Clamp((BinaryPrimitives.ReadUInt16LittleEndian(targetInput.Pixels.Span[(i * 2)..]) -
                BinaryPrimitives.ReadUInt16LittleEndian(background.Pixels.Span[(i * 2)..])) * 255 / 500, 0, 255);
            display[i * 3] = display[i * 3 + 1] = display[i * 3 + 2] = (byte)value;
        }
        SavePng(Path.Combine(output, name + "-residual.png"), display, layout.Width, layout.Height);
        return result;
    }

    private static object[] SatelliteCompare(IReadOnlyList<PixelPoint[]> segments, IReadOnlyList<Linear16TransientComponent> components)
    {
        // All comparisons happen after extraction and include every component; no oracle-based candidate filtering.
        return components.Select((component, index) => (object)new
        {
            candidate = index + 1,
            segments = segments.Select((segment, segmentIndex) =>
            {
                var a = new PixelPoint(component.StartX * 2, component.StartY * 2);
                var b = new PixelPoint(component.EndX * 2, component.EndY * 2);
                var length = double.Hypot(b.X - a.X, b.Y - a.Y);
                var residuals = segment.Select(p => Math.Abs((b.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (b.Y - a.Y)) /
                    Math.Max(length, 1e-12)).ToArray();
                static double Distance(PixelPoint x, PixelPoint y) => double.Hypot(x.X - y.X, x.Y - y.Y);
                return new
                {
                    segment = segmentIndex + 1,
                    rawStart = a,
                    rawEnd = b,
                    rawLengthPixels = length,
                    truthRmsPerpendicularPixels = Math.Sqrt(residuals.Select(v => v * v).Average()),
                    truthMaximumPerpendicularPixels = residuals.Max(),
                    unorderedMaximumEndpointErrorPixels = Math.Min(Math.Max(Distance(a, segment[0]), Distance(b, segment[^1])),
                        Math.Max(Distance(a, segment[^1]), Distance(b, segment[0])))
                };
            }).ToArray()
        }).ToArray();
    }
}
