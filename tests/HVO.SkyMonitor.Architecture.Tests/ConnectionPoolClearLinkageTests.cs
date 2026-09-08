using HVO.SkyMonitor.TestSettings;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Architecture.Tests;

/// <summary>
/// The issue #754 connection-pool contract is compile-linked into every test project, so it can only guard
/// an assembly it was compiled into. A project that does not receive it runs no check and says nothing
/// about it, which is the failure this whole family is about. Nothing inside an assembly can observe its
/// own absence, so the question is asked once, here, for the repository.
/// </summary>
/// <remarks>
/// <para>
/// This asks MSBuild whether each project compiles the contract, rather than reading the project file. An
/// earlier version read the props file for exclusion conditions and could be defeated by one
/// <c>&lt;Compile Remove&gt;</c> in a csproj: the project silently lost the contract while every other
/// project's check still passed. Matching that spelling would have closed one spelling; asking the tool
/// that owns the answer closes the question. MSBuild also answers whether a project references the test
/// framework, which replaces a text search for the package name that was maskable in the same way.
/// </para>
/// <para>
/// <b>The enumeration is the solution file, and that is the remaining approximation.</b> A project escapes
/// this question set by not being listed there. Two things narrow it: every test project on disk under
/// <c>tests/</c> is asserted to be listed, and the population of the remaining route was measured rather
/// than described. Exactly one project is invoked by a workflow while absent from the solution, and it is a
/// console executable with no test framework reference, run with <c>dotnet run</c>. So the route exists and
/// has no test-project inhabitants today. The solution file itself is still read as text, which is the one
/// text read left in this check.
/// </para>
/// <para>
/// Cost, measured on the development host: about 0.5 s per project and roughly 4 s for the whole solution
/// at eight workers. It is affordable because it runs once for the repository. Running it from inside each
/// of the twenty assemblies that carry the contract would be twenty times that, which is why this lives in
/// one project rather than in the shared file.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class ConnectionPoolClearLinkageTests
{
    private const string ContractFileName = "ConnectionPoolClearContract.cs";
    private const string SolutionFileName = "HVO.SkyMonitor.v9.slnx";
    private const string TestFrameworkPackagePrefix = "MSTest";

    private static readonly Regex SolutionProjectPath = new(
        @"Path=""([^""]+\.csproj)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [TestMethod]
    public void EveryTestProjectInTheSolutionCompilesThePoolClearContract()
    {
        var root = RepositoryRoot();
        var projects = SolutionProjects(root);
        Assert.IsNotEmpty(projects, $"No projects were read from {SolutionFileName}, so this check examined nothing.");

        var evaluated = projects
            .AsParallel()
            .WithDegreeOfParallelism(8)
            .Select(project => (Project: project, Items: ProjectEvaluation.Evaluate(Path.Combine(root, project))))
            .ToList();

        var unevaluated = evaluated.Where(static e => e.Items is null).Select(static e => e.Project).ToList();
        Assert.IsEmpty(
            unevaluated,
            "MSBuild could not evaluate these projects, so whether they carry the pool-clear contract is "
                + "unknown rather than confirmed: " + string.Join(", ", unevaluated));

        var testProjects = evaluated
            .Where(static e => e.Items!.Packages.Any(static p => p.StartsWith(TestFrameworkPackagePrefix, StringComparison.Ordinal)))
            .ToList();
        Assert.IsNotEmpty(
            testProjects,
            "No project in the solution references the test framework, which cannot be true and means this "
                + "check is asking the wrong question set.");

        var unguarded = testProjects
            .Where(static e => !e.Items!.Compiles.Any(static c => c.EndsWith(ContractFileName, StringComparison.Ordinal)))
            .Select(static e => e.Project)
            .ToList();
        Assert.IsEmpty(
            unguarded,
            "These projects contain tests but do not compile the issue #754 connection-pool contract, so they "
                + "are unguarded and nothing in them would report a parallelisable class clearing every "
                + "connection pool: " + string.Join(", ", unguarded));
    }

    /// <summary>
    /// A test project added under <c>tests/</c> but never listed in the solution would not be asked the
    /// question above at all. This is the cross-check for that, and it fails in a different direction: the
    /// primary check verifies the projects it knows about, this one verifies that it knows about them.
    /// </summary>
    [TestMethod]
    public void EveryTestProjectOnDiskIsListedInTheSolution()
    {
        var root = RepositoryRoot();
        var listed = SolutionProjects(root)
            .Select(static project => Path.GetFileName(project))
            .ToHashSet(StringComparer.Ordinal);
        var unlisted = Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !listed.Contains(Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();
        Assert.IsEmpty(
            unlisted,
            $"These test projects exist under tests/ but are not listed in {SolutionFileName}, so the linkage "
                + "check never asks about them and they could be unguarded without anything saying so: "
                + string.Join(", ", unlisted));
    }

    private static List<string> SolutionProjects(string root) =>
        SolutionProjectPath.Matches(File.ReadAllText(Path.Combine(root, SolutionFileName)))
            .Select(static match => match.Groups[1].Value.Replace('\\', '/'))
            .ToList();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException($"No ancestor of {AppContext.BaseDirectory} holds {SolutionFileName}.");
    }

}
