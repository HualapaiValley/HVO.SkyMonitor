using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace HVO.SkyMonitor.Tests;

public sealed partial class ArchitectureBoundaryTests
{
    private static SolutionSnapshot snapshot = null!;

    [AssemblyInitialize]
    public static void InitializeSolutionSnapshot(TestContext context) => snapshot = new SolutionSnapshot();

    [AssemblyCleanup]
    public static void DisposeSolutionSnapshot()
    {
        snapshot?.Dispose();
        snapshot = null!;
    }

    // MSTest runs these rules in parallel. Each input is published once, on
    // demand, so a focused rule keeps its original inputs and prerequisites.
    private sealed class SolutionSnapshot : IDisposable
    {
        private readonly Lazy<string> repositoryRoot = new(RepositoryGraph.FindRepositoryRoot);
        private readonly Lazy<RepositoryGraph> repository;
        private readonly ConcurrentDictionary<string, SourceFile> sources = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<CompiledDependencyChecker.AssemblySnapshot>> assemblies = new(StringComparer.Ordinal);
        private readonly Lazy<ImmutableArray<SourceFile>> productionSourceFiles;
        private readonly Lazy<ImmutableHashSet<string>> reviewedFiles;
        private readonly Lazy<CompiledDependencyChecker.Result> captureOrRecipeDependencies;
        private readonly Lazy<ImmutableArray<(int Id, string Path, int Line)>> eventIdDeclarations;

        internal SolutionSnapshot()
        {
            repository = new(() => RepositoryGraph.Load(Root));
            productionSourceFiles = new(() =>
                [.. Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
                    .Where(path => !RepositoryGraph.HasPathSegment(path, "bin") && !RepositoryGraph.HasPathSegment(path, "obj"))
                    .Order(StringComparer.Ordinal)
                    .Select(Source)]);
            reviewedFiles = new(() => CSharpFiles
                .Where(ReviewedCaptureOrRecipeFile)
                .Select(file => file.Path)
                .ToImmutableHashSet(StringComparer.Ordinal));
            captureOrRecipeDependencies = new(ScanCaptureOrRecipe);
            eventIdDeclarations = new(() => ScanLogEventDeclarations(CSharpFiles));
        }

        internal string Root => repositoryRoot.Value;
        internal RepositoryGraph Graph => repository.Value;
        internal ImmutableArray<SourceFile> CSharpFiles => productionSourceFiles.Value;
        internal ImmutableHashSet<string> ReviewedCaptureOrRecipeFiles => reviewedFiles.Value;
        internal CompiledDependencyChecker.Result CaptureOrRecipe => captureOrRecipeDependencies.Value;
        internal ImmutableArray<(int Id, string Path, int Line)> LogEventDeclarations => eventIdDeclarations.Value;
        internal CompiledDependencyChecker.AssemblySnapshot TestAssembly => Assembly(typeof(ArchitectureBoundaryTests).Assembly.Location);

        internal SourceFile Source(string path) =>
            sources.GetOrAdd(path, file => new SourceFile(file, Path.GetRelativePath(Root, file)));

        // MSBuild locates the Release outputs, including RID-specific runners.
        internal CompiledDependencyChecker.AssemblySnapshot ProductionAssembly(ProjectInfo project)
        {
            var path = project.ReleaseAssemblyPath;
            Assert.IsTrue(File.Exists(path), $"Build the Release solution before architecture checks: {path}");
            return Assembly(path);
        }

        private CompiledDependencyChecker.AssemblySnapshot Assembly(string path) =>
            assemblies.GetOrAdd(path, static file => new(() => CompiledDependencyChecker.AssemblySnapshot.Load(file))).Value;

        public void Dispose()
        {
            foreach (var assembly in assemblies.Values.Where(assembly => assembly.IsValueCreated))
            {
                assembly.Value.Dispose();
            }
        }

        private CompiledDependencyChecker.Result ScanCaptureOrRecipe()
        {
            var sourceOwners = ReviewedCaptureOrRecipeFiles;
            var results = Graph.Projects.Values
                .Where(project => project.Kind == ProjectKind.Production)
                .Select(project => ProductionAssembly(project).Check(
                    type => CompiledDependencyChecker.CaptureOrRecipe(type, sourceOwners), CompiledDependencyChecker.SpaceWeather))
                .ToArray();

            return new(results.Sum(result => result.TypesChecked),
                [.. results.SelectMany(result => result.Violations)], [.. results.SelectMany(result => result.SelectedTypes)]);
        }

        internal sealed class SourceFile
        {
            private readonly Lazy<string> text;
            private readonly Lazy<string> maskedText;

            internal SourceFile(string path, string relativePath)
            {
                Path = path;
                RelativePath = relativePath;
                text = new(() => File.ReadAllText(path));
                maskedText = new(() => MaskCSharpCommentsAndLiterals(Text));
            }

            internal string Path { get; }
            internal string RelativePath { get; }
            internal string Text => text.Value;
            internal string MaskedText => maskedText.Value;
        }
    }
}
