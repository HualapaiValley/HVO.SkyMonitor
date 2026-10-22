using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public sealed record MixedGainNormalizedMeanOptions();

/// <summary>
/// The compatibility axis of a mixed-gain stack (#1475): the calibration family, the gain-normalization policy and the
/// gain response, by content identity. Frames that share it may differ in gain inside the mixed-gain recipe only; a
/// change of any part is a window boundary. Global keys keep their gain.
/// </summary>
public static class GainNormalizationCompatibility
{
    public const string CurrentSchemaVersion = "hvo-gain-normalization-compatibility-v1";

    /// <summary>
    /// The window compatibility label that lets a raw window cross a gain change. A graph may declare it only on a node
    /// whose recipe is the mixed-gain recipe (<see cref="IsNormalizationRecipe"/>); every other window keeps gain.
    /// </summary>
    public const string WindowLabel = "gain-normalization";

    /// <summary>The only recipe version that may admit a gain difference.</summary>
    public const string RecipeSemanticVersion = "1.0.0";

    /// <summary>The auxiliary input that carries the frozen normalization plan's canonical bytes to the recipe.</summary>
    public const string PlanInputName = "normalization-plan";

    private const string GainSegment = "gain=";
    private const string NormalizedGainPrefix = "normalized:";

    /// <summary>True only for the mixed-gain recipe at the version that defines this compatibility contract.</summary>
    public static bool IsNormalizationRecipe(string? recipeName, string? semanticVersion)
        => string.Equals(recipeName, BuiltInProcessingRecipes.MixedGainNormalizedMean, StringComparison.Ordinal) &&
           string.Equals(semanticVersion, RecipeSemanticVersion, StringComparison.Ordinal);

    /// <summary>
    /// True for the recipes whose products may carry a normalized compatibility: the mixed-gain recipe, which assigns
    /// it, and the rolling mean at the version that averages normalized inputs only when every one is a combined
    /// product of these recipes sharing it, so it propagates an identity it never assigns.
    /// </summary>
    public static bool MayCarryNormalizedGain(string? recipeName, string? semanticVersion)
        => IsNormalizationRecipe(recipeName, semanticVersion) ||
           (string.Equals(recipeName, BuiltInProcessingRecipes.RollingMean, StringComparison.Ordinal) &&
            string.Equals(semanticVersion, RollingMeanSemanticVersion, StringComparison.Ordinal));

    private const string RollingMeanSemanticVersion = "1.0.0";

    /// <summary>True when the compatibility carries a normalized gain segment rather than an actual gain.</summary>
    public static bool IsNormalized(ProcessingCompatibilityIdentity compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        return compatibility.SetpointRegime is { } regime &&
            regime.Split(';').Any(static segment => segment.StartsWith(GainSegment + NormalizedGainPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The compatibility a frame has under a frozen plan: the calibration axis becomes the plan's family and the gain
    /// segment becomes the plan's normalization identity. Two frames that differ only in gain become equal; any other
    /// difference, or another plan's family, policy or response, stays a boundary.
    /// </summary>
    public static ProcessingCompatibilityIdentity Normalize(ProcessingCompatibilityIdentity compatibility, NormalizationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(plan);
        return compatibility with
        {
            Calibration = plan.CalibrationFamilyIdentitySha256,
            SetpointRegime = NormalizeSetpointRegime(compatibility.SetpointRegime, ComputeIdentitySha256(plan))
        };
    }

    public static string ComputeIdentitySha256(
        string calibrationFamilyIdentitySha256,
        string policyIdentitySha256,
        string responseIdentitySha256)
    {
        ArgumentNullException.ThrowIfNull(calibrationFamilyIdentitySha256);
        ArgumentNullException.ThrowIfNull(policyIdentitySha256);
        ArgumentNullException.ThrowIfNull(responseIdentitySha256);
        if (!Sha256(calibrationFamilyIdentitySha256) || !Sha256(policyIdentitySha256) || !Sha256(responseIdentitySha256))
        {
            throw new ArgumentException("Every gain-normalization compatibility part must be a SHA-256 identity.");
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new IdentityDocument(
            CurrentSchemaVersion,
            calibrationFamilyIdentitySha256.ToUpperInvariant(),
            policyIdentitySha256.ToUpperInvariant(),
            responseIdentitySha256.ToUpperInvariant())));
    }

    /// <summary>The compatibility identity a frozen plan was built under.</summary>
    public static string ComputeIdentitySha256(NormalizationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ComputeIdentitySha256(
            plan.CalibrationFamilyIdentitySha256,
            GainNormalizationContractJson.ComputeIdentitySha256(plan.Policy),
            plan.ResponseIdentitySha256);
    }

    /// <summary>
    /// Replaces the value of the single <c>gain=</c> segment of a setpoint regime with the normalization identity and
    /// keeps every other segment, so exposure, offset and temperature setpoint stay boundaries.
    /// </summary>
    public static string NormalizeSetpointRegime(string setpointRegime, string identitySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setpointRegime);
        ArgumentNullException.ThrowIfNull(identitySha256);
        if (!Sha256(identitySha256))
        {
            throw new ArgumentException("The normalization identity must be a SHA-256 identity.", nameof(identitySha256));
        }
        var segments = setpointRegime.Split(';');
        var gainSegments = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].StartsWith(GainSegment, StringComparison.Ordinal))
            {
                segments[index] = GainSegment + NormalizedGainPrefix + identitySha256.ToUpperInvariant();
                gainSegments++;
            }
        }
        return gainSegments == 1
            ? string.Join(';', segments)
            : throw new ArgumentException("The setpoint regime must contain exactly one gain segment.", nameof(setpointRegime));
    }

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record IdentityDocument(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("calibrationFamilyIdentitySha256")] string CalibrationFamilyIdentitySha256,
        [property: JsonPropertyName("policyIdentitySha256")] string PolicyIdentitySha256,
        [property: JsonPropertyName("responseIdentitySha256")] string ResponseIdentitySha256);
}

/// <summary>
/// The opt-in mixed-gain stack (#1475). It reproduces one raw window from its frozen
/// <see cref="NormalizationPlanV1"/> and the frozen light and master payloads the plan names, never from live
/// profiles, masters or gain control. Every input is calibrated at its own gain with its own member's masters and the
/// mean is encoded once at the reference gain and the plan's output scale.
/// </summary>
/// <remarks>
/// <para>
/// Masters are normalized Linear16 (<see cref="CalibrationMasterBuilder"/>): code <c>m</c> is the level
/// <c>black + m × (white − black) / 65535</c> of the plan's input layout. This recipe maps them back to stored codes
/// once per member tuple and light exposure (<see cref="MasterPlanesAlgorithmVersion"/>): bias as a stored code;
/// dark as the signed bias-subtracted signal scaled from the dark to the light exposure; flat as the bias- and
/// dark-corrected response divided by its frame mean, in 10^-6 units, with a non-positive or out-of-range response
/// left invalid so that sample carries no weight; any non-zero defect code marks a defect. Each value is rounded once,
/// ties to even, with integer arithmetic.
/// </para>
/// <para>
/// The kernel runs in bands of whole rows, so working buffers stay bounded by the band and the number of distinct
/// member tuples while the result is byte-identical to one full-frame call. Each light and each distinct master
/// payload is hashed once per window and must match the plan; a master is never substituted. A light must also carry
/// the plan's capture ID, descriptor identity, exposure, gain, offset and exact temperature, and a master the exposure
/// the plan froze for it: the dark and flat scaling use only those frozen exposures.
/// </para>
/// </remarks>
internal sealed class MixedGainNormalizedMeanRecipe : IProcessingRecipe
{
    internal const string PlanInput = GainNormalizationCompatibility.PlanInputName;
    internal const string MasterPlanesAlgorithmVersion = "mixed-gain-master-planes-v1";
    internal const int BandPhotosites = 16_384;

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.MixedGainNormalizedMean,
        GainNormalizationCompatibility.RecipeSemanticVersion,
        "mixed-gain-normalized-mean-v1",
        ProcessingOperationKind.Window);

    public JsonElement NormalizeOptions(JsonElement options)
        => ProcessingRecipeSupport.Normalize(ProcessingRecipeSupport.ParseOptions<MixedGainNormalizedMeanOptions>(options));

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Execute(request, identity, cancellationToken));
    }

    private static ProcessingOutcome Execute(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        if (request.Input.Kind != ProcessingInputKind.Raw || request.Input.Role != FrameArtifactRole.Raw)
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.OutputBackedWindow, nameof(request.Input));
        }
        var candidates = ProcessingRecipeSupport.ResolveMany(request, out var inputFailure);
        if (candidates.Count == 0)
        {
            return inputFailure!;
        }

        var planInput = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, PlanInput, StringComparison.OrdinalIgnoreCase));
        if (planInput is null)
        {
            return ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingNormalizationPlan, PlanInput);
        }
        if (planInput.Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            !string.Equals(planInput.SchemaVersion, NormalizationPlanV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidNormalizationPlan, PlanInput);
        }
        var (plan, _) = NormalizationPlanContractJson.Parse(planInput.Payload);
        if (plan is null || !string.Equals(
                planInput.IdentitySha256,
                ProcessingIdentity.ComputePayloadSha256(planInput.Payload),
                StringComparison.OrdinalIgnoreCase))
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidNormalizationPlan, PlanInput);
        }
        var primaryPlanInput = plan.Inputs.SingleOrDefault(static input => input.WindowPosition == 0);
        if (primaryPlanInput is null)
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidNormalizationPlan, PlanInput);
        }
        if (primaryPlanInput.Disposition != NormalizationPlanInputDispositions.Selected)
        {
            return ProcessingOutcome.Skipped(ProcessingReasonCodes.NormalizationPrimaryExcluded, PlanInput);
        }
        if (!TryResolveCodeMapping(plan.InputLayout, out var mapping))
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.UnsupportedFormat, "inputLayout");
        }

        var byId = new Dictionary<Guid, ProcessingArtifact>();
        foreach (var input in request.Inputs)
        {
            if (input.ArtifactId == Guid.Empty || !byId.TryAdd(input.ArtifactId, input))
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLineage, nameof(request.Inputs));
            }
        }
        var candidateIds = candidates.Select(static candidate => candidate.ArtifactId).ToHashSet();

        var selected = plan.Inputs
            .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
            .ToArray();
        var lights = new ProcessingArtifact[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planned = selected[index];
            if (!byId.TryGetValue(planned.ArtifactId, out var light))
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.NormalizationPlanInputMismatch, nameof(request.Inputs));
            }
            if (light.Role != FrameArtifactRole.Raw || light.ProductKind != ProcessingProductKind.PixelData)
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.OutputBackedWindow, nameof(request.Inputs));
            }
            if (!candidateIds.Contains(light.ArtifactId) ||
                light.CaptureId != planned.CaptureId ||
                !string.Equals(light.DescriptorIdentitySha256, planned.DescriptorSha256, StringComparison.OrdinalIgnoreCase) ||
                !ProcessingRecipeSupport.TryValidateFrame(light, out var layout, out _) ||
                layout != plan.InputLayout ||
                light.Integration != planned.Exposure ||
                light.Conditions is not { } conditions || conditions.Gain != planned.Gain || conditions.Offset != planned.Offset ||
                conditions.TemperatureC != planned.TemperatureC ||
                !string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(light.Payload),
                    planned.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.NormalizationPlanInputMismatch, nameof(request.Inputs));
            }
            lights[index] = light;
        }

        var masterLayout = CalibrationMasterBuilder.CreateNormalizedLayout(plan.InputLayout);
        var lightIds = selected.Select(static input => input.ArtifactId).ToHashSet();
        var masters = new Dictionary<Guid, ProcessingArtifact>();
        var masterOrder = new List<ProcessingArtifact>();
        foreach (var master in selected.SelectMany(static input => input.Masters!))
        {
            if (masters.ContainsKey(master.ArtifactId))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (lightIds.Contains(master.ArtifactId) || !byId.TryGetValue(master.ArtifactId, out var artifact))
            {
                return ProcessingOutcome.TerminalFailure(CalibrationFamilyReasonCodes.MasterMissing, $"{master.Kind}-master");
            }
            if (artifact.Layout != masterLayout || artifact.Payload.Length != masterLayout.ByteLength ||
                artifact.Integration != master.Exposure)
            {
                return ProcessingOutcome.TerminalFailure(CalibrationFamilyReasonCodes.MasterMismatch, $"{master.Kind}-master");
            }
            if (!string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(artifact.Payload),
                    master.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ProcessingOutcome.TerminalFailure(CalibrationFamilyReasonCodes.MasterCorrupt, $"{master.Kind}-master");
            }
            masters.Add(master.ArtifactId, artifact);
            masterOrder.Add(artifact);
        }

        var pixels = Compute(plan, selected, lights, masters, mapping, out var planeFailure, cancellationToken);
        if (pixels is null)
        {
            return planeFailure!;
        }

        var primaryLight = lights[Array.FindIndex(selected, static input => input.WindowPosition == 0)];
        var compatibility = GainNormalizationCompatibility.Normalize(primaryLight.Compatibility, plan);
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Combined,
            request.OutputVariant,
            "application/x-hvo-linear-frame",
            masterLayout,
            pixels,
            identity,
            [
                new("gain-normalized-mean", Linear16GainNormalizedMean.AlgorithmVersion),
                new("mixed-gain-master-planes", MasterPlanesAlgorithmVersion)
            ],
            [.. lights, .. masterOrder],
            TimeSpan.FromTicks(selected.Sum(static input => input.Exposure.Ticks)),
            compatibility);
        return ProcessingOutcome.Produced(product);
    }

    private static byte[]? Compute(
        NormalizationPlanV1 plan,
        NormalizationPlanInputV1[] selected,
        ProcessingArtifact[] lights,
        Dictionary<Guid, ProcessingArtifact> masters,
        CodeMapping mapping,
        out ProcessingOutcome? failure,
        CancellationToken cancellationToken)
    {
        var layout = plan.InputLayout;
        var width = layout.Width;
        var height = layout.Height;
        var bandRows = Math.Max(1, BandPhotosites / width);
        if (bandRows > 1 && bandRows % 2 != 0)
        {
            bandRows--;
        }
        bandRows = Math.Min(bandRows, height);
        var bandCapacity = checked(bandRows * width);

        var tuples = new Dictionary<MemberTuple, MasterPlanes>();
        var inputPlanes = new MasterPlanes[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planned = selected[index];
            var key = new MemberTuple(
                planned.Masters![0].ArtifactId,
                planned.Masters[1].ArtifactId,
                planned.Masters[2].ArtifactId,
                planned.Masters[3].ArtifactId,
                planned.Exposure);
            if (!tuples.TryGetValue(key, out var planes))
            {
                planes = MasterPlanes.Create(
                    masters[key.Bias],
                    masters[key.Dark],
                    masters[key.Flat],
                    masters[key.Defect],
                    planned.Masters[1].Exposure,
                    planned.Masters[2].Exposure,
                    planned.Exposure,
                    mapping,
                    bandCapacity,
                    cancellationToken);
                if (planes is null)
                {
                    failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidCalibrationFlat, "flat-master");
                    return null;
                }
                tuples.Add(key, planes);
            }
            inputPlanes[index] = planes;
        }

        var lightBands = new ushort[selected.Length][];
        for (var index = 0; index < lightBands.Length; index++)
        {
            lightBands[index] = new ushort[bandCapacity];
        }
        var saturationCode = checked((ushort)plan.Kernel.SaturationCode);
        var output = new byte[checked(width * height * 2)];
        var kernelInputs = new GainNormalizedMeanInput[selected.Length];
        for (var firstRow = 0; firstRow < height; firstRow += bandRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = Math.Min(bandRows, height - firstRow);
            var photosites = rows * width;
            foreach (var planes in tuples.Values)
            {
                failure = planes.Fill(firstRow, rows, cancellationToken);
                if (failure is not null)
                {
                    return null;
                }
            }
            for (var index = 0; index < selected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadBand(lights[index], firstRow, rows, lightBands[index]);
                var planes = inputPlanes[index];
                kernelInputs[index] = new GainNormalizedMeanInput(
                    lightBands[index].AsMemory(0, photosites),
                    planes.Bias.AsMemory(0, photosites),
                    planes.Dark.AsMemory(0, photosites),
                    planes.Flat.AsMemory(0, photosites),
                    planes.Defect.AsMemory(0, photosites),
                    saturationCode,
                    selected[index].Coefficient!.ElectronsPerAduE12);
            }
            var band = Linear16GainNormalizedMean.Compute(
                width,
                rows,
                layout.PixelFormat,
                kernelInputs,
                plan.ReferenceCoefficient.ElectronsPerAduE12,
                plan.Kernel.OutputScale,
                photosites,
                cancellationToken);
            band.PixelData.Span.CopyTo(output.AsSpan(firstRow * width * 2));
        }
        failure = null;
        return output;
    }

    private static void ReadBand(ProcessingArtifact light, int firstRow, int rows, ushort[] destination)
    {
        var layout = light.Layout!;
        var payload = light.Payload.Span;
        for (var row = 0; row < rows; row++)
        {
            var source = payload.Slice((firstRow + row) * layout.StrideBytes, layout.Width * 2);
            var target = destination.AsSpan(row * layout.Width, layout.Width);
            for (var x = 0; x < target.Length; x++)
            {
                target[x] = BinaryPrimitives.ReadUInt16LittleEndian(source[(x * 2)..]);
            }
        }
    }

    /// <summary>
    /// The exact factor from a master's level code to a stored code: the level code space is the stored container, or
    /// native samples whose stored code is the native code (identity, right-aligned) or its full-range scaling.
    /// </summary>
    private static bool TryResolveCodeMapping(FrameLayoutDescriptor layout, out CodeMapping mapping)
    {
        mapping = default;
        if (layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
            !double.IsInteger(black) || !double.IsInteger(white) || black < 0 || white > ushort.MaxValue || white <= black ||
            layout.SampleDepthBits is < 1 or > 16)
        {
            return false;
        }
        var nativeMaximum = (1L << layout.SampleDepthBits) - 1;
        (long Numerator, long Denominator)? factor = layout.LevelCodeSpace switch
        {
            FrameLevelCodeSpace.StoredContainer => (1, 1),
            FrameLevelCodeSpace.NativeSample => layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => (1, 1),
                FrameStoredCodeTransform.FullRangeScaledV1 => (ushort.MaxValue, nativeMaximum),
                _ => null
            },
            _ => null
        };
        if (factor is not { } value)
        {
            return false;
        }
        mapping = new CodeMapping((long)black, (long)(white - black), value.Numerator, value.Denominator);
        return true;
    }

    /// <summary>Rounds a rational to the nearest integer, ties to even, for a positive denominator.</summary>
    internal static Int128 RoundHalfEven(Int128 numerator, Int128 denominator)
    {
        var quotient = Int128.DivRem(numerator, denominator);
        var doubled = Int128.Abs(quotient.Remainder) * 2;
        if (doubled > denominator || doubled == denominator && !Int128.IsEvenInteger(quotient.Quotient))
        {
            return quotient.Quotient + (numerator < 0 ? -1 : 1);
        }
        return quotient.Quotient;
    }

    private readonly record struct CodeMapping(long Black, long Span, long Numerator, long Denominator);

    private readonly record struct MemberTuple(Guid Bias, Guid Dark, Guid Flat, Guid Defect, TimeSpan LightExposure);

    /// <summary>The kernel planes of one member tuple and light exposure, filled one band at a time.</summary>
    private sealed class MasterPlanes
    {
        private readonly ProcessingArtifact _bias;
        private readonly ProcessingArtifact _dark;
        private readonly ProcessingArtifact _flat;
        private readonly ProcessingArtifact _defect;
        private readonly CodeMapping _mapping;
        private readonly Int128 _lightTicks;
        private readonly long _darkTicks;
        private readonly long _flatTicks;
        private readonly Int128 _flatSum;
        private readonly Int128 _photosites;

        private MasterPlanes(
            ProcessingArtifact bias,
            ProcessingArtifact dark,
            ProcessingArtifact flat,
            ProcessingArtifact defect,
            long darkTicks,
            long flatTicks,
            TimeSpan lightExposure,
            CodeMapping mapping,
            int bandCapacity,
            Int128 flatSum)
        {
            _bias = bias;
            _dark = dark;
            _flat = flat;
            _defect = defect;
            _mapping = mapping;
            _lightTicks = lightExposure.Ticks;
            _darkTicks = darkTicks;
            _flatTicks = flatTicks;
            Bias = new ushort[bandCapacity];
            Dark = new int[bandCapacity];
            Flat = new int[bandCapacity];
            Defect = new byte[bandCapacity];
            _photosites = bias.Payload.Length / 2;
            _flatSum = flatSum;
        }

        /// <summary>
        /// The planes of one tuple at the frozen dark and flat exposures, or <see langword="null"/> when the flat's frame
        /// mean is not positive.
        /// </summary>
        public static MasterPlanes? Create(
            ProcessingArtifact bias,
            ProcessingArtifact dark,
            ProcessingArtifact flat,
            ProcessingArtifact defect,
            TimeSpan darkExposure,
            TimeSpan flatExposure,
            TimeSpan lightExposure,
            CodeMapping mapping,
            int bandCapacity,
            CancellationToken cancellationToken)
        {
            Int128 sum = 0;
            var photosites = bias.Payload.Length / 2;
            var biasCodes = bias.Payload.Span;
            var darkCodes = dark.Payload.Span;
            var flatCodes = flat.Payload.Span;
            var darkTicks = darkExposure.Ticks;
            var flatTicks = flatExposure.Ticks;
            for (var index = 0; index < photosites; index++)
            {
                if ((index & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                sum += FlatResponse(biasCodes, darkCodes, flatCodes, darkTicks, flatTicks, index);
            }
            return sum > 0
                ? new MasterPlanes(bias, dark, flat, defect, darkTicks, flatTicks, lightExposure, mapping, bandCapacity, sum)
                : null;
        }

        public ushort[] Bias { get; }

        public int[] Dark { get; }

        public int[] Flat { get; }

        public byte[] Defect { get; }

        /// <summary>Fills the planes for one band, or returns the failure of a bias or dark outside the kernel's range.</summary>
        public ProcessingOutcome? Fill(int firstRow, int rows, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var width = _bias.Layout!.Width;
            var first = firstRow * width;
            var count = rows * width;
            var levelDenominator = (Int128)ushort.MaxValue * _mapping.Denominator;
            var biasCodes = _bias.Payload.Span;
            var darkCodes = _dark.Payload.Span;
            var flatCodes = _flat.Payload.Span;
            var defectCodes = _defect.Payload.Span;
            for (var offset = 0; offset < count; offset++)
            {
                var index = first + offset;
                var bias = Read(biasCodes, index);
                var storedBias = RoundHalfEven(
                    ((Int128)_mapping.Black * ushort.MaxValue + (Int128)bias * _mapping.Span) * _mapping.Numerator,
                    levelDenominator);
                if (storedBias > ushort.MaxValue)
                {
                    return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidCalibrationBias, "bias-master");
                }
                Bias[offset] = (ushort)storedBias;

                var dark = RoundHalfEven(
                    (Int128)(Read(darkCodes, index) - bias) * _mapping.Span * _mapping.Numerator * _lightTicks,
                    levelDenominator * _darkTicks);
                if (Int128.Abs(dark) > Linear16GainNormalizedMean.MaximumDarkMagnitude)
                {
                    return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidCalibrationDark, "dark-master");
                }
                Dark[offset] = (int)dark;

                var response = FlatResponse(biasCodes, darkCodes, flatCodes, _darkTicks, _flatTicks, index);
                Flat[offset] = response <= 0
                    ? 0
                    : (int)Int128.Min(
                        RoundHalfEven(response * Linear16GainNormalizedMean.FlatScale * _photosites, _flatSum),
                        Linear16GainNormalizedMean.MaximumFlatScaledValue + 1);
                Defect[offset] = Read(defectCodes, index) == 0 ? (byte)0 : (byte)1;
            }
            return null;
        }

        /// <summary>The flat's bias- and dark-corrected response at photosite <paramref name="index"/>, times the dark exposure.</summary>
        private static Int128 FlatResponse(
            ReadOnlySpan<byte> biasCodes,
            ReadOnlySpan<byte> darkCodes,
            ReadOnlySpan<byte> flatCodes,
            long darkTicks,
            long flatTicks,
            int index)
        {
            var bias = Read(biasCodes, index);
            return (Int128)(Read(flatCodes, index) - bias) * darkTicks - (Int128)(Read(darkCodes, index) - bias) * flatTicks;
        }

        private static int Read(ReadOnlySpan<byte> codes, int index)
            => BinaryPrimitives.ReadUInt16LittleEndian(codes[(index * 2)..]);
    }
}
