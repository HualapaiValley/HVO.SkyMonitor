using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>Offline reference encoder for the immutable fixtures. S1 supplies the production encoder.</summary>
internal static class SmallBodyFixtureCanonicalJson
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(byte[] source)
    {
        // JsonDocument can defer decoding string tokens. Check the full byte input before parsing.
        _ = Utf8.GetCharCount(source);
        using var document = JsonDocument.Parse(source);
        return Encode(document.RootElement);
    }

    internal static byte[] Encode(JsonElement value, params string[] omitRootProperties)
    {
        var output = new StringBuilder();
        Write(value, output, omitRootProperties);
        return Utf8.GetBytes(output.ToString());
    }

    private static void Write(JsonElement value, StringBuilder output, string[]? omitProperties = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                string? previous = null;
                var separator = "";
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (property.Name == previous)
                        throw new FormatException("Duplicate JSON property.");
                    previous = property.Name;
                    if (omitProperties?.Contains(property.Name, StringComparer.Ordinal) == true)
                        continue;

                    output.Append(separator);
                    WriteString(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                    separator = ",";
                }
                output.Append('}');
                break;

            case JsonValueKind.Array:
                output.Append('[');
                var delimiter = "";
                foreach (var item in value.EnumerateArray())
                {
                    output.Append(delimiter);
                    Write(item, output);
                    delimiter = ",";
                }
                output.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(value.GetString()!, output);
                break;

            case JsonValueKind.Number:
                output.Append(FormatNumber(value.GetDouble()));
                break;

            default:
                output.Append(value.GetRawText());
                break;
        }
    }

    private static string FormatNumber(double number)
    {
        if (!double.IsFinite(number))
            throw new FormatException("JCS requires a finite binary64 number.");
        if (number == 0)
            return "0";

        // .NET supplies the shortest correctly rounded binary64 digit sequence.
        // Render it using the ECMAScript decimal-point and exponent rules.
        var parts = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).Split('E');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var significand = parts[0];
        var point = significand.IndexOf('.', StringComparison.Ordinal);
        var digits = significand.Replace(".", "", StringComparison.Ordinal);
        var trimmed = digits.TrimStart('0');
        var decimalPosition = (point < 0 ? significand.Length : point) + exponent - (digits.Length - trimmed.Length);
        digits = trimmed.TrimEnd('0');
        var sign = number < 0 ? "-" : "";

        if (decimalPosition is <= -6 or > 21)
        {
            var scientificExponent = decimalPosition - 1;
            var mantissa = digits.Length > 1 ? digits.Insert(1, ".") : digits;
            return sign + mantissa + "e" + (scientificExponent >= 0 ? "+" : "") +
                scientificExponent.ToString(CultureInfo.InvariantCulture);
        }
        if (decimalPosition <= 0)
            return sign + "0." + new string('0', -decimalPosition) + digits;
        return sign + (decimalPosition < digits.Length ? digits.Insert(decimalPosition, ".") :
            digits.PadRight(decimalPosition, '0'));
    }

    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var character in value)
        {
            var escape = character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => null
            };
            if (escape is null)
                output.Append(character);
            else
                output.Append(escape);
        }
        output.Append('"');
    }
}
