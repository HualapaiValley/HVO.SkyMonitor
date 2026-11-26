using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>One CelesTrak GP CSV element set after strict column validation, before contract normalization.</summary>
internal sealed record CelesTrakGpElementSet(string CatalogNumber, string? ObjectName, string? ObjectId,
    DateTimeOffset EpochUtc, double MeanMotion, double Eccentricity, double Inclination, double AscendingNode,
    double ArgumentOfPericenter, double MeanAnomaly, double BStar, double MeanMotionDot, double MeanMotionDdot,
    int EphemerisType, string Classification, long? ElementSetNumber, long? RevolutionNumber);

internal sealed record CelesTrakGpParseResult(IReadOnlyList<CelesTrakGpElementSet>? ElementSets, string? ReasonCode,
    string Detail);

/// <summary>
/// Strict parser for CelesTrak GP CSV with a header of OMM field names. Invalid headers or rows reject the whole
/// payload. Diagnostics identify the row and known column without including provider field values.
/// </summary>
/// <remarks>
/// EPOCH grammar: <c>YYYY-MM-DDTHH:MM:SS</c>, an optional <c>.</c> plus one to seven fraction digits, and an optional
/// trailing <c>Z</c>. Only ASCII digits and uppercase <c>T</c>/<c>Z</c> are accepted; no offset, no leap second and no
/// other separator. The value is UTC by the GP definition and must fall in
/// <c>[1957-10-04T00:00:00Z, RetrievedUtc + 30 days]</c> inclusive.
/// </remarks>
internal static class CelesTrakGpParser
{
    internal const int MaximumTextLength = 256;
    internal const int MaximumCatalogDigits = 9;
    internal const int MaximumIntegerDigits = 18;
    internal static readonly DateTimeOffset MinimumEpochUtc = new(1957, 10, 4, 0, 0, 0, TimeSpan.Zero);
    internal static readonly TimeSpan MaximumFutureEpoch = TimeSpan.FromDays(30);
    internal static readonly string[] SupportedOmmVersions = ["2.0", "3.0"];

    private const int MaximumEpochLength = 28;
    private const int MaximumTextBytes = MaximumTextLength * 4;

    internal enum MemberKind
    {
        Number,
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
        public byte[]? FixedUtf8Value { get; } = FixedValue is null ? null : Encoding.UTF8.GetBytes(FixedValue);
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
        if (raw.IsEmpty)
        {
            return Reject("orbital.empty-payload", "raw-bytes=0");
        }
        if (!Utf8.IsValid(raw))
        {
            return Reject("orbital.csv-syntax", "utf8-invalid");
        }
        if (raw.StartsWith("\uFEFF"u8))
        {
            raw = raw[3..];
        }

        Span<Range> fields = stackalloc Range[Rules.Count];
        Span<Member> columns = stackalloc Member[Rules.Count];
        var header = ReadLine(ref raw);
        var failure = ReadHeader(header, fields, columns, out var count);
        if (failure is not null)
        {
            return failure;
        }

        var utc = retrievedUtc.ToUniversalTime();
        var maximumEpoch = utc > DateTimeOffset.MaxValue - MaximumFutureEpoch
            ? DateTimeOffset.MaxValue
            : utc + MaximumFutureEpoch;
        var sets = new List<CelesTrakGpElementSet>();
        while (!raw.IsEmpty)
        {
            var index = sets.Count;
            if (index == OrbitalContract.MaximumRecords)
            {
                return Reject("orbital.record-bound",
                    FormattableString.Invariant($"records>{OrbitalContract.MaximumRecords}"));
            }

            var row = ReadLine(ref raw);
            if (!TryReadFields(row, fields, out var rowCount))
            {
                return Reject("orbital.csv-syntax", FormattableString.Invariant($"record={index} invalid-row"));
            }
            if (rowCount != count)
            {
                return Reject("orbital.csv-row-width",
                    FormattableString.Invariant($"record={index} columns={rowCount} expected={count}"));
            }

            failure = ParseRecord(row, fields[..count], columns[..count], index, maximumEpoch, out var set);
            if (failure is not null)
            {
                return failure;
            }
            sets.Add(set!);
        }
        return sets.Count == 0
            ? Reject("orbital.empty-payload", "records=0")
            : new CelesTrakGpParseResult(sets, null, string.Empty);
    }

    private static ReadOnlySpan<byte> ReadLine(ref ReadOnlySpan<byte> raw)
    {
        var newline = raw.IndexOf((byte)'\n');
        if (newline < 0)
        {
            var last = raw;
            raw = [];
            return last;
        }
        var line = raw[..newline];
        raw = raw[(newline + 1)..];
        return !line.IsEmpty && line[^1] == '\r' ? line[..^1] : line;
    }

    // A line contains one record. Quoted fields may contain commas and doubled quotes. Embedded line breaks are
    // refused, as control characters are outside the GP field profile.
    private static bool TryReadFields(ReadOnlySpan<byte> line, Span<Range> fields, out int count)
    {
        count = 0;
        if (line.Contains((byte)'\r'))
        {
            return false;
        }
        var position = 0;
        while (count < fields.Length)
        {
            if (position < line.Length && line[position] == '"')
            {
                var start = ++position;
                while (true)
                {
                    var quote = line[position..].IndexOf((byte)'"');
                    if (quote < 0)
                    {
                        return false;
                    }
                    position += quote + 1;
                    if (position == line.Length || line[position] != '"')
                    {
                        break;
                    }
                    position++;
                }
                fields[count++] = start..(position - 1);
            }
            else
            {
                var start = position;
                var separator = line[position..].IndexOfAny((byte)',', (byte)'"');
                position = separator < 0 ? line.Length : position + separator;
                fields[count++] = start..position;
            }

            if (position == line.Length)
            {
                return true;
            }
            if (line[position++] != ',')
            {
                return false;
            }
        }
        return false;
    }

    private static CelesTrakGpParseResult? ReadHeader(ReadOnlySpan<byte> row, Span<Range> fields, Span<Member> columns,
        out int count)
    {
        if (!TryReadFields(row, fields, out count))
        {
            return Reject("orbital.csv-header", "invalid-header");
        }
        uint seen = 0;
        for (var column = 0; column < count; column++)
        {
            var member = Lookup(row[fields[column]]);
            if (member is null)
            {
                return Reject("orbital.unknown-member", FormattableString.Invariant($"column={column}"));
            }
            var bit = 1u << (int)member.Value;
            if ((seen & bit) != 0)
            {
                return Reject("orbital.duplicate-member", $"header member={Rules[(int)member.Value].Name}");
            }
            columns[column] = member.Value;
            seen |= bit;
        }
        var missing = RequiredMask & ~seen;
        if (missing != 0)
        {
            var first = System.Numerics.BitOperations.TrailingZeroCount(missing);
            return Reject("orbital.missing-member", $"header member={Rules[first].Name}");
        }
        return null;
    }

    private static Member? Lookup(ReadOnlySpan<byte> name)
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            if (name.SequenceEqual(Rules[i].Utf8Name))
            {
                return (Member)i;
            }
        }
        return null;
    }

    private static CelesTrakGpParseResult? ParseRecord(ReadOnlySpan<byte> row, ReadOnlySpan<Range> fields,
        ReadOnlySpan<Member> columns, int index, DateTimeOffset maximumEpoch, out CelesTrakGpElementSet? set)
    {
        set = null;
        string? objectName = null, objectId = null;
        string catalog = string.Empty;
        DateTimeOffset epoch = default;
        double meanMotion = 0, eccentricity = 0, inclination = 0, node = 0, pericenter = 0, anomaly = 0,
            bstar = 0, dot = 0, ddot = 0;
        long? elementSet = null, revolution = null;

        for (var column = 0; column < columns.Length; column++)
        {
            var member = columns[column];
            var field = row[fields[column]];
            var rule = Rules[(int)member];
            var reason = member switch
            {
                Member.ObjectName => ReadOptionalText(field, out objectName),
                Member.ObjectId => ReadOptionalText(field, out objectId),
                Member.Epoch => ReadEpoch(field, maximumEpoch, out epoch),
                Member.MeanMotion => ReadNumber(field, out meanMotion),
                Member.Eccentricity => ReadNumber(field, out eccentricity),
                Member.Inclination => ReadNumber(field, out inclination),
                Member.AscendingNode => ReadNumber(field, out node),
                Member.ArgumentOfPericenter => ReadNumber(field, out pericenter),
                Member.MeanAnomaly => ReadNumber(field, out anomaly),
                Member.BStar => ReadNumber(field, out bstar),
                Member.MeanMotionDot => ReadNumber(field, out dot),
                Member.MeanMotionDdot => ReadNumber(field, out ddot),
                Member.EphemerisType => ReadEphemerisType(field),
                Member.Classification => field.SequenceEqual("U"u8) ? null : "orbital.unsupported-classification",
                Member.CatalogNumber => ReadCatalogNumber(field, out catalog),
                Member.ElementSetNumber => ReadOptionalUnsignedInteger(field, out elementSet),
                Member.RevolutionNumber => ReadOptionalUnsignedInteger(field, out revolution),
                Member.OmmVersion => field.SequenceEqual("2.0"u8) || field.SequenceEqual("3.0"u8)
                    ? null : "orbital.conflicting-metadata",
                Member.Comment or Member.Originator => ReadOptionalText(field, out _),
                _ => field.SequenceEqual(rule.FixedUtf8Value) ? null : "orbital.conflicting-metadata"
            };
            if (reason is not null)
            {
                return Reject(reason, FormattableString.Invariant($"record={index} member={rule.Name}"));
            }
        }

        set = new CelesTrakGpElementSet(catalog, objectName, objectId, epoch, meanMotion, eccentricity, inclination,
            node, pericenter, anomaly, bstar, dot, ddot, EphemerisType: 0, Classification: "U", elementSet, revolution);
        return null;
    }

    private static string? ReadNumber(ReadOnlySpan<byte> field, out double value) =>
        Utf8Parser.TryParse(field, out value, out var consumed) && consumed == field.Length && double.IsFinite(value)
            ? null : "orbital.invalid-number";

    private static string? ReadEphemerisType(ReadOnlySpan<byte> field) =>
        !TryParseDigits(field, MaximumIntegerDigits, out var value) ? "orbital.invalid-number"
            : value == 0 ? null : "orbital.unsupported-ephemeris-type";

    private static string? ReadOptionalUnsignedInteger(ReadOnlySpan<byte> field, out long? value)
    {
        value = null;
        if (field.IsEmpty)
        {
            return null;
        }
        if (!TryParseDigits(field, MaximumIntegerDigits, out var parsed))
        {
            return "orbital.invalid-number";
        }
        value = parsed;
        return null;
    }

    private static string? ReadCatalogNumber(ReadOnlySpan<byte> field, out string catalog)
    {
        catalog = string.Empty;
        if (!TryParseDigits(field, MaximumCatalogDigits, out var parsed) || parsed == 0)
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

    private static string? ReadOptionalText(ReadOnlySpan<byte> field, out string? value)
    {
        value = null;
        if (field.Length > MaximumTextBytes)
        {
            return "orbital.invalid-text";
        }
        var text = Encoding.UTF8.GetString(field).Replace("\"\"", "\"", StringComparison.Ordinal);
        if (text.Length > MaximumTextLength || text.Any(char.IsControl))
        {
            return "orbital.invalid-text";
        }
        value = string.IsNullOrWhiteSpace(text) ? null : text;
        return null;
    }

    private static string? ReadEpoch(ReadOnlySpan<byte> field, DateTimeOffset maximumEpoch, out DateTimeOffset epoch)
    {
        if (!TryParseEpoch(field, out epoch))
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
        builder.Append("format=celestrak-gp-csv;profile=").Append(OrbitalContract.CelesTrakGpCsvProfile).Append('\n');
        builder.Append("csv=utf8-strict;bom=optional;lines=crlf-or-lf;final-newline=optional;embedded-newlines=reject;")
            .Append("quoting=double-quote;escape=doubled-quote;header=omm-names-any-order;row-width=header\n");
        builder.Append("records=1..").Append(OrbitalContract.MaximumRecords.ToString(CultureInfo.InvariantCulture))
            .Append(";unknown-member=reject;duplicate-member=reject\n");
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
        builder.Append("number=invariant-decimal-or-exponent;finite;whole-field\n");
        builder.Append("unsigned-integer=ascii-digits;no-leading-zero;max-digits=")
            .Append(MaximumIntegerDigits.ToString(CultureInfo.InvariantCulture)).Append(";empty=optional-only\n");
        builder.Append("catalog=ascii-digits;1..")
            .Append(MaximumCatalogDigits.ToString(CultureInfo.InvariantCulture)).Append("-digits;nonzero;canonical-decimal\n");
        builder.Append("text=utf8;max-utf16-chars=").Append(MaximumTextLength.ToString(CultureInfo.InvariantCulture))
            .Append(";control=reject;blank=null\n");
        builder.Append("epoch=YYYY-MM-DDTHH:MM:SS[.f{1,7}][Z];utc;min=")
            .Append(MinimumEpochUtc.ToString("O", CultureInfo.InvariantCulture)).Append(";max=retrieved+")
            .Append(MaximumFutureEpoch.TotalDays.ToString(CultureInfo.InvariantCulture)).Append("d\n");
        builder.Append("ephemeris-type=0;classification=U;metadata-omitted=profile-default;metadata-conflict=reject\n");
        return builder.ToString();
    }
}
