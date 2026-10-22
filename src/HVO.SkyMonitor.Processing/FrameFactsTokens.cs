using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

public enum FrameFactsValueKind
{
    Text = 1,
    WholeNumber,
    Number,
    Timestamp
}

public enum FrameFactsAvailability
{
    Missing,
    Available,
    Stale,
    Conflicting,
    Unsupported
}

/// <summary>A frozen scalar, never a lookup or an arbitrary formattable object.</summary>
public readonly record struct FrameFactsValue
{
    private FrameFactsValue(FrameFactsValueKind kind, string? text = null, long wholeNumber = 0,
        double number = 0, DateTimeOffset timestamp = default)
    {
        Kind = kind;
        Text = text;
        WholeNumber = wholeNumber;
        Number = number;
        Timestamp = timestamp;
    }

    public FrameFactsValueKind Kind { get; }
    internal string? Text { get; }
    internal long WholeNumber { get; }
    internal double Number { get; }
    internal DateTimeOffset Timestamp { get; }

    public static FrameFactsValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, FrameFactsTemplate.MaximumSourceLength);
        return new(FrameFactsValueKind.Text, text: value);
    }

    public static FrameFactsValue FromWholeNumber(long value) => new(FrameFactsValueKind.WholeNumber, wholeNumber: value);

    public static FrameFactsValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A frame fact must be finite.");
        }
        return new(FrameFactsValueKind.Number, number: value);
    }

    /// <summary>The offset is explicit and retained; evaluation never consults the host time zone.</summary>
    public static FrameFactsValue FromTimestamp(DateTimeOffset value) => new(FrameFactsValueKind.Timestamp, timestamp: value);
}

public readonly record struct FrameFactsFact
{
    private FrameFactsFact(FrameFactsAvailability availability, FrameFactsValue value = default)
    {
        Availability = availability;
        Value = value;
    }

    public FrameFactsAvailability Availability { get; }
    public FrameFactsValue Value { get; }

    public static FrameFactsFact Available(FrameFactsValue value)
    {
        if (!Enum.IsDefined(value.Kind))
        {
            throw new ArgumentException("An initialized typed value is required.", nameof(value));
        }
        return new(FrameFactsAvailability.Available, value);
    }

    public static FrameFactsFact Unavailable(FrameFactsAvailability availability)
    {
        if (!Enum.IsDefined(availability) || availability == FrameFactsAvailability.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }
        return new(availability);
    }
}

/// <summary>
/// Immutable bounded input for one retained snapshot. Missing keys are missing facts. The binder, not the
/// evaluator, decides freshness and conflicts. Duplicate keys are rejected rather than resolved by order.
/// </summary>
public sealed class FrameFactsTokenContext
{
    public const int MaximumFacts = 256;
    private readonly ImmutableDictionary<string, FrameFactsFact> _facts;

    public FrameFactsTokenContext(IEnumerable<KeyValuePair<string, FrameFactsFact>> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var builder = ImmutableDictionary.CreateBuilder<string, FrameFactsFact>(StringComparer.Ordinal);
        var count = 0;
        foreach (var pair in facts)
        {
            if (++count > MaximumFacts)
            {
                throw new ArgumentException("Too many frame facts.", nameof(facts));
            }
            FrameFactsTokenDefinition.RequireFactKey(pair.Key);
            if (!builder.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Duplicate frame fact key.", nameof(facts));
            }
        }
        _facts = builder.ToImmutable();
    }

    public static FrameFactsTokenContext Empty { get; } = new([]);

    public FrameFactsFact Get(string key) => _facts.GetValueOrDefault(key);
}

/// <summary>
/// One catalogue entry. Formats are closed by value kind; no custom format or formatter can be supplied.
/// Every scalar has a generated text-only .status companion, independent of scalar availability.
/// </summary>
public sealed class FrameFactsTokenDefinition
{
    public const int MaximumKeyLength = 96;

    public FrameFactsTokenDefinition(string key, FrameFactsValueKind kind)
        : this(key, kind, kind, false)
    {
        RequireFactKey(key);
    }

    private FrameFactsTokenDefinition(string key, FrameFactsValueKind kind, FrameFactsValueKind factKind, bool isStatus)
    {
        Key = key;
        Kind = kind;
        FactKind = factKind;
        IsStatus = isStatus;
        Formats = kind switch
        {
            FrameFactsValueKind.Text => ["text"],
            FrameFactsValueKind.WholeNumber => ["integer"],
            FrameFactsValueKind.Number => ["number", "fixed0", "fixed1", "fixed2", "fixed3"],
            FrameFactsValueKind.Timestamp => ["iso8601", "date", "time"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public string Key { get; }
    public FrameFactsValueKind Kind { get; }
    internal FrameFactsValueKind FactKind { get; }
    public bool IsStatus { get; }
    public ImmutableArray<string> Formats { get; }
    public string DefaultFormat => Formats[0];
    internal string FactKey => IsStatus ? Key[..^7] : Key;
    internal FrameFactsTokenDefinition Status() => new(Key + ".status", FrameFactsValueKind.Text, Kind, true);

    internal static void RequireFactKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsKey(key) || key.StartsWith("spaceWeather.", StringComparison.Ordinal) ||
            key.EndsWith(".status", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid, reserved or status-only frame fact key.", nameof(key));
        }
    }

    internal static bool IsKey(string key)
    {
        if (key.Length is 0 or > MaximumKeyLength)
        {
            return false;
        }
        var segmentStart = true;
        var hasDot = false;
        foreach (var c in key)
        {
            if (c == '.' && !segmentStart)
            {
                segmentStart = true;
                hasDot = true;
            }
            else if (segmentStart ? c is >= 'a' and <= 'z' : char.IsAsciiLetterOrDigit(c))
            {
                segmentStart = false;
            }
            else
            {
                return false;
            }
        }
        return hasDot && !segmentStart;
    }
}

/// <summary>Slice B supplies retained-fact definitions. No producer or built-in registry is changed here.</summary>
public sealed class FrameFactsTokenCatalog
{
    private readonly ImmutableDictionary<string, FrameFactsTokenDefinition> _byKey;

    public FrameFactsTokenCatalog(IEnumerable<FrameFactsTokenDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var builder = ImmutableDictionary.CreateBuilder<string, FrameFactsTokenDefinition>(StringComparer.Ordinal);
        var ordered = ImmutableArray.CreateBuilder<FrameFactsTokenDefinition>();
        var count = 0;
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.IsStatus)
            {
                throw new ArgumentException("Status definitions are generated from scalar definitions.", nameof(definitions));
            }
            if (++count > FrameFactsTokenContext.MaximumFacts)
            {
                throw new ArgumentException("Too many frame fact definitions.", nameof(definitions));
            }
            var status = definition.Status();
            if (!builder.TryAdd(definition.Key, definition) || !builder.TryAdd(status.Key, status))
            {
                throw new ArgumentException("Duplicate frame fact definition.", nameof(definitions));
            }
            ordered.Add(definition);
            ordered.Add(status);
        }
        Definitions = ordered.ToImmutable();
        _byKey = builder.ToImmutable();
    }

    public ImmutableArray<FrameFactsTokenDefinition> Definitions { get; }
    public FrameFactsTokenDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}
