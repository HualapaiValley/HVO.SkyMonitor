using System.Globalization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// The 0.9.1 upgrade note tells an operator which presentation producers a 0.9.0 graph loses to supersession, by
/// version number. Those numbers are prose, and the versions themselves are constants in Processing. This guard reads
/// both as text, with no reference to Processing, and fails when a producer is bumped without the note following.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class UpgradeNoteProducerVersionTests
{
    private const string NoteVersion = "0.9.1";
    private const string SourcePath = "src/HVO.SkyMonitor.Processing/PresentationLayerProducers.cs";

    /// <summary>Each producer: its constant in Processing, the note's name for it, and the sentence that states its version.</summary>
    private static readonly (string Constant, string Name, string NotePattern)[] Producers =
    [
        ("SceneProducerVersion", "scene-overlay producer", @"scene-overlay producer changes from v\d+ to v(\d+)"),
        ("CloudProducerVersion", "cloud producer", @"cloud producer from v\d+ to v(\d+)"),
        ("MetadataProducerVersion", "environment producer", @"environment producer from v\d+ to v(\d+)")
    ];

    [TestMethod]
    public void UpgradeNote_NamesTheProducerVersionsTheSourceDeclares()
    {
        var mismatches = Compare(ReadSource(), ReadNote());

        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches) + Environment.NewLine +
            $"Before {NoteVersion} is tagged, correct docs/upgrade-notes/{NoteVersion}.md. Once it is released that note is " +
            "history: state the new version in the next release's upgrade note and point this guard at that note.");
    }

    [TestMethod]
    public void UpgradeNote_SaysWhichPlanStringTheEnvironmentProducerIs()
    {
        var planString = Declared(ReadSource(), "MetadataProducerVersion");
        Assert.IsNotNull(planString);

        // Plans carry the producer as this identifier; the note's "environment producer" is otherwise unrecognizable in one.
        StringAssert.Contains(Normalize(ReadNote()), $"Graph plans name the environment producer `{planString}`.", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("SceneProducerVersion", "scene-overlay producer")]
    [DataRow("CloudProducerVersion", "cloud producer")]
    [DataRow("MetadataProducerVersion", "environment producer")]
    public void AProducerBumpedInSourceAloneIsReported(string constant, string name)
    {
        var source = ReadSource();
        var declared = Declared(source, constant)!;
        var number = VersionNumber(declared)!.Value;
        var bumped = source.Replace(declared, declared.Replace($"-v{number}", $"-v{number + 1}", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.AreNotEqual(source, bumped);

        var mismatches = Compare(bumped, ReadNote());

        // Exactly the bumped producer is reported, with both numbers; the other two still agree. The scene producer
        // is stated twice in the note, for a 0.9.0 host and for the early pre-releases, and both statements are reported.
        var expected = constant == "SceneProducerVersion" ? new[] { name, "early pre-release scene producers" } : [name];
        Assert.HasCount(expected.Length, mismatches);
        for (var index = 0; index < expected.Length; index++)
        {
            StringAssert.Contains(mismatches[index], expected[index], StringComparison.Ordinal);
            StringAssert.Contains(mismatches[index], $"is v{number + 1} in", StringComparison.Ordinal);
            StringAssert.Contains(mismatches[index], $"states v{number}.", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("scene-overlay producer changes from v10 to v13", "scene-overlay producer changes from v10 to v12", "scene-overlay producer")]
    [DataRow("cloud producer from v4 to v5", "cloud producer from v4 to v6", "cloud producer")]
    [DataRow("environment producer from v5 to v6", "environment producer from v5 to v7", "environment producer")]
    [DataRow("also move to v13", "also move to v12", "early pre-release scene producers")]
    public void ANoteThatStatesAnotherVersionIsReported(string stated, string changed, string name)
    {
        var note = Normalize(ReadNote());
        StringAssert.Contains(note, stated, StringComparison.Ordinal);

        var mismatch = Compare(ReadSource(), note.Replace(stated, changed, StringComparison.Ordinal)).Single();

        StringAssert.Contains(mismatch, name, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("the cloud producer from v4 to v5, and ", "cloud producer")]
    [DataRow("Early 0.9.1-dev.1 through dev.5 scene producers v11/v12 also move to v13.", "early pre-release scene producers")]
    public void ANoteThatNoLongerStatesAVersionIsReportedRatherThanPassed(string removed, string name)
    {
        var note = Normalize(ReadNote());
        StringAssert.Contains(note, removed, StringComparison.Ordinal);

        var mismatch = Compare(ReadSource(), note.Replace(removed, string.Empty, StringComparison.Ordinal)).Single();

        StringAssert.Contains(mismatch, name, StringComparison.Ordinal);
        StringAssert.Contains(mismatch, "does not state", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ASourceWithoutAProducerConstantIsReportedRatherThanPassed()
    {
        var source = ReadSource().Replace("public const string CloudProducerVersion", "public const string CloudLayerVersion", StringComparison.Ordinal);

        var mismatch = Compare(source, ReadNote()).Single();

        StringAssert.Contains(mismatch, "CloudProducerVersion", StringComparison.Ordinal);
        StringAssert.Contains(mismatch, SourcePath, StringComparison.Ordinal);
    }

    /// <summary>Every disagreement between the producer versions the source declares and the ones the note states.</summary>
    private static List<string> Compare(string source, string note)
    {
        var text = Normalize(note);
        var mismatches = new List<string>();
        foreach (var (constant, name, pattern) in Producers)
        {
            if (Declared(source, constant) is not { } declared || VersionNumber(declared) is not { } version)
            {
                mismatches.Add($"{SourcePath} no longer declares {constant} as a versioned producer identifier.");
                continue;
            }
            Check(name, pattern, version);
            if (constant == "SceneProducerVersion")
            {
                Check("early pre-release scene producers", @"scene producers v\d+/v\d+ also move to v(\d+)", version);
            }
        }
        return mismatches;

        void Check(string name, string pattern, int version)
        {
            var stated = Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
            if (!stated.Success)
            {
                mismatches.Add($"docs/upgrade-notes/{NoteVersion}.md does not state the version of the {name}; the source declares v{version}.");
            }
            else if (int.Parse(stated.Groups[1].Value, CultureInfo.InvariantCulture) != version)
            {
                mismatches.Add($"The {name} is v{version} in {SourcePath}, but docs/upgrade-notes/{NoteVersion}.md states v{stated.Groups[1].Value}.");
            }
        }
    }

    private static string? Declared(string source, string constant)
    {
        var match = Regex.Match(source, $@"public const string {Regex.Escape(constant)} = ""([^""]+)"";",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return match.Success ? match.Groups[1].Value : null;
    }

    // A producer identifier carries its version as one "-vN" segment, as in "cloud-presentation-v5-original-colours".
    private static int? VersionNumber(string identifier)
    {
        var matches = Regex.Matches(identifier, @"-v(\d+)(?=-|\z)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return matches.Count == 1 ? int.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    // The note wraps its lines, so a sentence is matched with every run of white space read as one space.
    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ReadSource() => File.ReadAllText(Path.Combine([RepositoryRoot(), .. SourcePath.Split('/')]));

    private static string ReadNote() => File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "upgrade-notes", NoteVersion + ".md"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
