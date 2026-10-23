using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.Processing;

public enum FrameFactsMissingPolicy
{
    Placeholder,
    DropLine
}

public sealed record FrameFactsTextRun(string Text, FrameFactsTextStyle Style);

public sealed record FrameFactsRenderedLine(ImmutableArray<FrameFactsTextRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

public sealed record FrameFactsEvaluationResult(
    ImmutableArray<FrameFactsRenderedLine> Lines, FrameFactsTemplateDiagnostic? Error)
{
    public bool IsValid => Error is null;
    public string Text => string.Join('\n', Lines.Select(line => line.Text));
}

/// <summary>Pure evaluation of one corner; results are atomic, bounded plain-text runs, never markup.</summary>
public static class FrameFactsEvaluator
{
    public const string UnavailableText = "UNAVAILABLE";

    public static FrameFactsEvaluationResult Evaluate(FrameFactsTemplate template, FrameFactsTokenContext context,
        FrameFactsMissingPolicy missingPolicy = FrameFactsMissingPolicy.Placeholder)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(missingPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(missingPolicy));
        }
        var lines = ImmutableArray.CreateBuilder<FrameFactsRenderedLine>();
        foreach (var line in template.Lines)
        {
            // Decide suppression before expansion. Status tokens keep their entire line visible, even
            // beside an unavailable scalar, so DropLine cannot erase the reason a fact is unavailable.
            if (missingPolicy == FrameFactsMissingPolicy.DropLine &&
                !line.Parts.Any(part => part.Token?.IsStatus == true) &&
                line.Parts.Any(part => part.Token is { } token && Availability(token, context) != FrameFactsAvailability.Available))
            {
                continue;
            }
            var runs = ImmutableArray.CreateBuilder<FrameFactsTextRun>();
            var characters = 0;
            foreach (var part in line.Parts)
            {
                var text = part.Text ?? Expand(part.Token!, part.Format!, context);
                for (var i = 0; i < text.Length;)
                {
                    var status = Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed);
                    if (status != OperationStatus.Done || !FrameFactsTemplate.IsTextRune(rune))
                    {
                        return Failure("frame-facts.invalid-expansion", part.SourceOffset);
                    }
                    if (++characters > FrameFactsTemplate.MaximumLineCharacters)
                    {
                        return Failure("frame-facts.expansion-too-long", part.SourceOffset);
                    }
                    i += consumed;
                }
                if (text.Length > 0)
                {
                    runs.Add(new(text, part.Style));
                }
            }
            lines.Add(new(runs.ToImmutable()));
        }
        return new(lines.ToImmutable(), null);
    }

    private static FrameFactsAvailability Availability(FrameFactsTokenDefinition token, FrameFactsTokenContext context)
    {
        var fact = context.Get(token.FactKey);
        return fact.Availability == FrameFactsAvailability.Available && fact.Value.Kind != token.FactKind
            ? FrameFactsAvailability.Unsupported : fact.Availability;
    }

    private static string Expand(FrameFactsTokenDefinition token, string format, FrameFactsTokenContext context)
    {
        var availability = Availability(token, context);
        if (token.IsStatus)
        {
            return availability switch
            {
                FrameFactsAvailability.Available => "AVAILABLE",
                FrameFactsAvailability.Missing => "MISSING",
                FrameFactsAvailability.Stale => "STALE",
                FrameFactsAvailability.Conflicting => "CONFLICTING",
                _ => "UNSUPPORTED"
            };
        }
        if (availability != FrameFactsAvailability.Available)
        {
            return UnavailableText;
        }
        var value = context.Get(token.FactKey).Value;
        return token.Kind switch
        {
            FrameFactsValueKind.Text => value.Text!,
            FrameFactsValueKind.WholeNumber => value.WholeNumber.ToString(CultureInfo.InvariantCulture),
            FrameFactsValueKind.Number => value.Number.ToString(format switch
            {
                "fixed0" => "F0",
                "fixed1" => "F1",
                "fixed2" => "F2",
                "fixed3" => "F3",
                _ => "G17"
            }, CultureInfo.InvariantCulture),
            FrameFactsValueKind.Timestamp => value.Timestamp.ToString(format switch
            {
                "date" => "yyyy-MM-dd",
                "time" => "HH:mm:ss",
                _ => "yyyy-MM-dd'T'HH:mm:ss.fffzzz"
            }, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Unknown frame fact kind.")
        };
    }

    private static FrameFactsEvaluationResult Failure(string code, int offset) => new([], new(code, offset));
}
