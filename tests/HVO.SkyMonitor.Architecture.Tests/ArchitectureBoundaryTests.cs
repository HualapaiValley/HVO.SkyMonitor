using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    private const string AgentCore = "HVO.SkyMonitor.AgentCore";
    private const string Astronomy = "HVO.SkyMonitor.Astronomy";
    private const string Imaging = "HVO.SkyMonitor.Imaging";
    private const string FleetContracts = "HVO.SkyMonitor.Fleet.Contracts";
    private const string Processing = "HVO.SkyMonitor.Processing";
    private const string Catalog = "HVO.SkyMonitor.Catalog.Sqlite";
    private const string Common = "HVO.SkyMonitor.Common";
    private const string CameraAgentCommon = "HVO.SkyMonitor.CameraAgent.Common";
    private const string CameraAgentZwo = "HVO.SkyMonitor.CameraAgent.Modules.Zwo";
    private const string CameraAgentReplay = "HVO.SkyMonitor.CameraAgent.Replay";
    private const string CameraAgentReplayRunner = "HVO.SkyMonitor.CameraAgent.ReplayRunner";
    private const string ProcessingRunnerContracts = "HVO.SkyMonitor.ProcessingRunner.Contracts";
    private const string ProcessingRunner = "HVO.SkyMonitor.ProcessingRunner";
    private const string CameraAgent = "HVO.SkyMonitor.CameraAgent";
    private const string LogicHost = "HVO.SkyMonitor.LogicHost";
    private const string DeploymentContracts = "HVO.SkyMonitor.Deployment.Contracts";
    private const string DeploymentDistribution = "HVO.SkyMonitor.Deployment.Distribution";
    private const string DeploymentCli = "HVO.SkyMonitor.Deployment.Cli";

    // Host-neutral filesystem primitives (#592): mechanisms only, references nothing, consumed
    // by the LogicHost filesystem provider (#585) and later CameraAgent.Common (#587). Its
    // dependency rule is asserted by StorageFileSystemBoundaryIsNarrowWhenPresent.
    private const string StorageFileSystem = "HVO.SkyMonitor.Storage.FileSystem";
    private const string TestSupport = "HVO.SkyMonitor.TestSupport";
    private const string LogicHostTestInfrastructure = "HVO.SkyMonitor.LogicHost.TestInfrastructure";
    private const string FixtureCatalogSha256 = "F80689217769A6B13C1B9BFB9711485D3CB1AD8DE009D3D6B0F0B0A4F1FA9840";

    private static readonly string[] ProjectSearchDirectories = ["src", "tests", "scripts", "tools"];

    private static readonly string[] SupportedConfigurations = ["Debug", "Release"];

    private static readonly string[] TestOnlyPackagePrefixes =
    [
        "MSTest",
        "Microsoft.NET.Test.Sdk",
        "coverlet",
        "Testcontainers",
        "Microsoft.AspNetCore.Mvc.Testing",
        "FluentAssertions",
        "Moq",
        "bunit"
    ];

    private static readonly Lazy<RepositoryGraph> Repository = new(RepositoryGraph.Load);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedProductionReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [AgentCore] = Set(),
            [Astronomy] = Set(AgentCore),
            [Imaging] = Set(AgentCore, Astronomy),
            [FleetContracts] = Set(),
            [Processing] = Set(AgentCore, Astronomy, Imaging),
            [Catalog] = Set(Astronomy),
            [Common] = Set(),
            [StorageFileSystem] = Set(),
            [CameraAgentCommon] = Set(AgentCore, Astronomy, Imaging, Processing, FleetContracts, CameraAgentReplay),
            [CameraAgentZwo] = Set(AgentCore),
            [CameraAgentReplay] = Set(AgentCore, Processing),
            [CameraAgentReplayRunner] = Set(Processing, CameraAgentReplay),
            [ProcessingRunnerContracts] = Set(AgentCore, Processing),
            [ProcessingRunner] = Set(AgentCore, Processing, ProcessingRunnerContracts),
            [CameraAgent] = Set(CameraAgentCommon, CameraAgentZwo, Catalog, Common),
            [LogicHost] = Set(AgentCore, Astronomy, Imaging, Processing, ProcessingRunnerContracts, FleetContracts, Catalog, Common, StorageFileSystem),
            [DeploymentContracts] = Set(),
            [DeploymentDistribution] = Set(DeploymentContracts),
            [DeploymentCli] = Set(AgentCore, Catalog, DeploymentContracts, DeploymentDistribution)
        };

    private static readonly IReadOnlySet<string> SharedTestProjects = Set(
        "HVO.SkyMonitor.AgentCore.Tests",
        "HVO.SkyMonitor.Architecture.Tests",
        "HVO.SkyMonitor.Astronomy.Tests",
        "HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests",
        "HVO.SkyMonitor.Catalog.Sqlite.Tests",
        "HVO.SkyMonitor.Common.Tests",
        "HVO.SkyMonitor.Deployment.Cli.Tests",
        "HVO.SkyMonitor.Deployment.Distribution.Tests",
        "HVO.SkyMonitor.Fleet.Contracts.Tests",
        "HVO.SkyMonitor.Imaging.Tests",
        "HVO.SkyMonitor.Processing.Tests",
        "HVO.SkyMonitor.ProcessingRunner.Tests",
        "HVO.SkyMonitor.Storage.FileSystem.Tests",
        "HVO.SkyMonitor.TestSupport.Tests");

    private static readonly IReadOnlySet<string> CameraAgentTestProjects = Set(
        "HVO.SkyMonitor.CameraAgent.AcceptanceTests",
        "HVO.SkyMonitor.CameraAgent.IntegrationTests",
        "HVO.SkyMonitor.CameraAgent.Tests");

    private static readonly IReadOnlySet<string> LogicHostTestProjects = Set(
        "HVO.SkyMonitor.LogicHost.IntegrationTests",
        "HVO.SkyMonitor.LogicHost.Tests");

    private static readonly IReadOnlySet<string> CombinedTestProjects = Set(
        "HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests",
        "HVO.SkyMonitor.CameraAgent.LogicHost.Tests");

    private static readonly IReadOnlySet<string> CameraAgentImplementations = Set(
        CameraAgent,
        CameraAgentCommon,
        CameraAgentZwo,
        CameraAgentReplay,
        CameraAgentReplayRunner);

    [TestMethod]
    [TestCategory("Integration")]
    public void ProductionProjectReferencesFollowDocumentedGraph()
    {
        var repository = Repository.Value;
        var violations = repository.ValidateProductionReferences(AllowedProductionReferences)
            .Concat(repository.ValidateTestOwnership(
                SharedTestProjects,
                CameraAgentTestProjects,
                LogicHostTestProjects,
                CombinedTestProjects,
                LogicHostTestInfrastructure))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StorageFileSystemBoundaryIsNarrowWhenPresent()
    {
        // #584 defines the boundary; #592 delivers the project; #585 and #587 consume it.
        // The project may reference nothing: no host, no AgentCore, no Common, no persistence,
        // no provider SDK. Its consumers are the LogicHost filesystem provider and, after
        // RM-017, CameraAgent.Common. Nothing else may reference it, and it may not appear
        // in the graph with a wider rule than an empty set.
        var repository = Repository.Value;
        if (!repository.Projects.TryGetValue(StorageFileSystem, out var project))
        {
            Assert.IsFalse(AllowedProductionReferences.ContainsKey(StorageFileSystem),
                $"{StorageFileSystem} is documented in the graph before it exists.");
            return;
        }

        Assert.AreEqual(ProjectKind.Production, project.Kind);
        Assert.IsTrue(AllowedProductionReferences.TryGetValue(StorageFileSystem, out var allowed)
            && allowed.Count == 0,
            $"{StorageFileSystem} must be documented with an empty reference set.");
        Assert.IsEmpty(project.References,
            $"{StorageFileSystem} must reference no project: {string.Join(", ", project.References)}");

        var forbiddenPackages = project.PackageReferences
            .Where(package => package.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || package.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                || package.StartsWith("AWSSDK", StringComparison.Ordinal)
                || package.StartsWith("Azure.", StringComparison.Ordinal)
                || package.StartsWith("Minio", StringComparison.Ordinal)
                || package.StartsWith("SkiaSharp", StringComparison.Ordinal))
            .ToArray();
        Assert.IsEmpty(forbiddenPackages,
            $"{StorageFileSystem} must not take host, persistence, or provider packages: {string.Join(", ", forbiddenPackages)}");

        var consumers = repository.Projects.Values
            .Where(candidate => candidate.Kind == ProjectKind.Production && candidate.References.Contains(StorageFileSystem))
            .Select(candidate => candidate.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var permittedConsumers = new[] { CameraAgentCommon, LogicHost };
        Assert.IsEmpty(consumers.Except(permittedConsumers, StringComparer.Ordinal).ToArray(),
            $"Only {string.Join(" and ", permittedConsumers)} may reference {StorageFileSystem}: {string.Join(", ", consumers)}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ProductionTransitiveGraphDoesNotReachHostsOrTestProjects()
    {
        var repository = Repository.Value;
        var violations = repository.ValidateTransitiveBoundaries(CameraAgent, LogicHost);

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NativeOpenCallers_SelectDirectoryAndNoFollowFlagsPerArchitecture()
    {
        // O_DIRECTORY and O_NOFOLLOW are 0x10000/0x20000 on the asm-generic ABI (x86, loongarch, riscv, s390) but
        // 0x4000/0x8000 on arm and powerpc, so a hard-coded value silently breaks aarch64 while every x86-64 lane
        // stays green (#603). Any file that imports open(2)/openat(2) from libc must select the flags by
        // RuntimeInformation.ProcessArchitecture, either through a per-architecture table it declares itself or by
        // delegating to LinuxOpenFlags, and no importer outside the four declared tables may spell the values on a
        // line that talks about open flags. The sweep covers src/; tests and tools do not import libc open.
        // Storage.FileSystem (#592) carries the table the shared primitives use; #587 retires the CameraAgent copy
        // in favour of it, at which point the CameraAgent entry below and one caller leave this guard.
        var root = RepositoryGraph.FindRepositoryRoot();
        string[] tableFiles =
        [
            Path.Combine("src", "HVO.SkyMonitor.CameraAgent.Common", "Storage", "LinuxOpenFlags.cs"),
            Path.Combine("src", "HVO.SkyMonitor.Deployment.Cli", "NativeLinux.cs"),
            Path.Combine("src", "HVO.SkyMonitor.Catalog.Sqlite", "CatalogSnapshotResolver.cs"),
            Path.Combine("src", "HVO.SkyMonitor.Storage.FileSystem", "LinuxOpenFlags.cs"),
        ];
        var importPattern = new System.Text.RegularExpressions.Regex(
            """"libc"[^;]*(EntryPoint\s*=\s*"open(at)?"|\bopen(at)?\s*\()"""",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        // The flag values in every spelling a caller might reach for, checked only on lines that talk about open
        // flags so that a 64 KiB buffer constant elsewhere in the file is not mistaken for one.
        var literalPattern = new System.Text.RegularExpressions.Regex(
            """\b(0x0*(10000|20000|A0000)|65536|131072|655360)\b""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var flagContextPattern = new System.Text.RegularExpressions.Regex(
            """O_|open|flag|nofollow|directory|architecture""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var violations = new List<string>();
        var callers = 0;

        foreach (var table in tableFiles.Where(table => !File.Exists(Path.Combine(root, table))))
        {
            violations.Add($"{table}: the declared flag table file is missing");
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !RepositoryGraph.HasPathSegment(path, "bin") && !RepositoryGraph.HasPathSegment(path, "obj"))
                     .Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);
            var declaresTable = tableFiles.Contains(relative, StringComparer.Ordinal);
            if (declaresTable && !text.Contains("Architecture.Arm64", StringComparison.Ordinal))
            {
                violations.Add($"{relative}: the per-architecture flag table is missing");
            }

            if (!importPattern.IsMatch(text))
            {
                continue;
            }

            callers++;
            if (!declaresTable)
            {
                foreach (var line in text.Split('\n').Where(line => literalPattern.IsMatch(line) && flagContextPattern.IsMatch(line)))
                {
                    violations.Add($"{relative}: hard-codes an open(2) flag value: {line.Trim()}");
                }

                if (!text.Contains("LinuxOpenFlags.", StringComparison.Ordinal))
                {
                    violations.Add($"{relative}: imports libc open but does not select its flags per architecture");
                }
            }
        }

        // Six importers: RawIngressFileStore and the CameraAgent table's other callers, the deployment CLI, the
        // SQLite catalog resolver, and Storage.FileSystem's DurableSync (#592), which delegates to its own table.
        Assert.AreEqual(6, callers, "the set of libc open importers changed; update this guard deliberately");
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LogEventIdDeclarationGuardCoversEverySupportedForm()
    {
        var declarationPattern = LogEventIdDeclarationPattern();
        const string syntaxWitness = """
            [LoggerMessage(EventId = 9100, Level = LogLevel.Information)]
            [LoggerMessage(
                9101,
                LogLevel.Warning)]
            new EventId(
                9102,
                "explicit");
            private static readonly EventId TargetTyped = new(
                9103,
                "target-typed");
            // [LoggerMessage(9190, LogLevel.Error)]
            const string FalseExplicit = "new EventId(9191, ignored)";
            /* EventId FalseTargetTyped = new(9192, "ignored"); */
            """;
        CollectionAssert.AreEqual(
            new[] { 9100, 9101, 9102, 9103 },
            declarationPattern.Matches(MaskCSharpCommentsAndLiterals(syntaxWitness))
                .Select(match => int.Parse(
                    match.Groups["id"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToArray(),
            "The source guard must cover every production EventId declaration form without reading comments or strings.");
        const string interpolationWitness = """"
            var regular = $"{Echo("new EventId(9193, ignored)")}";
            var verbatim = $@"{Echo(@"[LoggerMessage(9194, ignored)]")}";
            var raw = $"""{Echo("EventId Target = new(9195, ignored)")}""";
            var doubleRaw = $$"""{{Echo("new EventId(9196, ignored)")}}""";
            """";
        Assert.AreEqual(
            0,
            declarationPattern.Count(MaskCSharpCommentsAndLiterals(interpolationWitness)),
            "Nested literals inside interpolations must not be exposed as EventId declarations.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CameraAgentLogEventIdsAreUniqueAcrossProductionSources()
        => AssertHostOwnedLogEventIdsAreUnique("CameraAgent", "HVO.SkyMonitor.CameraAgent");

    [TestMethod]
    [TestCategory("Unit")]
    public void LogicHostLogEventIdsAreUniqueAcrossProductionSources()
        => AssertHostOwnedLogEventIdsAreUnique("LogicHost", "HVO.SkyMonitor.LogicHost");

    private static System.Text.RegularExpressions.Regex LogEventIdDeclarationPattern()
        => new(
            """\b(?:EventId\s*=\s*|new\s+EventId\s*\(\s*|LoggerMessage\s*\(\s*|EventId\b[^;=]*=\s*new\s*\(\s*)(?<id>\d+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void AssertHostOwnedLogEventIdsAreUnique(string hostName, string projectDirectoryName)
    {
        var root = RepositoryGraph.FindRepositoryRoot();
        var declarationPattern = LogEventIdDeclarationPattern();
        var declarations = new List<(int Id, string Path, int Line)>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !RepositoryGraph.HasPathSegment(path, "bin") && !RepositoryGraph.HasPathSegment(path, "obj"))
                     .Order(StringComparer.Ordinal))
        {
            var source = MaskCSharpCommentsAndLiterals(File.ReadAllText(file));
            foreach (System.Text.RegularExpressions.Match match in declarationPattern.Matches(source))
            {
                var line = 1;
                for (var index = 0; index < match.Index; index++)
                {
                    if (source[index] == '\n')
                    {
                        line++;
                    }
                }

                declarations.Add((
                    int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Path.GetRelativePath(root, file),
                    line));
            }
        }

        var ownedPrefix = Path.Combine("src", projectDirectoryName);
        var duplicates = declarations
            .GroupBy(declaration => declaration.Id)
            .Where(group => group.Count() > 1)
            .Where(group => group.Any(declaration => declaration.Path.StartsWith(ownedPrefix, StringComparison.Ordinal)))
            .OrderBy(group => group.Key)
            .Select(group => $"EventId {group.Key}: {string.Join(", ", group.Select(declaration => $"{declaration.Path}:{declaration.Line}"))}")
            .ToArray();

        Assert.IsEmpty(
            duplicates,
            $"Every {hostName} log event ID must be unique across src/:{Environment.NewLine}{string.Join(Environment.NewLine, duplicates)}");
    }

    private static string MaskCSharpCommentsAndLiterals(string source)
    {
        var masked = source.ToCharArray();

        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                var end = SkipLineComment(source, index);
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = SkipBlockComment(source, index);
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (source[index] == '\'')
            {
                var end = SkipCharacterLiteral(source, index);
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (TrySkipStringLiteral(source, index, out var stringEnd))
            {
                MaskRange(masked, index, stringEnd);
                index = stringEnd;
                continue;
            }

            index++;
        }

        return new string(masked);
    }

    private static bool TrySkipStringLiteral(string source, int start, out int end)
    {
        var quoteIndex = start;
        var dollarCount = 0;
        var verbatim = false;

        if (source[start] == '@')
        {
            verbatim = true;
            quoteIndex++;
            if (quoteIndex < source.Length && source[quoteIndex] == '$')
            {
                dollarCount = 1;
                quoteIndex++;
            }
        }
        else if (source[start] == '$')
        {
            while (quoteIndex < source.Length && source[quoteIndex] == '$')
            {
                dollarCount++;
                quoteIndex++;
            }

            if (quoteIndex < source.Length && source[quoteIndex] == '@')
            {
                verbatim = true;
                quoteIndex++;
            }
        }

        if (quoteIndex >= source.Length || source[quoteIndex] != '"')
        {
            end = start;
            return false;
        }

        var quoteCount = CountRun(source, quoteIndex, '"');
        if (quoteCount >= 3)
        {
            if (verbatim)
            {
                end = start;
                return false;
            }

            end = SkipRawStringLiteral(source, quoteIndex, quoteCount, dollarCount);
            return true;
        }

        if (dollarCount > 1)
        {
            end = start;
            return false;
        }

        end = SkipQuotedStringLiteral(source, quoteIndex, dollarCount == 1, verbatim);
        return true;
    }

    private static int SkipQuotedStringLiteral(string source, int quoteIndex, bool interpolated, bool verbatim)
    {
        var index = quoteIndex + 1;
        while (index < source.Length)
        {
            if (!verbatim && source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
                continue;
            }

            if (source[index] == '"')
            {
                if (verbatim && index + 1 < source.Length && source[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            if (interpolated && source[index] == '{')
            {
                if (index + 1 < source.Length && source[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                index = SkipInterpolationExpression(source, index + 1, 1);
                continue;
            }

            index++;
        }

        return source.Length;
    }

    private static int SkipRawStringLiteral(
        string source,
        int quoteIndex,
        int quoteCount,
        int dollarCount)
    {
        var index = quoteIndex + quoteCount;
        while (index < source.Length)
        {
            if (source[index] == '"' && CountRun(source, index, '"') >= quoteCount)
            {
                return index + quoteCount;
            }

            if (dollarCount > 0 && source[index] == '{')
            {
                var braceCount = CountRun(source, index, '{');
                if (braceCount >= dollarCount)
                {
                    index = SkipInterpolationExpression(source, index + dollarCount, dollarCount);
                    continue;
                }

                index += braceCount;
                continue;
            }

            index++;
        }

        return source.Length;
    }

    private static int SkipInterpolationExpression(string source, int start, int closingBraceCount)
    {
        var index = start;
        var nestedBraces = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index = SkipLineComment(source, index);
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index = SkipBlockComment(source, index);
                continue;
            }

            if (source[index] == '\'')
            {
                index = SkipCharacterLiteral(source, index);
                continue;
            }

            if (TrySkipStringLiteral(source, index, out var stringEnd))
            {
                index = stringEnd;
                continue;
            }

            if (source[index] == '{')
            {
                nestedBraces++;
                index++;
                continue;
            }

            if (source[index] != '}')
            {
                index++;
                continue;
            }

            var braceRun = CountRun(source, index, '}');
            while (nestedBraces > 0 && braceRun > 0)
            {
                nestedBraces--;
                braceRun--;
                index++;
            }

            if (nestedBraces == 0 && braceRun >= closingBraceCount)
            {
                return index + closingBraceCount;
            }

            index += braceRun;
        }

        return source.Length;
    }

    private static int SkipLineComment(string source, int start)
    {
        var index = start + 2;
        while (index < source.Length && source[index] is not ('\r' or '\n'))
        {
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string source, int start)
    {
        var index = start + 2;
        while (index + 1 < source.Length && (source[index] != '*' || source[index + 1] != '/'))
        {
            index++;
        }

        return Math.Min(source.Length, index + 2);
    }

    private static int SkipCharacterLiteral(string source, int start)
    {
        var index = start + 1;
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
            }
            else if (source[index++] == '\'')
            {
                break;
            }
        }

        return index;
    }

    private static int CountRun(string source, int start, char value)
    {
        var index = start;
        while (index < source.Length && source[index] == value)
        {
            index++;
        }

        return index - start;
    }

    private static void MaskRange(char[] source, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (source[index] is not ('\r' or '\n'))
            {
                source[index] = ' ';
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EveryForbiddenProductionPairIsRejectedByTheAllowlist()
    {
        foreach (var source in AllowedProductionReferences.Keys)
        {
            foreach (var target in AllowedProductionReferences.Keys.Where(target => target != source))
            {
                if (!AllowedProductionReferences[source].Contains(target))
                {
                    AssertForbiddenSyntheticReference(source, target, ProjectKind.Production);
                }
            }

            AssertForbiddenSyntheticReference(source, TestSupport, ProjectKind.Test);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SyntheticTransitiveHostAndTestSupportPathsAreRejected()
    {
        var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal)
        {
            [CameraAgent] = Project(CameraAgent, ProjectKind.Production, CameraAgentCommon),
            [CameraAgentCommon] = Project(CameraAgentCommon, ProjectKind.Production, LogicHost),
            [LogicHost] = Project(LogicHost, ProjectKind.Production, Common),
            [Common] = Project(Common, ProjectKind.Production, TestSupport),
            [TestSupport] = Project(TestSupport, ProjectKind.Test)
        };
        var graph = new RepositoryGraph(string.Empty, projects);

        var violations = graph.ValidateTransitiveBoundaries(CameraAgent, LogicHost);

        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"{CameraAgent} -> {CameraAgentCommon} -> {LogicHost}", StringComparison.Ordinal)));
        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"{LogicHost} -> {Common} -> {TestSupport}", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void AgentCoreHasNoHostInfrastructureOrImagingDependencies()
    {
        var repository = Repository.Value;
        var agentCore = repository.Projects[AgentCore];
        var forbiddenNames = new[]
        {
            "AspNetCore", "EntityFramework", "Sqlite", "Minio", "SkiaSharp", "OpenIddict", "StackExchange.Redis"
        };
        var dependencies = agentCore.PackageReferences
            .Concat(agentCore.FrameworkReferences)
            .Where(dependency => forbiddenNames.Any(forbidden => dependency.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(dependencies,
            $"ARCH-AGENTCORE: '{AgentCore}' must remain transport-neutral but references host infrastructure or imaging dependencies: {string.Join(", ", dependencies)}.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void LogicHostProviderSdkUsageIsConfinedToObjectStorageInfrastructure()
    {
        var logicHostRoot = Path.Combine(Repository.Value.Root, "src", LogicHost);
        var providerRoot = Path.Combine(logicHostRoot, "Infrastructure", "ObjectStorage")
            + Path.DirectorySeparatorChar;
        var violations = Directory.EnumerateFiles(logicHostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(providerRoot, StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("using Minio", StringComparison.Ordinal)
                    || source.Contains("using Amazon.S3", StringComparison.Ordinal)
                    || source.Contains("using Amazon.Runtime", StringComparison.Ordinal)
                    || source.Contains("IAmazonS3", StringComparison.Ordinal)
                    || source.Contains("AmazonS3Client", StringComparison.Ordinal)
                    || source.Contains("IMinioClient", StringComparison.Ordinal)
                    || source.Contains("MinioClient", StringComparison.Ordinal)
                    || source.Contains("MinioException", StringComparison.Ordinal)
                    || source.Contains("AWSCredentials", StringComparison.Ordinal)
                    || source.Contains("FallbackCredentialsFactory", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(Repository.Value.Root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(violations,
            $"Provider SDK usage must remain under LogicHost/Infrastructure/ObjectStorage:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void EvaluatedGraphIncludesImportedReferencesAndExcludesDisabledConditions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"skymonitor-architecture-evaluation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var importedProject = Path.Combine(directory, "Imported.csproj");
            var debugProject = Path.Combine(directory, "DebugOnly.csproj");
            File.WriteAllText(importedProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(debugProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(directory, "references.props"),
                "<Project><ItemGroup><ProjectReference Include=\"Imported.csproj\" /></ItemGroup></Project>");
            var sourceProject = Path.Combine(directory, "Source.csproj");
            File.WriteAllText(sourceProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"references.props\" />" +
                "<ItemGroup Condition=\"'$(Configuration)' == 'Debug'\"><ProjectReference Include=\"DebugOnly.csproj\" /></ItemGroup></Project>");

            var releaseEvaluation = RepositoryGraph.EvaluateProject(sourceProject, "Release");
            var debugEvaluation = RepositoryGraph.EvaluateProject(sourceProject, "Debug");

            Assert.ContainsSingle(releaseEvaluation.ProjectReferencePaths, Path.GetFullPath(importedProject));
            CollectionAssert.AreEquivalent(
                new[] { Path.GetFullPath(importedProject), Path.GetFullPath(debugProject) },
                debugEvaluation.ProjectReferencePaths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TestSupportDirectionRejectsNonTestConsumersAndNonProductionDependencies()
    {
        const string tool = "Architecture.Tool";
        var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal)
        {
            [AgentCore] = Project(AgentCore, ProjectKind.Production),
            [TestSupport] = Project(TestSupport, ProjectKind.Test, tool),
            [tool] = Project(tool, ProjectKind.Tool, TestSupport)
        };
        var graph = new RepositoryGraph(string.Empty, projects);

        var violations = graph.ValidateProductionReferences(
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal) { [AgentCore] = Set() });

        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"'{TestSupport}' references non-production dependency '{tool}'", StringComparison.Ordinal)));
        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"Non-test project '{tool}' references '{TestSupport}'", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task HostReleasePublishOutputsContainNoTestAssemblies()
    {
        var repository = Repository.Value;
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_PUBLISH_EVIDENCE_ROOT");
        var testAssemblies = repository.Projects.Values
            .Where(project => project.Kind == ProjectKind.Test)
            .Select(project => project.AssemblyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hostName in new[] { CameraAgent, LogicHost, CameraAgentReplayRunner, ProcessingRunner })
        {
            var output = string.IsNullOrWhiteSpace(evidenceRoot)
                ? Path.Combine(Path.GetTempPath(), $"skymonitor-architecture-{hostName}-{Guid.NewGuid():N}")
                : hostName switch
                {
                    CameraAgent => Path.Combine(evidenceRoot, "cameraagent"),
                    LogicHost => Path.Combine(evidenceRoot, "logichost"),
                    ProcessingRunner => Path.Combine(evidenceRoot, "processing-runner", "linux-x64"),
                    _ => Path.Combine(evidenceRoot, "replay-runner", "linux-x64")
                };
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            try
            {
                await PublishAsync(
                    repository.Root,
                    repository.Projects[hostName].Path,
                    output,
                    hostName is CameraAgentReplayRunner or ProcessingRunner ? "linux-x64" : null).ConfigureAwait(false);
                var forbiddenAssemblies = testAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var forbiddenHost in new[] { CameraAgent, LogicHost, CameraAgentReplayRunner, ProcessingRunner }.Where(
                    candidate => candidate != hostName))
                {
                    forbiddenAssemblies.Add(repository.Projects[forbiddenHost].AssemblyName);
                }
                var violations = FindForbiddenPublishArtifacts(output, forbiddenAssemblies);
                Assert.IsEmpty(violations,
                    $"ARCH-PUBLISH: Release publish for '{hostName}' contains test or opposite-host artifacts:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
            }
            finally
            {
                if (string.IsNullOrWhiteSpace(evidenceRoot))
                {
                    Directory.Delete(output, recursive: true);
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HostDockerfilesDoNotPackageTestCatalogFixtures()
    {
        var repositoryRoot = Repository.Value.Root;

        foreach (var hostName in new[] { CameraAgent, LogicHost })
        {
            var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "src", hostName, "Dockerfile"));
            Assert.IsFalse(dockerfile.Contains("tests/fixtures/catalog", StringComparison.OrdinalIgnoreCase),
                $"ARCH-PUBLISH: '{hostName}' Dockerfile must not package a test catalog fixture.");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void PublishScannerReportsTestAssemblyFilesAndDependencyEntries()
    {
        var output = Path.Combine(Path.GetTempPath(), $"skymonitor-architecture-scanner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);

        try
        {
            File.WriteAllBytes(Path.Combine(output, $"{TestSupport}.dll"), []);
            var testAssembly = typeof(ArchitectureBoundaryTests).Assembly;
            var testAssemblyName = testAssembly.GetName().Name!;
            File.Copy(testAssembly.Location, Path.Combine(output, "renamed-test.dll"));
            File.Copy(
                Path.Combine(Repository.Value.Root, "tests", "fixtures", "catalog", "hyg-v42-bright-stars.sqlite"),
                Path.Combine(output, "renamed-catalog.dat"));
            File.WriteAllText(Path.Combine(output, "catalog-package.json"),
                """{"package":{"kind":"fixture"}}""");
            File.WriteAllText(Path.Combine(output, "host.deps.json"),
                JsonSerializer.Serialize(new
                {
                    libraries = new Dictionary<string, object>
                    {
                        [$"{TestSupport}/1.0.0"] = new { type = "project" },
                        ["MSTest/4.1.0"] = new { type = "package" }
                    }
                }));

            var violations = FindForbiddenPublishArtifacts(output, Set(TestSupport, testAssemblyName));

            Assert.HasCount(6, violations);
            Assert.IsTrue(violations.Any(violation => violation.Contains(testAssemblyName, StringComparison.Ordinal)));
            Assert.IsTrue(violations.Any(violation => violation.Contains("MSTest", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static void AssertForbiddenSyntheticReference(string source, string target, ProjectKind targetKind)
    {
        var projects = AllowedProductionReferences.Keys.ToDictionary(
            name => name,
            name => new ProjectInfo(
                name,
                name,
                $"{name}.csproj",
                ProjectKind.Production,
                name == source ? [target] : [],
                [],
                []),
            StringComparer.Ordinal);
        if (!projects.ContainsKey(target))
        {
            projects.Add(target, new ProjectInfo(target, target, $"{target}.csproj", targetKind, [], [], []));
        }

        var graph = new RepositoryGraph(string.Empty, projects);
        var violations = graph.ValidateProductionReferences(AllowedProductionReferences);

        Assert.IsTrue(
            violations.Any(violation => violation.Contains($"'{source}'", StringComparison.Ordinal) &&
                violation.Contains($"'{target}'", StringComparison.Ordinal)),
            $"ARCH-DIRECT: Synthetic forbidden edge '{source}' -> '{target}' was not rejected.");
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static ProjectInfo Project(string name, ProjectKind kind, params string[] references) =>
        new(name, name, $"{name}.csproj", kind, references, [], []);

    private static async Task PublishAsync(
        string root,
        string projectPath,
        string output,
        string? runtimeIdentifier = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("publish");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add("Release");
        process.StartInfo.ArgumentList.Add("--no-restore");
        if (runtimeIdentifier is not null)
        {
            process.StartInfo.ArgumentList.Add("--runtime");
            process.StartInfo.ArgumentList.Add(runtimeIdentifier);
        }
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add(output);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        Assert.AreEqual(0, process.ExitCode,
            $"ARCH-PUBLISH: dotnet publish failed for '{projectPath}'.{Environment.NewLine}{await standardOutput.ConfigureAwait(false)}{Environment.NewLine}{await standardError.ConfigureAwait(false)}");
    }

    private static string[] FindForbiddenPublishArtifacts(string output, HashSet<string> forbiddenAssemblies)
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            foreach (var assembly in forbiddenAssemblies)
            {
                if (string.Equals(fileName, assembly, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{assembly}.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{assembly}.pdb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{assembly}.deps.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{assembly}.runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{assembly}.xml", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"File '{Path.GetRelativePath(output, file)}' is produced by forbidden project '{assembly}'.");
                }
            }

            if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyName = ReadManagedAssemblyName(file);
                if (assemblyName is not null && forbiddenAssemblies.Contains(assemblyName) &&
                    !violations.Any(violation => violation.Contains($"'{Path.GetRelativePath(output, file)}'", StringComparison.Ordinal)))
                {
                    violations.Add($"File '{Path.GetRelativePath(output, file)}' contains forbidden managed assembly '{assemblyName}'.");
                }
            }

            if (new FileInfo(file).Length == 16_384 &&
                string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                    FixtureCatalogSha256, StringComparison.Ordinal))
            {
                violations.Add($"File '{Path.GetRelativePath(output, file)}' contains the test catalog fixture.");
            }

            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && ContainsFixtureCatalogManifest(file))
            {
                violations.Add($"File '{Path.GetRelativePath(output, file)}' contains a fixture catalog manifest.");
            }

            if (!fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            {
                continue;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                var separator = library.Name.IndexOf('/', StringComparison.Ordinal);
                var libraryName = separator < 0 ? library.Name : library.Name[..separator];
                if (forbiddenAssemblies.Contains(libraryName) || IsTestOnlyPackage(libraryName))
                {
                    violations.Add($"Dependency manifest '{fileName}' contains forbidden dependency '{libraryName}'.");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool ContainsFixtureCatalogManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("package", out var package) &&
                package.ValueKind == JsonValueKind.Object &&
                package.TryGetProperty("kind", out var kind) &&
                string.Equals(kind.GetString(), "fixture", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadManagedAssemblyName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return null;
            }

            return metadata.GetString(metadata.GetAssemblyDefinition().Name);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsTestOnlyPackage(string packageName) =>
        TestOnlyPackagePrefixes.Any(prefix => packageName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            packageName.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase));

    private enum ProjectKind
    {
        Production,
        Test,
        Tool
    }

    private sealed record ProjectInfo(
        string Name,
        string AssemblyName,
        string Path,
        ProjectKind Kind,
        IReadOnlyList<string> References,
        IReadOnlyList<string> PackageReferences,
        IReadOnlyList<string> FrameworkReferences);

    private sealed class RepositoryGraph
    {
        internal RepositoryGraph(string root, IReadOnlyDictionary<string, ProjectInfo> projects)
        {
            Root = root;
            Projects = projects;
        }

        public string Root { get; }

        public IReadOnlyDictionary<string, ProjectInfo> Projects { get; }

        public static RepositoryGraph Load()
        {
            var root = FindRepositoryRoot();
            var projectPaths = ProjectSearchDirectories
                .SelectMany(directory => Directory.EnumerateFiles(
                    Path.Combine(root, directory),
                    "*.csproj",
                    SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly))
                .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var pathsByName = projectPaths.ToDictionary(
                static path => Path.GetFileNameWithoutExtension(path)!,
                StringComparer.Ordinal);
            var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);

            foreach (var path in projectPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path)!;
                var relativePath = Path.GetRelativePath(root, path);
                var evaluations = SupportedConfigurations
                    .Select(configuration => EvaluateProject(path, configuration))
                    .ToArray();
                var evaluation = evaluations[^1];
                var kind = relativePath.StartsWith($"src{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && name != TestSupport
                    ? ProjectKind.Production
                    : relativePath.StartsWith($"tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || name == TestSupport
                        ? ProjectKind.Test
                        : ProjectKind.Tool;
                var references = evaluations.SelectMany(item => item.ProjectReferencePaths)
                    .Distinct(StringComparer.Ordinal)
                    .Select(reference => ResolveReference(reference, pathsByName))
                    .ToArray();

                projects.Add(name, new ProjectInfo(
                    name,
                    evaluation.AssemblyName,
                    path,
                    kind,
                    references,
                    evaluations.SelectMany(item => item.PackageReferences).Distinct(StringComparer.Ordinal).ToArray(),
                    evaluations.SelectMany(item => item.FrameworkReferences).Distinct(StringComparer.Ordinal).ToArray()));
            }

            return new RepositoryGraph(root, projects);
        }

        public string[] ValidateProductionReferences(
            IReadOnlyDictionary<string, IReadOnlySet<string>> allowedReferences)
        {
            var violations = new List<string>();
            foreach (var required in allowedReferences.Keys.Where(name => !Projects.ContainsKey(name)))
            {
                violations.Add($"ARCH-MISSING: Documented production project '{required}' was not found.");
            }

            foreach (var project in Projects.Values.Where(project => project.Kind == ProjectKind.Production))
            {
                if (!allowedReferences.TryGetValue(project.Name, out var allowed))
                {
                    violations.Add($"ARCH-CLASSIFY: Production project '{project.Name}' is not in the documented graph.");
                    continue;
                }

                foreach (var reference in project.References)
                {
                    if (!Projects.TryGetValue(reference, out var target) || target.Kind != ProjectKind.Production || !allowed.Contains(reference))
                    {
                        violations.Add($"ARCH-DIRECT: Consumer '{project.Name}' references forbidden dependency '{reference}'.");
                    }
                }


                foreach (var package in project.PackageReferences.Where(IsTestOnlyPackage))
                {
                    violations.Add($"ARCH-PACKAGE: Production project '{project.Name}' references test-only package '{package}'.");
                }
            }

            if (Projects.TryGetValue(TestSupport, out var testSupport))
            {
                foreach (var reference in testSupport.References.Where(reference =>
                    !Projects.TryGetValue(reference, out var target) || target.Kind != ProjectKind.Production))
                {
                    violations.Add($"ARCH-TESTSUPPORT: '{TestSupport}' references non-production dependency '{reference}'.");
                }

                foreach (var project in Projects.Values.Where(project =>
                    project.Kind != ProjectKind.Test && project.References.Contains(TestSupport, StringComparer.Ordinal)))
                {
                    violations.Add($"ARCH-TESTSUPPORT: Non-test project '{project.Name}' references '{TestSupport}'.");
                }
            }

            return violations.Order(StringComparer.Ordinal).ToArray();
        }

        public string[] ValidateTransitiveBoundaries(string cameraAgent, string logicHost)
        {
            var violations = new List<string>();
            foreach (var source in Projects.Values.Where(project => project.Kind == ProjectKind.Production))
            {
                foreach (var path in FindReachablePaths(source.Name))
                {
                    var target = Projects[path[^1]];
                    var crossesHostBoundary = source.Name == cameraAgent && target.Name == logicHost ||
                        source.Name == logicHost && target.Name == cameraAgent;
                    if (target.Kind != ProjectKind.Production || crossesHostBoundary)
                    {
                        violations.Add($"ARCH-TRANSITIVE: Consumer '{source.Name}' reaches forbidden dependency '{target.Name}' through {string.Join(" -> ", path)}.");
                    }
                }
            }

            return violations.Order(StringComparer.Ordinal).ToArray();
        }

        public string[] ValidateTestOwnership(
            IReadOnlySet<string> sharedProjects,
            IReadOnlySet<string> cameraAgentProjects,
            IReadOnlySet<string> logicHostProjects,
            IReadOnlySet<string> combinedProjects,
            string logicHostInfrastructure)
        {
            var violations = new List<string>();
            var classified = sharedProjects
                .Concat(cameraAgentProjects)
                .Concat(logicHostProjects)
                .Concat(combinedProjects)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var projectName in classified.Append(logicHostInfrastructure).Where(name => !Projects.ContainsKey(name)))
            {
                violations.Add($"ARCH-TEST-MISSING: Documented test project '{projectName}' was not found.");
            }

            foreach (var project in Projects.Values.Where(project =>
                project.Kind == ProjectKind.Test &&
                project.Name != TestSupport &&
                project.Name != logicHostInfrastructure &&
                !classified.Contains(project.Name)))
            {
                violations.Add($"ARCH-TEST-CLASSIFY: Test project '{project.Name}' has no documented owner.");
            }

            foreach (var projectName in classified.Where(Projects.ContainsKey))
            {
                var project = Projects[projectName];
                foreach (var reference in project.References.Where(reference =>
                    Projects.TryGetValue(reference, out var target) &&
                    target.Kind == ProjectKind.Test &&
                    reference != TestSupport &&
                    reference != logicHostInfrastructure))
                {
                    violations.Add($"ARCH-TEST-ASSEMBLY: Test project '{projectName}' references test assembly '{reference}'.");
                }

                var reachable = FindReachablePaths(projectName)
                    .Select(path => path[^1])
                    .ToHashSet(StringComparer.Ordinal);
                var reachesCameraAgent = reachable.Overlaps(CameraAgentImplementations);
                var reachesLogicHost = reachable.Contains(LogicHost);

                if (sharedProjects.Contains(projectName) && (reachesCameraAgent || reachesLogicHost))
                {
                    violations.Add($"ARCH-TEST-SHARED: Shared test project '{projectName}' reaches a host implementation.");
                }
                if (cameraAgentProjects.Contains(projectName) && reachesLogicHost)
                {
                    violations.Add($"ARCH-TEST-CAMERA: CameraAgent test project '{projectName}' reaches '{LogicHost}'.");
                }
                if (logicHostProjects.Contains(projectName) && reachesCameraAgent)
                {
                    violations.Add($"ARCH-TEST-LOGIC: LogicHost test project '{projectName}' reaches a CameraAgent implementation.");
                }
                if (!combinedProjects.Contains(projectName) && reachesCameraAgent && reachesLogicHost)
                {
                    violations.Add($"ARCH-TEST-COMBINED: Non-combined test project '{projectName}' reaches both hosts.");
                }
            }

            return violations.Order(StringComparer.Ordinal).ToArray();
        }

        private IEnumerable<IReadOnlyList<string>> FindReachablePaths(string source)
        {
            var queue = new Queue<IReadOnlyList<string>>();
            queue.Enqueue([source]);
            var visited = new HashSet<string>(StringComparer.Ordinal) { source };

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                foreach (var reference in Projects[path[^1]].References)
                {
                    if (!Projects.ContainsKey(reference) || !visited.Add(reference))
                    {
                        continue;
                    }

                    var next = path.Append(reference).ToArray();
                    queue.Enqueue(next);
                    yield return next;
                }
            }
        }

        internal static ProjectEvaluation EvaluateProject(string projectPath, string configuration)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("msbuild");
            process.StartInfo.ArgumentList.Add(projectPath);
            process.StartInfo.ArgumentList.Add("-nologo");
            process.StartInfo.ArgumentList.Add($"-property:Configuration={configuration}");
            process.StartInfo.ArgumentList.Add("-getProperty:AssemblyName");
            process.StartInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference,FrameworkReference");

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ARCH-EVALUATE: MSBuild evaluation failed for '{projectPath}'.{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var assemblyName = root.GetProperty("Properties").GetProperty("AssemblyName").GetString()
                ?? Path.GetFileNameWithoutExtension(projectPath)!;
            var items = root.GetProperty("Items");
            var projectReferences = GetItemValues(items, "ProjectReference", "FullPath");
            var packageReferences = GetItemValues(items, "PackageReference", "Identity");
            var frameworkReferences = items.GetProperty("FrameworkReference")
                .EnumerateArray()
                .Where(item => !item.TryGetProperty("IsImplicitlyDefined", out var implicitValue) ||
                    !string.Equals(implicitValue.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.GetProperty("Identity").GetString()!)
                .ToArray();

            return new ProjectEvaluation(assemblyName, projectReferences, packageReferences, frameworkReferences);
        }

        private static string[] GetItemValues(JsonElement items, string itemName, string propertyName) =>
            items.GetProperty(itemName)
                .EnumerateArray()
                .Select(item => item.GetProperty(propertyName).GetString()!)
                .ToArray();

        private static string ResolveReference(string resolved, Dictionary<string, string> pathsByName)
        {
            var name = Path.GetFileNameWithoutExtension(resolved);
            return pathsByName.TryGetValue(name, out var knownPath) && string.Equals(resolved, knownPath, StringComparison.Ordinal)
                ? name
                : $"<unknown:{resolved}>";
        }

        internal static bool HasPathSegment(string path, string segment) =>
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(segment, StringComparer.OrdinalIgnoreCase);

        internal static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Repository root was not found.");
        }

        internal sealed record ProjectEvaluation(
            string AssemblyName,
            string[] ProjectReferencePaths,
            string[] PackageReferences,
            string[] FrameworkReferences);
    }
}
