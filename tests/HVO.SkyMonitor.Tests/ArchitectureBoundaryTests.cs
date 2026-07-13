using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    private const string AgentCore = "HVO.SkyMonitor.AgentCore";
    private const string Astronomy = "HVO.SkyMonitor.Astronomy";
    private const string Imaging = "HVO.SkyMonitor.Imaging";
    private const string Processing = "HVO.SkyMonitor.Processing";
    private const string Catalog = "HVO.SkyMonitor.Catalog.Sqlite";
    private const string Common = "HVO.SkyMonitor.Common";
    private const string CameraAgentCommon = "HVO.SkyMonitor.CameraAgent.Common";
    private const string CameraAgent = "HVO.SkyMonitor.CameraAgent";
    private const string LogicHost = "HVO.SkyMonitor.LogicHost";
    private const string TestSupport = "HVO.SkyMonitor.TestSupport";

    private static readonly string[] ProjectSearchDirectories = ["src", "tests", "scripts"];

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
            [Processing] = Set(AgentCore, Astronomy, Imaging),
            [Catalog] = Set(Astronomy),
            [Common] = Set(),
            [CameraAgentCommon] = Set(AgentCore, Astronomy, Imaging, Processing),
            [CameraAgent] = Set(CameraAgentCommon, Catalog, Common),
            [LogicHost] = Set(AgentCore, Astronomy, Imaging, Processing, Catalog, Common)
        };

    [TestMethod]
    public void ProductionProjectReferencesFollowDocumentedGraph()
    {
        var repository = Repository.Value;
        var violations = repository.ValidateProductionReferences(AllowedProductionReferences);

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void ProductionTransitiveGraphDoesNotReachHostsOrTestProjects()
    {
        var repository = Repository.Value;
        var violations = repository.ValidateTransitiveBoundaries(CameraAgent, LogicHost);

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
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
    public async Task HostReleasePublishOutputsContainNoTestAssemblies()
    {
        var repository = Repository.Value;
        var testAssemblies = repository.Projects.Values
            .Where(project => project.Kind == ProjectKind.Test)
            .Select(project => project.AssemblyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hostName in new[] { CameraAgent, LogicHost })
        {
            var output = Path.Combine(Path.GetTempPath(), $"skymonitor-architecture-{hostName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(output);

            try
            {
                await PublishAsync(repository.Root, repository.Projects[hostName].Path, output).ConfigureAwait(false);
                var forbiddenAssemblies = testAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase);
                forbiddenAssemblies.Add(repository.Projects[hostName == CameraAgent ? LogicHost : CameraAgent].AssemblyName);
                var violations = FindForbiddenPublishArtifacts(output, forbiddenAssemblies);
                Assert.IsEmpty(violations,
                    $"ARCH-PUBLISH: Release publish for '{hostName}' contains test or opposite-host artifacts:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
            }
            finally
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [TestMethod]
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

            Assert.HasCount(4, violations);
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

    private static async Task PublishAsync(string root, string projectPath, string output)
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
                    fileName.StartsWith($"{assembly}.", StringComparison.OrdinalIgnoreCase))
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
            foreach (var required in allowedReferences.Keys.Where(name => name != Processing && !Projects.ContainsKey(name)))
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

        private static bool HasPathSegment(string path, string segment) =>
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(segment, StringComparer.OrdinalIgnoreCase);

        private static string FindRepositoryRoot()
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
