namespace HVO.SkyMonitor.CameraAgent.Tests.Deployment;

/// <summary>
/// Keeps the CameraAgent image's build identity in its published assemblies and labels. The build context excludes
/// <c>.git</c>, so the SDK cannot derive the source revision; before #1297 an image built from a known commit
/// reported <c>1.0.0</c> on the Software page, <c>vunknown</c> in telemetry, and the base image's version label.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentImageBuildIdentityTests
{
    private const string StampArguments =
        "\"-p:InformationalVersion=${HVO_VERSION}\" \"-p:SourceRevisionId=${HVO_SOURCE_REVISION}\"";

    // Each stage inherits the one declared default instead of restating it.
    private static readonly string[] StageIdentityDeclarations =
        ["ARG HVO_VERSION", "ARG HVO_SOURCE_REVISION", "ARG HVO_VERSION", "ARG HVO_SOURCE_REVISION"];

    [TestMethod]
    public void Dockerfile_DeclaresADevelopmentVersionAndAnUnknownRevisionByDefault()
    {
        var instructions = ReadInstructions();
        var firstStage = instructions.FindIndex(static instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal));
        var global = instructions[..firstStage];

        CollectionAssert.Contains(global, "ARG HVO_VERSION=0.0.0-dev");
        CollectionAssert.Contains(global, "ARG HVO_SOURCE_REVISION=unknown");
        CollectionAssert.AreEqual(
            StageIdentityDeclarations,
            instructions[firstStage..].Where(static instruction => instruction.StartsWith("ARG HVO_", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public void Dockerfile_StampsEveryPublishedOutputAfterTheCachedRestoreLayers()
    {
        var instructions = ReadInstructions();
        var runtimeStage = instructions.FindLastIndex(static instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal));
        var build = instructions[..runtimeStage];
        var publishes = build
            .Select(static (instruction, index) => (instruction, index))
            .Where(static item => item.instruction.Contains("dotnet publish ", StringComparison.Ordinal))
            .ToArray();
        var identity = build.IndexOf("ARG HVO_SOURCE_REVISION");

        Assert.HasCount(2, publishes);
        foreach (var (instruction, _) in publishes)
        {
            StringAssert.Contains(instruction, StampArguments, StringComparison.Ordinal);
        }
        // A change of revision alone must keep reusing restore and the source copy.
        Assert.IsGreaterThan(build.IndexOf("COPY src/ src/"), identity);
        Assert.IsGreaterThan(build.FindLastIndex(static instruction => instruction.StartsWith("RUN dotnet restore ", StringComparison.Ordinal)), identity);
        Assert.IsLessThan(publishes[0].index, identity);
    }

    [TestMethod]
    public void Dockerfile_LabelsTheVersionExplicitlyAfterThePayload()
    {
        var instructions = ReadInstructions();
        var runtimeStage = instructions.FindLastIndex(static instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal));
        var runtime = instructions[runtimeStage..];
        var label = runtime.Single(static instruction => instruction.StartsWith("LABEL ", StringComparison.Ordinal));

        StringAssert.Contains(label, "org.opencontainers.image.version=\"${HVO_VERSION}\"", StringComparison.Ordinal);
        StringAssert.Contains(label, "org.opencontainers.image.revision=\"${HVO_SOURCE_REVISION}\"", StringComparison.Ordinal);
        // The package install and payload copy stay cached across revisions.
        Assert.IsGreaterThan(
            runtime.FindLastIndex(static instruction => instruction.StartsWith("COPY --from=build ", StringComparison.Ordinal)),
            runtime.IndexOf("ARG HVO_SOURCE_REVISION"));
    }

    /// <summary>Reads the Dockerfile as logical instructions, joining continuation lines and dropping comments.</summary>
    private static List<string> ReadInstructions()
    {
        var instructions = new List<string>();
        var current = string.Empty;
        foreach (var raw in File.ReadLines(Path.Combine(RepositoryRoot(), "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.EndsWith('\\'))
            {
                current += line[..^1].TrimEnd() + " ";
                continue;
            }
            instructions.Add(current + line);
            current = string.Empty;
        }
        return instructions;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
