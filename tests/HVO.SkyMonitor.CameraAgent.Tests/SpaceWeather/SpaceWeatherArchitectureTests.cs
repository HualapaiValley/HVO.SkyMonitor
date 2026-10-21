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
        var references = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(static file => !Path.GetFileName(file).StartsWith("HVO.SkyMonitor.CameraAgent.Tests", StringComparison.Ordinal))
            .Select(static file => MetadataReference.CreateFromFile(file)).ToList();
        references.AddRange(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static file => MetadataReference.CreateFromFile(file)));
        var callers = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("RunAsync", StringComparison.Ordinal)) continue;
            var tree = CSharpSyntaxTree.ParseText(text, path: path);
            var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;");
            var compilation = CSharpCompilation.Create("BoundaryInspection", [tree, globals], references.DistinctBy(static reference => reference.Display), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "RunAsync" } method &&
                    method.ContainingType.ToDisplayString() == typeof(SwpcAttemptRunner).FullName)
                    callers.Add(Path.GetFileName(path));
        }
        string[] expected = ["SpaceWeatherRefreshJob.cs"];
        CollectionAssert.AreEqual(expected, callers);
    }
}
