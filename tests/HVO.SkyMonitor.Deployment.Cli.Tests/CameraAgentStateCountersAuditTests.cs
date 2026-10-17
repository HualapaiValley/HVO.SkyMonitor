using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The guard for #1314 R9: every counter-like column in every CameraAgent store is either carried across a snapshot
/// restore (<see cref="CameraAgentStateCounters.All"/>) or audited as one that never leaves the instance and is never
/// sent back (<see cref="CameraAgentStateCounters.Local"/>), exactly once. The stores are the previous-release images,
/// which the CameraAgent's own readability test holds equal to the stores this release creates, so a new store or
/// column of this kind fails here until it is classified.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class CameraAgentStateCountersAuditTests
{
    private static readonly Regex CounterName = CounterNamePattern();

    [TestMethod]
    public void EveryCounterLikeColumnInEveryStore_IsCarriedOrAuditedAsLocalExactlyOnce()
    {
        var columns = CounterLikeColumns();
        Assert.IsGreaterThan(50, columns.Count, "The DDL scan found too few counter-like columns to be reading the stores.");
        var unclassified = new List<string>();
        var both = new List<string>();
        foreach (var (location, table, column, autoIncrement) in columns)
        {
            var carried = CameraAgentStateCounters.All.Any(counter => counter.Store == location &&
                (counter.Table == table && counter.ValueColumn == column ||
                 autoIncrement && counter.Kind == CameraAgentStateCounters.CounterKind.AutoIncrement && counter.FixedKey == table));
            var local = CameraAgentStateCounters.Local.ContainsKey($"{table}.{column}");
            if (!carried && !local) unclassified.Add($"{location} {table}.{column}");
            if (carried && local) both.Add($"{location} {table}.{column}");
        }

        Assert.IsEmpty(unclassified,
            "These columns may hand out a sequence or version a client holds; carry them in CameraAgentStateCounters.All " +
            "or audit them in CameraAgentStateCounters.Local with why they never leave the instance: " + string.Join("; ", unclassified));
        Assert.IsEmpty(both, "Classified as both carried and local: " + string.Join("; ", both));
    }

    [TestMethod]
    public void EveryRegistrationNamesAColumnTheStoresHave()
    {
        var columns = CounterLikeColumns();
        var stale = CameraAgentStateCounters.Local.Keys
            .Where(key => !columns.Any(column => $"{column.Table}.{column.Column}" == key))
            .ToList();
        stale.AddRange(CameraAgentStateCounters.All
            .Where(counter => !columns.Any(column => column.Location == counter.Store &&
                (counter.Kind == CameraAgentStateCounters.CounterKind.AutoIncrement
                    ? column.AutoIncrement && column.Table == counter.FixedKey
                    : column.Table == counter.Table && column.Column == counter.ValueColumn)))
            .Select(static counter => $"{counter.Store} {counter.Name}"));

        Assert.IsEmpty(stale, "Registered for a column no store has: " + string.Join("; ", stale));
    }

    /// <summary>
    /// #1314 R15: a restore reads a counter's key only in the storage class the registry names for it, so that class must
    /// be the one the previous release declares; a key a candidate stores in any other class makes its store unreadable.
    /// </summary>
    [TestMethod]
    public void EveryCarriedCounterKey_IsTheTypeThePreviousReleaseDeclaresForIt()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var mismatched = new List<string>();
        foreach (var counter in CameraAgentStateCounters.All)
        {
            if (counter.Kind == CameraAgentStateCounters.CounterKind.AutoIncrement)
            {
                // sqlite_sequence names each table as text.
                if (counter.IntegerKey) mismatched.Add($"{counter.Store} {counter.Name}");
                continue;
            }
            var dump = manifest.RootElement.GetProperty("stores").EnumerateArray()
                .Single(store => store.GetProperty("location").GetString() == counter.Store).GetProperty("dump").GetString()!;
            var table = CreateTablePattern().Matches(File.ReadAllText(Path.Combine(directory, dump)))
                .Single(match => match.Groups["table"].Value == counter.Table);
            var key = SplitTopLevel(table.Groups["body"].Value).Single(definition => Regex.IsMatch(
                definition, $"^\"?{Regex.Escape(counter.KeyColumn)}\"?\\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            var integer = Regex.IsMatch(
                key, $"^\"?{Regex.Escape(counter.KeyColumn)}\"?\\s+INT(EGER)?\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
            if (integer != counter.IntegerKey) mismatched.Add($"{counter.Store} {counter.Name}");
        }

        Assert.IsEmpty(mismatched, "The registry names a key type the previous release does not declare: " + string.Join("; ", mismatched));
    }

    /// <summary>
    /// Every INTEGER column named for a sequence, version, revision, generation, epoch, serial, next or last value
    /// (timestamps excluded), and every integer row-ID primary key that is not a singleton key, in every store image.
    /// </summary>
    private static List<(string Location, string Table, string Column, bool AutoIncrement)> CounterLikeColumns()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var columns = new List<(string, string, string, bool)>();
        foreach (var store in manifest.RootElement.GetProperty("stores").EnumerateArray())
        {
            var location = store.GetProperty("location").GetString()!;
            var sql = File.ReadAllText(Path.Combine(directory, store.GetProperty("dump").GetString()!));
            foreach (Match table in CreateTablePattern().Matches(sql))
            {
                foreach (var definition in SplitTopLevel(table.Groups["body"].Value))
                {
                    var column = ColumnPattern().Match(definition);
                    if (!column.Success) continue;
                    var name = column.Groups["name"].Value;
                    var rest = column.Groups["rest"].Value;
                    var primaryKey = rest.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
                    var singleton = Regex.IsMatch(rest, $"CHECK\\s*\\(\\s*\"?{Regex.Escape(name)}\"?\\s*=\\s*1\\s*\\)",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    var timestamp = name.EndsWith("_unix_ms", StringComparison.Ordinal) || name.EndsWith("_ticks", StringComparison.Ordinal);
                    if (!timestamp && CounterName.IsMatch(name) || primaryKey && !singleton)
                    {
                        // A store under any storage root keeps the same outbox schema, so each location is checked.
                        columns.Add((location, table.Groups["table"].Value, name,
                            rest.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }
        return columns;
    }

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '(') depth++;
            else if (body[index] == ')') depth--;
            else if (body[index] == ',' && depth == 0)
            {
                parts.Add(body[start..index].Trim());
                start = index + 1;
            }
        }
        parts.Add(body[start..].Trim());
        return parts;
    }

    [GeneratedRegex("(version|revision|sequence|generation|epoch|serial|next_|last_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CounterNamePattern();

    [GeneratedRegex("CREATE TABLE \"?(?<table>[A-Za-z_]+)\"?\\s*\\((?<body>.*?)\\)\\s*(STRICT)?;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTablePattern();

    [GeneratedRegex("^\"?(?<name>[A-Za-z_]+)\"?\\s+(INTEGER|INT)\\b(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnPattern();
}
