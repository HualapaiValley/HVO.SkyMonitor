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

            AnalyzeRoot(root, relativePath, candidates, violations);
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
        const string additionalSource = """
            partial class Fixture
            {
                void InterpolatedOnly()
                {
                    SqliteConnectionConfigurationGate.RunAsync(async () =>
                    {
                        command.CommandText = $"PRAGMA busy_timeout={timeout};";
                    }, token);
                }

                void DiagnosticOnly()
                {
                    var message = "Example: PRAGMA foreign_keys=ON;";
                    SqliteConnectionConfigurationGate.RunAsync(() => default, token);
                }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(string.Concat(source, additionalSource)).GetRoot();
        var candidates = new List<string>();
        var violations = new List<string>();
        AnalyzeRoot(root, "fixture.cs", candidates, violations);

        CollectionAssert.Contains(candidates, "fixture.cs:Local");
        CollectionAssert.Contains(candidates, "fixture.cs:InterpolatedOnly");
        CollectionAssert.DoesNotContain(candidates, "fixture.cs:Outer");
        CollectionAssert.DoesNotContain(candidates, "fixture.cs:Fake");
        CollectionAssert.DoesNotContain(candidates, "fixture.cs:DiagnosticOnly");
        Assert.HasCount(3, violations.Where(static violation =>
            violation.Contains("combines connection-configuration PRAGMAs", StringComparison.Ordinal)));
        Assert.HasCount(3, violations.Where(static violation =>
            violation.Contains("without the process-wide gate", StringComparison.Ordinal)));
    }

    private static void AnalyzeRoot(
        SyntaxNode root,
        string relativePath,
        List<string> candidates,
        List<string> violations)
    {
        foreach (var expression in FindCommandTextExpressions(root))
        {
            var value = TryEvaluateConstantString(expression);
            if (value is null || !IsConnectionConfigurationPragma(value))
            {
                continue;
            }

            var callable = expression.Ancestors().FirstOrDefault(static node =>
                node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
            if (callable is null)
            {
                violations.Add($"{relativePath} contains a connection-configuration PRAGMA outside a callable.");
                continue;
            }

            var candidate = $"{relativePath}:{CallableName(callable)}";
            candidates.Add(candidate);
            if (!OwnDescendants(callable).OfType<InvocationExpressionSyntax>().Any(IsGateInvocation))
            {
                violations.Add($"{candidate} configures a SQLite connection without the process-wide gate.");
            }
            if (CountConfigurationPragmas(value) > 1)
            {
                violations.Add($"{candidate} combines connection-configuration PRAGMAs in one string expression.");
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> FindCommandTextExpressions(SyntaxNode root)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == "CommandText")
            {
                yield return assignment.Right;
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invokedName = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => string.Empty
            };
            if (!invokedName.Contains("Execute", StringComparison.Ordinal) &&
                !invokedName.Contains("Configure", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                yield return argument.Expression;
            }
        }
    }

    private static IEnumerable<SyntaxNode> OwnDescendants(SyntaxNode callable)
        => callable.DescendantNodes(node =>
            ReferenceEquals(node, callable) ||
            node is not BaseMethodDeclarationSyntax and not LocalFunctionStatementSyntax);

    private static bool IsGateInvocation(InvocationExpressionSyntax invocation)
    {
        var expression = invocation.Expression.ToString();
        return expression.EndsWith("SqliteConnectionConfigurationGate.RunAsync", StringComparison.Ordinal) ||
            expression.EndsWith("SqliteConnectionConfigurationGate.OpenAndConfigureAsync", StringComparison.Ordinal);
    }

    private static string CallableName(SyntaxNode callable)
        => callable switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            LocalFunctionStatementSyntax local => local.Identifier.ValueText,
            _ => callable.Kind().ToString()
        };

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
        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            return string.Concat(interpolated.Contents.Select(static content => content switch
            {
                InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                InterpolationSyntax => "{value}",
                _ => string.Empty
            }));
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
