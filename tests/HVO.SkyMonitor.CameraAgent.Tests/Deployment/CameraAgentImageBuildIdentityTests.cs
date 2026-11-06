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
    // A build given a version passes it as -p:Version, which Directory.Build.targets checks against the product version
    // (#1310); one given none is the product's development build.
    private const string StampArguments =
        "${HVO_VERSION:+\"-p:Version=${HVO_VERSION}\"} \"-p:SourceRevisionId=${HVO_SOURCE_REVISION}\"";
    private const string CameraAgentPublishCommand = "/src/scripts/release:cameraagent-publish publish -c Release -o /app/publish ";
    private const string ReplayRunnerPublishCommand =
        "dotnet publish ../HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj ";
    private static readonly string[] GlobalIdentityDeclarations = ["ARG HVO_VERSION=\"\"", "ARG HVO_SOURCE_REVISION=unknown"];

    // Version/revision inherit the global defaults. ReadyToRun is a build-only opt-in, and the release's created time
    // has no default in either stage: only a release build is dated.
    private static readonly string[] StageIdentityDeclarations =
        [
            "ARG HVO_READY_TO_RUN=false",
            "ARG HVO_CREATED",
            "ARG HVO_VERSION",
            "ARG HVO_SOURCE_REVISION",
            "ARG HVO_VERSION",
            "ARG HVO_SOURCE_REVISION",
            "ARG HVO_CREATED"
        ];

    [TestMethod]
    public void Dockerfile_DeclaresNoVersionAndAnUnknownRevisionByDefault()
    {
        var instructions = ReadInstructions();
        var firstStage = instructions.FindIndex(static instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal));
        var global = instructions[..firstStage];

        // No version is the product version's development build (Directory.Build.props), not a version of its own.
        CollectionAssert.AreEqual(
            GlobalIdentityDeclarations,
            global.Where(static instruction => instruction.StartsWith("ARG HVO_", StringComparison.Ordinal)).ToArray());
        CollectionAssert.AreEqual(
            StageIdentityDeclarations,
            instructions[firstStage..].Where(static instruction => instruction.StartsWith("ARG HVO_", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public void Dockerfile_StampsEveryPublishedOutputAfterTheCachedRestoreLayers()
    {
        var instructions = ReadInstructions();
        var buildStage = instructions.FindIndex(static instruction => instruction.EndsWith(" AS build", StringComparison.Ordinal));
        Assert.IsGreaterThan(-1, buildStage);
        var nextStage = instructions.FindIndex(buildStage + 1, static instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal));
        var build = instructions[buildStage..nextStage];
        var publishes = build
            .Select(static (instruction, index) => (instruction, index))
            .Where(static item => item.instruction.Contains("dotnet publish ", StringComparison.Ordinal) ||
                                  item.instruction.Contains("/src/scripts/release:cameraagent-publish publish ", StringComparison.Ordinal))
            .ToArray();

        Assert.HasCount(2, publishes);
        Assert.HasCount(1, publishes.Where(static item => item.instruction.Contains(CameraAgentPublishCommand, StringComparison.Ordinal)));
        Assert.HasCount(1, publishes.Where(static item => item.instruction.Contains(ReplayRunnerPublishCommand, StringComparison.Ordinal)));
        foreach (var (instruction, _) in publishes)
        {
            StringAssert.Contains(instruction, StampArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("InformationalVersion", instruction, StringComparison.Ordinal);
        }
        // The Dockerfile's helper invocation reaches exactly this phase-validated CameraAgent dotnet command;
        // caller arguments (including the stamps above) remain separate argv entries after its own publish options.
        var helper = ReadInstructions("scripts", "release:cameraagent-publish");
        const string phaseAssignment = "phase=\"${1:-}\"";
        const string phaseGuard = "case \"$phase\" in restore | publish) shift ;; *) echo 'Expected restore or publish.' >&2; exit 64 ;; esac";
        CollectionAssert.Contains(helper, phaseAssignment);
        CollectionAssert.Contains(helper, phaseGuard);
        Assert.IsGreaterThan(helper.IndexOf(phaseAssignment), helper.IndexOf(phaseGuard));
        Assert.HasCount(1, helper.Where(static instruction => instruction.StartsWith("exec ", StringComparison.Ordinal)));
        Assert.AreEqual(
            "exec dotnet \"$phase\" \"$root/src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj\" \"${arguments[@]}\" \"$@\"",
            helper[^1]);
        Assert.DoesNotContain("InformationalVersion", string.Join('\n', helper), StringComparison.Ordinal);
        // The product version and its check are in the build context the publishes run in.
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            Assert.HasCount(1, build.Where(instruction => instruction.StartsWith($"COPY {file} ", StringComparison.Ordinal)));
        }
        // Both restore passes stay cached when only the revision or epoch changes, regardless of cd/WORKDIR spelling.
        var restores = build
            .Select(static (instruction, index) => (instruction, index))
            .Where(static item => item.instruction.StartsWith("RUN ", StringComparison.Ordinal) &&
                                  item.instruction.Contains(" restore", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(4, restores);
        var sourceCopy = build.FindIndex(static instruction => instruction.StartsWith("COPY src/ ", StringComparison.Ordinal));
        Assert.IsGreaterThan(-1, sourceCopy);
        Assert.HasCount(2, restores.Where(item => item.index < sourceCopy));
        Assert.HasCount(2, restores.Where(item => item.index > sourceCopy));
        foreach (var declaration in new[] { "ARG HVO_VERSION", "ARG HVO_SOURCE_REVISION", "ARG SOURCE_DATE_EPOCH" })
        {
            var identity = build.FindIndex(instruction => instruction == declaration ||
                                                         instruction.StartsWith(declaration + "=", StringComparison.Ordinal));
            Assert.IsGreaterThan(sourceCopy, identity);
            foreach (var (_, index) in restores)
            {
                Assert.IsGreaterThan(index, identity, $"{declaration} must follow every cached restore instruction.");
            }
            foreach (var (_, index) in publishes)
            {
                Assert.IsLessThan(index, identity);
            }
        }
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
        // The release's created time (install contract section 1), never the base image's.
        StringAssert.Contains(label, "org.opencontainers.image.created=\"${HVO_CREATED}\"", StringComparison.Ordinal);
        // The package install and payload copy stay cached across revisions and release times.
        var payload = runtime.FindLastIndex(static instruction => instruction.StartsWith("COPY --from=build ", StringComparison.Ordinal));
        Assert.IsGreaterThan(payload, runtime.IndexOf("ARG HVO_SOURCE_REVISION"));
        Assert.IsGreaterThan(payload, runtime.IndexOf("ARG HVO_CREATED"));
    }

    /// <summary>Reads Dockerfile/helper lines, joining explicit continuations and dropping comments.</summary>
    private static List<string> ReadInstructions() => ReadInstructions("src", "HVO.SkyMonitor.CameraAgent", "Dockerfile");

    private static List<string> ReadInstructions(params string[] path)
    {
        var instructions = new List<string>();
        var current = string.Empty;
        foreach (var raw in File.ReadLines(Path.Combine([RepositoryRoot(), .. path])))
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
