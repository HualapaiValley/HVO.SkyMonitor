using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>A value the catalogue's rules refuse, found before anything is sent. Its message never repeats the value.</summary>
internal sealed class SkySettingValueException : Exception
{
    public SkySettingValueException()
    {
    }

    public SkySettingValueException(string message)
        : base(message)
    {
    }

    public SkySettingValueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One setting: its catalogue entry, its current state and its group's.</summary>
internal sealed record SkySettingField(SkySettingDescriptor Setting, SkySettingState? State, SkySettingsGroupForm Group)
{
    public string Key => Setting.Key;

    public bool CanWrite => Group.State?.CanWrite == true && State?.CanWrite == true;

    /// <summary>Why the setting cannot be changed here, the setting's own reason first.</summary>
    public string? ReadOnlyReason => CanWrite
        ? null
        : State?.ReadOnlyReason ?? Group.State?.ReadOnlyReason ?? "CameraAgent did not return this setting's state.";

    /// <summary>The enum values in force: the catalogue's, or those the values read listed for a state-dependent enum.</summary>
    public IReadOnlyList<SkySettingChoice>? Choices => Setting.AllowedValues ?? State?.AllowedValues;

    public string DisplayValue => Setting.Secret
        ? (State?.IsSet == true ? "(set)" : "(not set)")
        : SkySettingText.Describe(State?.Value);

    /// <summary>Restart edits use the staged choice; other edits, including schedule activation, use the active value.</summary>
    public JsonElement? ValueToEdit => Setting.AppliesAfterRestart
        ? State?.PendingValue ?? State?.Value
        : State?.Value;
}

/// <summary>One settings group: its catalogue entry, its state (with the version a change sends back) and its fields.</summary>
internal sealed class SkySettingsGroupForm
{
    private readonly List<SkySettingField> _fields = [];

    public SkySettingsGroupForm(SkySettingsGroupDescriptor descriptor, SkySettingsGroupState? state)
    {
        Descriptor = descriptor;
        State = state;
    }

    public SkySettingsGroupDescriptor Descriptor { get; }

    public SkySettingsGroupState? State { get; }

    public string Name => Descriptor.Name;

    public IReadOnlyList<SkySettingField> Fields => _fields;

    internal void Add(SkySettingField field) => _fields.Add(field);
}

/// <summary>
/// The settings as the catalogue describes them, with their values: what <c>config</c> shows, and the rules a change is
/// checked against before it is sent (the server checks them again).
/// </summary>
internal sealed class SkySettingsForm
{
    private SkySettingsForm(IReadOnlyList<SkySettingsGroupForm> groups)
    {
        Groups = groups;
        Fields = groups.SelectMany(static group => group.Fields).ToArray();
    }

    public IReadOnlyList<SkySettingsGroupForm> Groups { get; }

    public IReadOnlyList<SkySettingField> Fields { get; }

    /// <summary>Joins the catalogue and the values, in the catalogue's order.</summary>
    /// <exception cref="SkyProtocolException">CameraAgent serves a schema this client does not read.</exception>
    public static SkySettingsForm Create(SkySettingsCatalogue catalogue, SkySettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(values);
        if (catalogue.SchemaVersion != SkySettingsContract.SchemaVersion || values.SchemaVersion != SkySettingsContract.SchemaVersion)
        {
            throw new SkyProtocolException(
                $"CameraAgent serves settings schema {catalogue.SchemaVersion}, and this hvo-sky reads schema {SkySettingsContract.SchemaVersion}. Use the hvo-sky of the same release.");
        }

        var groups = new List<SkySettingsGroupForm>();
        foreach (var descriptor in catalogue.Groups)
        {
            var state = values.Groups.FirstOrDefault(candidate => string.Equals(candidate.Group, descriptor.Name, StringComparison.Ordinal));
            var group = new SkySettingsGroupForm(descriptor, state);
            foreach (var setting in descriptor.Settings)
            {
                group.Add(new SkySettingField(
                    setting,
                    state?.Settings.FirstOrDefault(candidate => string.Equals(candidate.Key, setting.Key, StringComparison.Ordinal)),
                    group));
            }

            groups.Add(group);
        }

        return new SkySettingsForm(groups);
    }

    public SkySettingsGroupForm? FindGroup(string name)
        => Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A setting by its full key, ignoring case, or by the part after its group when that part is unique; null when
    /// there is none or the short name is ambiguous.
    /// </summary>
    public SkySettingField? FindField(string key, bool exactOnly = false)
    {
        var exact = Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || exactOnly)
        {
            return exact;
        }

        var matches = Fields.Where(field => string.Equals(ShortName(field.Key), key, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string ShortName(string key) => key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..];

    /// <summary>
    /// Reads a value typed on the command line by the setting's type and checks it against the catalogue. An empty text
    /// clears a nullable setting (JSON null).
    /// </summary>
    /// <exception cref="SkySettingValueException">The catalogue's rules refuse the value.</exception>
    public static JsonElement ParseText(SkySettingField field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(text);
        var setting = field.Setting;
        if (text.Length == 0)
        {
            return setting.Nullable
                ? Null
                : throw new SkySettingValueException($"{setting.Key} may not be empty.");
        }

        return setting.Type switch
        {
            SkySettingsContract.IntegerType => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? CheckNumber(field, whole, JsonSerializer.SerializeToElement(whole))
                : throw new SkySettingValueException($"{setting.Key} takes a whole number."),
            SkySettingsContract.NumberType => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? CheckNumber(field, number, JsonSerializer.SerializeToElement(number))
                : throw new SkySettingValueException($"{setting.Key} takes a number, such as 35.2."),
            _ => Check(field, JsonSerializer.SerializeToElement(text))
        };
    }

    /// <summary>Checks a value from a settings file, already JSON, against the setting's type and the catalogue.</summary>
    /// <exception cref="SkySettingValueException">The catalogue's rules refuse the value.</exception>
    public static JsonElement Check(SkySettingField field, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var setting = field.Setting;
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return setting.Nullable ? Null : throw new SkySettingValueException($"{setting.Key} may not be cleared.");
            case JsonValueKind.Number when setting.Type == SkySettingsContract.IntegerType:
                return value.TryGetInt64(out var whole)
                    ? CheckNumber(field, whole, value)
                    : throw new SkySettingValueException($"{setting.Key} takes a whole number.");
            case JsonValueKind.Number when setting.Type == SkySettingsContract.NumberType:
                return CheckNumber(field, value.GetDouble(), value);
            case JsonValueKind.String when setting.Type == SkySettingsContract.StringType:
                var text = value.GetString()!;
                return setting.MaximumLength is { } longest && text.Length > longest
                    ? throw new SkySettingValueException($"{setting.Key} takes at most {longest} characters.")
                    : value;
            case JsonValueKind.String when setting.Type == SkySettingsContract.EnumType:
                var choice = field.Choices?.FirstOrDefault(candidate =>
                    string.Equals(candidate.Value, value.GetString(), StringComparison.OrdinalIgnoreCase));
                return choice is not null
                    ? JsonSerializer.SerializeToElement(choice.Value)
                    : throw new SkySettingValueException(
                        $"{setting.Key} takes one of: {string.Join(", ", field.Choices?.Select(static candidate => candidate.Value) ?? [])}.");
            default:
                throw new SkySettingValueException($"{setting.Key} takes {DescribeType(setting)}.");
        }
    }

    /// <summary>True when two values mean the same: numbers by value, text and enums by their characters.</summary>
    public static bool SameValue(JsonElement? left, JsonElement? right)
    {
        var a = left ?? Null;
        var b = right ?? Null;
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return a.GetDouble().Equals(b.GetDouble());
        }

        return a.ValueKind == b.ValueKind && (a.ValueKind != JsonValueKind.String
            ? JsonElement.DeepEquals(a, b)
            : string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal));
    }

    public static JsonElement Null { get; } = JsonSerializer.SerializeToElement<object?>(null);

    private static JsonElement CheckNumber(SkySettingField field, double number, JsonElement value)
    {
        var setting = field.Setting;
        if (setting.Minimum is { } low && number < low || setting.Maximum is { } high && number > high)
        {
            throw new SkySettingValueException($"{setting.Key} takes {SkySettingText.DescribeRange(setting)}.");
        }

        return value;
    }

    private static string DescribeType(SkySettingDescriptor setting) => setting.Type switch
    {
        SkySettingsContract.IntegerType => "a whole number",
        SkySettingsContract.NumberType => "a number",
        SkySettingsContract.EnumType => "one of its allowed values, as text",
        _ => "text"
    };
}

/// <summary>How settings and their values are written for a person.</summary>
internal static class SkySettingText
{
    public static string Describe(JsonElement? value) => value switch
    {
        null => "(none)",
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "(none)",
        { ValueKind: JsonValueKind.String } text => text.GetString() is { Length: > 0 } s ? s : "(empty)",
        { } other => other.GetRawText()
    };

    public static string DescribeRange(SkySettingDescriptor setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var unit = string.IsNullOrWhiteSpace(setting.Unit) ? string.Empty : " " + setting.Unit;
        return (setting.Minimum, setting.Maximum) switch
        {
            ({ } low, { } high) => $"{Number(low)} to {Number(high)}{unit}",
            ({ } low, null) => $"at least {Number(low)}{unit}",
            (null, { } high) => $"at most {Number(high)}{unit}",
            _ => setting.Type == SkySettingsContract.IntegerType ? "a whole number" : "a number"
        };
    }

    /// <summary>What a setting accepts: a range, a length, or its choices.</summary>
    public static string DescribeRule(SkySettingField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var setting = field.Setting;
        var rule = setting.Type switch
        {
            SkySettingsContract.IntegerType or SkySettingsContract.NumberType => DescribeRange(setting),
            SkySettingsContract.EnumType => field.Choices is { Count: > 0 } choices
                ? "one of " + string.Join(", ", choices.Select(static choice => choice.Value))
                : "one of the values CameraAgent lists",
            _ => setting.MaximumLength is { } longest ? $"text, at most {longest} characters" : "text"
        };
        return setting.Nullable ? rule + "; empty clears it" : rule;
    }

    /// <summary>When a saved change takes effect.</summary>
    public static string DescribeApplication(string application) => application switch
    {
        "Immediate" => "at once",
        "NextCaptureBoundary" => "at the next capture boundary",
        "Restart" => "at the next CameraAgent start",
        _ => application
    };

    /// <summary>What a change can affect.</summary>
    public static string DescribeSafety(string safety) => safety switch
    {
        "Descriptive" => "labels only",
        "CaptureOutput" => "changes what future captures record",
        SkySettingsContract.CaptureGeometrySafety => "changes the location and time basis of future captures",
        _ => safety
    };

    /// <summary>The short notes of a setting's row in <c>config show</c>.</summary>
    public static string DescribeNotes(SkySettingField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var notes = new List<string>();
        if (!field.CanWrite)
        {
            notes.Add("read-only");
        }

        if (field.State?.PendingValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } pending)
        {
            notes.Add($"pending {Describe(pending)}");
        }

        if (field.Setting.AppliesAfterRestart)
        {
            notes.Add("applies after a restart");
        }

        if (field.State?.Problem is { Length: > 0 })
        {
            notes.Add("PROBLEM");
        }

        return string.Join(", ", notes);
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
