using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>
/// One operator setting: its key, rules and wire conversions. A value is held as <see cref="int"/>, <see cref="double"/>,
/// <see cref="string"/> (also an enum's canonical value), or null.
/// </summary>
internal sealed class CameraAgentSettingDefinition
{
    public required string Key { get; init; }

    public required string Group { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required CameraAgentSettingType Type { get; init; }

    public required CameraAgentSettingSafety Safety { get; init; }

    public required CameraAgentSettingApplication Application { get; init; }

    public CameraAgentSettingRole ReadRole { get; init; } = CameraAgentSettingRole.OperationsRead;

    public CameraAgentSettingRole WriteRole { get; init; } = CameraAgentSettingRole.OperationsMutate;

    public bool Nullable { get; init; }

    public bool Secret { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public int? MaximumLength { get; init; }

    /// <summary>An enum's values in display order, or null when they depend on the host's state.</summary>
    public IReadOnlyList<CameraAgentSettingChoice>? AllowedValues { get; init; }

    public string? Unit { get; init; }

    /// <summary>Appended to a range problem, for example a coordinate's sign convention.</summary>
    public string? RangeNote { get; init; }

    /// <summary>The value in effect when nothing sets it.</summary>
    public object? Default { get; init; }

    /// <summary>The host configuration key of a setting kept in the operator settings file.</summary>
    public string? ConfigurationKey { get; init; }

    /// <summary>A further rule a value must meet, returning the problem or null; it runs after the type and range checks.</summary>
    public Func<object, string?>? Rule { get; init; }

    public bool AppliesAfterRestart => Application == CameraAgentSettingApplication.Restart;

    /// <summary>
    /// Parses a value sent through the API. Text is trimmed and empty text clears a nullable setting; an enum value is
    /// matched without regard to case and returned in its canonical form. The problem names the setting and the rule it
    /// breaks, never the value sent.
    /// </summary>
    /// <param name="allowedValues">The current values of an enum whose catalogue entry lists none.</param>
    public bool TryParse(
        JsonElement element,
        IReadOnlyList<CameraAgentSettingChoice>? allowedValues,
        out object? value,
        out string? problem)
    {
        value = null;
        problem = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            problem = Nullable ? null : $"{Title} cannot be cleared.";
            return problem is null;
        }

        switch (Type)
        {
            case CameraAgentSettingType.Integer when element.ValueKind == JsonValueKind.Number &&
                                                    element.TryGetInt32(out var integer):
                value = integer;
                break;
            case CameraAgentSettingType.Number when element.ValueKind == JsonValueKind.Number &&
                                                   element.TryGetDouble(out var number) && double.IsFinite(number):
                value = number;
                break;
            case CameraAgentSettingType.String when element.ValueKind == JsonValueKind.String:
                var text = element.GetString()?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    problem = Nullable ? null : $"{Title} cannot be empty.";
                    return problem is null;
                }

                value = text;
                break;
            case CameraAgentSettingType.Enum when element.ValueKind == JsonValueKind.String:
                var name = element.GetString() ?? string.Empty;
                value = (AllowedValues ?? allowedValues ?? [])
                    .FirstOrDefault(choice => string.Equals(choice.Value, name, StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? name;
                break;
            default:
                problem = $"{Title} must be {DescribeType()}.";
                return false;
        }

        problem = Check(value, allowedValues);
        if (problem is not null)
        {
            value = null;
        }

        return problem is null;
    }

    /// <summary>
    /// The rule a value breaks, or null. An enum value is checked against <paramref name="allowedValues"/> when the
    /// catalogue lists none of its own.
    /// </summary>
    public string? Check(object? value, IReadOnlyList<CameraAgentSettingChoice>? allowedValues = null)
    {
        if (value is null)
        {
            return Nullable ? null : $"{Title} cannot be cleared.";
        }

        string? problem = value switch
        {
            int integer when OutOfRange(integer) => RangeProblem(),
            double number when !double.IsFinite(number) || OutOfRange(number) => RangeProblem(),
            string text when Type == CameraAgentSettingType.String &&
                             (text.Length > (MaximumLength ?? int.MaxValue) || text.Any(char.IsControl)) => TextRule,
            string choice when Type == CameraAgentSettingType.Enum &&
                               !(AllowedValues ?? allowedValues ?? []).Any(allowed =>
                                   string.Equals(allowed.Value, choice, StringComparison.Ordinal)) =>
                $"{Title} must be one of {string.Join(", ", (AllowedValues ?? allowedValues ?? []).Select(allowed => allowed.Value))}.",
            _ => null
        };
        return problem ?? Rule?.Invoke(value);
    }

    /// <summary>The value on the wire, or null.</summary>
    public static JsonElement? ToJson(object? value) => value switch
    {
        null => null,
        int integer => JsonSerializer.SerializeToElement(integer),
        double number => JsonSerializer.SerializeToElement(number),
        string text => JsonSerializer.SerializeToElement(text),
        _ => throw new InvalidOperationException($"Unexpected setting value type {value.GetType().Name}.")
    };

    public CameraAgentSettingDescriptor ToDescriptor() => new(
        Key,
        Group,
        Title,
        Description,
        Type,
        Nullable,
        Minimum,
        Maximum,
        MaximumLength,
        AllowedValues,
        Unit,
        Secret ? null : ToJson(Default),
        ReadRole,
        WriteRole,
        Safety,
        Application,
        AppliesAfterRestart,
        Secret,
        ConfigurationKey);

    /// <summary>The rule a text setting's value must meet.</summary>
    public string TextRule => MaximumLength is { } maximum
        ? string.Create(CultureInfo.InvariantCulture,
            $"{Title} must be at most {maximum} characters without control characters.")
        : $"{Title} cannot contain control characters.";

    /// <summary>The rule a number setting's value must meet.</summary>
    public string NumberRule => RangeProblem();

    /// <summary>The label of an enum value, or the value itself when the catalogue lists no label for it.</summary>
    public string LabelOf(string value)
        => AllowedValues?.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Label
           ?? value;

    private bool OutOfRange(double value) => value < (Minimum ?? double.MinValue) || value > (Maximum ?? double.MaxValue);

    private string RangeProblem()
    {
        if (Type == CameraAgentSettingType.Integer && Minimum is { } low && Maximum is { } high)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Title} must be a whole number from {low:N0} to {high:N0}.");
        }

        var unit = Unit is null ? string.Empty : $" {Unit}";
        return (Minimum, Maximum) switch
        {
            ({ } minimum, { } maximum) =>
                $"{Title} must be a number between {Format(minimum)} and {Format(maximum)}{unit}{RangeNote}.",
            ({ } minimum, null) => $"{Title} must be at least {Format(minimum)}{unit}{RangeNote}.",
            (null, { } maximum) => $"{Title} must be at most {Format(maximum)}{unit}{RangeNote}.",
            _ => $"{Title} must be a finite number."
        };

        static string Format(double bound) => bound.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private string DescribeType() => Type switch
    {
        CameraAgentSettingType.Integer => "a whole number",
        CameraAgentSettingType.Number => "a number",
        CameraAgentSettingType.String => "text",
        _ => "one of its allowed values"
    };
}
