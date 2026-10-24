using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public sealed record ProcessingDecodedOption(
    ProcessingOptionDescriptor Descriptor,
    ProcessingOptionValue Value,
    bool IsExplicit);

/// <summary>A node's typed options. <see cref="Options"/> is populated only when the options are valid.</summary>
public sealed record ProcessingNodeOptionsDecodeResult(
    ImmutableArray<ProcessingDecodedOption> Options,
    ImmutableArray<ProcessingGraphDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.IsEmpty;

    public ProcessingDecodedOption Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Options.FirstOrDefault(option => string.Equals(option.Descriptor.Key, key, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"No decoded option '{key}'.");
    }
}

/// <summary>Edited options. <see cref="Options"/> is present only when the whole result validates.</summary>
public sealed record ProcessingNodeOptionsEncodeResult(
    JsonElement? Options,
    ImmutableArray<ProcessingGraphDiagnostic> Diagnostics)
{
    public bool IsValid => Options is not null && Diagnostics.IsEmpty;
}

/// <summary>
/// Typed reading and editing of a node's effective options against its descriptor (#1532). Members match ignoring
/// case, as CameraAgent's options binding does. Values are strict JSON types: a whole number is an integer token, a
/// toggle is <c>true</c> or <c>false</c>, and a quoted number is the wrong type. Only a nullable whole number accepts <c>null</c>. Members the descriptor
/// does not describe are never read or rewritten.
/// </summary>
/// <remarks>
/// The declared differences from CameraAgent's options binding: a quoted number, which the binder reads, is the wrong
/// type here; a described member repeated, also ignoring case, is a duplicate here where the binder keeps the last;
/// and comments or trailing commas inside an element are not seen here, because the codec reads the element rather
/// than re-parsing its text as the binder does. The graph and configuration parsers admit neither comments nor
/// trailing commas, so no parsed node carries them.
/// </remarks>
public static class ProcessingNodeOptionsCodec
{
    public const string DefaultPath = "effectiveOptions";

    private const byte MaximumChannel = byte.MaxValue;

    // An element built with comments or trailing commas allowed keeps them in its raw bytes, so the edit's span scan
    // tolerates them and preserves them rather than failing.
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ProcessingNodeOptionsDecodeResult Decode(
        ProcessingNodeOptionsDescriptor descriptor,
        JsonElement options,
        string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(path);
        var diagnostics = ImmutableArray.CreateBuilder<ProcessingGraphDiagnostic>();
        if (options.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.NotAnObject, path, "Step options must be a JSON object."));
            return new([], diagnostics.ToImmutable());
        }

        var described = descriptor.Options.SelectMany(static option => option.Members)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
        {
            if (described.Contains(property.Name) && !members.TryAdd(property.Name, property))
            {
                diagnostics.Add(new(
                    ProcessingOptionReasonCodes.DuplicateMember,
                    MemberPath(path, property.Name),
                    $"Member '{property.Name}' appears more than once, ignoring case."));
            }
        }

        var decoded = ImmutableArray.CreateBuilder<ProcessingDecodedOption>(descriptor.Options.Length);
        foreach (var option in descriptor.Options)
        {
            var defaults = MemberValues(option.DefaultValue);
            var values = new ProcessingOptionValue[option.Members.Length];
            var isExplicit = false;
            var valid = true;
            for (var index = 0; index < option.Members.Length; index++)
            {
                if (!members.TryGetValue(option.Members[index], out var member))
                {
                    values[index] = defaults[index];
                    continue;
                }
                isExplicit = true;
                if (ReadMember(option, member, path) is { } failure)
                {
                    diagnostics.Add(failure);
                    valid = false;
                    continue;
                }
                values[index] = ReadToken(MemberKind(option), member.Value, out _)!.Value;
            }
            if (!valid)
            {
                continue;
            }
            var value = option.Kind == ProcessingOptionKind.Color
                ? ProcessingOptionValue.FromColor(new(
                    (byte)values[0].AsWholeNumber(), (byte)values[1].AsWholeNumber(), (byte)values[2].AsWholeNumber()))
                : values[0];
            if (option.Check(value) is { } invalid)
            {
                diagnostics.Add(new(invalid.Code, MemberPath(path, members[option.Members[0]].Name), invalid.Message));
                continue;
            }
            decoded.Add(new(option, value, isExplicit));
        }
        if (diagnostics.Count == 0 && PreviewOptionDescriptors.IsPreviewAlias(descriptor.StepAlias) &&
            descriptor.StepVersion == PreviewOptionDescriptors.PercentileVersion &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "blackPercentile") is { } black &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "whitePercentile") is { } white &&
            black.Value.AsRealNumber() >= white.Value.AsRealNumber())
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.NotAllowed, MemberPath(path, "blackPercentile"),
                "BlackPercentile must be less than WhitePercentile."));
        }
        if (diagnostics.Count == 0 && descriptor.StepAlias == FrameFactsPresentation.StepAlias &&
            descriptor.StepVersion == FrameFactsPresentation.ProducerVersion)
        {
            try
            {
                FrameFactsPresentation.ValidateTemplates(options.Deserialize<FrameFactsPresentationOptions>(FrameFactsOptionsJson)!);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                diagnostics.Add(new(ProcessingOptionReasonCodes.NotAllowed, path, exception.Message));
            }
        }
        return diagnostics.Count == 0 ? new(decoded.ToImmutable(), []) : new([], diagnostics.ToImmutable());
    }

    private static readonly JsonSerializerOptions FrameFactsOptionsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Applies typed edits to <paramref name="source"/>. An edit that leaves an option's effective value unchanged,
    /// including setting an absent member to its default, writes nothing, so a no-op edit returns
    /// <paramref name="source"/> byte for byte and defaults are never materialized. Otherwise only the edited members'
    /// values change; every other byte of the source, including unknown members and their formatting, is kept, and
    /// new members are appended after the last member.
    /// </summary>
    public static ProcessingNodeOptionsEncodeResult Encode(
        ProcessingNodeOptionsDescriptor descriptor,
        JsonElement source,
        IReadOnlyDictionary<string, ProcessingOptionValue> edits,
        string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(path);
        if (source.ValueKind != JsonValueKind.Object)
        {
            return new(null, [new(ProcessingOptionReasonCodes.NotAnObject, path, "Step options must be a JSON object.")]);
        }
        var diagnostics = ImmutableArray.CreateBuilder<ProcessingGraphDiagnostic>();
        foreach (var (key, value) in edits.OrderBy(static edit => edit.Key, StringComparer.Ordinal))
        {
            var option = descriptor.Find(key);
            if (option is null)
            {
                diagnostics.Add(new(
                    ProcessingOptionReasonCodes.UnknownOption,
                    MemberPath(path, key),
                    $"'{key}' is not an option of {descriptor.DescriptorId}."));
            }
            else if (option.Check(value) is { } failure)
            {
                diagnostics.Add(new(failure.Code, MemberPath(path, key), failure.Message));
            }
        }
        if (diagnostics.Count > 0)
        {
            return new(null, diagnostics.ToImmutable());
        }

        var raw = JsonMarshal.GetRawUtf8Value(source);
        var properties = source.EnumerateObject().ToArray();
        var spans = ValueSpans(raw);
        var replacements = new List<(int Start, int End, byte[] Bytes)>();
        var appended = new List<(string Member, byte[] Bytes)>();
        foreach (var option in descriptor.Options)
        {
            if (!edits.TryGetValue(option.Key, out var edit))
            {
                continue;
            }
            var values = MemberValues(edit);
            var defaults = MemberValues(option.DefaultValue);
            for (var index = 0; index < option.Members.Length; index++)
            {
                var at = Array.FindIndex(properties, property =>
                    string.Equals(property.Name, option.Members[index], StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    // A member is unchanged only when it is valid as written and reads as the edited value, so an
                    // invalid spelling of that value, such as a signed-zero channel, is rewritten rather than kept.
                    if (ReadMember(option, properties[at], path) is not null ||
                        ReadToken(values[index].Kind, properties[at].Value, out _) != values[index])
                    {
                        replacements.Add((spans[at].Start, spans[at].End, WriteValue(values[index])));
                    }
                }
                else if (values[index] != defaults[index])
                {
                    appended.Add((option.Members[index], WriteValue(values[index])));
                }
            }
        }

        var result = replacements.Count == 0 && appended.Count == 0
            ? source
            : Splice(raw, spans, replacements, appended);
        var decoded = Decode(descriptor, result, path);
        return decoded.IsValid ? new(result, []) : new(null, decoded.Diagnostics);
    }

    private static ProcessingGraphDiagnostic? ReadMember(ProcessingOptionDescriptor option, JsonProperty member, string path)
    {
        var kind = MemberKind(option);
        if (ReadToken(kind, member.Value, out var outOfRange) is not { } value)
        {
            return outOfRange
                ? new(ProcessingOptionReasonCodes.OutOfRange, MemberPath(path, member.Name),
                    $"Option '{option.Key}' member '{member.Name}' is outside the representable range.")
                : new(ProcessingOptionReasonCodes.WrongType, MemberPath(path, member.Name),
                    $"Option '{option.Key}' member '{member.Name}' must be {Describe(kind)}.");
        }
        // A channel is read as CameraAgent binds it, as an unsigned byte, so a signed zero is out of range like -1.
        return option.Kind == ProcessingOptionKind.Color && !member.Value.TryGetByte(out _)
            ? new(ProcessingOptionReasonCodes.OutOfRange, MemberPath(path, member.Name), string.Create(
                CultureInfo.InvariantCulture,
                $"Option '{option.Key}' member '{member.Name}' must be an unsigned whole number between 0 and {MaximumChannel}."))
            : null;
    }

    private static ProcessingOptionValue? ReadToken(ProcessingOptionKind kind, JsonElement token, out bool outOfRange)
    {
        outOfRange = false;
        switch (kind)
        {
            case ProcessingOptionKind.Toggle:
                return token.ValueKind switch
                {
                    JsonValueKind.True => ProcessingOptionValue.FromToggle(true),
                    JsonValueKind.False => ProcessingOptionValue.FromToggle(false),
                    _ => null
                };
            case ProcessingOptionKind.NullableWholeNumber when token.ValueKind == JsonValueKind.Null:
                return ProcessingOptionValue.FromNullableWholeNumber(null);
            case ProcessingOptionKind.NullableWholeNumber:
            case ProcessingOptionKind.WholeNumber:
                if (!IsIntegerToken(token))
                {
                    return null;
                }
                if (token.TryGetInt64(out var whole))
                {
                    return kind == ProcessingOptionKind.NullableWholeNumber
                        ? ProcessingOptionValue.FromNullableWholeNumber(whole)
                        : ProcessingOptionValue.FromWholeNumber(whole);
                }
                outOfRange = true;
                return null;
            case ProcessingOptionKind.RealNumber:
                if (token.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }
                if (token.TryGetDouble(out var real) && double.IsFinite(real))
                {
                    return ProcessingOptionValue.FromRealNumber(real);
                }
                outOfRange = true;
                return null;
            case ProcessingOptionKind.Text:
                return token.ValueKind == JsonValueKind.String ? ProcessingOptionValue.FromText(token.GetString()!) : null;
            case ProcessingOptionKind.Choice:
                return token.ValueKind == JsonValueKind.String ? ProcessingOptionValue.FromChoice(token.GetString()!) : null;
            case ProcessingOptionKind.Font:
                return token.ValueKind == JsonValueKind.String ? ProcessingOptionValue.FromFont(token.GetString()!) : null;
            case ProcessingOptionKind.Glyph:
                return token.ValueKind == JsonValueKind.String ? ProcessingOptionValue.FromGlyph(token.GetString()!) : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "A colour is read one channel at a time.");
        }
    }

    private static bool IsIntegerToken(JsonElement token)
    {
        if (token.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        var raw = JsonMarshal.GetRawUtf8Value(token);
        var digits = raw[0] == (byte)'-' ? raw[1..] : raw;
        return !digits.IsEmpty && !digits.ContainsAnyExceptInRange((byte)'0', (byte)'9');
    }

    // A colour is stored as three whole-number channel members; every other option is one member.
    private static ProcessingOptionKind MemberKind(ProcessingOptionDescriptor option)
        => option.Kind == ProcessingOptionKind.Color ? ProcessingOptionKind.WholeNumber : option.Kind;

    private static ProcessingOptionValue[] MemberValues(ProcessingOptionValue value)
    {
        if (value.Kind != ProcessingOptionKind.Color)
        {
            return [value];
        }
        var color = value.AsColor();
        return
        [
            ProcessingOptionValue.FromWholeNumber(color.Red),
            ProcessingOptionValue.FromWholeNumber(color.Green),
            ProcessingOptionValue.FromWholeNumber(color.Blue)
        ];
    }

    private static string Describe(ProcessingOptionKind kind) => kind switch
    {
        ProcessingOptionKind.Toggle => "true or false",
        ProcessingOptionKind.WholeNumber => "a whole JSON number",
        ProcessingOptionKind.NullableWholeNumber => "a whole JSON number or null",
        ProcessingOptionKind.RealNumber => "a JSON number",
        _ => "a JSON string"
    };

    private static string MemberPath(string path, string member) => $"{path}.{member}";

    private static byte[] WriteValue(ProcessingOptionValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value.Kind)
            {
                case ProcessingOptionKind.Toggle:
                    writer.WriteBooleanValue(value.AsToggle());
                    break;
                case ProcessingOptionKind.WholeNumber:
                    writer.WriteNumberValue(value.AsWholeNumber());
                    break;
                case ProcessingOptionKind.NullableWholeNumber:
                    if (value.AsNullableWholeNumber() is { } whole)
                        writer.WriteNumberValue(whole);
                    else
                        writer.WriteNullValue();
                    break;
                case ProcessingOptionKind.RealNumber:
                    writer.WriteNumberValue(value.AsRealNumber());
                    break;
                default:
                    writer.WriteStringValue(value.AsText());
                    break;
            }
        }
        return buffer.WrittenSpan.ToArray();
    }

    // The byte range of each top-level member value of the object, in member order.
    private static List<(int Start, int End)> ValueSpans(ReadOnlySpan<byte> json)
    {
        var spans = new List<(int Start, int End)>();
        var reader = new Utf8JsonReader(json, ReaderOptions);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            reader.Read();
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            spans.Add((start, checked((int)reader.BytesConsumed)));
        }
        return spans;
    }

    private static JsonElement Splice(
        ReadOnlySpan<byte> raw,
        List<(int Start, int End)> spans,
        List<(int Start, int End, byte[] Bytes)> replacements,
        List<(string Member, byte[] Bytes)> appended)
    {
        if (appended.Count > 0)
        {
            var insertion = new ArrayBufferWriter<byte>();
            var separate = spans.Count > 0;
            foreach (var (member, bytes) in appended)
            {
                if (separate)
                {
                    insertion.Write(","u8);
                }
                separate = true;
                insertion.Write(Encoding.UTF8.GetBytes($"\"{member}\":"));
                insertion.Write(bytes);
            }
            var at = spans.Count > 0 ? spans[^1].End : 1;
            replacements.Add((at, at, insertion.WrittenSpan.ToArray()));
        }
        replacements.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var output = new ArrayBufferWriter<byte>(raw.Length + 64);
        var cursor = 0;
        foreach (var (start, end, bytes) in replacements)
        {
            output.Write(raw[cursor..start]);
            output.Write(bytes);
            cursor = end;
        }
        output.Write(raw[cursor..]);
        using var document = JsonDocument.Parse(output.WrittenMemory, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return document.RootElement.Clone();
    }
}
