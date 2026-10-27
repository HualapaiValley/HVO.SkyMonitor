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
/// toggle is <c>true</c> or <c>false</c>, and a quoted number is the wrong type. Only a nullable number accepts
/// <c>null</c>. Enum collections use the consumer's string-enum binding, including defined numeric values.
/// Members the descriptor does not describe are never read or rewritten.
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

        if (RollingCombinationOptionDescriptors.IsDescriptor(descriptor) &&
            !RollingCombinationOptionDescriptors.IsSameGain(options))
        {
            return new([], [new(ProcessingOptionReasonCodes.NotAllowed, path,
                "This panel only edits the same-gain rolling mean; recipe and normalization bindings remain opaque.")]);
        }
        var scopes = ReadScopes(descriptor, options, path, diagnostics);
        var decoded = ImmutableArray.CreateBuilder<ProcessingDecodedOption>(descriptor.Options.Length);
        foreach (var option in descriptor.Options)
        {
            var scope = option.ParentMember ?? string.Empty;
            if (!scopes.TryGetValue(scope, out var members))
                continue;
            var optionPath = option.ParentMember is null ? path : MemberPath(path, option.ParentMember);
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
                if (ReadMember(option, member, optionPath) is { } failure)
                {
                    diagnostics.Add(failure);
                    valid = false;
                    continue;
                }
                values[index] = ReadOptionToken(option, member.Value, out _)!.Value;
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
                diagnostics.Add(new(invalid.Code, MemberPath(optionPath, members[option.Members[0]].Name), invalid.Message));
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
        if (diagnostics.Count == 0 && PreviewOptionDescriptors.IsPreviewAlias(descriptor.StepAlias) &&
            descriptor.StepVersion == PreviewOptionDescriptors.FixedVersion &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "blackLevel") is { } fixedBlack &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "whiteLevel") is { } fixedWhite &&
            fixedBlack.Value.AsRealNumber() >= fixedWhite.Value.AsRealNumber())
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.NotAllowed, MemberPath(path, "fixedTransfer.blackLevel"),
                "FixedTransfer BlackLevel must be less than WhiteLevel."));
        }
        if (diagnostics.Count == 0 && RollingCombinationOptionDescriptors.IsDescriptor(descriptor) &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "windowKind") is { } windowKind &&
            decoded.FirstOrDefault(static option => option.Descriptor.Key == "windowSize") is { } windowSize &&
            windowKind.Value.AsText() == nameof(ProcessingGraphWindowKind.Centered) &&
            windowSize.Value.AsWholeNumber() > ProcessingGraphCompiler.MaximumContractsPerNode)
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.NotAllowed, MemberPath(path, "windowSize"),
                $"Centered windows support at most {ProcessingGraphCompiler.MaximumContractsPerNode} required positions."));
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

        // Validate object shape and duplicate bindings before splicing. Invalid scalars can still be repaired.
        var shapeDiagnostics = ImmutableArray.CreateBuilder<ProcessingGraphDiagnostic>();
        ReadScopes(descriptor, source, path, shapeDiagnostics);
        if (shapeDiagnostics.Count > 0)
            return new(null, shapeDiagnostics.ToImmutable());
        var result = EditScope(source, descriptor.Options.Where(static option => option.ParentMember is null), edits, path);
        foreach (var group in descriptor.Options.Where(static option => option.ParentMember is not null)
            .GroupBy(static option => option.ParentMember!, StringComparer.OrdinalIgnoreCase))
        {
            var properties = result.EnumerateObject().ToArray();
            var at = Array.FindIndex(properties, property => string.Equals(property.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            var nested = properties[at].Value;
            var changed = EditScope(nested, group, edits, MemberPath(path, properties[at].Name));
            if (changed.GetRawText() != nested.GetRawText())
            {
                var raw = JsonMarshal.GetRawUtf8Value(result);
                var spans = ValueSpans(raw);
                result = Splice(raw, spans, [(spans[at].Start, spans[at].End, JsonMarshal.GetRawUtf8Value(changed).ToArray())], []);
            }
        }
        var decoded = Decode(descriptor, result, path);
        return decoded.IsValid ? new(result, []) : new(null, decoded.Diagnostics);
    }

    private static Dictionary<string, Dictionary<string, JsonProperty>> ReadScopes(
        ProcessingNodeOptionsDescriptor descriptor, JsonElement source, string path,
        ImmutableArray<ProcessingGraphDiagnostic>.Builder diagnostics)
    {
        var scopes = new Dictionary<string, Dictionary<string, JsonProperty>>(StringComparer.OrdinalIgnoreCase);
        var parents = descriptor.Options.Select(static option => option.ParentMember).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rootMembers = descriptor.Options.Where(static option => option.ParentMember is null)
            .SelectMany(static option => option.Members).Concat(parents);
        scopes[string.Empty] = ReadMembers(source, rootMembers, path, diagnostics);
        foreach (var parent in parents)
        {
            if (!scopes[string.Empty].TryGetValue(parent, out var member) || member.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new(ProcessingOptionReasonCodes.NotAnObject, MemberPath(path, parent),
                    $"An existing {parent} object is required; this panel cannot create it or switch transfer modes."));
                continue;
            }
            scopes[parent] = ReadMembers(member.Value, descriptor.Options
                .Where(option => string.Equals(option.ParentMember, parent, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static option => option.Members), MemberPath(path, member.Name), diagnostics);
        }
        return scopes;
    }

    private static Dictionary<string, JsonProperty> ReadMembers(JsonElement source, IEnumerable<string> describedMembers,
        string path, ImmutableArray<ProcessingGraphDiagnostic>.Builder diagnostics)
    {
        var described = describedMembers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
        {
            if (described.Contains(property.Name) && !members.TryAdd(property.Name, property))
                diagnostics.Add(new(ProcessingOptionReasonCodes.DuplicateMember, MemberPath(path, property.Name),
                    $"Member '{property.Name}' appears more than once, ignoring case."));
        }
        return members;
    }

    private static JsonElement EditScope(JsonElement source, IEnumerable<ProcessingOptionDescriptor> options,
        IReadOnlyDictionary<string, ProcessingOptionValue> edits, string path)
    {
        var raw = JsonMarshal.GetRawUtf8Value(source);
        var properties = source.EnumerateObject().ToArray();
        var spans = ValueSpans(raw);
        var replacements = new List<(int Start, int End, byte[] Bytes)>();
        var appended = new List<(string Member, byte[] Bytes)>();
        foreach (var option in options)
        {
            if (!edits.TryGetValue(option.Key, out var edit))
                continue;
            var values = MemberValues(edit);
            var defaults = MemberValues(option.DefaultValue);
            for (var index = 0; index < option.Members.Length; index++)
            {
                var at = Array.FindIndex(properties, property =>
                    string.Equals(property.Name, option.Members[index], StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    if (ReadMember(option, properties[at], path) is not null ||
                        ReadOptionToken(option, properties[at].Value, out _) != values[index])
                        replacements.Add((spans[at].Start, spans[at].End, WriteEditedValue(option, properties[at].Value, values[index])));
                }
                else if (values[index] != defaults[index])
                    appended.Add((option.Members[index], WriteEditedValue(option, default, values[index])));
            }
        }
        return replacements.Count == 0 && appended.Count == 0 ? source : Splice(raw, spans, replacements, appended);
    }

    private static byte[] WriteEditedValue(ProcessingOptionDescriptor option, JsonElement source, ProcessingOptionValue value)
    {
        if (option.Kind is not (ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection))
            return WriteValue(value);
        if (ReadOptionToken(option, source, out _) is not { } previous)
            return WriteValue(ProcessingOptionValue.FromCollection(value.Kind, new(value.AsCollection().Items)));

        var old = previous.AsCollection();
        var edited = value.AsCollection();
        var tokens = new string?[edited.Items.Length];
        var used = new HashSet<int>();
        // First reserve the exact occurrences carried by the editor, then match unannotated typed edits.
        // A source token from another option cannot smuggle a different enum binding into this one.
        for (var index = 0; index < edited.Items.Length; index++)
        {
            if (edited.SourceTokens[index] is not { } token) continue;
            for (var prior = 0; prior < old.Items.Length; prior++)
            {
                if (old.Items[prior] == edited.Items[index] && old.SourceTokens[prior] == token && used.Add(prior))
                {
                    tokens[index] = token;
                    break;
                }
            }
        }
        for (var index = 0; index < edited.Items.Length; index++)
        {
            if (tokens[index] is not null) continue;
            for (var prior = 0; prior < old.Items.Length; prior++)
            {
                if (old.Items[prior] == edited.Items[index] && used.Add(prior))
                {
                    tokens[index] = old.SourceTokens[prior];
                    break;
                }
            }
        }
        return WriteValue(ProcessingOptionValue.FromCollection(value.Kind, new(edited.Items, tokens)));
    }

    // WindowKind already accepts both enum numbers and names. Read its effective choice without rewriting
    // an untouched numeric token or its casing when the operator opens and saves an existing graph.
    private static ProcessingOptionValue? ReadOptionToken(ProcessingOptionDescriptor option, JsonElement token, out bool outOfRange)
    {
        if (option.Kind == ProcessingOptionKind.RowCollection)
        {
            outOfRange = token.ValueKind == JsonValueKind.Array && token.GetArrayLength() > option.MaximumItems;
            if (token.ValueKind != JsonValueKind.Array || outOfRange) return null;
            return ProcessingOptionValue.FromCollection(option.Kind, new(token.EnumerateArray().Select(static row => row.GetRawText())));
        }
        if (option.Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection or ProcessingOptionKind.ChoiceCollection)
        {
            outOfRange = false;
            if (token.ValueKind != JsonValueKind.Array) return null;
            if (token.GetArrayLength() > option.MaximumItems)
            {
                outOfRange = true;
                return null;
            }
            var items = new List<string>();
            var tokens = new List<string?>();
            foreach (var item in token.EnumerateArray())
            {
                var value = option.Kind == ProcessingOptionKind.EnumCollection
                    ? option.ReadEnumItem!(item)
                    : item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (value is null) return null;
                items.Add(value);
                tokens.Add(item.GetRawText());
            }
            return ProcessingOptionValue.FromCollection(option.Kind, new(items, tokens));
        }
        if (ReferenceEquals(option, RollingCombinationOptionDescriptors.Descriptor.Find("windowKind")))
        {
            outOfRange = false;
            var text = token.ValueKind == JsonValueKind.String ? token.GetString() : token.GetRawText();
            if (token.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                Enum.TryParse<ProcessingGraphWindowKind>(text, true, out var kind) && Enum.IsDefined(kind))
                return ProcessingOptionValue.FromChoice(kind.ToString());
            return null;
        }
        return ReadToken(MemberKind(option), token, out outOfRange);
    }

    private static ProcessingGraphDiagnostic? ReadMember(ProcessingOptionDescriptor option, JsonProperty member, string path)
    {
        var kind = MemberKind(option);
        if (ReadOptionToken(option, member.Value, out var outOfRange) is not { } value)
        {
            return outOfRange
                ? new(ProcessingOptionReasonCodes.OutOfRange, MemberPath(path, member.Name),
                    $"Option '{option.Key}' member '{member.Name}' is outside the representable range.")
                : new(ProcessingOptionReasonCodes.WrongType, MemberPath(path, member.Name),
                    $"Option '{option.Key}' member '{member.Name}' must be {Describe(kind)}.");
        }
        // Channels and nullable byte overrides must use the producer's unsigned token spelling. Null stays valid
        // for a nullable byte; a signed zero must be rejected and rewritten by an explicit zero edit.
        return (option.Kind == ProcessingOptionKind.Color || option.IsUnsignedByte && member.Value.ValueKind != JsonValueKind.Null) &&
            !member.Value.TryGetByte(out _)
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
            case ProcessingOptionKind.NullableRealNumber when token.ValueKind == JsonValueKind.Null:
                return ProcessingOptionValue.FromNullableRealNumber(null);
            case ProcessingOptionKind.NullableRealNumber:
            case ProcessingOptionKind.RealNumber:
                if (token.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }
                if (token.TryGetDouble(out var real) && double.IsFinite(real))
                {
                    return kind == ProcessingOptionKind.NullableRealNumber
                        ? ProcessingOptionValue.FromNullableRealNumber(real)
                        : ProcessingOptionValue.FromRealNumber(real);
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
        ProcessingOptionKind.NullableRealNumber => "a JSON number or null",
        ProcessingOptionKind.TextCollection => "an array of nonblank strings",
        ProcessingOptionKind.EnumCollection => "an array of defined enum names or numbers",
        ProcessingOptionKind.ChoiceCollection => "an array of supported string choices",
        ProcessingOptionKind.RowCollection => "an array of layer override objects",
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
                case ProcessingOptionKind.RowCollection:
                    writer.WriteStartArray();
                    foreach (var row in value.AsCollection().Items) writer.WriteRawValue(row);
                    writer.WriteEndArray();
                    break;
                case ProcessingOptionKind.TextCollection:
                case ProcessingOptionKind.EnumCollection:
                case ProcessingOptionKind.ChoiceCollection:
                    writer.WriteStartArray();
                    var collection = value.AsCollection();
                    for (var index = 0; index < collection.Items.Length; index++)
                    {
                        if (collection.SourceTokens[index] is { } token)
                            writer.WriteRawValue(token);
                        else
                            writer.WriteStringValue(collection.Items[index]);
                    }
                    writer.WriteEndArray();
                    break;
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
                case ProcessingOptionKind.NullableRealNumber:
                    if (value.AsNullableRealNumber() is { } real)
                        writer.WriteNumberValue(real);
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
