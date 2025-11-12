using System.Text.Json;

namespace HVO;

public interface IOneOf
{
    /// <summary>
    /// Gets the underlying value
    /// </summary>
    object? Value { get; }

    /// <summary>
    /// Gets the type of the underlying value
    /// </summary>
    Type? ValueType { get; }

    /// <summary>
    /// Gets the raw JSON element if available (fallback for unknown types)
    /// </summary>
    JsonElement? RawJson { get; }
}

