namespace HVO;

public static class OneOfExtensions
{
    /// <summary>
    /// Checks if the OneOf contains a value of type T.
    /// </summary>
    public static bool Is<T>(this IOneOf oneOf)
    {
        ArgumentNullException.ThrowIfNull(oneOf);
        return oneOf.Value is T;
    }

    /// <summary>
    /// Attempts to get the value of type T from the OneOf.
    /// </summary>
    public static bool TryGet<T>(this IOneOf oneOf, out T value)
    {
        ArgumentNullException.ThrowIfNull(oneOf);
        if (oneOf.Value is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Gets the value as T or throws an InvalidOperationException if the value is not of type T.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the value is null or not of type T</exception>
    public static T As<T>(this IOneOf oneOf)
    {
        ArgumentNullException.ThrowIfNull(oneOf);
        if (oneOf.Value is null)
            throw new InvalidOperationException("Cannot cast null value to type " + typeof(T).Name);

        if (oneOf.Value is not T)
            throw new InvalidOperationException($"Cannot cast value of type {oneOf.ValueType?.Name ?? "unknown"} to type {typeof(T).Name}");

        return (T)oneOf.Value;
    }
}

