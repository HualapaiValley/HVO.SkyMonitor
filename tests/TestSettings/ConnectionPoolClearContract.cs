using System.Reflection;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.TestSettings;

/// <summary>
/// Keeps issue #754 from returning under a new name. The defect is not any particular test: it is that a
/// process-global pool clear, called from a class that runs in parallel with others, disposes idle pooled
/// connections belonging to whatever else is running, so a test renting one at that moment gets a disposed
/// handle. Removing today's instances removes today's failures; this asserts the property that keeps new
/// ones from appearing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guard is exact about who is serialised and approximate about who calls the API.</b> Whether this
/// assembly runs its tests in parallel, which of its classes opt out, and which of its types are test
/// classes at all, are read from compiled metadata, which cannot be misread. Which files call the API is
/// found by searching source text, which can be. A reader can trust the first half and should treat the
/// second as a good search rather than a proof.
/// </para>
/// <para>
/// This file is compile-linked into every test project rather than living in one of them, so each assembly
/// checks itself using its own metadata and its own sources. That is why there is no discovery step, no
/// cross-assembly loading and no dependency on build ordering, and why an earlier discovery anchor was
/// removed: there is nothing left to discover that could narrow silently.
/// </para>
/// <para>
/// The inversion exists because the textual version of the first half failed four times, each attempt
/// narrowing the surface and each leaving a spelling nobody had thought to ask about. A marker matched
/// anywhere in a file, so that guard's own string literals excluded its own assembly. Then at column zero,
/// which a raw string literal's content also satisfies. Then in the file prologue, which still contains
/// block comments and <c>#if</c> regions that are never defined. Then with directives detected by
/// <c>StartsWith("#if")</c>, which does not see <c>#  if</c>, since C# permits whitespace after the hash.
/// Every one shipped a green result with a real offending call present. None of them exists in metadata.
/// </para>
/// <para>
/// There are two ways to satisfy this guard, and which one applies depends on what the clear was for.
/// If it was a teardown clear in a parallelisable class, meaning it only released handles before deleting
/// the test's own temporary directory, delete it: this repository is developed and built on POSIX
/// filesystems, where unlinking an open file succeeds, so such a clear was never load-bearing for the
/// deletion in the first place. A Windows checkout is required to live on the WSL2 Linux filesystem for
/// exactly that reason. If the clear is load-bearing, meaning the test would silently stop exercising what
/// it claims without it, keep it and give the class a class-level <c>[DoNotParallelize]</c> so nothing runs
/// beside it. That attribute really does mean nothing runs beside the class rather than merely that its own
/// methods run in sequence; that was verified by measurement under MSTest 4.3.3 rather than taken from the
/// attribute's documentation, and the measurement establishes the observable rather than the mechanism.
/// </para>
/// <para>
/// What stays approximate, all of it in the second half. A clear reached through a helper is usually
/// reported rather than missed, which corrects a limit this guard recorded as open for several revisions:
/// a helper file declares no test class, so nothing excuses it and it is named. That holds only while the
/// helper has a file to itself. A helper sharing a file with a serialised test class is still missed,
/// because the file is excused by that class, and no analysis of method bodies happens here to notice the
/// call came from elsewhere. Measured both ways rather than reasoned. Type names are read from source, so a
/// comments are removed before declarations are read, so a comment naming a serialised class cannot excuse
/// a file that declares none. A declaration inside a string literal still counts, which is the one route
/// left by which text could excuse a file wrongly, and it needs a literal shaped exactly like a class
/// declaration in a file that also clears pools and declares no test class of its own. Only names that
/// metadata confirms are test classes are considered, so private helper types and stray words are ignored,
/// and a generic test class would not be matched at all, which reports rather than excuses. Where a simple
/// type name belongs to several types, it counts as serialised only when all of them are, so a collision in
/// another namespace cannot excuse a file.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class ConnectionPoolClearContractTests
{
    private const string ThisFileName = "ConnectionPoolClearContract.cs";
    private const string SupportLibraryWithoutTests = "HVO.SkyMonitor.LogicHost.TestInfrastructure";

    private static readonly Regex GlobalClearInvocation = new(
        @"SqliteConnection\s*\.\s*ClearAllPools\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Anchored at the start of a line, so prose such as "this class is not one" does not read as a
    // declaration of a type called "is". An attribute list may precede the modifiers on the same line:
    // "[TestClass] public sealed class X" is legal C#, and a pattern that only allowed modifiers there
    // missed such a declaration entirely, which let a real clear hide in a file holding a serialised decoy.
    private static readonly Regex DeclaredTypeName = new(
        @"^[ \t]*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|unsafe)\s+)*(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Declarations are read from code, so comments are removed first. Without this, a file holding a clear
    // and no test class of its own could be excused by a comment that merely names a serialised one, which
    // is a false green rather than a false alarm.
    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex LineComment = new(
        @"//[^\n]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExcludedProjectName = new(
        @"\$\(MSBuildProjectName\)'\s*!=\s*'([^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [TestMethod]
    public void NoParallelisableTestClassClearsEveryConnectionPool()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var attributes = assembly.GetCustomAttributesData().Select(static a => a.AttributeType.Name).ToList();
        if (attributes.Contains("DoNotParallelizeAttribute", StringComparer.Ordinal) ||
            !attributes.Contains("ParallelizeAttribute", StringComparer.Ordinal))
        {
            return;
        }

        var testClasses = NamesOfTypesWith(assembly, "TestClassAttribute");
        var serialised = NamesOfTypesWith(assembly, "DoNotParallelizeAttribute");
        var projectDirectory = ProjectDirectory();
        var offenders = new List<string>();
        foreach (var file in SourcesUnder(projectDirectory))
        {
            if (string.Equals(Path.GetFileName(file), ThisFileName, StringComparison.Ordinal))
            {
                continue;
            }
            var source = File.ReadAllText(file);
            if (!GlobalClearInvocation.IsMatch(source))
            {
                continue;
            }
            // Only test classes decide this. A file's private helper types are never scheduled by the
            // runner, and a name metadata does not know as a test class cannot excuse or condemn anything.
            var code = LineComment.Replace(BlockComment.Replace(source, string.Empty), string.Empty);
            var declared = DeclaredTypeName.Matches(code)
                .Select(static match => match.Groups[1].Value)
                .Where(testClasses.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (declared.Count == 0 || !declared.All(serialised.Contains))
            {
                offenders.Add(Path.GetRelativePath(projectDirectory, file));
            }
        }

        Assert.IsEmpty(
            offenders,
            $"In {assembly.GetName().Name}, which runs test methods in parallel, these files clear every "
                + "connection pool without every test class they declare opting out. That disposes pooled "
                + "connections owned by tests running beside them and reproduces issue #754. If it is a "
                + "teardown clear, meaning it only released handles before deleting the test's own temporary "
                + "directory, delete it; POSIX unlinks open files. If it is load-bearing, give the class a "
                + "class-level [DoNotParallelize]: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// This file only guards an assembly it is compiled into, so a test project that does not receive it is
    /// unguarded and silent about being so. Two ways that happens: the shared props file excludes the
    /// project, or the project sits outside that file's reach entirely. Both are asserted, and neither is a
    /// pinned count, because a number teaches people to edit the number.
    /// </summary>
    [TestMethod]
    public void EveryTestProjectReceivesThisContract()
    {
        var repositoryRoot = RepositoryRoot();
        var testsRoot = Path.Combine(repositoryRoot, "tests");
        var props = Path.Combine(testsRoot, "Directory.Build.props");
        Assert.IsTrue(
            File.Exists(props),
            $"The shared props file that compile-links this contract into every test project is missing from "
                + $"{testsRoot}. Without it this guard runs only where it happens to be linked.");

        var excluded = ExcludedProjectName.Matches(File.ReadAllText(props))
            .Select(static match => match.Groups[1].Value)
            .Where(static name => !string.Equals(name, SupportLibraryWithoutTests, StringComparison.Ordinal))
            .ToList();
        Assert.IsEmpty(
            excluded,
            "These projects are excluded from the shared props file that compile-links this contract, so they "
                + "are unguarded. Excluding a project that contains tests removes the check silently: "
                + string.Join(", ", excluded));

        var outsideTests = Directory
            .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("MSTest", StringComparison.Ordinal))
            .Where(path => !path.StartsWith(testsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToList();
        Assert.IsEmpty(
            outsideTests,
            "These projects contain tests but sit outside the directory the shared props file governs, so "
                + "this contract is never compiled into them and they are unguarded without anything saying "
                + "so. Move them under tests/, or widen the props file to reach them: "
                + string.Join(", ", outsideTests));
    }

    private static HashSet<string> NamesOfTypesWith(Assembly assembly, string attributeName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in assembly.GetTypes().GroupBy(static type => type.Name, StringComparer.Ordinal))
        {
            // A simple name shared by several types counts only when every one of them carries the
            // attribute, so a collision in another namespace cannot excuse a file.
            if (group.All(type => type.GetCustomAttributesData()
                    .Any(a => string.Equals(a.AttributeType.Name, attributeName, StringComparison.Ordinal))))
            {
                names.Add(group.Key);
            }
        }
        return names;
    }

    private static IEnumerable<string> SourcesUnder(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string ProjectDirectory() =>
        Ascend(static directory =>
            Directory.EnumerateFiles(directory.FullName, "*.csproj", SearchOption.TopDirectoryOnly).Any());

    private static string RepositoryRoot() =>
        Ascend(static directory => File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")));

    private static string Ascend(Func<DirectoryInfo, bool> found)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !found(directory))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException($"No ancestor of {AppContext.BaseDirectory} matched.");
    }
}
