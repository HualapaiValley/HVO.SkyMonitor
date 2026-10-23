using System.Collections.Immutable;
using System.Text;

namespace HVO.SkyMonitor.Processing;

/// <summary>Editable source with a semantic name, independent of corner placement and graph identity.</summary>
public sealed record FrameFactsCornerPreset(string Id, string Name, string Source);

/// <summary>Atomic conversion result. Failure never returns a partially converted set or clipped text.</summary>
public sealed record FrameFactsPresetConversionResult(
    ImmutableArray<FrameFactsCornerPreset> Presets, string? FailedPresetId, FrameFactsTemplateDiagnostic? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>Explicit pure conversion; neither entry point modifies a graph, product, registry or renderer.</summary>
public static class FrameFactsCornerPresets
{
    public const string CaptureSource = "CAPTURE {{capture.sequence}}\n{{capture.width}} × {{capture.height}} PIXELS\nUTC {{time.captureLegacyUtc}}";
    public const string AcquisitionSource = "ACQUISITION\nSCHEDULE {{acquisition.scheduleProfile}}\nEXPOSURE {{acquisition.exposureSeconds|fixed3}} S\nGAIN {{acquisition.gain|fixed3}}\nOFFSET {{acquisition.offset|fixed1}}\nSETPOINT {{acquisition.sensorSetpointC|fixed1}} C";
    private const string LineageBeforeStack = "LINEAGE\nCATALOG {{lineage.catalog.name}} {{lineage.catalog.version}}\nCALIBRATION {{lineage.calibration.version}} / {{lineage.calibration.shortHash}}\n";
    private const string LineageAfterStack = "\nPROFILE {{lineage.processing.version}} / {{lineage.processing.shortHash}}";

    /// <summary>
    /// Converts the four actual retained legacy corners to literal editable template source. Escapes all template
    /// syntax, preserving exact retained text, including text already clipped by an older producer. Does not infer
    /// tokens or numeric values from display text. Input corner placement only determines the original source map.
    /// </summary>
    public static FrameFactsPresetConversionResult ConvertLegacy(PresentationMetadataFactsV1 facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Convert([
            ("capture", "Capture", facts.TopLeft), ("acquisition", "Acquisition", facts.TopRight),
            ("environment", "Environment", facts.BottomLeft), ("lineage", "Lineage", facts.BottomRight)
        ]);
    }

    /// <summary>
    /// Creates token-based equivalents of the current fixed corner layout from retained inputs. Environment lines
    /// must be the retained DisplayLine strings; they are literals because legacy products lack structured values.
    /// Stack absence is frozen explicitly, not replaced by a zero. Validates parsing and expansion before returning.
    /// </summary>
    public static FrameFactsPresetConversionResult FromRetained(
        FrameFactsRetainedContext context, IReadOnlyList<string>? retainedEnvironmentLines = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (retainedEnvironmentLines?.Count >= FrameFactsTemplate.MaximumLines)
            return Failure("environment", new("frame-facts.too-many-lines", 0));
        var environment = LiteralSource(retainedEnvironmentLines is { Count: > 0 }
            ? retainedEnvironmentLines.Prepend("ENVIRONMENT").ToArray() : ["ENVIRONMENT", "NOT RETAINED"]);
        if (environment.Error is not null) return Failure("environment", environment.Error);
        var stack = context.Tokens.Get("stack.count").Availability == FrameFactsAvailability.Available &&
            context.Tokens.Get("stack.integrationSeconds").Availability == FrameFactsAvailability.Available
            ? "STACK {{stack.count}} {{stack.integrationSeconds|fixed3}} S" : "STACK UNAVAILABLE";
        var presets = ImmutableArray.Create(
            new FrameFactsCornerPreset("capture", "Capture", CaptureSource),
            new FrameFactsCornerPreset("acquisition", "Acquisition", AcquisitionSource),
            new FrameFactsCornerPreset("environment", "Environment", environment.Source!),
            new FrameFactsCornerPreset("lineage", "Lineage", LineageBeforeStack + stack + LineageAfterStack));
        foreach (var preset in presets)
        {
            var parsed = FrameFactsTemplate.Parse(preset.Source, FrameFactsRetainedCatalog.Catalog);
            if (!parsed.IsValid) return Failure(preset.Id, parsed.Error!);
            var evaluated = FrameFactsEvaluator.Evaluate(parsed.Template!, context.Tokens);
            if (!evaluated.IsValid) return Failure(preset.Id, evaluated.Error!);
        }
        return new(presets, null, null);
    }

    private static FrameFactsPresetConversionResult Convert(
        (string Id, string Name, IReadOnlyList<string> Lines)[] inputs)
    {
        var presets = ImmutableArray.CreateBuilder<FrameFactsCornerPreset>();
        foreach (var input in inputs)
        {
            var source = LiteralSource(input.Lines);
            if (source.Error is not null) return Failure(input.Id, source.Error);
            presets.Add(new(input.Id, input.Name, source.Source!));
        }
        return new(presets.ToImmutable(), null, null);
    }

    private static (string? Source, FrameFactsTemplateDiagnostic? Error) LiteralSource(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count > FrameFactsTemplate.MaximumLines)
            return (null, new("frame-facts.too-many-lines", 0));
        var source = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line?.Length > FrameFactsTemplate.MaximumSourceLength)
                return (null, new("frame-facts.source-too-long", FrameFactsTemplate.MaximumSourceLength));
            if (line is null || line.Any(char.IsControl)) return (null, new("frame-facts.invalid-character", source.Length));
            if (index > 0) source.Append('\n');
            foreach (var character in line)
            {
                if (character is '\\' or '*' or '_' or '{' or '}' or '|' or '<' or '>') source.Append('\\');
                source.Append(character);
                if (source.Length > FrameFactsTemplate.MaximumSourceLength)
                    return (null, new("frame-facts.source-too-long", FrameFactsTemplate.MaximumSourceLength));
            }
        }
        // An empty retained line and zero retained lines have distinct meanings in slice A.
        if (lines.Count == 1 && lines[0].Length == 0) source.Append("****");
        var parsed = FrameFactsTemplate.Parse(source.ToString(), FrameFactsRetainedCatalog.Catalog);
        return parsed.IsValid ? (parsed.Template!.Source, null) : (null, parsed.Error);
    }

    private static FrameFactsPresetConversionResult Failure(string id, FrameFactsTemplateDiagnostic error) => new([], id, error);
}
