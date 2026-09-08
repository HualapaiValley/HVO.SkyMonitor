using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Keeps issue #754 from returning under a new name. The defect is not any particular test: it is that a
/// process-global pool clear, called from a class that runs in parallel with others, disposes idle pooled
/// connections belonging to whatever else is running, so a test renting one at that moment gets a disposed
/// handle. Removing today's instances removes today's failures; this asserts the property that keeps new
/// ones from appearing.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways to satisfy this guard, and which one applies depends on what the clear was for.
/// If it only released handles before deleting the test's own temporary directory, delete it: this
/// repository is developed and built on POSIX filesystems, where unlinking an open file succeeds, and a
/// Windows checkout is required to live on the WSL2 Linux filesystem for exactly that reason. If the
/// clear is load-bearing, meaning the test would silently stop exercising what it claims without it, keep
/// it and give the class a class-level <c>[DoNotParallelize]</c> so nothing runs beside it.
/// </para>
/// <para>
/// This guard reads source text, not compiled metadata, and has two known limits. Both are recorded here
/// rather than only in the pull request, because the person reading a clean guard result in two years is
/// reading this code and not that discussion.
/// </para>
/// <para>
/// First, it only sees a direct <c>SqliteConnection.ClearAllPools</c> call. A parallelisable test that
/// reaches the same API through a helper method defined elsewhere passes this guard. Closing that would
/// require analysing the compiled assembly rather than the source, which is a larger mechanism than this
/// fix warrants.
/// </para>
/// <para>
/// Second, it decides whether a class is serialised from the position of its <c>[DoNotParallelize]</c>
/// attribute: an attribute at the start of a line is class-level, an indented one is method-level. That
/// holds only because <c>dotnet format</c> is a required gate and normalises this indentation. An earlier
/// version of this guard treated a <c>[DoNotParallelize]</c> anywhere in the file as an opt-out, which
/// silently excused seven genuinely parallelisable call sites in the environmental outbox tests. A guard
/// that goes quiet exactly where its assumption breaks is worse than no guard, so a file whose shape this
/// cannot analyse now fails instead of being skipped.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class ConnectionPoolClearContractTests
{
    private static readonly Regex GlobalClearInvocation = new(
        @"SqliteConnection\s*\.\s*ClearAllPools\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClassLevelDoNotParallelize = new(
        @"^\[DoNotParallelize\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex TestClassDeclaration = new(
        @"^\[TestClass\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [TestMethod]
    public void NoParallelisableTestClassClearsEveryConnectionPool()
    {
        var assemblyRoot = Path.Combine(GetRepositoryRoot(), "tests", "HVO.SkyMonitor.CameraAgent.Tests");
        var offenders = new List<string>();
        var unanalysable = new List<string>();
        foreach (var file in Directory.EnumerateFiles(assemblyRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            // This file names the API it forbids, in prose and in the pattern itself.
            if (string.Equals(Path.GetFileName(file), "ConnectionPoolClearContractTests.cs", StringComparison.Ordinal))
            {
                continue;
            }
            var source = File.ReadAllText(file);
            if (!GlobalClearInvocation.IsMatch(source))
            {
                continue;
            }
            // A class that opts out of parallelisation has nothing running beside it, so its clear cannot
            // reach another test's pool. Those call sites are safe and are deliberately left alone. The
            // opt-out has to be the class's own attribute; an indented one belongs to a single method and
            // says nothing about the rest of the class.
            if (!ClassLevelDoNotParallelize.IsMatch(source))
            {
                offenders.Add(Path.GetRelativePath(assemblyRoot, file));
                continue;
            }
            // One serialised class per file is the only shape this guard can reason about. A file holding
            // a serialised class beside a parallelisable one would be excused wholesale by the check
            // above, so refuse to analyse it rather than report it clean.
            if (TestClassDeclaration.Count(source) > 1)
            {
                unanalysable.Add(Path.GetRelativePath(assemblyRoot, file));
            }
        }

        Assert.IsEmpty(
            unanalysable,
            "These files declare more than one test class alongside a class-level [DoNotParallelize] and call "
                + "SqliteConnection.ClearAllPools. This guard cannot tell which class owns the opt-out, so it "
                + "cannot say whether the call is safe. Split each file so every test class lives in its own "
                + "file: " + string.Join(", ", unanalysable));

        Assert.IsEmpty(
            offenders,
            "These parallelisable files call SqliteConnection.ClearAllPools, which disposes pooled connections "
                + "owned by tests running beside them and reproduces issue #754. If the call only released "
                + "handles before deleting the test's own temporary directory, delete it; POSIX unlinks open "
                + "files. If it is load-bearing, give the class a class-level [DoNotParallelize]: "
                + string.Join(", ", offenders));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
