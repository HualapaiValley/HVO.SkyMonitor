using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

// An independent byte check for the frozen corpus. No dependency on the future snapshot parser.
internal static class MeteorShowerFixtureCanonicalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(JsonElement value, string? omittedRootProperty = null)
    {
        var output = new StringBuilder();
        AppendValue(output, value, omittedRootProperty);
        return StrictUtf8.GetBytes(output.ToString());
    }

    private static void AppendValue(StringBuilder output, JsonElement value, string? omittedProperty = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var names = new HashSet<string>(StringComparer.Ordinal);
                var objectSeparator = "";
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    Assert.IsTrue(names.Add(property.Name), $"Duplicate property: {property.Name}");
                    if (!StringComparer.Ordinal.Equals(property.Name, omittedProperty))
                    {
                        output.Append(objectSeparator);
                        objectSeparator = ",";
                        AppendString(output, property.Name);
                        output.Append(':');
                        AppendValue(output, property.Value);
                    }
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var arraySeparator = "";
                foreach (var item in value.EnumerateArray())
                {
                    output.Append(arraySeparator);
                    arraySeparator = ",";
                    AppendValue(output, item);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(output, value.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Append(FormatNumber(value.GetDouble()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                Assert.Fail($"Unexpected JSON kind: {value.ValueKind}");
                break;
        }
    }

    private static void AppendString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        }
        output.Append('"');
    }

    private static string FormatNumber(double number)
    {
        Assert.IsTrue(double.IsFinite(number), "JCS numbers must be finite.");
        if (number == 0)
        {
            return "0";
        }

        // Use the shortest binary64 significand, then apply ECMAScript's fixed/exponent cutoffs.
        var parts = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).Split('E');
        var decimalPosition = parts[0].IndexOf('.', StringComparison.Ordinal);
        if (decimalPosition < 0)
        {
            decimalPosition = parts[0].Length;
        }

        if (parts.Length == 2)
        {
            decimalPosition += int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        var mantissa = parts[0].Replace(".", "", StringComparison.Ordinal);
        var digits = mantissa.TrimStart('0');
        decimalPosition -= mantissa.Length - digits.Length;
        digits = digits.TrimEnd('0');
        var exponent = decimalPosition - 1;
        var sign = number < 0 ? "-" : "";
        if (exponent is < -6 or >= 21)
        {
            var coefficient = digits.Length == 1 ? digits : digits.Insert(1, ".");
            var exponentSign = exponent >= 0 ? "+" : "";
            return sign + coefficient + "e" + exponentSign + exponent.ToString(CultureInfo.InvariantCulture);
        }

        if (decimalPosition <= 0)
        {
            return sign + "0." + new string('0', -decimalPosition) + digits;
        }

        return sign + (decimalPosition >= digits.Length
            ? digits.PadRight(decimalPosition, '0')
            : digits.Insert(decimalPosition, "."));
    }
}
