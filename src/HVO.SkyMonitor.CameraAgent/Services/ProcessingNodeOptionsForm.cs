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
    private readonly Dictionary<string, ProcessingOptionSelection> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ProcessingNodeOptionsForm>> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidSourceKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invalidSourceValues = new(StringComparer.Ordinal);

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
            if (!decoded.IsValid)
            {
                _invalidSourceKeys.Add(option.Key);
                if (option.Kind is ProcessingOptionKind.Choice or ProcessingOptionKind.Font or ProcessingOptionKind.Glyph &&
                    TryReadRawStringInput(option, Source, out var rawChoice))
                {
                    _invalidSourceValues[option.Key] = rawChoice;
                }
            }
            if (option.Kind == ProcessingOptionKind.RowCollection)
            {
                _rows[option.Key] = LoadRows(option, decoded);
            }
            else if (option.Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection)
            {
                var value = decoded.IsValid ? decoded.Get(option.Key).Value : option.DefaultValue;
                _collections[option.Key] = value.AsCollection();
            }
            else if (decoded.IsValid)
            {
                _inputs[option.Key] = Format(decoded.Get(option.Key).Value);
            }
            else if (TryReadTolerantScalarInput(option, Source, out var tolerantInput))
            {
                _inputs[option.Key] = tolerantInput;
            }
            else
            {
                _inputs[option.Key] = Format(option.DefaultValue);
            }
        }
        TryBuild(out _, out _);
    }

    public ProcessingNodeOptionsDescriptor Descriptor { get; }
    public IReadOnlyList<ProcessingGraphDiagnostic> Diagnostics { get; private set; } = [];
    public bool HasEdits => _edited.Count > 0;
    private JsonElement Source => _source ?? JsonSerializer.SerializeToElement(new { });

    public string? InvalidSourceNotice(string key)
    {
        if (!_invalidSourceKeys.Contains(key) || _edited.Contains(key))
        {
            return null;
        }
        return _invalidSourceValues.TryGetValue(key, out var raw)
            ? $"Saved value '{raw}' is no longer supported; choose a supported option or click Reset to code defaults."
            : null;
    }

    // The descriptor is bound to the actual producer version. A future producer cannot silently reuse this panel.
    public static ProcessingNodeOptionsDescriptor? DescriptorFor(string alias, JsonElement? options = null)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (string.Equals(alias, "RollingCombination", StringComparison.OrdinalIgnoreCase))
            return options is null || RollingCombinationOptionDescriptors.IsSameGain(options.Value)
                ? RollingCombinationOptionDescriptors.Descriptor
                : RollingCombinationOptionDescriptors.IsMixedGain(options.Value) ? RollingCombinationOptionDescriptors.MixedGain : null;
        if (PreviewOptionDescriptors.IsPreviewAlias(alias))
        {
            return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, PreviewOptionDescriptors.VersionFor(options)).Descriptor;
        }
        if (string.Equals(alias, ExposureAssociationProducts.StepAlias, StringComparison.OrdinalIgnoreCase))
            return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, ExposureAssociationProducts.ImplementationVersion).Descriptor;
        if (string.Equals(alias, CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias, StringComparison.OrdinalIgnoreCase))
            return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, CaptureAnalysisOptionDescriptors.CenteredAstrometry.StepVersion).Descriptor;
        var version = alias.ToUpperInvariant() switch
        {
            "SCENESTYLEPRESENTATIONLAYERV1" => SceneStylePresentation.ProducerVersion,
            "IMAGEQUALITY" => CaptureScalarOptionDescriptors.ImageQuality.StepVersion,
            "TELEMETRY" => CaptureScalarOptionDescriptors.Telemetry.StepVersion,
            "VIRTUALSKYCLOUDOBSERVATION" => CaptureScalarOptionDescriptors.VirtualSkyCloudObservation.StepVersion,
            "WEATHERCLOUDOVERLAY" => WeatherCloudOverlayOptionDescriptors.Descriptor.StepVersion,
            "CLOUDASSESSMENT" => CloudAssessmentOptionDescriptors.Descriptor.StepVersion,
            "REGIONALSTELLARVISIBILITY" => RegionalStellarVisibilityOptionDescriptors.Descriptor.StepVersion,
            "CALIBRATION" => CalibrationVersionFor(options),
            "PROJECTEDSCENE" => "canonical-projected-scene-v1",
            "ASTROMETRYPREDICTION" => CaptureAnalysisOptionDescriptors.Prediction.StepVersion,
            "MEASUREDSTELLARASSOCIATIONS" => CaptureAnalysisOptionDescriptors.StellarAssociations.StepVersion,
            "MEASUREDASTROMETRY" => CaptureAnalysisOptionDescriptors.Astrometry.StepVersion,
            "CAPTURESATELLITETRACKS" => CaptureSatelliteOptionDescriptors.Tracks.StepVersion,
            "CAPTURESATELLITELAYER" => CaptureSatelliteOptionDescriptors.Layer.StepVersion,
            "STORAGE" => StorageOptionDescriptors.Descriptor.StepVersion,
            "ANNOTATION" => AnnotationOptionDescriptors.Descriptor.StepVersion,
            "OVERLAYMANIFEST" => OverlayManifestOptionDescriptors.Descriptor.StepVersion,
            "PRESENTATIONMATERIALIZER" => PresentationMaterializerOptionDescriptors.Descriptor.StepVersion,
            "CLOUDPRESENTATIONLAYER" => PresentationLayerProducers.CloudProducerVersion,
            "ENVIRONMENTPRESENTATIONLAYER" => PresentationLayerProducers.MetadataProducerVersion,
            "JPEGENCODING" => "packed-jpeg-v1",
            _ => string.Equals(alias, FrameFactsPresentation.StepAlias, StringComparison.OrdinalIgnoreCase)
                ? FrameFactsPresentation.ProducerVersion : PresentationLayerProducers.SceneProducerVersion
        };
        return ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, version).Descriptor;
    }

    public string Get(string key) => _inputs[key];

    /// <summary>Valid #rrggbb value for HTML color pickers; falls back to code default while invalid text is being repaired.</summary>
    public string ColorPickerValue(string key)
    {
        var option = Descriptor.Find(key)!;
        var current = _inputs[key];
        return Parse(ProcessingOptionKind.Color, current) is { } parsed
            ? Format(parsed)
            : Format(EffectiveDefault(option));
    }

    /// <summary>Human-readable code default for display alongside option help and bounds.</summary>
    public string DefaultDisplay(string key)
    {
        var option = Descriptor.Find(key)!;
        var value = EffectiveDefault(option);
        return option.Kind switch
        {
            ProcessingOptionKind.Toggle => value.AsToggle() ? "On" : "Off",
            ProcessingOptionKind.NullableWholeNumber => value.AsNullableWholeNumber() is { } whole
                ? WithUnit(whole.ToString(CultureInfo.InvariantCulture), option.Unit)
                : option.NullValueLabel ?? "None",
            ProcessingOptionKind.NullableRealNumber => value.AsNullableRealNumber() is { } real
                ? WithUnit(real.ToString("R", CultureInfo.InvariantCulture), option.Unit)
                : option.NullValueLabel ?? "None",
            ProcessingOptionKind.WholeNumber => WithUnit(value.AsWholeNumber().ToString(CultureInfo.InvariantCulture), option.Unit),
            ProcessingOptionKind.RealNumber => WithUnit(value.AsRealNumber().ToString("R", CultureInfo.InvariantCulture), option.Unit),
            ProcessingOptionKind.Color => Format(value),
            ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection
                => value.AsCollection().Items is { Length: > 0 } items
                    ? string.Join(", ", items)
                    : option.EmptyCollectionLabel ?? "Empty",
            ProcessingOptionKind.RowCollection => option.EmptyCollectionLabel ?? "Producer defaults",
            _ => string.IsNullOrEmpty(value.AsText()) ? "Empty" : value.AsText()
        };

        static string WithUnit(string text, string? unit)
            => string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }

    /// <summary>Whether the option's current form state equals its code default.</summary>
    public bool IsAtDefault(string key)
    {
        var option = Descriptor.Find(key)!;
        if (_invalidSourceKeys.Contains(key) && !_edited.Contains(key))
        {
            return false;
        }
        var defaultValue = EffectiveDefault(option);
        return option.Kind switch
        {
            ProcessingOptionKind.RowCollection => _rows[key].Count == 0
                ? defaultValue.AsCollection().Items.Length == 0
                : _rows[key].All(row => row.AreOptionsAtDefault(option.RowIdentityKey)),
            ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection
                => _collections[key].Items.SequenceEqual(defaultValue.AsCollection().Items, StringComparer.Ordinal),
            _ => Parse(option.Kind, _inputs[key]) is { } parsed && parsed.Equals(defaultValue)
        };
    }

    /// <summary>True when any option in this form currently differs from its code default.</summary>
    public bool HasNonDefaultValues => !AreOptionsAtDefault();

    /// <summary>True when any option outside <paramref name="excludedKeys"/> differs from its code default.</summary>
    public bool HasNonDefaultValuesExcluding(IReadOnlySet<string> excludedKeys)
    {
        ArgumentNullException.ThrowIfNull(excludedKeys);
        return Descriptor.Options.Any(option => !excludedKeys.Contains(option.Key) && !IsAtDefault(option.Key));
    }

    /// <summary>Resets a single option back to its code default without disturbing unknown JSON fields.</summary>
    public void ResetOptionToDefault(string key)
    {
        var option = Descriptor.Find(key)!;
        ApplyDefault(option);
        _edited.Add(key);
        TryBuild(out _, out _);
    }

    /// <summary>Resets every option described by this form back to its code default while keeping unknown JSON fields untouched.</summary>
    public void ResetAllToDefaults() => ResetOptionsToDefaults(excludedKey: null, excludedKeys: null);

    /// <summary>Resets every option outside <paramref name="excludedKeys"/> back to its code default while keeping unknown JSON fields untouched.</summary>
    public void ResetAllToDefaultsExcluding(IReadOnlySet<string> excludedKeys)
    {
        ArgumentNullException.ThrowIfNull(excludedKeys);
        ResetOptionsToDefaults(excludedKey: null, excludedKeys: excludedKeys);
    }

    private bool AreOptionsAtDefault(string? excludedKey = null)
        => Descriptor.Options.All(option =>
            (excludedKey is not null && string.Equals(option.Key, excludedKey, StringComparison.Ordinal)) ||
            IsAtDefault(option.Key));

    private void ResetOptionsToDefaults(string? excludedKey, IReadOnlySet<string>? excludedKeys)
    {
        foreach (var option in Descriptor.Options)
        {
            if (excludedKey is not null && string.Equals(option.Key, excludedKey, StringComparison.Ordinal))
                continue;
            if (excludedKeys is not null && excludedKeys.Contains(option.Key))
                continue;
            ApplyDefault(option);
            _edited.Add(option.Key);
        }
        TryBuild(out _, out _);
    }

    private void ApplyDefault(ProcessingOptionDescriptor option)
    {
        var defaultValue = EffectiveDefault(option);
        if (option.Kind == ProcessingOptionKind.RowCollection)
        {
            // Reserve row deletion for explicit RemoveRow/ClearRows actions so unknown members inside
            // existing rows (such as OverlayManifest layer overrides) survive reset losslessly.
            foreach (var row in _rows[option.Key])
            {
                row.ResetOptionsToDefaults(option.RowIdentityKey, excludedKeys: null);
            }
        }
        else if (option.Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection)
        {
            _collections[option.Key] = defaultValue.AsCollection();
        }
        else
        {
            _inputs[option.Key] = Format(defaultValue);
        }
    }

    private ProcessingOptionValue EffectiveDefault(ProcessingOptionDescriptor option)
    {
        if (string.Equals(Descriptor.DescriptorId, OverlayManifestOptionDescriptors.Layer.DescriptorId, StringComparison.Ordinal) &&
            string.Equals(option.Key, "enabled", StringComparison.Ordinal) &&
            _inputs.TryGetValue("kind", out var kind) &&
            PresentationLayerKindSelection.Canonicalize(kind) is OrbitalTrackPresentation.LayerKind or MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind)
        {
            return ProcessingOptionValue.FromToggle(false);
        }
        return option.DefaultValue;
    }

    private List<ProcessingNodeOptionsForm> LoadRows(ProcessingOptionDescriptor option, ProcessingNodeOptionsDecodeResult decoded)
    {
        if (decoded.IsValid)
        {
            return decoded.Get(option.Key).Value.AsCollection().Items
                .Select(row => new ProcessingNodeOptionsForm(option.RowDescriptor!, JsonSerializer.Deserialize<JsonElement>(row)))
                .ToList();
        }

        // Runtime deserialization accepts quoted numbers (AllowReadingFromString) inside row objects.
        // Retain every object row from the source array so editing or resetting repairs fields losslessly
        // without discarding rows or unknown JSON members.
        if (Source.ValueKind == JsonValueKind.Object &&
            TryGetMemberIgnoreCase(Source, option.Members[0], out var member) &&
            member.ValueKind == JsonValueKind.Array)
        {
            var rows = new List<ProcessingNodeOptionsForm>();
            foreach (var item in member.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    rows.Add(new ProcessingNodeOptionsForm(option.RowDescriptor!, item.Clone()));
                }
            }
            return rows;
        }

        return [];
    }

    private static bool TryReadRawStringInput(ProcessingOptionDescriptor option, JsonElement source, out string text)
    {
        text = string.Empty;
        if (option.Members.Length != 1 || source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var container = source;
        if (option.ParentMember is not null &&
            (!TryGetMemberIgnoreCase(source, option.ParentMember, out container) || container.ValueKind != JsonValueKind.Object))
        {
            return false;
        }
        if (!TryGetMemberIgnoreCase(container, option.Members[0], out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        text = value.GetString() ?? string.Empty;
        return text.Length > 0;
    }

    private static bool TryReadTolerantScalarInput(ProcessingOptionDescriptor option, JsonElement source, out string input)
    {
        input = string.Empty;
        if (option.Members.Length != 1 || source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var container = source;
        if (option.ParentMember is not null &&
            (!TryGetMemberIgnoreCase(source, option.ParentMember, out container) || container.ValueKind != JsonValueKind.Object))
        {
            return false;
        }

        if (!TryGetMemberIgnoreCase(container, option.Members[0], out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (option.Kind is ProcessingOptionKind.WholeNumber or ProcessingOptionKind.NullableWholeNumber &&
            long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            input = whole.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (option.Kind is ProcessingOptionKind.RealNumber or ProcessingOptionKind.NullableRealNumber &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) &&
            double.IsFinite(real))
        {
            input = real.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool TryGetMemberIgnoreCase(JsonElement obj, string memberName, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, memberName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }
        return found;
    }

    private static string CalibrationVersionFor(JsonElement? options)
    {
        if (options is { ValueKind: JsonValueKind.Object } source)
        {
            var strategy = source.EnumerateObject()
                .LastOrDefault(static member => string.Equals(member.Name, "strategy", StringComparison.OrdinalIgnoreCase));
            if (strategy.Value.ValueKind == JsonValueKind.String &&
                strategy.Value.GetString() is "SyntheticReferences" or "CalibrationLibrary")
            {
                return CalibrationOptionDescriptors.References.StepVersion;
            }
        }
        return CalibrationOptionDescriptors.Normalization.StepVersion;
    }

    public ProcessingOptionSelection Collection(string key) => _collections[key];

    public void SetCollectionItem(string key, int index, string value) => SetCollection(key, Collection(key).Set(index, value));

    /// <summary>The reason a closed collection has nothing left to add.</summary>
    public const string EveryChoiceSelectedReason = "Every supported choice is already selected.";

    /// <summary>
    /// Adds one item. A closed enum or choice collection receives its first allowed value that is not selected yet,
    /// so the new item never repeats a selected one and never raises a duplicate error of its own; when every allowed
    /// value is already selected, nothing is added. A free-text collection receives an empty item to type into.
    /// </summary>
    public void AddCollectionItem(string key)
    {
        var option = Descriptor.Find(key)!;
        if (option.Kind is not (ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection))
        {
            SetCollection(key, Collection(key).Add(string.Empty));
        }
        else if (NextUnusedChoice(option) is { } choice)
        {
            SetCollection(key, Collection(key).Add(choice));
        }
    }

    /// <summary>Why adding an item to a closed collection would add nothing, or null while an unused value remains.</summary>
    public string? AddCollectionItemUnavailableReason(string key)
    {
        var option = Descriptor.Find(key)!;
        return option.Kind is ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection &&
            NextUnusedChoice(option) is null ? EveryChoiceSelectedReason : null;
    }

    // Selected items are compared as the option compares them for uniqueness, so an alias of a selected choice counts
    // as selected. Allowed values keep their declared order, which is the order the item's own choice list shows.
    private string? NextUnusedChoice(ProcessingOptionDescriptor option)
    {
        var identity = option.UniqueItemIdentity ?? (static value => value);
        var selected = Collection(option.Key).Items.Select(identity).ToHashSet(StringComparer.Ordinal);
        return option.AllowedValues.FirstOrDefault(value => !selected.Contains(identity(value)));
    }

    public void ClearCollection(string key) => SetCollection(key, ProcessingOptionSelection.Empty);
    public void RemoveCollectionItem(string key, int index) => SetCollection(key, Collection(key).RemoveAt(index));
    public void MoveCollectionItem(string key, int index, int destination) => SetCollection(key, Collection(key).Move(index, destination));

    private void SetCollection(string key, ProcessingOptionSelection value)
    {
        _collections[key] = value;
        _edited.Add(key);
        TryBuild(out _, out _);
    }

    public IReadOnlyList<ProcessingNodeOptionsForm> Rows(string key) => _rows[key];

    public void AddRow(string key)
    {
        var option = Descriptor.Find(key)!;
        if (_rows[key].Count >= option.MaximumItems) return;
        var identity = option.RowDescriptor!.Find(option.RowIdentityKey!)!;
        var source = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [identity.Members[0]] = identity.DefaultValue.AsText() });
        _rows[key].Add(new(option.RowDescriptor, source));
        RowsChanged(key);
    }

    public void RemoveRow(string key, int index) { _rows[key].RemoveAt(index); RowsChanged(key); }
    public void ClearRows(string key) { _rows[key].Clear(); RowsChanged(key); }
    public void MoveRow(string key, int index, int destination)
    {
        var row = _rows[key][index];
        _rows[key].RemoveAt(index);
        _rows[key].Insert(destination, row);
        RowsChanged(key);
    }

    public void RowsChanged(string key) { _edited.Add(key); TryBuild(out _, out _); }

    public string NullableNumberStatus(string key)
    {
        var option = Descriptor.Find(key)!;
        if (_edited.Contains(key))
            return Get(key).Length == 0 ? $"{option.NullValueLabel} (cleared)." : "Value edited.";
        var single = new ProcessingNodeOptionsDescriptor(Descriptor.DescriptorId, Descriptor.StepAlias + ".field",
            Descriptor.StepVersion, Descriptor.Title, Descriptor.Help, [option]);
        var result = ProcessingNodeOptionsCodec.Decode(single, Source);
        if (!result.IsValid)
            return "Correct the invalid value before saving.";
        var decoded = result.Get(key);
        return !decoded.IsExplicit ? "Not specified; using default." : (option.Kind == ProcessingOptionKind.NullableRealNumber
            ? decoded.Value.AsNullableRealNumber() is null : decoded.Value.AsNullableWholeNumber() is null)
            ? $"{option.NullValueLabel} (explicitly selected)." : "Value set.";
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
            if (option.Kind == ProcessingOptionKind.RowCollection)
            {
                var rows = new List<string>();
                for (var index = 0; index < _rows[option.Key].Count; index++)
                {
                    if (_rows[option.Key][index].TryBuild(out var row, out var rowErrors)) rows.Add(row!.Value.GetRawText());
                    else errors.AddRange(rowErrors.Select(error => error with { Path = $"effectiveOptions.{option.Key}[{index}].{error.Path}" }));
                }
                edits.Add(option.Key, ProcessingOptionValue.FromRows(rows));
            }
            else if (option.Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection)
            {
                edits.Add(option.Key, ProcessingOptionValue.FromCollection(option.Kind, _collections[option.Key]));
            }
            else if (Parse(option.Kind, _inputs[option.Key]) is { } value)
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
        ProcessingOptionKind.NullableRealNumber when input.Length == 0 => ProcessingOptionValue.FromNullableRealNumber(null),
        ProcessingOptionKind.NullableRealNumber when double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var nullableReal) && double.IsFinite(nullableReal) => ProcessingOptionValue.FromNullableRealNumber(nullableReal),
        ProcessingOptionKind.RealNumber when double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) => ProcessingOptionValue.FromRealNumber(real),
        ProcessingOptionKind.Text => ProcessingOptionValue.FromText(input),
        ProcessingOptionKind.Choice => ProcessingOptionValue.FromChoice(input),
        ProcessingOptionKind.Font => ProcessingOptionValue.FromFont(input),
        ProcessingOptionKind.Glyph => ProcessingOptionValue.FromGlyph(input),
        ProcessingOptionKind.Color when input.Length == 7 && input[0] == '#' && uint.TryParse(input.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            => ProcessingOptionValue.FromColor(new PresentationColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
        _ => null
    };

    private static string Format(ProcessingOptionValue value) => value.Kind switch
    {
        ProcessingOptionKind.Toggle => value.AsToggle().ToString(CultureInfo.InvariantCulture),
        ProcessingOptionKind.WholeNumber => value.AsWholeNumber().ToString(CultureInfo.InvariantCulture),
        ProcessingOptionKind.NullableWholeNumber => value.AsNullableWholeNumber()?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ProcessingOptionKind.NullableRealNumber => value.AsNullableRealNumber()?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        ProcessingOptionKind.RealNumber => value.AsRealNumber().ToString("R", CultureInfo.InvariantCulture),
        ProcessingOptionKind.Color => string.Create(CultureInfo.InvariantCulture,
            $"#{value.AsColor().Red:x2}{value.AsColor().Green:x2}{value.AsColor().Blue:x2}"),
        _ => value.AsText()
    };
}
