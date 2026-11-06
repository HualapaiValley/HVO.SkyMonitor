using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherArchitectureTests
{
    [TestMethod]
    public void TheJobIsTheOnlyProductionCallerOfSwpcRunAsync()
    {
        var root = SpaceWeatherCompositionTests.FindRoot();
        var callers = new List<string>();
        foreach (var project in new[] { "HVO.SkyMonitor.CameraAgent.Common", "HVO.SkyMonitor.CameraAgent" })
        {
            var trees = Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
                .Where(static path => !path.Split(Path.DirectorySeparatorChar).Any(static part => part is "obj" or "bin"))
                .Select(static path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
            // All partial declarations share one project compilation. Its own binary must not shadow source.
            var compilation = Compile(project, trees);
            callers.AddRange(FindRunnerCalls(compilation));
        }
        string[] expected = ["SpaceWeatherRefreshJob.cs"];
        CollectionAssert.AreEqual(expected, callers);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SplitPartialCallerIsDetectedIncludingUnresolvedCandidates(bool invalidArguments)
    {
        var field = CSharpSyntaxTree.ParseText("""
            using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
            partial class UnauthorizedCaller
            {
                private readonly SwpcAttemptRunner runner;
                internal UnauthorizedCaller(SwpcAttemptRunner value) { runner = value; }
            }
            """, path: "field.cs");
        var call = CSharpSyntaxTree.ParseText("""
            using HVO.SkyMonitor.Processing.SpaceWeather;
            using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
            partial class UnauthorizedCaller
            {
                internal void Execute() { _ = runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, ARGUMENTS); }
            }
            """.Replace("ARGUMENTS", invalidArguments ? "123" : "\"negative-control\", default", StringComparison.Ordinal), path: "call.cs");
        var detected = FindRunnerCalls(Compile("NegativeControl", [field, call]));
        CollectionAssert.AreEqual(new[] { invalidArguments ? "unresolved:call.cs" : "call.cs" }, detected);
    }

    /// <summary>One semantic model per tree in a shared compilation resolves fields declared in other partials.</summary>
    private static List<string> FindRunnerCalls(CSharpCompilation compilation)
    {
        var callers = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(call);
                if (info.Symbol is IMethodSymbol method && IsRunner(method)) callers.Add(Path.GetFileName(tree.FilePath));
                else if (info.Symbol is null && info.CandidateSymbols.OfType<IMethodSymbol>().Any(IsRunner))
                    callers.Add("unresolved:" + Path.GetFileName(tree.FilePath));
            }
        }
        return callers;
    }

    private static bool IsRunner(IMethodSymbol method) => method.Name == "RunAsync" &&
        method.ContainingType.ToDisplayString() == typeof(SwpcAttemptRunner).FullName;

    private static CSharpCompilation Compile(string project, IEnumerable<SyntaxTree> trees)
    {
        var references = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(file => Path.GetFileNameWithoutExtension(file) != project && !Path.GetFileName(file).EndsWith(".Tests.dll", StringComparison.Ordinal))
            .Concat(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            .Distinct(StringComparer.Ordinal).Select(static file => MetadataReference.CreateFromFile(file));
        var globals = CSharpSyntaxTree.ParseText("""
            global using System; global using System.Collections.Generic; global using System.IO;
            global using System.Linq; global using System.Net.Http; global using System.Threading;
            global using System.Threading.Tasks; global using Microsoft.AspNetCore.Builder;
            global using Microsoft.AspNetCore.Hosting; global using Microsoft.AspNetCore.Http;
            global using Microsoft.AspNetCore.Routing; global using Microsoft.Extensions.Configuration;
            global using Microsoft.Extensions.DependencyInjection; global using Microsoft.Extensions.Hosting;
            global using Microsoft.Extensions.Logging;
            """);
        return CSharpCompilation.Create(project, trees.Append(globals), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
