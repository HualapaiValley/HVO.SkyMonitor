using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    // F6 (PR-1496-R2): the review's rows. Each held a quote before two credentials, and the nonbacktracking forms of the
    // patterns that began with an optional quote skipped the first of them.
    private static readonly string[] Diagnostic1491ReviewRows =
    [
        "'(password:review-1491-secret,Token=x",
        "SQLite Error 19: 'Connection failed: Host=db;Password=review-1491-secret;Token=abc'.",
        "Backup failed: Connection failed: \"Host=db;Username=app;Password=review-1491-secret;Token=review-1491-secret\"",
        "SQLite Error 19: 'CHECK constraint failed: 'x' password=review-1491-secret, token=review-1491-secret'.",
        "SQLite Error 19: '\"user=x password=review-1491-secret token=review-1491-secret\"'.",
        "SQLite Error 19: ''--password=review-1491-secret --token=review-1491-secret''.",
    ];

    // F6 (PR-1496-R2): every nonbacktracking pattern returns exactly the matches, and the groups, of its backtracking form, over
    // a fixed corpus. Matching in linear time must not change what is matched.
    [TestMethod]
    public void Diagnostic1491_LinearPatternsMatchTheirBacktrackingForms()
    {
        var declared = typeof(Redaction).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(field => field.GetValue(null)).OfType<Regex>()
            .Where(pattern => pattern.Options.HasFlag(RegexOptions.NonBacktracking)).ToList();
        CollectionAssert.AreEquivalent(declared, Redaction.LinearPatterns.ToList(), "Every nonbacktracking pattern is compared.");
        var corpus = Diagnostic1491Corpus(10_000).Concat(Diagnostic1491ReviewRows).Concat(Diagnostic1499Cases().Select(row => (string)row[2]).Distinct())
            .Concat(Diagnostic1509Cases().Select(row => (string)row[1]).Distinct())
            .Concat(Diagnostic1523Cases().Select(row => (string)row[1]).Distinct())
            .Concat(Diagnostic1569Cases().Select(row => (string)row[0]).Distinct())
            .Concat(["\u001b]0;SecretKey\u0007 value", "\u009d0;ObjectStorage__SecretKey\u009c value", "\u001b]0;tokenid\u0007 safe"])
            .Concat(["<redacted>… tail", @"\u003Credacted\u003E\u2026 tail", @"\\u003credacted\\u003e\\u2026 tail",
                @"<redacted>\u2026 tail", @"\u003Credacted>… tail", "<redacted> tail", @"\u003Credacted\u003E\u202"])
            .ToList();
        var differences = new List<string>();
        foreach (var pattern in Redaction.LinearPatterns)
        {
            var backtracking = new Regex(pattern.ToString(), pattern.Options & ~RegexOptions.NonBacktracking, pattern.MatchTimeout);
            var differing = corpus.Where(input => Diagnostic1491Matches(pattern, input) != Diagnostic1491Matches(backtracking, input)).ToList();
            if (differing.Count > 0)
            {
                differences.Add($"{pattern}: {differing.Count} inputs differ, the first {JsonSerializer.Serialize(differing[0])}");
            }
        }
        Console.WriteLine($"{Redaction.LinearPatterns.Count} patterns, {corpus.Count} inputs");
        Assert.IsEmpty(differences, string.Join(Environment.NewLine, differences));
    }

    private static string Diagnostic1491Matches(Regex pattern, string input) => string.Join("|", pattern.Matches(input).Select(
        match => string.Join(",", match.Groups.Cast<Group>().Select(group => group.Success ? $"{group.Index}+{group.Length}" : "-"))));

    /// <summary>
    /// A fixed corpus from a seeded generator: half are runs of fragments, and half are two key-value pairs after a lead, each
    /// part drawn from quotes, separators, credential words, values, decoration and controls.
    /// </summary>
    private static List<string> Diagnostic1491Corpus(int count)
    {
        string[] fragments =
        [
            "\"", "'", "\\\"", "''", "=", ":", " ", "  ", ",", ";", "}", "{", "(", ")", "\t", "=\"", ": '",
            "password", "Password", "PASSWORD", "token", "Token", "secret", "api-key", "api_key", "apikey", "authorization",
            "Authorization", "bearer", "Bearer", "db_password", "accessToken", "x-api-key", "tokenid", "secret-santa",
            "\u001b[1m", "\u001b[0m", "\u001b]8;;https://example.test\u001b\\", "\u001b]8;;\u001b\\", "\u001b]0\u0007", "\u001b[",
            "\u009b", "\u009b1m", "\u001b", "\u0085", "\u009c", "\u001bP", "\u001b\\",
            "x", "abc", "a b", "0", "-", "/", "@", "://", "\\", "Host=db", "user",
        ];
        string[] leads =
        [
            "", "\"", "'", "(", "'(", "\"(", "x", " ", "Host=db;", "'x' ", "\u001b[1m", "\u001b]8;;\u001b\\", "\u0085", "\u001b",
            "SQLite Error 19: '", "--",
        ];
        string[] keys =
        [
            "password", "Password", "token", "Token", "secret", "api-key", "apikey", "authorization", "Authorization", "bearer",
            "db_password", "accessToken", "tokenid", "user", "\u001b[1mtoken\u001b[0m", "--password", "SecretKey", "secret-access-key",
            "secretkeeper",
        ];
        string[] quotes = ["", "", "", "\"", "'", "\\\""];
        string[] separators = ["=", ":", " ", ": ", " = ", "=\t", "\t", "\u001b[2J", "\u0085"];
        string[] values = ["value", "a b", "x", "", "a\\\"b", "a,b", "a;b", "<v>", "\u001b[31mv"];
        string[] betweens = ["", ",", ";", " ", ", ", "'", "\"", ")", "}", "&", "\u001b[0m", "\u001b]0\u0007", "'."];
        var state = 1491UL;
        int Next(int bound)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            return (int)((state >> 33) % (ulong)bound);
        }
        string Pick(string[] choices) => choices[Next(choices.Length)];
        var corpus = new List<string>(count);
        var text = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            text.Clear();
            if (index % 2 == 0)
            {
                for (var fragment = 1 + Next(12); fragment > 0; fragment--) text.Append(Pick(fragments));
            }
            else
            {
                text.Append(Pick(leads)).Append(Pick(leads));
                for (var pair = 0; pair < 2; pair++)
                {
                    text.Append(Pick(keys)).Append(Pick(quotes)).Append(Pick(separators)).Append(Pick(quotes)).Append(Pick(values))
                        .Append(Pick(quotes)).Append(Pick(betweens));
                }
            }
            corpus.Add(text.ToString());
        }
        return corpus;
    }
}
