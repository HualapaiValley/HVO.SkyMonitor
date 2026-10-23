using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace HVO.SkyMonitor.Processing;

[Flags]
public enum FrameFactsTextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4
}

public sealed record FrameFactsTemplateDiagnostic(string Code, int SourceOffset);

public sealed record FrameFactsParseResult(FrameFactsTemplate? Template, FrameFactsTemplateDiagnostic? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>A literal or resolved catalogue reference; only the parser constructs these parts.</summary>
public sealed class FrameFactsTemplatePart
{
    internal FrameFactsTemplatePart(string? text, FrameFactsTokenDefinition? token, string? format,
        FrameFactsTextStyle style, int sourceOffset)
    {
        Text = text;
        Token = token;
        Format = format;
        Style = style;
        SourceOffset = sourceOffset;
    }

    public string? Text { get; }
    public FrameFactsTokenDefinition? Token { get; }
    public string? Format { get; }
    public FrameFactsTextStyle Style { get; }
    public int SourceOffset { get; }
}

public sealed record FrameFactsTemplateLine(ImmutableArray<FrameFactsTemplatePart> Parts);

/// <summary>
/// A parsed single corner in hvo-frame-facts-template-v1. Bounded linear scanning, no recursion, regular
/// expressions, HTML, executable expressions or live lookups. All offsets are source UTF-16 offsets.
/// </summary>
public sealed class FrameFactsTemplate
{
    public const string GrammarId = "hvo-frame-facts-template-v1";
    public const int MaximumSourceLength = 4096;
    public const int MaximumLines = 8;
    public const int MaximumLineCharacters = 64;

    private FrameFactsTemplate(string source, ImmutableArray<FrameFactsTemplateLine> lines)
    {
        Source = source;
        Lines = lines;
    }

    public string Source { get; }
    public ImmutableArray<FrameFactsTemplateLine> Lines { get; }

    public static FrameFactsParseResult Parse(string? source, FrameFactsTokenCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (source is null)
        {
            return Failure("frame-facts.source-required", 0);
        }
        if (source.Length > MaximumSourceLength)
        {
            return Failure("frame-facts.source-too-long", MaximumSourceLength);
        }
        if (source.Length == 0)
        {
            return new(new(source, []), null);
        }

        var lines = ImmutableArray.CreateBuilder<FrameFactsTemplateLine>();
        var parts = ImmutableArray.CreateBuilder<FrameFactsTemplatePart>();
        var styles = new Stack<(FrameFactsTextStyle Style, int Offset)>();
        var style = FrameFactsTextStyle.None;
        var literal = new StringBuilder();
        var literalOffset = 0;
        var literalCharacters = 0;

        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new(literal.ToString(), null, null, style, literalOffset));
                literal.Clear();
            }
        }

        for (var i = 0; i < source.Length;)
        {
            var offset = i;
            var c = source[i];
            if (c is '\r' or '\n')
            {
                Flush();
                lines.Add(new(parts.ToImmutable()));
                parts.Clear();
                literalCharacters = 0;
                if (lines.Count == MaximumLines)
                {
                    return Failure("frame-facts.too-many-lines", i);
                }
                i += c == '\r' && i + 1 < source.Length && source[i + 1] == '\n' ? 2 : 1;
                continue;
            }
            if (c == '{' && i + 1 < source.Length && source[i + 1] == '{')
            {
                Flush();
                var end = source.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return Failure("frame-facts.malformed-token", i);
                }
                var expression = source[(i + 2)..end];
                var separator = expression.IndexOf('|', StringComparison.Ordinal);
                var key = separator < 0 ? expression : expression[..separator];
                var format = separator < 0 ? null : expression[(separator + 1)..];
                // Status companions may extend the maximum base key by seven characters.
                var baseKey = key.EndsWith(".status", StringComparison.Ordinal) ? key[..^7] : key;
                if (!FrameFactsTokenDefinition.IsKey(baseKey))
                {
                    return Failure("frame-facts.malformed-token", i);
                }
                if (key.StartsWith("spaceWeather.", StringComparison.Ordinal))
                {
                    return Failure("frame-facts.reserved-namespace", i);
                }
                var token = catalog.Find(key);
                if (token is null)
                {
                    return Failure("frame-facts.unknown-token", i);
                }
                if (format is not null && !token.Formats.Contains(format, StringComparer.Ordinal))
                {
                    return Failure("frame-facts.unknown-format", i);
                }
                parts.Add(new(null, token, format ?? token.DefaultFormat, style, i));
                i = end + 2;
                continue;
            }
            if (c is '{' or '}')
            {
                return Failure("frame-facts.malformed-token", i);
            }
            if (c is '<' or '>')
            {
                return Failure("frame-facts.unsupported-syntax", i);
            }
            if (c == '*' || c == '_' && i + 1 < source.Length && source[i + 1] == '_')
            {
                Flush();
                // Close the innermost marker first: *** can close italic followed by bold.
                var marker = c == '_' ? FrameFactsTextStyle.Underline
                    : styles.TryPeek(out var top) && top.Style == FrameFactsTextStyle.Italic &&
                        i + 2 < source.Length && source[i + 1] == '*' && source[i + 2] == '*'
                        ? FrameFactsTextStyle.Italic
                        : i + 1 < source.Length && source[i + 1] == '*' ? FrameFactsTextStyle.Bold
                        : FrameFactsTextStyle.Italic;
                var width = marker == FrameFactsTextStyle.Italic ? 1 : 2;
                if ((style & marker) != 0)
                {
                    if (styles.Peek().Style != marker)
                    {
                        return Failure("frame-facts.mismatched-style", i);
                    }
                    styles.Pop();
                    style &= ~marker;
                }
                else
                {
                    styles.Push((marker, i));
                    style |= marker;
                }
                i += width;
                continue;
            }
            if (c == '\\')
            {
                if (++i == source.Length || source[i] is not ('\\' or '*' or '_' or '{' or '}' or '|' or '<' or '>'))
                {
                    return Failure("frame-facts.invalid-escape", offset);
                }
            }
            var status = Rune.DecodeFromUtf16(source.AsSpan(i), out var rune, out var consumed);
            if (status != OperationStatus.Done || !IsTextRune(rune))
            {
                return Failure("frame-facts.invalid-character", offset);
            }
            if (++literalCharacters > MaximumLineCharacters)
            {
                return Failure("frame-facts.line-too-long", offset);
            }
            if (literal.Length == 0)
            {
                literalOffset = offset;
            }
            literal.Append(source.AsSpan(i, consumed));
            i += consumed;
        }
        if (styles.TryPeek(out var unclosed))
        {
            return Failure("frame-facts.unclosed-style", unclosed.Offset);
        }
        Flush();
        lines.Add(new(parts.ToImmutable()));
        return new(new(source, lines.ToImmutable()), null);
    }

    internal static bool IsTextRune(Rune rune) => !Rune.IsControl(rune) && rune.Value is not (0x2028 or 0x2029);

    private static FrameFactsParseResult Failure(string code, int offset) => new(null, new(code, offset));
}
