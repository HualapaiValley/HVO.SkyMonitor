using System.Text.Json;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Extension methods for common test assertions and utilities.
/// </summary>
public static class TestExtensions
{
    /// <summary>
    /// Serializes an object to JSON for debugging/comparison.
    /// </summary>
    public static string ToJson<T>(this T obj, bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Serialize(obj, options);
    }

    /// <summary>
    /// Deserializes JSON string to an object.
    /// </summary>
    public static T? FromJson<T>(this string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Deserialize<T>(json, options);
    }

    /// <summary>
    /// Asserts that a string is not null or empty.
    /// Throws ArgumentException with a helpful message if it is.
    /// </summary>
    public static string AssertNotNullOrEmpty(this string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"{parameterName} must not be null or empty", parameterName);
        }
        return value;
    }

    /// <summary>
    /// Asserts that a value is not null.
    /// Throws ArgumentNullException if it is.
    /// </summary>
    public static T AssertNotNull<T>(this T? value, string parameterName) where T : class
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    /// <summary>
    /// Creates a deep copy of an object via JSON serialization.
    /// Useful for test data setup.
    /// </summary>
    public static T DeepCopy<T>(this T obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Failed to deserialize deep copy");
    }

    /// <summary>
    /// Waits for a condition to be true within a timeout period.
    /// Useful for async testing scenarios.
    /// </summary>
    public static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollingInterval = null)
    {
        var interval = pollingInterval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(interval);
        }

        return false;
    }

    /// <summary>
    /// Waits for an async condition to be true within a timeout period.
    /// </summary>
    public static async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollingInterval = null)
    {
        var interval = pollingInterval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(interval);
        }

        return false;
    }
}
