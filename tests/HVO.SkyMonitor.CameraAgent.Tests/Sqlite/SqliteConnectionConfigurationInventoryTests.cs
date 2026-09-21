using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Sqlite;

[TestClass]
[TestCategory("Unit")]
public sealed partial class SqliteConnectionConfigurationInventoryTests
{
    private const string SolutionFileName = "HVO.SkyMonitor.v9.slnx";

    [TestMethod]
    public void EveryConnectionConfigurationPragmaUsesTheProcessWideGateAndOneStatementPerCommand()
    {
        var sourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HVO.SkyMonitor.CameraAgent.Common");
        var candidates = new List<(string Path, string Method)>();
        var violations = new List<string>();

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var method in FindMethodBodies(source))
            {
                if (!ConnectionConfigurationPragma().IsMatch(method.Body))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                candidates.Add((relativePath, method.Name));

                if (!method.Body.Contains("SqliteConnectionConfigurationGate.RunAsync", StringComparison.Ordinal) &&
                    !method.Body.Contains("SqliteConnectionConfigurationGate.OpenAndConfigureAsync", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{method.Name} configures a SQLite connection without the process-wide gate.");
                }

                var normalized = CSharpStringSyntax().Replace(method.Body, string.Empty);
                if (CombinedConfigurationPragmas().IsMatch(normalized))
                {
                    violations.Add($"{relativePath}:{method.Name} combines connection-configuration PRAGMAs in one command string.");
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(
            17,
            candidates.Count,
            "The discovered SQLite connection-configuration surface unexpectedly shrank; inspect renamed or removed stores.");
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static List<(string Name, string Body)> FindMethodBodies(string source)
    {
        var methods = new List<(string Name, string Body)>();
        foreach (Match declaration in MethodDeclaration().Matches(source))
        {
            var bodyStart = source.IndexOf('{', declaration.Index + declaration.Length);
            if (bodyStart < 0)
            {
                continue;
            }

            var depth = 0;
            for (var index = bodyStart; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    methods.Add((declaration.Groups["name"].Value, source[bodyStart..(index + 1)]));
                    break;
                }
            }
        }
        return methods;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No ancestor of {AppContext.BaseDirectory} contains {SolutionFileName}.");
    }

    [GeneratedRegex(
        @"\b(?:private|internal|public)\b[^;{}]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>{}]+>)?\s*\([^;{}]*\)\s*(?:where\s+[^{}]+)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(
        @"PRAGMA\s+(?:busy_timeout\s*=|foreign_keys\s*=\s*ON|synchronous\s*=\s*FULL|journal_mode\s*=\s*WAL|query_only\s*=\s*ON|wal_autocheckpoint\s*=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionConfigurationPragma();

    // Remove only C# string-expression syntax and whitespace. Separate command assignments retain
    // their intervening C# statements, while concatenated and raw literals collapse to
    // PRAGMA...;PRAGMA... and are rejected by CombinedConfigurationPragmas.
    [GeneratedRegex("[\\s\\\"@$+]", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpStringSyntax();

    [GeneratedRegex(
        @"PRAGMA[^;]*;PRAGMA(?:busy_timeout|foreign_keys|synchronous|journal_mode|query_only|wal_autocheckpoint)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CombinedConfigurationPragmas();
}
