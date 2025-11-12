using System.Text.Json;

namespace HVO;

public readonly struct Option<T> where T : notnull
{
    public T? Value { get; }
    public bool HasValue { get; }
    public JsonElement? RawJson { get; } // Fallback raw JSON

    public Option(T? value, JsonElement? rawJson = null)
    {
        Value = value;
        HasValue = value is not null;
        RawJson = rawJson;
    }

    public static Option<T> None(JsonElement? rawJson = null) => new Option<T>(default, rawJson);
    public override string ToString() => HasValue ? Value?.ToString() ?? "" : "<None>";
}

