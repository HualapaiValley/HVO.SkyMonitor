using System.Collections.Immutable;
using System.Globalization;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// The kinds of value a processing step option declares (#1532, slice A of #1386). A colour binds three 0-255 channel
/// members of the node's effective options; choice, font and glyph values are strings from a closed set.
/// </summary>
public enum ProcessingOptionKind
{
    Toggle,
    WholeNumber,
    RealNumber,
    Text,
    Choice,
    Color,
    Font,
    Glyph
}

/// <summary>A typed option value. Text, choice, font and glyph values are strings; a colour is three channels.</summary>
public readonly record struct ProcessingOptionValue
{
    private readonly bool _toggle;
    private readonly long _wholeNumber;
    private readonly double _realNumber;
    private readonly string? _text;
    private readonly PresentationColor _color;

    private ProcessingOptionValue(
        ProcessingOptionKind kind,
        bool toggle = false,
        long wholeNumber = 0,
        double realNumber = 0,
        string? text = null,
        PresentationColor color = default)
    {
        Kind = kind;
        _toggle = toggle;
        _wholeNumber = wholeNumber;
        _realNumber = realNumber;
        _text = text;
        _color = color;
    }

    public ProcessingOptionKind Kind { get; }

    public static ProcessingOptionValue FromToggle(bool value) => new(ProcessingOptionKind.Toggle, toggle: value);

    public static ProcessingOptionValue FromWholeNumber(long value)
        => new(ProcessingOptionKind.WholeNumber, wholeNumber: value);

    public static ProcessingOptionValue FromRealNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Option values must be finite.");
        }
        return new(ProcessingOptionKind.RealNumber, realNumber: value);
    }

    public static ProcessingOptionValue FromText(string value) => FromString(ProcessingOptionKind.Text, value);

    public static ProcessingOptionValue FromChoice(string value) => FromString(ProcessingOptionKind.Choice, value);

    public static ProcessingOptionValue FromFont(string value) => FromString(ProcessingOptionKind.Font, value);

    public static ProcessingOptionValue FromGlyph(string value) => FromString(ProcessingOptionKind.Glyph, value);

    public static ProcessingOptionValue FromColor(PresentationColor value) => new(ProcessingOptionKind.Color, color: value);

    public bool AsToggle() => Kind == ProcessingOptionKind.Toggle ? _toggle : throw WrongKind();

    public long AsWholeNumber() => Kind == ProcessingOptionKind.WholeNumber ? _wholeNumber : throw WrongKind();

    public double AsRealNumber() => Kind == ProcessingOptionKind.RealNumber ? _realNumber : throw WrongKind();

    /// <summary>The string of a text, choice, font or glyph value.</summary>
    public string AsText() => Kind is ProcessingOptionKind.Text or ProcessingOptionKind.Choice or
        ProcessingOptionKind.Font or ProcessingOptionKind.Glyph
        ? _text!
        : throw WrongKind();

    public PresentationColor AsColor() => Kind == ProcessingOptionKind.Color ? _color : throw WrongKind();

    private static ProcessingOptionValue FromString(ProcessingOptionKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(kind, text: value);
    }

    private InvalidOperationException WrongKind() => new($"The option value is a {Kind} value.");
}

/// <summary>
/// One operator-facing option of a processing step: its stable key, kind, label, help, default and bounds, and the
/// effective-options members it binds. Defaults are the step's code defaults and are never written into a pipeline.
/// </summary>
public sealed class ProcessingOptionDescriptor
{
    public const int MaximumNameLength = 64;
    public const int MaximumAllowedValues = 256;

    private ProcessingOptionDescriptor(
        string key,
        ProcessingOptionKind kind,
        string label,
        string help,
        ImmutableArray<string> members,
        ProcessingOptionValue defaultValue,
        string? unit = null,
        long? minimumWholeNumber = null,
        long? maximumWholeNumber = null,
        double? minimumRealNumber = null,
        double? maximumRealNumber = null,
        int? maximumLength = null,
        ImmutableArray<string> allowedValues = default)
    {
        RequireName(key, nameof(key), lowerCamel: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(help);
        foreach (var member in members)
        {
            RequireName(member, nameof(members), lowerCamel: false);
        }
        if (members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
        {
            throw new ArgumentException("Option members must be distinct.", nameof(members));
        }
        if (unit is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        }
        Key = key;
        Kind = kind;
        Label = label;
        Help = help;
        Members = members;
        DefaultValue = defaultValue;
        Unit = unit;
        MinimumWholeNumber = minimumWholeNumber;
        MaximumWholeNumber = maximumWholeNumber;
        MinimumRealNumber = minimumRealNumber;
        MaximumRealNumber = maximumRealNumber;
        MaximumLength = maximumLength;
        AllowedValues = allowedValues.IsDefault ? [] : allowedValues;
        if (Check(defaultValue) is { } reason)
        {
            throw new ArgumentException($"The default value of option '{key}' is invalid: {reason}", nameof(defaultValue));
        }
    }

    /// <summary>The stable, camel-case key an editor and an edit address the option by.</summary>
    public string Key { get; }

    public ProcessingOptionKind Kind { get; }

    public string Label { get; }

    public string Help { get; }

    /// <summary>The effective-options members, one per value or red, green and blue for a colour.</summary>
    public ImmutableArray<string> Members { get; }

    public ProcessingOptionValue DefaultValue { get; }

    public string? Unit { get; }

    public long? MinimumWholeNumber { get; }

    public long? MaximumWholeNumber { get; }

    public double? MinimumRealNumber { get; }

    public double? MaximumRealNumber { get; }

    public int? MaximumLength { get; }

    /// <summary>The closed set of a choice, font or glyph option, compared ordinally.</summary>
    public ImmutableArray<string> AllowedValues { get; }

    public static ProcessingOptionDescriptor Toggle(string key, string member, string label, string help, bool defaultValue)
        => new(key, ProcessingOptionKind.Toggle, label, help, [member], ProcessingOptionValue.FromToggle(defaultValue));

    public static ProcessingOptionDescriptor WholeNumber(
        string key,
        string member,
        string label,
        string help,
        long defaultValue,
        long minimum,
        long maximum,
        string? unit = null)
    {
        if (minimum > maximum)
        {
            throw new ArgumentException("The minimum must not exceed the maximum.", nameof(minimum));
        }
        return new(key, ProcessingOptionKind.WholeNumber, label, help, [member],
            ProcessingOptionValue.FromWholeNumber(defaultValue), unit,
            minimumWholeNumber: minimum, maximumWholeNumber: maximum);
    }

    public static ProcessingOptionDescriptor RealNumber(
        string key,
        string member,
        string label,
        string help,
        double defaultValue,
        double minimum,
        double maximum,
        string? unit = null)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
        {
            throw new ArgumentException("The range must be finite and ordered.", nameof(minimum));
        }
        return new(key, ProcessingOptionKind.RealNumber, label, help, [member],
            ProcessingOptionValue.FromRealNumber(defaultValue), unit,
            minimumRealNumber: minimum, maximumRealNumber: maximum);
    }

    public static ProcessingOptionDescriptor Text(
        string key,
        string member,
        string label,
        string help,
        string defaultValue,
        int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        return new(key, ProcessingOptionKind.Text, label, help, [member],
            ProcessingOptionValue.FromText(defaultValue), maximumLength: maximumLength);
    }

    public static ProcessingOptionDescriptor Choice(
        string key,
        string member,
        string label,
        string help,
        string defaultValue,
        IEnumerable<string> allowedValues)
        => ClosedSet(ProcessingOptionKind.Choice, key, member, label, help,
            ProcessingOptionValue.FromChoice(defaultValue), allowedValues);

    public static ProcessingOptionDescriptor Font(
        string key,
        string member,
        string label,
        string help,
        string defaultValue,
        IEnumerable<string> allowedValues)
        => ClosedSet(ProcessingOptionKind.Font, key, member, label, help,
            ProcessingOptionValue.FromFont(defaultValue), allowedValues);

    public static ProcessingOptionDescriptor Glyph(
        string key,
        string member,
        string label,
        string help,
        string defaultValue,
        IEnumerable<string> allowedValues)
        => ClosedSet(ProcessingOptionKind.Glyph, key, member, label, help,
            ProcessingOptionValue.FromGlyph(defaultValue), allowedValues);

    public static ProcessingOptionDescriptor Color(
        string key,
        string redMember,
        string greenMember,
        string blueMember,
        string label,
        string help,
        PresentationColor defaultValue)
        => new(key, ProcessingOptionKind.Color, label, help, [redMember, greenMember, blueMember],
            ProcessingOptionValue.FromColor(defaultValue));

    /// <summary>Why <paramref name="value"/> is not a valid value of this option, or <see langword="null"/>.</summary>
    internal (string Code, string Message)? Check(ProcessingOptionValue value)
    {
        if (value.Kind != Kind)
        {
            return (ProcessingOptionReasonCodes.WrongType, $"Option '{Key}' takes a {Kind} value, not a {value.Kind} value.");
        }
        return Kind switch
        {
            ProcessingOptionKind.WholeNumber when value.AsWholeNumber() < MinimumWholeNumber ||
                                                  value.AsWholeNumber() > MaximumWholeNumber
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be between {MinimumWholeNumber} and {MaximumWholeNumber}.")),
            ProcessingOptionKind.RealNumber when value.AsRealNumber() < MinimumRealNumber ||
                                                 value.AsRealNumber() > MaximumRealNumber
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be between {MinimumRealNumber:R} and {MaximumRealNumber:R}.")),
            ProcessingOptionKind.Text when value.AsText().Length > MaximumLength
                => (ProcessingOptionReasonCodes.TooLong, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must not exceed {MaximumLength} characters.")),
            ProcessingOptionKind.Choice or ProcessingOptionKind.Font or ProcessingOptionKind.Glyph
                when !AllowedValues.Contains(value.AsText(), StringComparer.Ordinal)
                => (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' must be one of its allowed values."),
            _ => null
        };
    }

    private static ProcessingOptionDescriptor ClosedSet(
        ProcessingOptionKind kind,
        string key,
        string member,
        string label,
        string help,
        ProcessingOptionValue defaultValue,
        IEnumerable<string> allowedValues)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);
        var allowed = allowedValues.ToImmutableArray();
        if (allowed.IsEmpty || allowed.Length > MaximumAllowedValues ||
            allowed.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumNameLength) ||
            allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Length)
        {
            throw new ArgumentException(
                $"A closed set needs 1 to {MaximumAllowedValues} distinct, non-blank values of at most {MaximumNameLength} characters.",
                nameof(allowedValues));
        }
        return new(key, kind, label, help, [member], defaultValue, allowedValues: allowed);
    }

    private static void RequireName(string name, string parameterName, bool lowerCamel)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);
        if (name.Length == 0 || name.Length > MaximumNameLength || !char.IsAsciiLetter(name[0]) ||
            (lowerCamel && !char.IsAsciiLetterLower(name[0])) || !name.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"'{name}' must be an ASCII identifier of at most {MaximumNameLength} characters.", parameterName);
        }
    }
}

/// <summary>
/// The options of one versioned step: a step alias at one exact step version. A different step version is a different
/// descriptor, reached only through an explicit whole-draft upgrade (#1386 design decision 2).
/// </summary>
public sealed class ProcessingNodeOptionsDescriptor
{
    public ProcessingNodeOptionsDescriptor(
        string descriptorId,
        string stepAlias,
        string stepVersion,
        string title,
        string help,
        IEnumerable<ProcessingOptionDescriptor> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(help);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsVersionedId(descriptorId))
        {
            throw new ArgumentException(
                "A descriptor id is lower-case kebab words ending in a version, such as 'example-options-v1'.",
                nameof(descriptorId));
        }
        var described = options.ToImmutableArray();
        if (described.IsEmpty || described.Any(static option => option is null))
        {
            throw new ArgumentException("A descriptor describes at least one option.", nameof(options));
        }
        if (described.Select(static option => option.Key).Distinct(StringComparer.Ordinal).Count() != described.Length)
        {
            throw new ArgumentException("Option keys must be distinct.", nameof(options));
        }
        var members = described.SelectMany(static option => option.Members).ToArray();
        if (members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
        {
            throw new ArgumentException("No two options may bind the same member.", nameof(options));
        }
        DescriptorId = descriptorId;
        StepAlias = stepAlias;
        StepVersion = stepVersion;
        Title = title;
        Help = help;
        Options = described;
    }

    /// <summary>The versioned alias of this descriptor, such as <c>scene-presentation-layer-options-v1</c>.</summary>
    public string DescriptorId { get; }

    public string StepAlias { get; }

    public string StepVersion { get; }

    public string Title { get; }

    public string Help { get; }

    public ImmutableArray<ProcessingOptionDescriptor> Options { get; }

    public ProcessingOptionDescriptor? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal));
    }

    private static bool IsVersionedId(string id)
    {
        var versionAt = id.LastIndexOf("-v", StringComparison.Ordinal);
        return id.Length <= ProcessingOptionDescriptor.MaximumNameLength && versionAt > 0 &&
            versionAt + 2 < id.Length && id[(versionAt + 2)..].All(char.IsAsciiDigit) && id[versionAt + 2] != '0' &&
            id.Split('-').All(static word => word.Length > 0 && word.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)));
    }
}

/// <summary>Stable reason codes of option decoding, validation and upgrade.</summary>
public static class ProcessingOptionReasonCodes
{
    public const string NotAnObject = "processing.options.not-an-object";
    public const string DuplicateMember = "processing.options.duplicate-member";
    public const string WrongType = "processing.options.wrong-type";
    public const string OutOfRange = "processing.options.out-of-range";
    public const string TooLong = "processing.options.too-long";
    public const string NotAllowed = "processing.options.not-allowed";
    public const string UnknownOption = "processing.options.unknown-option";
    public const string UpgradeNotRequired = "processing.options.upgrade-not-required";
    public const string UpgradePlanStale = "processing.options.upgrade-plan-stale";
    public const string UpgradeRevisionRequired = "processing.options.upgrade-revision-required";
}

/// <summary>The built-in option descriptors. Each binds one exact step version, pinned literally.</summary>
public static class ProcessingNodeOptionsDescriptors
{
    public const string ScenePresentationLayerStepAlias = "ScenePresentationLayer";

    /// <summary>
    /// The scene presentation layer's existing visibility and scalar controls. The defaults are CameraAgent's
    /// <c>ScenePresentationLayerProcessingStepOptions</c> defaults, the overlay defaults #1383 restored.
    /// </summary>
    public static ProcessingNodeOptionsDescriptor ScenePresentationLayerV12 { get; } = new(
        "scene-presentation-layer-options-v1",
        ScenePresentationLayerStepAlias,
        "projected-scene-presentation-v12-plain-circle-markers",
        "Scene overlay",
        "Stars, constellations, the image circle and the cardinal directions drawn over the sky.",
        [
            ProcessingOptionDescriptor.Toggle("drawMarkers", "drawMarkers", "Show markers",
                "Draw markers and resolved solar-system outlines at the existing scene positions.", true),
            ProcessingOptionDescriptor.WholeNumber("markerRadius", "markerRadius", "Marker radius",
                "The radius of point markers; resolved bodies retain their existing outline rules.", 6, 0, 32, "px"),
            ProcessingOptionDescriptor.Toggle("drawLabels", "drawLabels", "Show labels",
                "Draw eligible labels using the existing measured/expected qualification rules.", true),
            ProcessingOptionDescriptor.WholeNumber("labelScale", "labelScale", "Label scale",
                "The existing label size multiplier.", 2, 1, 8, "×"),
            ProcessingOptionDescriptor.WholeNumber("maximumLabelCharacters", "maximumLabelCharacters", "Maximum label length",
                "The total label character budget; zero hides object labels. Expected markers stay complete or the label is suppressed.", 24, 0, 64, "characters"),
            ProcessingOptionDescriptor.RealNumber("maximumLabelMagnitude", "maximumLabelMagnitude", "Maximum label magnitude",
                "Select named stellar markers and labels through this magnitude; solar-system bodies keep their existing rules.", 2.5, -30, 30, "mag"),
            ProcessingOptionDescriptor.Toggle("drawConstellationLines", "drawConstellationLines", "Show constellation lines",
                "Draw the retained constellation selection with its existing colour and thickness.", true),
            ProcessingOptionDescriptor.Color(
                "constellationLineColor",
                "constellationLineRed",
                "constellationLineGreen",
                "constellationLineBlue",
                "Constellation line colour",
                "The colour of the constellation figure lines.",
                new PresentationColor(96, 160, 255)),
            ProcessingOptionDescriptor.WholeNumber(
                "constellationLineThickness",
                "constellationLineThickness",
                "Constellation line thickness",
                "The width of the constellation figure lines.",
                2,
                1,
                8,
                "px"),
            ProcessingOptionDescriptor.Toggle("drawImageCircle", "drawImageCircle", "Show image circle",
                "Draw the existing projection image circle when the scene supplies one.", true),
            ProcessingOptionDescriptor.Toggle("drawCardinalDirections", "drawCardinalDirections", "Show cardinal directions",
                "Draw the available N, E, S and W landmarks using the existing layout.", true),
            ProcessingOptionDescriptor.WholeNumber("cardinalScale", "cardinalScale", "Cardinal scale",
                "The existing cardinal-direction size multiplier.", 2, 1, 8, "×")
        ]);

    /// <summary>The measured-label producer keeps the same scalar controls at its own exact version.</summary>
    public static ProcessingNodeOptionsDescriptor ScenePresentationLayer { get; } = new(
        "scene-presentation-layer-options-v2",
        ScenePresentationLayerStepAlias,
        "projected-scene-presentation-v13-plain-circle-bounded-expected-labels",
        ScenePresentationLayerV12.Title,
        ScenePresentationLayerV12.Help,
        ScenePresentationLayerV12.Options);

    public static ImmutableArray<ProcessingNodeOptionsDescriptor> All { get; } =
        [ScenePresentationLayerV12, ScenePresentationLayer, FrameFactsPresentationOptionDescriptors.Descriptor];
}
