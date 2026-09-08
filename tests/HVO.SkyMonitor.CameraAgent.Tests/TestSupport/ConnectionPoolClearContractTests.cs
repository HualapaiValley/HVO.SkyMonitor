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
/// If it was a teardown clear in a parallelisable class, meaning it only released handles before deleting
/// the test's own temporary directory, delete it: this repository is developed and built on POSIX
/// filesystems, where unlinking an open file succeeds, so such a clear was never load-bearing for the
/// deletion in the first place. A Windows checkout is required to live on the WSL2 Linux filesystem for
/// exactly that reason. If the clear is load-bearing, meaning the test would silently stop exercising what
/// it claims without it, keep it and give the class a class-level <c>[DoNotParallelize]</c> so nothing runs
/// beside it. That attribute really does mean nothing runs beside the class rather than merely that its own
/// methods run in sequence.
/// </para>
/// <para>
/// That exclusion is relied upon, so it was verified by measurement under MSTest 4.3.3 rather than taken
/// from the attribute's documentation: one serialised test held the floor for two seconds beside twelve
/// parallelisable ones, and no parallel interval overlapped it, while thirty-one overlapping pairs among
/// the parallel set proved the probe could have seen an overlap had one occurred. The bound worth knowing
/// is that this establishes the observable and not the mechanism. If the runner separates serialised tests
/// into their own phase, the same data would be produced by ordering rather than by exclusion. Either
/// delivers what this remedy needs, but a future change to MSTest's scheduling could alter the guarantee,
/// and the probe is not committed, so nothing here would re-measure it.
/// </para>
/// <para>
/// Scope. This checks every test assembly that runs methods in parallel, which is more than the one this
/// file lives in. An assembly parallelises if it declares <c>[assembly: Parallelize]</c> itself or if its
/// project compile-links the shared settings file that declares it, and an assembly-level
/// <c>[DoNotParallelize]</c> overrides both. The linked case is the reason for checking projects at all: a
/// contributor working in one of those assemblies has no parallelisation attribute anywhere in front of
/// them, so the constraint is invisible from inside the code it governs.
/// </para>
/// <para>
/// The two markers are matched differently, and deliberately. The opt-out is honoured only where it is
/// unambiguously a declaration: inside the file's prologue, meaning the region after using directives and
/// before the first namespace or type declaration, which is the only place C# permits a global attribute,
/// and then only in what remains of that prologue once comments and conditional regions are removed. The
/// opt-in is read from the whole file with nothing removed at all.
/// </para>
/// <para>
/// The asymmetry is the point, because the two fail in opposite directions. Missing an opt-out means
/// scanning an assembly that opted out, which produces a finding someone reads. Missing an opt-in means
/// dropping an entire assembly from the scan with nothing said. So the opt-out is read strictly and the
/// opt-in loosely, and every inaccuracy in either lands on the side of scanning more.
/// </para>
/// <para>
/// Three attempts were needed to get that right, and each earlier one shipped a real false green with a
/// genuine offending call still present. Matching the marker anywhere in a file let this guard's own string
/// literals exclude its own assembly. Line anchoring did not fix it, because a raw string literal's content
/// genuinely begins at column zero, so a file quoting the attribute read as declaring it. Restricting to
/// the prologue did not fix it either, because a prologue legitimately contains text that is not a
/// declaration: a marker inside a block comment, or inside an <c>#if</c> that is never defined, or in the
/// dead branch of an <c>#else</c>. Commenting an attribute out is the most ordinary thing anyone does to
/// one, so the rare case was closed first and the common ones were left.
/// </para>
/// <para>
/// What remains is bounded and lands on the safe side. An opt-out spelled across several lines, or with a
/// Unicode escape in its identifier, is not matched, so its assembly is scanned. Comment removal is
/// textual, so a line comment containing a block-comment opener can over-strip, which again only scans
/// more. A string literal inside a prologue is the one case that is neither matched nor safely ignorable,
/// since a literal can forge either marker, and rather than guess this reports the file and asks for the
/// literal to be moved.
/// </para>
/// <para>
/// The general rule behind that, worth more than the instance: an exclusion marker deserves stricter
/// matching than an inclusion marker, because the two fail in different directions. An inclusion marker
/// that over-matches produces noise, and someone investigates noise. An exclusion marker that over-matches
/// produces quiet, and quiet is indistinguishable from correctness.
/// </para>
/// <para>
/// This guard reads source text, not compiled metadata, and has three known limits. They are recorded here
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
/// <para>
/// Third, it reasons one file at a time, and a test class may be <c>partial</c> across several files. If
/// the attributes sit in one file and a clear in another, the file holding the clear is reported as an
/// offender even when the class is in fact serialised. That direction is safe, since it produces a false
/// alarm rather than silence, but a reader who hits it should know it is the guard's shape and not
/// necessarily a real defect. <c>VirtualSkyCloudPerformanceTests</c> is one such partial class.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class ConnectionPoolClearContractTests
{
    private const string ThisFileName = "ConnectionPoolClearContractTests.cs";

    /// <summary>The assembly this issue was raised against; discovery that loses it has silently narrowed.</summary>
    private const string AnchorAssembly = "HVO.SkyMonitor.CameraAgent.Tests";

    private static readonly Regex GlobalClearInvocation = new(
        @"SqliteConnection\s*\.\s*ClearAllPools\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClassLevelDoNotParallelize = new(
        @"^\[DoNotParallelize\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex TestClassDeclaration = new(
        @"^\[TestClass\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Assembly attributes sit at the start of a line, and only in a file's prologue. Line anchoring alone
    // is not enough: a raw string literal's content genuinely begins at column zero, so a file merely
    // quoting the opt-out attribute would read as declaring it and would exclude its whole assembly. That
    // is why these are matched against the prologue only. See PrologueOf.
    private static readonly Regex AssemblyParallelize = new(
        @"^\[assembly:\s*Parallelize", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex AssemblyDoNotParallelize = new(
        @"^\[assembly:\s*DoNotParallelize\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // The first namespace or type declaration ends the region where an assembly attribute is legal. A
    // non-global attribute such as [TestClass] also ends it, since it can only be attaching to a type.
    private static readonly Regex PrologueTerminator = new(
        @"^[ \t]*(namespace\b|(public|internal|private|protected|static|sealed|abstract|partial|unsafe|file|class|struct|record|interface|enum|delegate)\b|\[\s*(?!assembly\s*:|module\s*:))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex LineComment = new(
        @"//[^\n]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A prologue has no executable code, so it should never contain a string literal that could disguise an
    // attribute. If one appears, this cannot tell a declaration from quoted content, and it says so rather
    // than guessing, because guessing wrong here is silent.
    // Only raw and verbatim literals can span lines, and spanning lines is what lets quoted content sit at
    // column zero and impersonate a declaration.
    private static bool HasAmbiguousPrologue(string prologue) =>
        prologue.Contains("\"\"\"", StringComparison.Ordinal) ||
        prologue.Contains("@\"", StringComparison.Ordinal);

    [TestMethod]
    public void NoParallelisableTestClassClearsEveryConnectionPool()
    {
        var testsRoot = Path.Combine(GetRepositoryRoot(), "tests");

        // Closed first, because it decides whether the rest of this method is reading what it thinks it is.
        var ambiguous = FindAmbiguousPrologues(testsRoot);
        Assert.IsEmpty(
            ambiguous,
            "These files have a string literal in the region where assembly attributes are declared, so this "
                + "guard cannot tell a real [assembly: DoNotParallelize] from quoted text and would exclude "
                + "the whole assembly from its scan if it guessed wrong. Move the literal below the first "
                + "type declaration: " + string.Join(", ", ambiguous));

        var parallelising = FindParallelisingAssemblies(testsRoot);

        // Discovery is the part that can fail open. If the shared settings file is renamed or the marker
        // changes, this silently narrows to fewer assemblies and reports clean over a smaller tree than it
        // claims. Anchor it on the assembly this issue was raised against.
        Assert.IsTrue(
            parallelising.Count > 0 && parallelising.Any(directory =>
                string.Equals(Path.GetFileName(directory), AnchorAssembly, StringComparison.Ordinal)),
            $"This guard found no parallelising test assembly named {AnchorAssembly}, so its own discovery is "
                + "broken and a clean result would mean nothing. Check the assembly-level Parallelize marker "
                + $"and the shared settings link. Found: {FormatNames(parallelising)}");

        var offenders = new List<string>();
        var unanalysable = new List<string>();
        foreach (var assembly in parallelising)
        {
            foreach (var file in EnumerateSources(assembly))
            {
                // This file names the API it forbids, in prose and in the pattern itself.
                if (string.Equals(Path.GetFileName(file), ThisFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                var source = File.ReadAllText(file);
                if (!GlobalClearInvocation.IsMatch(source))
                {
                    continue;
                }
                // A class that opts out of parallelisation has nothing running beside it, so its clear
                // cannot reach another test's pool. Those call sites are safe and are deliberately left
                // alone. The opt-out has to be the class's own attribute; an indented one belongs to a
                // single method and says nothing about the rest of the class.
                if (!ClassLevelDoNotParallelize.IsMatch(source))
                {
                    offenders.Add(Path.GetRelativePath(testsRoot, file));
                    continue;
                }
                // One serialised class per file is the only shape this guard can reason about. A file
                // holding a serialised class beside a parallelisable one would be excused wholesale by the
                // check above, so refuse to analyse it rather than report it clean.
                if (TestClassDeclaration.Count(source) > 1)
                {
                    unanalysable.Add(Path.GetRelativePath(testsRoot, file));
                }
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
                + "owned by tests running beside them and reproduces issue #754. If it is a teardown clear, "
                + "meaning it only released handles before deleting the test's own temporary directory, delete "
                + "it; POSIX unlinks open files. If it is load-bearing, give the class a class-level "
                + "[DoNotParallelize]: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Files whose prologue contains a string literal. A prologue holds only using directives, comments and
    /// attributes, so a literal there means this cannot distinguish a declaration from quoted content, and
    /// the failure direction of guessing wrong is silence.
    /// </summary>
    private static List<string> FindAmbiguousPrologues(string testsRoot)
    {
        var ambiguous = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(testsRoot))
        {
            foreach (var file in EnumerateSources(directory))
            {
                if (string.Equals(Path.GetFileName(file), ThisFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (HasAmbiguousPrologue(PrologueOf(File.ReadAllText(file))))
                {
                    ambiguous.Add(Path.GetRelativePath(testsRoot, file));
                }
            }
        }
        return ambiguous;
    }

    /// <summary>
    /// Test assembly directories whose methods run in parallel, either by declaring the attribute or by
    /// compile-linking the shared settings file that declares it. An assembly-level opt-out wins over both.
    /// </summary>
    private static List<string> FindParallelisingAssemblies(string testsRoot)
    {
        var discovered = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(testsRoot))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (project is null)
            {
                continue;
            }
            var sources = EnumerateSources(directory)
                .Where(static file => !string.Equals(Path.GetFileName(file), ThisFileName, StringComparison.Ordinal))
                .Select(File.ReadAllText)
                .ToList();

            // The opt-out is honoured only where it is unambiguously a declaration: in the prologue, and in
            // the part of it left after comments and conditional regions are removed. Everything discarded
            // there is provably not a declaration, and being wrong in this direction only means scanning an
            // assembly that opted out, which produces a finding someone reads rather than silence.
            var declarations = sources.Select(static source =>
                StripCommentsAndConditionalRegions(PrologueOf(source))).ToList();
            if (declarations.Any(static declaration => AssemblyDoNotParallelize.IsMatch(declaration)))
            {
                continue;
            }

            // The opt-in is read from the whole file with nothing removed, because its failure direction is
            // the opposite: a missed opt-in drops an entire assembly from the scan silently, while a spurious
            // one only scans an assembly that did not need it. Reading it loosely is the safe error.
            var declaresItself = sources.Any(static source => AssemblyParallelize.IsMatch(source));
            var linksSharedSettings = File.ReadAllText(project)
                .Contains("MSTestSettings.cs", StringComparison.Ordinal);
            if (declaresItself || linksSharedSettings)
            {
                discovered.Add(directory);
            }
        }
        return discovered;
    }

    private static IEnumerable<string> EnumerateSources(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FormatNames(List<string> directories) =>
        directories.Count == 0 ? "none" : string.Join(", ", directories.Select(Path.GetFileName));

    /// <summary>
    /// A prologue reduced to the text that can actually be a declaration. Comments and conditional regions
    /// are removed rather than interpreted: a marker inside a block comment or inside any <c>#if</c> is not
    /// a declaration this guard can rely on, and no string handling is needed because a prologue carrying a
    /// literal has already failed the ambiguity check above.
    /// </summary>
    private static string StripCommentsAndConditionalRegions(string prologue)
    {
        var stripped = LineComment.Replace(BlockComment.Replace(prologue, string.Empty), string.Empty);
        var kept = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var line in stripped.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                depth++;
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            // #else and #elif open a branch of a region that is already being discarded.
            if (trimmed.StartsWith("#el", StringComparison.Ordinal))
            {
                continue;
            }
            if (depth == 0)
            {
                kept.Append(line).Append('\n');
            }
        }
        return kept.ToString();
    }

    /// <summary>
    /// The part of a file in which an assembly attribute can legally appear: after using directives and
    /// before the first namespace or type declaration. Anything past that point is provably not an assembly
    /// attribute however it is spelled, which is what makes a file that merely quotes the attribute text
    /// harmless rather than authoritative.
    /// </summary>
    private static string PrologueOf(string source)
    {
        var terminator = PrologueTerminator.Match(source);
        return terminator.Success ? source[..terminator.Index] : source;
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
