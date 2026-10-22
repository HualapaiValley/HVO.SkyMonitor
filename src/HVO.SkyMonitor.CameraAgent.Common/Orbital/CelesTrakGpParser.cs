using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>One CelesTrak GP JSON element set after strict member validation, before contract normalization.</summary>
internal sealed record CelesTrakGpElementSet(int Index, string CatalogNumber, string? ObjectName, string? ObjectId,
    DateTimeOffset EpochUtc, double MeanMotion, double Eccentricity, double Inclination, double AscendingNode,
    double ArgumentOfPericenter, double MeanAnomaly, double BStar, double MeanMotionDot, double MeanMotionDdot,
    int EphemerisType, string Classification, long? ElementSetNumber, long? RevolutionNumber);

internal sealed record CelesTrakGpParseResult(IReadOnlyList<CelesTrakGpElementSet>? ElementSets, string? ReasonCode,
    string Detail);

/// <summary>
/// Strict parser for the CelesTrak GP JSON format (<c>FORMAT=JSON</c>): a root array of flat OMM-keyed objects. The
/// member allow-list is closed; any unknown, duplicate, mistyped or missing member rejects the whole payload with the
/// zero-based record index and a sanitized member name, never record text.
/// </summary>
/// <remarks>
/// EPOCH grammar: <c>YYYY-MM-DDTHH:MM:SS</c>, an optional <c>.</c> plus one to seven fraction digits, and an optional
/// trailing <c>Z</c>. Only ASCII digits and uppercase <c>T</c>/<c>Z</c> are accepted; no offset, no leap second and no
/// other separator. The value is UTC by the GP definition and must fall in
/// <c>[1957-10-04T00:00:00Z, RetrievedUtc + 30 days]</c> inclusive.
/// </remarks>
internal static class CelesTrakGpParser
{
    internal const int MaximumDepth = 4;
    internal const int MaximumTextLength = 256;
    internal const int MaximumMemberNameReport = 64;
    internal const int MaximumCatalogDigits = 9;
    internal const int MaximumIntegerDigits = 18;
    internal static readonly DateTimeOffset MinimumEpochUtc = new(1957, 10, 4, 0, 0, 0, TimeSpan.Zero);
    internal static readonly TimeSpan MaximumFutureEpoch = TimeSpan.FromDays(30);
    internal static readonly string[] SupportedOmmVersions = ["2.0", "3.0"];

    private const int MaximumEpochLength = 28;
    private const int MaximumEscapedTextBytes = MaximumTextLength * 6;
    private const int MaximumNameBytes = 128;

    internal enum MemberKind
    {
        Number,
        UnsignedInteger,
        CatalogNumber,
        Epoch,
        Classification,
        EphemerisType,
        OptionalText,
        OptionalUnsignedInteger,
        FixedMetadata,
        OmmVersion,
        IgnoredText
    }

    internal sealed record MemberRule(string Name, MemberKind Kind, bool Required, string? FixedValue = null)
    {
        public byte[] Utf8Name { get; } = Encoding.UTF8.GetBytes(Name);
    }

    private enum Member
    {
        ObjectName,
        ObjectId,
        Epoch,
        MeanMotion,
        Eccentricity,
        Inclination,
        AscendingNode,
        ArgumentOfPericenter,
        MeanAnomaly,
        EphemerisType,
        Classification,
        CatalogNumber,
        ElementSetNumber,
        RevolutionNumber,
        BStar,
        MeanMotionDot,
        MeanMotionDdot,
        CenterName,
        ReferenceFrame,
        TimeSystem,
        MeanElementTheory,
        OmmVersion,
        Comment,
        Originator
    }

    /// <summary>The closed allow-list, indexed by <see cref="Member"/>. The software identity is derived from it.</summary>
    internal static IReadOnlyList<MemberRule> Rules { get; } = Array.AsReadOnly(new MemberRule[]
    {
        new("OBJECT_NAME", MemberKind.OptionalText, Required: false),
        new("OBJECT_ID", MemberKind.OptionalText, Required: false),
        new("EPOCH", MemberKind.Epoch, Required: true),
        new("MEAN_MOTION", MemberKind.Number, Required: true),
        new("ECCENTRICITY", MemberKind.Number, Required: true),
        new("INCLINATION", MemberKind.Number, Required: true),
        new("RA_OF_ASC_NODE", MemberKind.Number, Required: true),
        new("ARG_OF_PERICENTER", MemberKind.Number, Required: true),
        new("MEAN_ANOMALY", MemberKind.Number, Required: true),
        new("EPHEMERIS_TYPE", MemberKind.EphemerisType, Required: true),
        new("CLASSIFICATION_TYPE", MemberKind.Classification, Required: true),
        new("NORAD_CAT_ID", MemberKind.CatalogNumber, Required: true),
        new("ELEMENT_SET_NO", MemberKind.OptionalUnsignedInteger, Required: false),
        new("REV_AT_EPOCH", MemberKind.OptionalUnsignedInteger, Required: false),
        new("BSTAR", MemberKind.Number, Required: true),
        new("MEAN_MOTION_DOT", MemberKind.Number, Required: true),
        new("MEAN_MOTION_DDOT", MemberKind.Number, Required: true),
        new("CENTER_NAME", MemberKind.FixedMetadata, Required: false, "EARTH"),
        new("REF_FRAME", MemberKind.FixedMetadata, Required: false, "TEME"),
        new("TIME_SYSTEM", MemberKind.FixedMetadata, Required: false, "UTC"),
        new("MEAN_ELEMENT_THEORY", MemberKind.FixedMetadata, Required: false, "SGP4"),
        new("CCSDS_OMM_VERS", MemberKind.OmmVersion, Required: false),
        new("COMMENT", MemberKind.IgnoredText, Required: false),
        new("ORIGINATOR", MemberKind.IgnoredText, Required: false)
    });

    private static readonly uint RequiredMask = ComputeRequiredMask();

    /// <summary>Canonical text of every rule above. Changing any rule changes the parser's pinned identity.</summary>
    internal static string RuleDescriptor { get; } = BuildRuleDescriptor();

    internal static CelesTrakGpParseResult Parse(ReadOnlySpan<byte> raw, DateTimeOffset retrievedUtc)
    {
        if (raw.Length == 0)
        {
            return Reject("orbital.empty-payload", "raw-bytes=0");
        }
        if (!Utf8.IsValid(raw))
        {
            return Reject("orbital.json-syntax", "utf8-invalid");
        }
        var maximumEpoch = retrievedUtc.ToUniversalTime() + MaximumFutureEpoch;
        var reader = new Utf8JsonReader(raw, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth
        });
        var sets = new List<CelesTrakGpElementSet>();
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return Reject("orbital.json-root", "root-not-array");
            }
            while (true)
            {
                if (!reader.Read())
                {
                    return Reject("orbital.json-syntax", "unterminated-array");
                }
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }
                var index = sets.Count;
                if (index >= OrbitalContract.MaximumRecords)
                {
                    return Reject("orbital.record-bound",
                        FormattableString.Invariant($"records>{OrbitalContract.MaximumRecords}"));
                }
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return Reject("orbital.record-shape", FormattableString.Invariant($"record={index} token={reader.TokenType}"));
                }
                var failure = ParseRecord(ref reader, index, maximumEpoch, out var set);
                if (failure is not null)
                {
                    return failure;
                }
                sets.Add(set!);
            }
            // A single JSON value only: any further token, including a second array, is a syntax error.
            if (reader.Read())
            {
                return Reject("orbital.json-syntax", "trailing-content");
            }
        }
        catch (JsonException exception)
        {
            return Reject("orbital.json-syntax",
                FormattableString.Invariant($"line={exception.LineNumber} byte={exception.BytePositionInLine}"));
        }
        catch (InvalidOperationException)
        {
            return Reject("orbital.json-syntax",
                FormattableString.Invariant($"invalid-escape offset={reader.TokenStartIndex}"));
        }
        if (sets.Count == 0)
        {
            return Reject("orbital.empty-payload", "records=0");
        }
        return new CelesTrakGpParseResult(sets.AsReadOnly(), null, FormattableString.Invariant($"records={sets.Count}"));
    }

    private static CelesTrakGpParseResult? ParseRecord(ref Utf8JsonReader reader, int index, DateTimeOffset maximumEpoch,
        out CelesTrakGpElementSet? set)
    {
        set = null;
        uint seen = 0;
        string catalog = string.Empty;
        string? objectName = null, objectId = null;
        var epoch = default(DateTimeOffset);
        double meanMotion = 0, eccentricity = 0, inclination = 0, node = 0, pericenter = 0, anomaly = 0;
        double bstar = 0, dot = 0, ddot = 0;
        long? elementSet = null, revolution = null;
        while (true)
        {
            if (!reader.Read())
            {
                return Reject("orbital.json-syntax", FormattableString.Invariant($"record={index} unterminated-object"));
            }
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            var member = Lookup(ref reader);
            if (member is null)
            {
                return Reject("orbital.unknown-member",
                    FormattableString.Invariant($"record={index} member={SanitizeMemberName(reader.ValueSpan)}"));
            }
            var rule = Rules[(int)member.Value];
            var bit = 1u << (int)member.Value;
            if ((seen & bit) != 0)
            {
                return Reject("orbital.duplicate-member", FormattableString.Invariant($"record={index} member={rule.Name}"));
            }
            seen |= bit;
            if (!reader.Read())
            {
                return Reject("orbital.json-syntax", FormattableString.Invariant($"record={index} member={rule.Name} missing-value"));
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                return MemberType(index, rule);
            }
            string? reason = null;
            switch (member.Value)
            {
                case Member.ObjectName:
                    reason = ReadOptionalText(ref reader, out objectName);
                    break;
                case Member.ObjectId:
                    reason = ReadOptionalText(ref reader, out objectId);
                    break;
                case Member.Epoch:
                    reason = ReadEpoch(ref reader, maximumEpoch, out epoch);
                    break;
                case Member.MeanMotion:
                    reason = ReadNumber(ref reader, out meanMotion);
                    break;
                case Member.Eccentricity:
                    reason = ReadNumber(ref reader, out eccentricity);
                    break;
                case Member.Inclination:
                    reason = ReadNumber(ref reader, out inclination);
                    break;
                case Member.AscendingNode:
                    reason = ReadNumber(ref reader, out node);
                    break;
                case Member.ArgumentOfPericenter:
                    reason = ReadNumber(ref reader, out pericenter);
                    break;
                case Member.MeanAnomaly:
                    reason = ReadNumber(ref reader, out anomaly);
                    break;
                case Member.BStar:
                    reason = ReadNumber(ref reader, out bstar);
                    break;
                case Member.MeanMotionDot:
                    reason = ReadNumber(ref reader, out dot);
                    break;
                case Member.MeanMotionDdot:
                    reason = ReadNumber(ref reader, out ddot);
                    break;
                case Member.EphemerisType:
                    reason = ReadUnsignedInteger(ref reader, out var ephemeris) ??
                        (ephemeris == 0 ? null : "orbital.unsupported-ephemeris-type");
                    break;
                case Member.Classification:
                    reason = reader.TokenType != JsonTokenType.String ? "orbital.member-type"
                        : reader.ValueTextEquals("U"u8) ? null : "orbital.unsupported-classification";
                    break;
                case Member.CatalogNumber:
                    reason = ReadCatalogNumber(ref reader, out catalog);
                    break;
                case Member.ElementSetNumber:
                    reason = ReadOptionalUnsignedInteger(ref reader, out elementSet);
                    break;
                case Member.RevolutionNumber:
                    reason = ReadOptionalUnsignedInteger(ref reader, out revolution);
                    break;
                case Member.CenterName or Member.ReferenceFrame or Member.TimeSystem or Member.MeanElementTheory:
                    reason = reader.TokenType != JsonTokenType.String ? "orbital.member-type"
                        : reader.ValueTextEquals(rule.FixedValue) ? null : "orbital.conflicting-metadata";
                    break;
                case Member.OmmVersion:
                    reason = reader.TokenType != JsonTokenType.String ? "orbital.member-type"
                        : IsSupportedOmmVersion(ref reader) ? null : "orbital.conflicting-metadata";
                    break;
                case Member.Comment or Member.Originator:
                    reason = reader.TokenType != JsonTokenType.String ? "orbital.member-type" : ReadIgnoredText(ref reader);
                    break;
            }
            if (reason is not null)
            {
                return Reject(reason, FormattableString.Invariant($"record={index} member={rule.Name}"));
            }
        }
        var missing = RequiredMask & ~seen;
        if (missing != 0)
        {
            var first = System.Numerics.BitOperations.TrailingZeroCount(missing);
            return Reject("orbital.missing-member", FormattableString.Invariant($"record={index} member={Rules[first].Name}"));
        }
        set = new CelesTrakGpElementSet(index, catalog, objectName, objectId, epoch, meanMotion, eccentricity, inclination,
            node, pericenter, anomaly, bstar, dot, ddot, EphemerisType: 0, Classification: "U", elementSet, revolution);
        return null;
    }

    private static Member? Lookup(ref Utf8JsonReader reader)
    {
        Span<byte> unescaped = stackalloc byte[MaximumNameBytes];
        scoped ReadOnlySpan<byte> name;
        if (!reader.ValueIsEscaped)
        {
            name = reader.ValueSpan;
        }
        else if (reader.ValueSpan.Length <= MaximumNameBytes)
        {
            name = unescaped[..reader.CopyString(unescaped)];
        }
        else
        {
            return null;
        }
        var rules = Rules;
        for (var i = 0; i < rules.Count; i++)
        {
            if (name.SequenceEqual(rules[i].Utf8Name))
            {
                return (Member)i;
            }
        }
        return null;
    }

    private static bool IsSupportedOmmVersion(ref Utf8JsonReader reader)
    {
        foreach (var version in SupportedOmmVersions)
        {
            if (reader.ValueTextEquals(version))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ReadNumber(ref Utf8JsonReader reader, out double value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number)
        {
            return "orbital.member-type";
        }
        // The reader returns infinity rather than failing for out-of-range literals such as 1e400.
        return reader.TryGetDouble(out value) && double.IsFinite(value) ? null : "orbital.invalid-number";
    }

    /// <summary>A JSON number spelled only with ASCII digits: no sign, fraction, exponent or leading zero.</summary>
    private static string? ReadUnsignedInteger(ref Utf8JsonReader reader, out long value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number)
        {
            return "orbital.member-type";
        }
        return TryParseDigits(reader.ValueSpan, MaximumIntegerDigits, out value) ? null : "orbital.invalid-number";
    }

    private static string? ReadOptionalUnsignedInteger(ref Utf8JsonReader reader, out long? value)
    {
        value = null;
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        var reason = ReadUnsignedInteger(ref reader, out var parsed);
        value = reason is null ? parsed : null;
        return reason;
    }

    /// <summary>NORAD catalog numbers are canonical decimal 1..999,999,999; never narrowed to five TLE digits.</summary>
    private static string? ReadCatalogNumber(ref Utf8JsonReader reader, out string catalog)
    {
        catalog = string.Empty;
        if (reader.TokenType != JsonTokenType.Number)
        {
            return "orbital.member-type";
        }
        var span = reader.ValueSpan;
        if (!TryParseDigits(span, MaximumCatalogDigits, out var parsed) || parsed == 0)
        {
            return "orbital.invalid-object-identity";
        }
        catalog = parsed.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryParseDigits(ReadOnlySpan<byte> span, int maximumDigits, out long value)
    {
        value = 0;
        if (span.Length is 0 || span.Length > maximumDigits || span[0] == (byte)'0' && span.Length > 1)
        {
            return false;
        }
        foreach (var b in span)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
            value = value * 10 + (b - '0');
        }
        return true;
    }

    private static string? ReadOptionalText(ref Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.String)
        {
            return "orbital.member-type";
        }
        var reason = ReadBoundedText(ref reader, out var text);
        if (reason is null && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
        }
        return reason;
    }

    private static string? ReadIgnoredText(ref Utf8JsonReader reader) => ReadBoundedText(ref reader, out _);

    private static string? ReadBoundedText(ref Utf8JsonReader reader, out string? text)
    {
        text = null;
        if (reader.ValueSpan.Length > MaximumEscapedTextBytes)
        {
            return "orbital.invalid-text";
        }
        try
        {
            text = reader.GetString();
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate cannot be represented as text.
            return "orbital.invalid-text";
        }
        return text is null || text.Length > MaximumTextLength || text.Any(char.IsControl) ? "orbital.invalid-text" : null;
    }

    private static string? ReadEpoch(ref Utf8JsonReader reader, DateTimeOffset maximumEpoch, out DateTimeOffset epoch)
    {
        epoch = default;
        if (reader.TokenType != JsonTokenType.String)
        {
            return "orbital.member-type";
        }
        Span<byte> scratch = stackalloc byte[MaximumEpochLength * 6];
        scoped ReadOnlySpan<byte> text;
        if (!reader.ValueIsEscaped)
        {
            text = reader.ValueSpan;
        }
        else if (reader.ValueSpan.Length <= scratch.Length)
        {
            text = scratch[..reader.CopyString(scratch)];
        }
        else
        {
            return "orbital.invalid-epoch";
        }
        if (!TryParseEpoch(text, out epoch))
        {
            return "orbital.invalid-epoch";
        }
        return epoch < MinimumEpochUtc || epoch > maximumEpoch ? "orbital.epoch-range" : null;
    }

    internal static bool TryParseEpoch(ReadOnlySpan<byte> s, out DateTimeOffset value)
    {
        value = default;
        if (s.Length is < 19 or > MaximumEpochLength || s[4] != '-' || s[7] != '-' || s[10] != 'T' || s[13] != ':' ||
            s[16] != ':' || !Digits(s, 0, 4, out var year) || !Digits(s, 5, 2, out var month) ||
            !Digits(s, 8, 2, out var day) || !Digits(s, 11, 2, out var hour) || !Digits(s, 14, 2, out var minute) ||
            !Digits(s, 17, 2, out var second))
        {
            return false;
        }
        var position = 19;
        long fractionTicks = 0;
        if (position < s.Length && s[position] == '.')
        {
            position++;
            var start = position;
            while (position < s.Length && s[position] is >= (byte)'0' and <= (byte)'9')
            {
                position++;
            }
            var count = position - start;
            if (count is < 1 or > 7 || !Digits(s, start, count, out var fraction))
            {
                return false;
            }
            fractionTicks = fraction;
            for (var i = count; i < 7; i++)
            {
                fractionTicks *= 10;
            }
        }
        if (position < s.Length && s[position] == 'Z')
        {
            position++;
        }
        if (position != s.Length || year < 1 || month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59 ||
            day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }
        value = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).AddTicks(fractionTicks);
        return true;
    }

    private static bool Digits(ReadOnlySpan<byte> s, int start, int count, out int value)
    {
        value = 0;
        for (var i = start; i < start + count; i++)
        {
            if (s[i] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
            value = value * 10 + (s[i] - '0');
        }
        return true;
    }

    /// <summary>Raw member bytes reduced to <c>[A-Z0-9_]</c> and at most 64 characters for reporting.</summary>
    internal static string SanitizeMemberName(ReadOnlySpan<byte> raw)
    {
        var length = Math.Min(raw.Length, MaximumMemberNameReport);
        if (length == 0)
        {
            return "_";
        }
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            var b = raw[i];
            chars[i] = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' ? (char)b : '_';
        }
        return new string(chars);
    }

    private static CelesTrakGpParseResult MemberType(int index, MemberRule rule) =>
        Reject("orbital.member-type", FormattableString.Invariant($"record={index} member={rule.Name}"));

    private static CelesTrakGpParseResult Reject(string reasonCode, string detail) => new(null, reasonCode, detail);

    private static uint ComputeRequiredMask()
    {
        uint mask = 0;
        for (var i = 0; i < Rules.Count; i++)
        {
            if (Rules[i].Required)
            {
                mask |= 1u << i;
            }
        }
        return mask;
    }

    private static string BuildRuleDescriptor()
    {
        var builder = new StringBuilder();
        builder.Append("format=celestrak-gp-json;profile=").Append(OrbitalContract.CelesTrakGpProfile).Append('\n');
        builder.Append("json=utf8-strict;bom=reject;comments=reject;trailing-commas=reject;max-depth=")
            .Append(MaximumDepth.ToString(CultureInfo.InvariantCulture))
            .Append(";root=array;trailing-content=reject;empty=reject\n");
        builder.Append("records=1..").Append(OrbitalContract.MaximumRecords.ToString(CultureInfo.InvariantCulture))
            .Append(";record=flat-object;unknown-member=reject;duplicate-member=reject;nested=reject\n");
        foreach (var rule in Rules)
        {
            builder.Append("member=").Append(rule.Name).Append(';').Append(rule.Kind.ToString())
                .Append(rule.Required ? ";required" : ";optional");
            if (rule.FixedValue is not null)
            {
                builder.Append(";value=").Append(rule.FixedValue);
            }
            builder.Append('\n');
        }
        builder.Append("omm-versions=").Append(string.Join(',', SupportedOmmVersions)).Append('\n');
        builder.Append("number=json-number;finite;string-number=reject\n");
        builder.Append("unsigned-integer=json-number;ascii-digits;no-leading-zero;max-digits=")
            .Append(MaximumIntegerDigits.ToString(CultureInfo.InvariantCulture)).Append(";null=optional-only\n");
        builder.Append("catalog=json-number;ascii-digits;1..")
            .Append(MaximumCatalogDigits.ToString(CultureInfo.InvariantCulture)).Append("-digits;nonzero;canonical-decimal\n");
        builder.Append("text=json-string-or-null;max-chars=").Append(MaximumTextLength.ToString(CultureInfo.InvariantCulture))
            .Append(";control=reject;blank=null\n");
        builder.Append("epoch=YYYY-MM-DDTHH:MM:SS[.f{1,7}][Z];utc;min=")
            .Append(MinimumEpochUtc.ToString("O", CultureInfo.InvariantCulture)).Append(";max=retrieved+")
            .Append(MaximumFutureEpoch.TotalDays.ToString(CultureInfo.InvariantCulture)).Append("d\n");
        builder.Append("ephemeris-type=0;classification=U;metadata-omitted=profile-default;metadata-conflict=reject\n");
        builder.Append("member-report=[A-Z0-9_];max-chars=")
            .Append(MaximumMemberNameReport.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString();
    }
}
