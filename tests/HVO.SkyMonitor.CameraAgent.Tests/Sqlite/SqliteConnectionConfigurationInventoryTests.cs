using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Sqlite;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteConnectionConfigurationInventoryTests
{
    private const string SolutionFileName = "HVO.SkyMonitor.v9.slnx";

    private static readonly string[] ConfigurationPragmas =
    [
        "busy_timeout",
        "foreign_keys",
        "synchronous",
        "journal_mode",
        "query_only",
        "wal_autocheckpoint"
    ];

    [TestMethod]
    public void EveryConnectionConfigurationPragmaUsesTheProcessWideGateAndOneStatementPerCommand()
    {
        var sourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HVO.SkyMonitor.CameraAgent.Common");
        var candidates = new List<string>();
        var violations = new List<string>();

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(sourcePath);
            var tree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
            var root = tree.GetRoot();
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');

            foreach (var callable in root.DescendantNodes().Where(static node =>
                node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                var pragmaValues = callable.DescendantNodes()
                    .OfType<LiteralExpressionSyntax>()
                    .Where(static literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                    .Select(static literal => literal.Token.ValueText)
                    .Where(IsConnectionConfigurationPragma)
                    .ToArray();
                if (pragmaValues.Length == 0)
                {
                    continue;
                }

                var name = callable switch
                {
                    BaseMethodDeclarationSyntax method => method switch
                    {
                        MethodDeclarationSyntax declaration => declaration.Identifier.ValueText,
                        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                        _ => method.Kind().ToString()
                    },
                    LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                    _ => callable.Kind().ToString()
                };
                candidates.Add($"{relativePath}:{name}");

                var usesGate = callable.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(static invocation => invocation.Expression.ToString())
                    .Any(static expression =>
                        expression.EndsWith("SqliteConnectionConfigurationGate.RunAsync", StringComparison.Ordinal) ||
                        expression.EndsWith("SqliteConnectionConfigurationGate.OpenAndConfigureAsync", StringComparison.Ordinal));
                if (!usesGate)
                {
                    violations.Add($"{relativePath}:{name} configures a SQLite connection without the process-wide gate.");
                }

                foreach (var pragma in pragmaValues)
                {
                    if (CountConfigurationPragmas(pragma) > 1)
                    {
                        violations.Add($"{relativePath}:{name} combines connection-configuration PRAGMAs in one string value.");
                    }
                }

                foreach (var expression in callable.DescendantNodes().OfType<ExpressionSyntax>())
                {
                    if (expression is not BinaryExpressionSyntax and not InvocationExpressionSyntax)
                    {
                        continue;
                    }

                    var combinedValue = TryEvaluateConstantString(expression);
                    if (combinedValue is not null && CountConfigurationPragmas(combinedValue) > 1)
                    {
                        violations.Add($"{relativePath}:{name} combines connection-configuration PRAGMAs in one string expression.");
                    }
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(
            17,
            candidates.Distinct(StringComparer.Ordinal).Count(),
            "The discovered SQLite connection-configuration surface unexpectedly shrank; inspect renamed or removed stores.");
        Assert.IsEmpty(violations.Distinct(StringComparer.Ordinal), string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void RoslynDiscoveryHandlesCommentsStringsLocalFunctionsAndCombinedExpressions()
    {
        const string source = """"
            class Fixture
            {
                void Outer()
                {
                    var brace = "}";
                    // void Fake() { command.CommandText = "PRAGMA foreign_keys=ON;"; }
                    async System.Threading.Tasks.Task Local()
                    {
                        await SqliteConnectionConfigurationGate.RunAsync(async () =>
                        {
                            command.CommandText = "PRAGMA foreign_keys=ON;";
                            await command.ExecuteNonQueryAsync();
                        }, token);
                    }
                }

                void Concatenated()
                {
                    command.CommandText = "PRAGMA foreign_keys=ON;" + " PRAGMA synchronous=FULL;";
                }

                void Raw()
                {
                    command.CommandText = """
                        PRAGMA query_only=ON;
                        PRAGMA busy_timeout=5000;
                        """;
                }

                void ConcatCall()
                {
                    command.CommandText = string.Concat("PRAGMA foreign_keys=ON;", "PRAGMA synchronous=FULL;");
                }
            }
            """";
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var callables = root.DescendantNodes().Where(static node =>
            node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax).ToArray();

        Assert.IsTrue(callables.OfType<LocalFunctionStatementSyntax>().Any(static node => node.Identifier.ValueText == "Local"));
        Assert.IsFalse(callables.OfType<MethodDeclarationSyntax>().Any(static node => node.Identifier.ValueText == "Fake"));

        var combinedExpressions = callables
            .SelectMany(static callable => callable.DescendantNodes().OfType<ExpressionSyntax>())
            .Select(TryEvaluateConstantString)
            .Where(static value => value is not null && CountConfigurationPragmas(value) > 1)
            .ToArray();
        Assert.HasCount(3, combinedExpressions);
    }

    private static bool IsConnectionConfigurationPragma(string value)
        => ConfigurationPragmas.Any(pragma =>
            Regex.IsMatch(
                value,
                $@"\bPRAGMA\s+{Regex.Escape(pragma)}\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static int CountConfigurationPragmas(string value)
        => ConfigurationPragmas.Sum(pragma =>
            Regex.Count(
                value,
                $@"\bPRAGMA\s+{Regex.Escape(pragma)}\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static string? TryEvaluateConstantString(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }
        if (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            return TryEvaluateConstantString(parenthesized.Expression);
        }
        if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        {
            var left = TryEvaluateConstantString(binary.Left);
            var right = TryEvaluateConstantString(binary.Right);
            return left is null || right is null ? null : string.Concat(left, right);
        }
        if (expression is InvocationExpressionSyntax invocation &&
            invocation.Expression.ToString().EndsWith("string.Concat", StringComparison.Ordinal))
        {
            var values = invocation.ArgumentList.Arguments
                .Select(static argument => TryEvaluateConstantString(argument.Expression))
                .ToArray();
            return values.Any(static value => value is null) ? null : string.Concat(values);
        }
        return null;
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
}
