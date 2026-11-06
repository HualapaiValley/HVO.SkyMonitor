using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// The kinds of value a processing step option declares (#1532, slice A of #1386). A colour binds three 0-255 channel
/// members of the node's effective options; choice, font and glyph values are strings from a closed set.
/// Collections retain ordered string IDs or defined enum choices, including repeated occurrences.
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
    Glyph,
    NullableWholeNumber,
    NullableRealNumber,
    TextCollection,
    EnumCollection,
    ChoiceCollection,
    RowCollection
}

/// <summary>A typed option value. Text, choice, font and glyph values are strings; a colour is three channels.</summary>
public readonly record struct ProcessingOptionValue
{
    private readonly bool _toggle;
    private readonly long _wholeNumber;
    private readonly double _realNumber;
    private readonly long? _nullableWholeNumber;
    private readonly double? _nullableRealNumber;
    private readonly string? _text;
    private readonly PresentationColor _color;
    private readonly ProcessingOptionSelection? _collection;

    private ProcessingOptionValue(
        ProcessingOptionKind kind,
        bool toggle = false,
        long wholeNumber = 0,
        double realNumber = 0,
        string? text = null,
        PresentationColor color = default,
        long? nullableWholeNumber = null,
        double? nullableRealNumber = null,
        ProcessingOptionSelection? collection = null)
    {
        Kind = kind;
        _toggle = toggle;
        _wholeNumber = wholeNumber;
        _realNumber = realNumber;
        _text = text;
        _color = color;
        _nullableWholeNumber = nullableWholeNumber;
        _nullableRealNumber = nullableRealNumber;
        _collection = collection;
    }

    public ProcessingOptionKind Kind { get; }

    public static ProcessingOptionValue FromToggle(bool value) => new(ProcessingOptionKind.Toggle, toggle: value);

    public static ProcessingOptionValue FromWholeNumber(long value)
        => new(ProcessingOptionKind.WholeNumber, wholeNumber: value);

    public static ProcessingOptionValue FromNullableWholeNumber(long? value)
        => new(ProcessingOptionKind.NullableWholeNumber, nullableWholeNumber: value);

    public static ProcessingOptionValue FromRealNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Option values must be finite.");
        }
        return new(ProcessingOptionKind.RealNumber, realNumber: value);
    }

    public static ProcessingOptionValue FromNullableRealNumber(double? value)
    {
        if (value is { } number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Option values must be finite.");
        return new(ProcessingOptionKind.NullableRealNumber, nullableRealNumber: value);
    }

    public double? AsNullableRealNumber() => Kind == ProcessingOptionKind.NullableRealNumber ? _nullableRealNumber : throw WrongKind();

    public static ProcessingOptionValue FromText(string value) => FromString(ProcessingOptionKind.Text, value);

    public static ProcessingOptionValue FromChoice(string value) => FromString(ProcessingOptionKind.Choice, value);

    public static ProcessingOptionValue FromFont(string value) => FromString(ProcessingOptionKind.Font, value);

    public static ProcessingOptionValue FromGlyph(string value) => FromString(ProcessingOptionKind.Glyph, value);

    public static ProcessingOptionValue FromColor(PresentationColor value) => new(ProcessingOptionKind.Color, color: value);

    public static ProcessingOptionValue FromTextCollection(IEnumerable<string> items)
        => FromCollection(ProcessingOptionKind.TextCollection, new(items));

    public static ProcessingOptionValue FromChoiceCollection(IEnumerable<string> items)
        => FromCollection(ProcessingOptionKind.ChoiceCollection, new(items));

    public static ProcessingOptionValue FromRows(IEnumerable<string> rows)
        => FromCollection(ProcessingOptionKind.RowCollection, new(rows));

    public static ProcessingOptionValue FromEnumCollection(IEnumerable<string> items)
        => FromCollection(ProcessingOptionKind.EnumCollection, new(items));

    public static ProcessingOptionValue FromCollection(ProcessingOptionKind kind, ProcessingOptionSelection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (kind is not (ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection or ProcessingOptionKind.RowCollection))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return new(kind, collection: collection);
    }

    public ProcessingOptionSelection AsCollection()
        => Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection or ProcessingOptionKind.RowCollection ? _collection! : throw WrongKind();

    public bool Equals(ProcessingOptionValue other)
        => Kind == other.Kind && _toggle == other._toggle && _wholeNumber == other._wholeNumber &&
            _realNumber.Equals(other._realNumber) && _nullableWholeNumber == other._nullableWholeNumber &&
            _nullableRealNumber == other._nullableRealNumber && _text == other._text && _color == other._color &&
            Equals(_collection, other._collection);

    public override int GetHashCode()
        => HashCode.Combine(HashCode.Combine(Kind, _toggle, _wholeNumber, _realNumber, _nullableWholeNumber,
            _nullableRealNumber, _text, _color), _collection);

    public bool AsToggle() => Kind == ProcessingOptionKind.Toggle ? _toggle : throw WrongKind();

    public long AsWholeNumber() => Kind == ProcessingOptionKind.WholeNumber ? _wholeNumber : throw WrongKind();

    public long? AsNullableWholeNumber() => Kind == ProcessingOptionKind.NullableWholeNumber ? _nullableWholeNumber : throw WrongKind();

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
        ImmutableArray<string> allowedValues = default,
        bool isIdentifier = false,
        bool isRequired = false,
        string nullValueLabel = "Default",
        string nullActionLabel = "Use default",
        bool isUnsignedByte = false,
        string? parentMember = null,
        int? maximumItems = null,
        Func<JsonElement, string?>? readEnumItem = null,
        Func<string, string>? uniqueItemIdentity = null,
        string? emptyCollectionLabel = null,
        string? clearCollectionLabel = null,
        ProcessingNodeOptionsDescriptor? rowDescriptor = null,
        string? rowIdentityKey = null)
    {
        RequireName(key, nameof(key), lowerCamel: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(help);
        foreach (var member in members)
        {
            RequireName(member, nameof(members), lowerCamel: false);
        }
        if (parentMember is not null)
            RequireName(parentMember, nameof(parentMember), lowerCamel: false);
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
        NullValueLabel = nullValueLabel;
        NullActionLabel = nullActionLabel;
        Members = members;
        ParentMember = parentMember;
        DefaultValue = defaultValue;
        Unit = unit;
        MinimumWholeNumber = minimumWholeNumber;
        MaximumWholeNumber = maximumWholeNumber;
        MinimumRealNumber = minimumRealNumber;
        MaximumRealNumber = maximumRealNumber;
        MaximumLength = maximumLength;
        MaximumItems = maximumItems;
        ReadEnumItem = readEnumItem;
        UniqueItemIdentity = uniqueItemIdentity;
        EmptyCollectionLabel = emptyCollectionLabel;
        ClearCollectionLabel = clearCollectionLabel;
        RowDescriptor = rowDescriptor;
        RowIdentityKey = rowIdentityKey;
        IsIdentifier = isIdentifier;
        IsRequired = isRequired;
        IsUnsignedByte = isUnsignedByte;
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

    /// <summary>The operator-facing meaning of an unset nullable number.</summary>
    public string NullValueLabel { get; }

    public string NullActionLabel { get; }

    public string? EmptyCollectionLabel { get; }

    public string? ClearCollectionLabel { get; }

    public Func<string, string>? UniqueItemIdentity { get; }

    /// <summary>Rows reuse the scalar codec; raw JSON carries unknown members through edits.</summary>
    public ProcessingNodeOptionsDescriptor? RowDescriptor { get; }
    public string? RowIdentityKey { get; }

    /// <summary>A non-null scalar token must bind as an unsigned byte, including its source spelling.</summary>
    public bool IsUnsignedByte { get; }

    /// <summary>The effective-options members, one per value or red, green and blue for a colour.</summary>
    public ImmutableArray<string> Members { get; }

    /// <summary>An existing object containing these members; null binds at the options root.</summary>
    public string? ParentMember { get; }

    public ProcessingOptionValue DefaultValue { get; }

    public string? Unit { get; }

    public long? MinimumWholeNumber { get; }

    public long? MaximumWholeNumber { get; }

    public double? MinimumRealNumber { get; }

    public double? MaximumRealNumber { get; }

    public int? MaximumLength { get; }

    public int? MaximumItems { get; }

    internal Func<JsonElement, string?>? ReadEnumItem { get; }

    /// <summary>Text must be non-empty, trimmed and contain no control characters, as graph identities require.</summary>
    public bool IsIdentifier { get; }

    /// <summary>Text must contain a non-whitespace character, matching an existing Required attribute.</summary>
    public bool IsRequired { get; }

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

    /// <summary>A bounded whole number or explicit JSON null. An absent member uses the default without writing it.</summary>
    public static ProcessingOptionDescriptor NullableWholeNumber(
        string key,
        string member,
        string label,
        string help,
        long? defaultValue,
        long minimum,
        long maximum,
        string? unit = null,
        string nullValueLabel = "Default",
        string nullActionLabel = "Use default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nullValueLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(nullActionLabel);
        if (minimum > maximum)
        {
            throw new ArgumentException("The minimum must not exceed the maximum.", nameof(minimum));
        }
        return new(key, ProcessingOptionKind.NullableWholeNumber, label, help, [member],
            ProcessingOptionValue.FromNullableWholeNumber(defaultValue), unit,
            minimumWholeNumber: minimum, maximumWholeNumber: maximum,
            nullValueLabel: nullValueLabel, nullActionLabel: nullActionLabel);
    }

    /// <summary>A nullable byte using the nullable-number editor and the producer's unsigned token semantics.</summary>
    public static ProcessingOptionDescriptor NullableByte(
        string key,
        string member,
        string label,
        string help,
        byte? defaultValue,
        string? unit = null,
        string nullValueLabel = "Default",
        string nullActionLabel = "Use default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nullValueLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(nullActionLabel);
        return new(key, ProcessingOptionKind.NullableWholeNumber, label, help, [member],
            ProcessingOptionValue.FromNullableWholeNumber(defaultValue), unit,
            minimumWholeNumber: byte.MinValue, maximumWholeNumber: byte.MaxValue,
            nullValueLabel: nullValueLabel, nullActionLabel: nullActionLabel, isUnsignedByte: true);
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

    public static ProcessingOptionDescriptor NullableRealNumber(
        string key, string member, string label, string help, double? defaultValue,
        double minimum, double maximum, string? unit = null,
        string nullValueLabel = "Default", string nullActionLabel = "Use default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nullValueLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(nullActionLabel);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentException("The range must be finite and ordered.", nameof(minimum));
        return new(key, ProcessingOptionKind.NullableRealNumber, label, help, [member],
            ProcessingOptionValue.FromNullableRealNumber(defaultValue), unit,
            minimumRealNumber: minimum, maximumRealNumber: maximum,
            nullValueLabel: nullValueLabel, nullActionLabel: nullActionLabel);
    }

    /// <summary>A bounded scalar inside an existing object. Editing cannot create or remove the object.</summary>
    public static ProcessingOptionDescriptor NestedRealNumber(
        string key, string parentMember, string member, string label, string help,
        double defaultValue, double minimum, double maximum, string? unit = null)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentException("The range must be finite and ordered.", nameof(minimum));
        return new(key, ProcessingOptionKind.RealNumber, label, help, [member],
            ProcessingOptionValue.FromRealNumber(defaultValue), unit,
            minimumRealNumber: minimum, maximumRealNumber: maximum, parentMember: parentMember);
    }

    public static ProcessingOptionDescriptor Text(
        string key,
        string member,
        string label,
        string help,
        string defaultValue,
        int? maximumLength,
        bool isIdentifier = false,
        bool isRequired = false)
    {
        if (maximumLength is { } length)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new(key, ProcessingOptionKind.Text, label, help, [member],
            ProcessingOptionValue.FromText(defaultValue), maximumLength: maximumLength, isIdentifier: isIdentifier,
            isRequired: isRequired);
    }

    public static ProcessingOptionDescriptor TextCollection(
        string key, string member, string label, string help, int maximumItems, int maximumItemLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItemLength);
        return new(key, ProcessingOptionKind.TextCollection, label, help, [member],
            ProcessingOptionValue.FromTextCollection([]), maximumLength: maximumItemLength, maximumItems: maximumItems);
    }

    /// <summary>A bounded, ordered collection of exact string choices, optionally unique by consumer identity.</summary>
    public static ProcessingOptionDescriptor ChoiceCollection(
        string key, string member, string label, string help, IEnumerable<string> allowedValues, int maximumItems,
        Func<string, string>? uniqueItemIdentity = null, string? emptyCollectionLabel = null, string? clearCollectionLabel = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentNullException.ThrowIfNull(allowedValues);
        var choices = allowedValues.ToImmutableArray();
        if (choices.IsEmpty || choices.Length > MaximumAllowedValues ||
            choices.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumNameLength) ||
            choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
            throw new ArgumentException("A choice collection needs distinct, bounded, nonblank supported values.", nameof(allowedValues));
        return new(key, ProcessingOptionKind.ChoiceCollection, label, help, [member],
            ProcessingOptionValue.FromChoiceCollection([]), allowedValues: choices, maximumItems: maximumItems,
            uniqueItemIdentity: uniqueItemIdentity, emptyCollectionLabel: emptyCollectionLabel, clearCollectionLabel: clearCollectionLabel);
    }

    /// <summary>Uses the consumer's JsonStringEnumConverter, including numeric and equivalent source spellings.</summary>
    public static ProcessingOptionDescriptor EnumCollection<TEnum>(string key, string member, string label, string help)
        where TEnum : struct, Enum
        => EnumCollection<TEnum>(key, member, label, help, [], Enum.GetValues<TEnum>().Length);

    /// <summary>A consumer-specific absent-member default and optional occurrence limit; repeated source values remain intact.</summary>
    public static ProcessingOptionDescriptor EnumCollection<TEnum>(string key, string member, string label, string help,
        IEnumerable<TEnum> defaultValue, int? maximumItems = null)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (maximumItems is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        string? Read(JsonElement token)
        {
            try
            {
                var value = token.Deserialize<TEnum>(json);
                return Enum.IsDefined(value) ? value.ToString() : null;
            }
            catch (JsonException) { return null; }
        }
        return new(key, ProcessingOptionKind.EnumCollection, label, help, [member],
            ProcessingOptionValue.FromEnumCollection(defaultValue.Select(static value => value.ToString())), allowedValues: [.. Enum.GetNames<TEnum>()],
            maximumItems: maximumItems, readEnumItem: Read);
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

    public static ProcessingOptionDescriptor RowCollection(
        string key, string member, string label, string help, int maximumItems,
        ProcessingNodeOptionsDescriptor rowDescriptor, string identityKey, Func<string, string> canonicalize,
        string emptyLabel, string clearLabel)
    {
        ArgumentNullException.ThrowIfNull(rowDescriptor);
        ArgumentNullException.ThrowIfNull(canonicalize);
        if (maximumItems is < 1 or > MaximumAllowedValues ||
            rowDescriptor.Find(identityKey)?.Kind != ProcessingOptionKind.Choice ||
            rowDescriptor.Options.Any(static option => option.ParentMember is not null || option.Kind == ProcessingOptionKind.RowCollection))
            throw new ArgumentException("Rows require bounded scalar fields and a choice identity.");
        return new(key, ProcessingOptionKind.RowCollection, label, help, [member],
            ProcessingOptionValue.FromCollection(ProcessingOptionKind.RowCollection, new([])),
            maximumItems: maximumItems, rowDescriptor: rowDescriptor, rowIdentityKey: identityKey,
            uniqueItemIdentity: canonicalize, emptyCollectionLabel: emptyLabel, clearCollectionLabel: clearLabel);
    }

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
        if (Kind == ProcessingOptionKind.RowCollection)
        {
            var rows = value.AsCollection().Items;
            if (rows.Length > MaximumItems)
                return (ProcessingOptionReasonCodes.OutOfRange, $"Option '{Key}' permits at most {MaximumItems} rows.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                try
                {
                    using var document = JsonDocument.Parse(row);
                    var decoded = ProcessingNodeOptionsCodec.Decode(RowDescriptor!, document.RootElement);
                    if (!decoded.IsValid)
                        return (decoded.Diagnostics[0].Code, decoded.Diagnostics[0].Message);
                    var identity = decoded.Get(RowIdentityKey!);
                    if (!identity.IsExplicit || !identities.Add(UniqueItemIdentity!(identity.Value.AsText())))
                        return (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' rows require unique supported kinds.");
                }
                catch (JsonException)
                {
                    return (ProcessingOptionReasonCodes.WrongType, $"Option '{Key}' requires object rows.");
                }
            }
        }
        if (Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection)
        {
            var items = value.AsCollection().Items;
            if (items.Length > MaximumItems)
                return (ProcessingOptionReasonCodes.OutOfRange, $"Option '{Key}' permits at most {MaximumItems} items.");
            if (Kind == ProcessingOptionKind.TextCollection && items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > MaximumLength))
                return (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' items must be nonblank and at most {MaximumLength} characters.");
            if (Kind is ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection && items.Any(item => !AllowedValues.Contains(item, StringComparer.Ordinal)))
                return (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' items must be supported choices.");
            if (UniqueItemIdentity is { } identity && items.Select(identity).Distinct(StringComparer.Ordinal).Count() != items.Length)
                return (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' items must be unique supported choices.");
        }
        return Kind switch
        {
            ProcessingOptionKind.WholeNumber when value.AsWholeNumber() < MinimumWholeNumber ||
                                                  value.AsWholeNumber() > MaximumWholeNumber
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be between {MinimumWholeNumber} and {MaximumWholeNumber}.")),
            ProcessingOptionKind.NullableWholeNumber when value.AsNullableWholeNumber() is { } whole &&
                (whole < MinimumWholeNumber || whole > MaximumWholeNumber)
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be null or between {MinimumWholeNumber} and {MaximumWholeNumber}.")),
            ProcessingOptionKind.RealNumber when value.AsRealNumber() < MinimumRealNumber ||
                                                 value.AsRealNumber() > MaximumRealNumber
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be between {MinimumRealNumber:R} and {MaximumRealNumber:R}.")),
            ProcessingOptionKind.NullableRealNumber when value.AsNullableRealNumber() is { } real &&
                (real < MinimumRealNumber || real > MaximumRealNumber)
                => (ProcessingOptionReasonCodes.OutOfRange, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must be null or between {MinimumRealNumber:R} and {MaximumRealNumber:R}.")),
            ProcessingOptionKind.Text when value.AsText().Length > MaximumLength
                => (ProcessingOptionReasonCodes.TooLong, string.Create(CultureInfo.InvariantCulture,
                    $"Option '{Key}' must not exceed {MaximumLength} characters.")),
            ProcessingOptionKind.Text when IsRequired && string.IsNullOrWhiteSpace(value.AsText())
                => (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' must not be empty or whitespace."),
            ProcessingOptionKind.Text when IsIdentifier && (string.IsNullOrWhiteSpace(value.AsText()) ||
                value.AsText() != value.AsText().Trim() || value.AsText().Any(char.IsControl))
                => (ProcessingOptionReasonCodes.NotAllowed, $"Option '{Key}' must be non-empty, trimmed and contain no control characters."),
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
        var members = described.SelectMany(static option => option.Members.Select(member =>
            option.ParentMember is null ? member : $"{option.ParentMember}.{member}")).ToArray();
        if (members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
        {
            throw new ArgumentException("No two options may bind the same member.", nameof(options));
        }
        if (described.Where(static option => option.ParentMember is null).SelectMany(static option => option.Members)
            .Intersect(described.Select(static option => option.ParentMember).OfType<string>(), StringComparer.OrdinalIgnoreCase).Any())
            throw new ArgumentException("An object cannot also bind a scalar option.", nameof(options));
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
            ProcessingOptionDescriptor.NullableByte("markerValue", "markerValue", "Marker intensity",
                "Optional grayscale override for markers and resolved outlines. Clear to restore the existing palette.",
                null, nullValueLabel: "Default palette", nullActionLabel: "Use default palette"),
            ProcessingOptionDescriptor.Toggle("drawLabels", "drawLabels", "Show labels",
                "Draw eligible labels using the existing measured/expected qualification rules.", true),
            ProcessingOptionDescriptor.WholeNumber("labelScale", "labelScale", "Label scale",
                "The existing label size multiplier.", 2, 1, 8, "×"),
            ProcessingOptionDescriptor.NullableByte("labelValue", "labelValue", "Label intensity",
                "Optional grayscale override for eligible labels; qualification and captions stay unchanged. Clear to restore the existing palette.",
                null, nullValueLabel: "Default palette", nullActionLabel: "Use default palette"),
            ProcessingOptionDescriptor.WholeNumber("maximumLabelCharacters", "maximumLabelCharacters", "Maximum label length",
                "The total label character budget; zero hides object labels. Expected markers stay complete or the label is suppressed.", 24, 0, 64, "characters"),
            ProcessingOptionDescriptor.RealNumber("maximumLabelMagnitude", "maximumLabelMagnitude", "Maximum label magnitude",
                "Select named stellar markers and labels through this magnitude; solar-system bodies keep their existing rules.", 2.5, -30, 30, "mag"),
            ProcessingOptionDescriptor.Toggle("drawConstellationLines", "drawConstellationLines", "Show constellation lines",
                "Draw the retained constellation selection with its existing colour and thickness.", true),
            ProcessingOptionDescriptor.TextCollection("constellationIds", "constellationIds", "Constellation IDs",
                "Draw only these retained scene constellation figures. An empty list draws all retained figures. IDs outside the installed display choices are retained; duplicates and source order are allowed.", 256, 16),
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
            ProcessingOptionDescriptor.NullableByte("imageCircleValue", "imageCircleValue", "Image circle intensity",
                "Optional grayscale override for the image circle. Clear to restore the existing palette.",
                null, nullValueLabel: "Default palette", nullActionLabel: "Use default palette"),
            ProcessingOptionDescriptor.Toggle("drawCardinalDirections", "drawCardinalDirections", "Show cardinal directions",
                "Draw the available N, E, S and W landmarks using the existing layout.", true),
            ProcessingOptionDescriptor.NullableByte("cardinalValue", "cardinalValue", "Cardinal intensity",
                "Optional grayscale override for N, E, S and W. Clear to restore the existing palette.",
                null, nullValueLabel: "Default palette", nullActionLabel: "Use default palette"),
            ProcessingOptionDescriptor.WholeNumber("cardinalScale", "cardinalScale", "Cardinal scale",
                "The existing cardinal-direction size multiplier.", 2, 1, 8, "×")
        ]);

    /// <summary>The current sky-context producer also exposes its explicitly qualified catalog-position labels.</summary>
    public static ProcessingNodeOptionsDescriptor ScenePresentationLayer { get; } = new(
        "scene-presentation-layer-options-v2",
        ScenePresentationLayerStepAlias,
        "projected-scene-presentation-v13-plain-circle-bounded-expected-labels",
        ScenePresentationLayerV12.Title,
        ScenePresentationLayerV12.Help,
        ScenePresentationLayerV12.Options.Add(ProcessingOptionDescriptor.Toggle(
            "expectedPositionDiagnostics", "expectedPositionDiagnostics", "Show expected star names",
            "Label catalog positions with (expected) when measured associations are unavailable. Turning this off keeps only qualified measured star labels.", true)));

    public static ImmutableArray<ProcessingNodeOptionsDescriptor> All =>
        [ScenePresentationLayerV12, ScenePresentationLayer, SceneStylePresentation.Descriptor, FrameFactsPresentationOptionDescriptors.Descriptor,
            CaptureScalarOptionDescriptors.ImageQuality, CaptureScalarOptionDescriptors.Telemetry,
            CaptureScalarOptionDescriptors.VirtualSkyCloudObservation, WeatherCloudOverlayOptionDescriptors.Descriptor,
            CloudAssessmentOptionDescriptors.Descriptor, RollingCombinationOptionDescriptors.Descriptor, CalibrationOptionDescriptors.Normalization,
            CalibrationOptionDescriptors.References, ProjectedSceneOptionDescriptors.Descriptor, AnnotationOptionDescriptors.Descriptor,
            RetainedLayerOptionDescriptors.Cloud, RetainedLayerOptionDescriptors.Environment, PresentationMaterializerOptionDescriptors.Descriptor,
            OverlayManifestOptionDescriptors.Descriptor,
            .. PreviewOptionDescriptors.All];
}
