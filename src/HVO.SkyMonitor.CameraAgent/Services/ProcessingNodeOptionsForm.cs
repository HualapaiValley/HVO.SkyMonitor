using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Transient form input only; the shared codec owns validation and lossless JSON edits.</summary>
public sealed class ProcessingNodeOptionsForm
{
    private readonly JsonElement? _source;
    private readonly Dictionary<string, string> _inputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal);

    public ProcessingNodeOptionsForm(ProcessingNodeOptionsDescriptor descriptor, JsonElement? source)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _source = source;
        foreach (var option in descriptor.Options)
        {
            // One bad field must not replace the other displayed values with defaults.
            var single = new ProcessingNodeOptionsDescriptor(descriptor.DescriptorId, descriptor.StepAlias + ".field",
                descriptor.StepVersion, descriptor.Title, descriptor.Help, [option]);
            var decoded = ProcessingNodeOptionsCodec.Decode(single, Source);
            _inputs[option.Key] = Format(decoded.IsValid ? decoded.Get(option.Key).Value : option.DefaultValue);
        }
        TryBuild(out _, out _);
    }

    public ProcessingNodeOptionsDescriptor Descriptor { get; }
    public IReadOnlyList<ProcessingGraphDiagnostic> Diagnostics { get; private set; } = [];
    private JsonElement Source => _source ?? JsonSerializer.SerializeToElement(new { });

    // The descriptor is bound to the actual producer version. A future producer cannot silently reuse this panel.
    public static ProcessingNodeOptionsDescriptor? DescriptorFor(string alias, JsonElement? options = null)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (PreviewOptionDescriptors.IsPreviewAlias(alias))
        {
            return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, PreviewOptionDescriptors.VersionFor(options)).Descriptor;
        }
        var version = alias.ToUpperInvariant() switch
        {
            "IMAGEQUALITY" => CaptureScalarOptionDescriptors.ImageQuality.StepVersion,
            "TELEMETRY" => CaptureScalarOptionDescriptors.Telemetry.StepVersion,
            "VIRTUALSKYCLOUDOBSERVATION" => CaptureScalarOptionDescriptors.VirtualSkyCloudObservation.StepVersion,
            "WEATHERCLOUDOVERLAY" => WeatherCloudOverlayOptionDescriptors.Descriptor.StepVersion,
            "CLOUDASSESSMENT" => CloudAssessmentOptionDescriptors.Descriptor.StepVersion,
            "CALIBRATION" => CalibrationOptionDescriptors.Normalization.StepVersion,
            "PROJECTEDSCENE" => "canonical-projected-scene-v1",
            "ANNOTATION" => "projected-annotation-v4",
            "CLOUDPRESENTATIONLAYER" => PresentationLayerProducers.CloudProducerVersion,
            "ENVIRONMENTPRESENTATIONLAYER" => PresentationLayerProducers.MetadataProducerVersion,
            "JPEGENCODING" => "packed-jpeg-v1",
            _ => string.Equals(alias, FrameFactsPresentation.StepAlias, StringComparison.OrdinalIgnoreCase)
                ? FrameFactsPresentation.ProducerVersion : PresentationLayerProducers.SceneProducerVersion
        };
        return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, version).Descriptor;
    }

    public string Get(string key) => _inputs[key];

    public string NullableNumberStatus(string key)
    {
        if (_edited.Contains(key))
            return Get(key).Length == 0 ? "No limit (cleared)." : "Limit edited.";
        var option = Descriptor.Find(key)!;
        var single = new ProcessingNodeOptionsDescriptor(Descriptor.DescriptorId, Descriptor.StepAlias + ".field",
            Descriptor.StepVersion, Descriptor.Title, Descriptor.Help, [option]);
        var result = ProcessingNodeOptionsCodec.Decode(single, Source);
        if (!result.IsValid)
            return "Correct the invalid value before saving.";
        var decoded = result.Get(key);
        return !decoded.IsExplicit ? "Not specified; using default." : decoded.Value.AsNullableWholeNumber() is null
            ? "No limit (explicitly selected)." : "Limit set.";
    }

    public void Set(string key, string value)
    {
        _inputs[key] = value;
        _edited.Add(key);
        TryBuild(out _, out _);
    }

    public bool TryBuild(out JsonElement? options, out IReadOnlyList<ProcessingGraphDiagnostic> diagnostics)
    {
        var edits = new Dictionary<string, ProcessingOptionValue>(StringComparer.Ordinal);
        var errors = new List<ProcessingGraphDiagnostic>();
        foreach (var option in Descriptor.Options.Where(option => _edited.Contains(option.Key)))
        {
            if (Parse(option.Kind, _inputs[option.Key]) is { } value)
            {
                edits.Add(option.Key, value);
            }
            else
            {
                errors.Add(new(ProcessingOptionReasonCodes.WrongType, $"effectiveOptions.{option.Key}",
                    $"{option.Label}: enter a valid {option.Kind} value."));
            }
        }
        var encoded = ProcessingNodeOptionsCodec.Encode(Descriptor, Source, edits);
        errors.AddRange(encoded.Diagnostics);
        Diagnostics = diagnostics = errors;
        options = errors.Count > 0 ? null : encoded.Options;
        if (errors.Count == 0 && options!.Value.GetRawText() == Source.GetRawText())
        {
            options = _source;
        }
        return errors.Count == 0;
    }

    private static ProcessingOptionValue? Parse(ProcessingOptionKind kind, string input) => kind switch
    {
        ProcessingOptionKind.Toggle when bool.TryParse(input, out var toggle) => ProcessingOptionValue.FromToggle(toggle),
        ProcessingOptionKind.WholeNumber when long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => ProcessingOptionValue.FromWholeNumber(whole),
        ProcessingOptionKind.NullableWholeNumber when input.Length == 0 => ProcessingOptionValue.FromNullableWholeNumber(null),
        ProcessingOptionKind.NullableWholeNumber when long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nullableWhole) => ProcessingOptionValue.FromNullableWholeNumber(nullableWhole),
        ProcessingOptionKind.RealNumber when double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) => ProcessingOptionValue.FromRealNumber(real),
        ProcessingOptionKind.Text => ProcessingOptionValue.FromText(input),
        ProcessingOptionKind.Choice => ProcessingOptionValue.FromChoice(input),
        ProcessingOptionKind.Font => ProcessingOptionValue.FromFont(input),
        ProcessingOptionKind.Color when input.Length == 7 && input[0] == '#' && uint.TryParse(input.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            => ProcessingOptionValue.FromColor(new PresentationColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
        _ => null
    };

    private static string Format(ProcessingOptionValue value) => value.Kind switch
    {
        ProcessingOptionKind.Toggle => value.AsToggle().ToString(CultureInfo.InvariantCulture),
        ProcessingOptionKind.WholeNumber => value.AsWholeNumber().ToString(CultureInfo.InvariantCulture),
        ProcessingOptionKind.NullableWholeNumber => value.AsNullableWholeNumber()?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ProcessingOptionKind.RealNumber => value.AsRealNumber().ToString("R", CultureInfo.InvariantCulture),
        ProcessingOptionKind.Color => string.Create(CultureInfo.InvariantCulture,
            $"#{value.AsColor().Red:x2}{value.AsColor().Green:x2}{value.AsColor().Blue:x2}"),
        _ => value.AsText()
    };
}
