using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public sealed partial class ArchitectureBoundaryTests
{
    public TestContext TestContext { get; set; } = null!;
    private const string AgentCore = "HVO.SkyMonitor.AgentCore";
    private const string Astronomy = "HVO.SkyMonitor.Astronomy";
    private const string Imaging = "HVO.SkyMonitor.Imaging";
    private const string FleetContracts = "HVO.SkyMonitor.Fleet.Contracts";
    private const string Processing = "HVO.SkyMonitor.Processing";
    private const string VideoFFmpeg = "HVO.SkyMonitor.Video.FFmpeg";
    private const string Catalog = "HVO.SkyMonitor.Catalog.Sqlite";
    private const string Common = "HVO.SkyMonitor.Common";
    private const string CameraAgentCommon = "HVO.SkyMonitor.CameraAgent.Common";
    private const string CameraAgentZwo = "HVO.SkyMonitor.CameraAgent.Modules.Zwo";
    private const string CameraAgentReplay = "HVO.SkyMonitor.CameraAgent.Replay";
    private const string CameraAgentReplayRunner = "HVO.SkyMonitor.CameraAgent.ReplayRunner";

    // hvo-sky (#1317), CameraAgent's runtime client: a separate executable that reaches CameraAgent only over its HTTP
    // operations API, so it references no host project; its terminal interface, 'hvo-sky ui', draws with TerminalUi.
    private const string CameraAgentCli = "HVO.SkyMonitor.CameraAgent.Cli";
    private const string ProcessingRunnerContracts = "HVO.SkyMonitor.ProcessingRunner.Contracts";
    private const string ProcessingRunner = "HVO.SkyMonitor.ProcessingRunner";
    private const string CameraAgent = "HVO.SkyMonitor.CameraAgent";
    private const string DeploymentContracts = "HVO.SkyMonitor.Deployment.Contracts";
    private const string DeploymentCli = "HVO.SkyMonitor.Deployment.Cli";

    // What the terminal programs share (#1317): HVO Dark for Terminal.Gui, used by the installer's wizard and hvo-sky.
    // Its dependency rule is asserted by TerminalUiBoundaryIsNarrow.
    private const string TerminalUi = "HVO.SkyMonitor.TerminalUi";

    // Host-neutral filesystem primitives (#592): mechanisms only, references nothing, consumed
    // by CameraAgent.Common and Video.FFmpeg. Its
    // dependency rule is asserted by StorageFileSystemBoundaryIsNarrowWhenPresent.
    private const string StorageFileSystem = "HVO.SkyMonitor.Storage.FileSystem";
    private const string TestSupport = "HVO.SkyMonitor.TestSupport";
    private const string FixtureCatalogSha256 = "F80689217769A6B13C1B9BFB9711485D3CB1AD8DE009D3D6B0F0B0A4F1FA9840";

    private static readonly string[] ProjectSearchDirectories = ["src", "tests", "scripts", "tools"];

    private static readonly IReadOnlySet<string> HostProjects = Set(CameraAgent, CameraAgentReplayRunner, ProcessingRunner);

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

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedProductionReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [AgentCore] = Set(),
            [Astronomy] = Set(AgentCore),
            [Imaging] = Set(AgentCore, Astronomy),
            [FleetContracts] = Set(),
            [Processing] = Set(AgentCore, Astronomy, Imaging),
            [VideoFFmpeg] = Set(Processing, StorageFileSystem),
            [Catalog] = Set(Astronomy),
            [Common] = Set(),
            [StorageFileSystem] = Set(),
            [CameraAgentCommon] = Set(DeploymentContracts, AgentCore, Astronomy, Imaging, Processing, VideoFFmpeg, FleetContracts, CameraAgentReplay, StorageFileSystem),
            [CameraAgentZwo] = Set(AgentCore),
            [CameraAgentReplay] = Set(AgentCore, Processing),
            [CameraAgentReplayRunner] = Set(Processing, CameraAgentReplay),
            [CameraAgentCli] = Set(TerminalUi),
            [ProcessingRunnerContracts] = Set(AgentCore, Processing),
            [ProcessingRunner] = Set(AgentCore, Processing, ProcessingRunnerContracts),
            [CameraAgent] = Set(CameraAgentCommon, CameraAgentZwo, Catalog, Common),
            [DeploymentContracts] = Set(),
            [TerminalUi] = Set(),
            // The installer verifies releases by release.json and SHA256SUMS (#1309); no project holds a signing trust
            // root since the signed manifests were retired (#1310). Its wizard draws with TerminalUi (#1317).
            [DeploymentCli] = Set(AgentCore, Catalog, DeploymentContracts, TerminalUi)
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

    private static readonly string[] ShippedSourceRoots = ["src", "tools"];

    private static readonly IReadOnlySet<string> CameraAgentImplementations = Set(
        CameraAgent,
        CameraAgentCli,
        CameraAgentCommon,
        CameraAgentZwo,
        CameraAgentReplay,
        CameraAgentReplayRunner);

    /// <summary>
    /// The key-signed manifests and indexes are retired (owner decision 2026-10-16, #1310): no project embeds a signing
    /// trust root, and none references the signed distribution layer that held one. A release is trusted by SHA256SUMS
    /// and its build provenance attestations.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void CustomScriptsAndOffSweepWorkerReferenceNoCaptureOrAcquisitionServices()
    {
        var result = snapshot.ProductionAssembly(snapshot.Graph.Projects[CameraAgentCommon]).Check(
            CustomScriptOrOffSweepOwner, CompiledDependencyChecker.CaptureOrAcquisition);
        Assert.IsTrue(result.SelectedTypes.Any(type => type.Type.Name == "CustomScriptTaskAdapter"));
        Assert.IsTrue(result.SelectedTypes.Any(type => type.Type.Name == "LocalAutomationOffSweepWorker"));
        Assert.IsEmpty(result.Violations, string.Join(Environment.NewLine, result.Violations));
    }

    /// <summary>
    /// The rule above covers acquisition types in every assembly, not only CameraAgent.Common (#1768 S-L4). The
    /// control's AgentCore and replay dependencies avoid the rule's words, so only the boundary itself rejects them.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void CustomScriptAcquisitionRuleRejectsDependenciesOutsideCameraAgentCommon()
    {
        var result = snapshot.TestAssembly.Check(
            type => CustomScriptOrOffSweepOwner(type) && type.Type.Name == "ScriptBoundaryDependencyControl",
            CompiledDependencyChecker.CaptureOrAcquisition);
        Assert.AreEqual(1, result.TypesChecked);
        foreach (var name in new[] { "HVO.SkyMonitor.AgentCore.RigTarget", "HVO.SkyMonitor.CameraAgent.Modules.Zwo.SensorTarget",
            "HVO.SkyMonitor.CameraAgent.Replay.ReplayTarget", "HVO.SkyMonitor.Imaging.Acquisition.ExposureTarget",
            "HVO.SkyMonitor.Astronomy.CaptureWindowTarget" })
        {
            Assert.IsTrue(result.Violations.Any(v => v.EndsWith(":" + name, StringComparison.Ordinal)), name);
            TestContext.WriteLine($"Acquisition control rejected: {name}");
        }
        Assert.HasCount(5, result.Violations, string.Join(Environment.NewLine, result.Violations));
        // Only the top-level canonical digest helper is exempt; the rest of AgentCore stays forbidden.
        Assert.IsFalse(CompiledDependencyChecker.CaptureOrAcquisition(new(AgentCore, AgentCore, "CaptureContractJson")));
        Assert.IsTrue(CompiledDependencyChecker.CaptureOrAcquisition(new(AgentCore, AgentCore, "CaptureContractJson+Nested")));
        Assert.IsTrue(CompiledDependencyChecker.CaptureOrAcquisition(new(AgentCore, AgentCore, "CameraFrame")));
    }

    private static bool CustomScriptOrOffSweepOwner(CompiledDependencyChecker.OwnedType type) =>
        type.Type.Namespace.StartsWith("HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts", StringComparison.Ordinal)
        || type.Type.Name.StartsWith("LocalAutomationOffSweepWorker", StringComparison.Ordinal);

    [TestMethod]
    [TestCategory("Unit")]
    public void Repository_EmbedsNoReleaseSigningTrustRoot()
    {
        var root = snapshot.Root;
        var keys = ShippedSourceRoots
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.pem", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Assert.IsEmpty(keys, string.Join(", ", keys));
        Assert.DoesNotContain(
            "HVO.SkyMonitor.Deployment.Distribution/",
            snapshot.Source(Path.Combine(root, "HVO.SkyMonitor.v9.slnx")).Text,
            StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SpaceWeatherFactsReferenceNoHostHttpOrPersistenceNamespace()
    {
        var repository = snapshot.Graph;
        var project = repository.Projects[Processing];
        Assert.IsTrue(project.References.All(reference => reference is AgentCore or Astronomy or Imaging));
        Assert.IsEmpty(project.FrameworkReferences);
        Assert.IsEmpty(project.PackageReferences);
        var result = snapshot.ProductionAssembly(project).Check(type => CompiledDependencyChecker.SpaceWeather(type.Type), CompiledDependencyChecker.HostHttpOrPersistence);
        Assert.IsGreaterThan(0, result.TypesChecked);
        Assert.IsEmpty(result.Violations, string.Join(Environment.NewLine, result.Violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SpaceWeatherHostWorkflowReferencesNoDeliveryOrEnvironmentalSource()
    {
        var project = snapshot.Graph.Projects[CameraAgentCommon];
        var result = snapshot.ProductionAssembly(project).Check(
            type => type.Type.Namespace.StartsWith("HVO.SkyMonitor.CameraAgent.Common.SpaceWeather", StringComparison.Ordinal),
            type => new[] { "Outbox", "Delivery", "Notification", "IEnvironmentalSource" }.Any(word =>
                type.Namespace.Contains(word, StringComparison.Ordinal) || type.Name.Contains(word, StringComparison.Ordinal)));
        Assert.IsGreaterThan(0, result.TypesChecked);
        Assert.IsEmpty(result.Violations, string.Join(Environment.NewLine, result.Violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SpaceWeatherEndpointsReferenceOnlyTheOperationsContract()
    {
        var result = snapshot.ProductionAssembly(snapshot.Graph.Projects[CameraAgent]).Check(
            type => type.Type.Name.StartsWith("CameraAgentSpaceWeatherOperationsEndpoints", StringComparison.Ordinal),
            type => type.Namespace.StartsWith("HVO.SkyMonitor.CameraAgent.Common.SpaceWeather", StringComparison.Ordinal)
                && !type.Name.StartsWith("SpaceWeatherOperations", StringComparison.Ordinal));
        Assert.IsGreaterThan(0, result.TypesChecked);
        Assert.IsEmpty(result.Violations, string.Join(Environment.NewLine, result.Violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureRawIngressAndRecipesReferenceNoSpaceWeatherFacts()
    {
        var result = snapshot.CaptureOrRecipe;
        Assert.IsGreaterThan(0, result.TypesChecked);
        Assert.IsEmpty(result.Violations, string.Join(Environment.NewLine, result.Violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SpaceWeatherMetadataRuleRejectsInternalFileCall()
    {
        var result = snapshot.TestAssembly.Check(
            type => CompiledDependencyChecker.SpaceWeather(type.Type) && type.Type.Name == "FileDependencyControl", CompiledDependencyChecker.HostHttpOrPersistence);
        Assert.AreEqual(1, result.TypesChecked);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("System.IO.File", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureMetadataRuleRejectsGlobalImportAndSameAssemblyTypeDefinition()
    {
        var result = snapshot.TestAssembly.Check(
            type => CompiledDependencyChecker.CaptureOrRecipe(type.Type) && type.Type.Name == "GlobalImportDependencyControl", CompiledDependencyChecker.SpaceWeather);
        Assert.AreEqual(1, result.TypesChecked);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("SpaceWeather.SwpcMessageRecord", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SpaceWeatherMetadataRuleCoversBaseSignaturesAndAttributeArguments()
    {
        var result = snapshot.TestAssembly.Check(
            type => CompiledDependencyChecker.SpaceWeather(type.Type) && type.Type.Name == "SignatureDependencyControl", CompiledDependencyChecker.HostHttpOrPersistence);
        Assert.AreEqual(1, result.TypesChecked);
        foreach (var name in new[] { "System.IO.MemoryStream", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.BinaryReader", "System.Net.Http.HttpClient" })
            Assert.IsTrue(result.Violations.Any(v => v.Contains(name, StringComparison.Ordinal)), name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureOrRecipeSelectionCoversReviewedSourceFiles()
    {
        var files = snapshot.ReviewedCaptureOrRecipeFiles;
        var selected = snapshot.CaptureOrRecipe.SelectedTypes;
        var covered = files.Where(file => selected.Any(type => type.Documents.Contains(file))).ToArray();
        // Conservative declaration detection: an ambiguous keyword stays required,
        // so a source file cannot silently disappear from the parity assertion.
        var excluded = files.Except(covered).Where(file => !System.Text.RegularExpressions.Regex.IsMatch(
            snapshot.Source(file).MaskedText, @"\b(?:class|struct|interface|enum|record|delegate)\b")).ToArray();
        TestContext.WriteLine($"Reviewed source set={files.Count}; covered={covered.Length}; excluded without types={excluded.Length}");
        foreach (var file in excluded) TestContext.WriteLine($"Excluded without types: {Path.GetRelativePath(snapshot.Graph.Root, file)}");
        Assert.IsGreaterThan(0, files.Count);
        var missing = files.Except(covered).Except(excluded).Select(file => Path.GetRelativePath(snapshot.Graph.Root, file)).ToArray();
        Assert.IsEmpty(missing, "Reviewed source owners absent from compiled selection: " + string.Join(", ", missing));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureOrRecipeSelectionIncludesReviewedNamedOwners()
    {
        var selected = snapshot.CaptureOrRecipe.SelectedTypes;
        foreach (var name in new[] { "ProcessingRecipeExecutor", "MeasuredStellarAssociationCaptureProcessingStep" })
        {
            Assert.IsTrue(selected.Any(type => type.Type.Name == name), name);
            TestContext.WriteLine($"Selected named owner: {name}");
        }
        var schedulePath = Path.Combine(snapshot.Graph.Root, "src", CameraAgentCommon, "Scheduling", "CaptureScheduleEngine.cs");
        var scheduleTypes = selected.Where(type => type.Documents.Contains(schedulePath)).ToArray();
        Assert.IsNotEmpty(scheduleTypes);
        foreach (var type in scheduleTypes) TestContext.WriteLine($"Selected schedule owner: {type.Type.Namespace}.{type.Type.Name}");
        var agentTypes = selected.Where(type => type.Type.Assembly == AgentCore && type.Type.Name.StartsWith("Capture", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(agentTypes);
        foreach (var type in agentTypes) TestContext.WriteLine($"Selected AgentCore owner: {type.Type.Name}");
        Assert.IsTrue(CompiledDependencyChecker.CaptureOrRecipe(new(AgentCore, "Flat", "recipeOwner+Nested")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureMetadataRuleRejectsSourcePathOnlyOwnersIncludingBodylessAndNestedTypes()
    {
        var file = Path.Combine(snapshot.Graph.Root, "tests", "HVO.SkyMonitor.Architecture.Tests", "RecipeOwnershipControls.cs");
        var sourceOwners = new HashSet<string>(StringComparer.Ordinal) { file };
        Assert.IsTrue(ReviewedCaptureOrRecipeFile(snapshot.Source(file)));
        var result = snapshot.TestAssembly.Check(
            type => type.Type.Namespace == "HVO.SkyMonitor.Tests.Flat" && type.Documents.Contains(file)
                && CompiledDependencyChecker.CaptureOrRecipe(type, sourceOwners), CompiledDependencyChecker.SpaceWeather);
        Assert.AreEqual(3, result.TypesChecked);
        foreach (var type in result.SelectedTypes)
        {
            Assert.IsFalse(CompiledDependencyChecker.CaptureOrRecipe(type.Type), type.Type.Name);
            Assert.IsTrue(result.Violations.Any(v => v.Contains(type.Type.Name + " ->", StringComparison.Ordinal)
                && v.Contains("SpaceWeather.SwpcMessageRecord", StringComparison.Ordinal)), type.Type.Name);
            TestContext.WriteLine($"Source-only control rejected: {type.Type.Name}");
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CaptureMetadataRuleRejectsTypeNameOnlyOwner()
    {
        var file = Path.Combine(snapshot.Graph.Root, "tests", "HVO.SkyMonitor.Architecture.Tests", "BoundaryNameControl.cs");
        Assert.IsFalse(ReviewedCaptureOrRecipeFile(snapshot.Source(file)));
        var sourceOwners = snapshot.ReviewedCaptureOrRecipeFiles;
        var result = snapshot.TestAssembly.Check(
            type => type.Type.Name == "RecipeNameDependencyControl" && CompiledDependencyChecker.CaptureOrRecipe(type, sourceOwners),
            CompiledDependencyChecker.SpaceWeather);
        Assert.AreEqual(1, result.TypesChecked);
        Assert.IsFalse(result.SelectedTypes.Single().Documents.Overlaps(sourceOwners));
        Assert.AreEqual("HVO.SkyMonitor.Tests.Flat", result.SelectedTypes.Single().Type.Namespace);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("SpaceWeather.SwpcMessageRecord", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CompiledOwnershipFailsWhenPortablePdbIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-ownership-");
        try
        {
            var path = Path.Combine(directory.FullName, "control.dll");
            File.Copy(typeof(ArchitectureBoundaryTests).Assembly.Location, path);
            var error = Assert.Throws<FileNotFoundException>(() => CompiledDependencyChecker.Check(path, _ => true, _ => false));
            StringAssert.Contains(error.Message, "Portable PDB required for complete source ownership selection");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static bool ReviewedCaptureOrRecipeFile(SolutionSnapshot.SourceFile file)
    {
        var relative = file.RelativePath;
        var source = file.MaskedText;
        // Exact fc52a26f reviewed selector; paths are repository-relative.
        return relative.Contains("Capture", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("RawIngress", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("Recipe", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(Path.Combine("src", Processing), StringComparison.Ordinal)
                && !relative.StartsWith(Path.Combine("src", Processing, "SpaceWeather") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(source, @"namespace\s+[^;{]*(?:Capture|RawIngress|Recipes)");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ProductionProjectReferencesFollowDocumentedGraph()
    {
        var repository = snapshot.Graph;
        var violations = repository.ValidateProductionReferences(AllowedProductionReferences)
            .Concat(repository.ValidateTestOwnership(SharedTestProjects, CameraAgentTestProjects))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StorageFileSystemBoundaryIsNarrowWhenPresent()
    {
        // The filesystem primitives have no project dependencies. CameraAgent.Common and Video.FFmpeg
        // are the only production consumers.
        var repository = snapshot.Graph;
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
        var permittedConsumers = new[] { CameraAgentCommon, VideoFFmpeg };
        Assert.IsEmpty(consumers.Except(permittedConsumers, StringComparer.Ordinal).ToArray(),
            $"Only {string.Join(" and ", permittedConsumers)} may reference {StorageFileSystem}: {string.Join(", ", consumers)}");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TerminalUiBoundaryIsNarrow()
    {
        // #1317: what the terminal programs share. It takes Terminal.Gui and nothing else, so the installer and hvo-sky
        // can both build on it. hvo-sky counts with CameraAgent for test ownership and draws its UI with TerminalUi.
        var repository = snapshot.Graph;
        Assert.IsTrue(repository.Projects.TryGetValue(TerminalUi, out var project), $"{TerminalUi} is in the repository.");
        Assert.AreEqual(ProjectKind.Production, project.Kind);
        Assert.IsTrue(AllowedProductionReferences.TryGetValue(TerminalUi, out var allowed) && allowed.Count == 0,
            $"{TerminalUi} must be documented with an empty reference set.");
        Assert.IsEmpty(project.References, $"{TerminalUi} must reference no project: {string.Join(", ", project.References)}");
        CollectionAssert.AreEqual(new[] { "Terminal.Gui" }, project.PackageReferences.ToArray(), $"{TerminalUi} takes Terminal.Gui only.");

        var hosts = repository.Projects.Values
            .Where(candidate => candidate.Kind == ProjectKind.Production && candidate.Name != CameraAgentCli
                && CameraAgentImplementations.Contains(candidate.Name))
            .Where(candidate => candidate.References.Contains(TerminalUi) || candidate.PackageReferences.Contains("Terminal.Gui"))
            .Select(candidate => candidate.Name)
            .ToArray();
        Assert.IsEmpty(hosts, $"No host draws a terminal: {string.Join(", ", hosts)}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ProductionTransitiveGraphDoesNotReachHostsOrTestProjects()
    {
        var repository = snapshot.Graph;
        var violations = repository.ValidateTransitiveBoundaries(HostProjects);

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
        // delegating to LinuxOpenFlags, and no importer outside the three declared tables may spell the values on a
        // line that talks about open flags. The sweep covers src/; tests and tools do not import libc open.
        // CameraAgent (#587) delegates to Storage.FileSystem; its public compatibility facade owns no table.
        var root = snapshot.Root;
        string[] tableFiles =
        [
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

        var cameraAgentFacade = snapshot.Source(Path.Combine(root, "src", "HVO.SkyMonitor.CameraAgent.Common", "Storage", "LinuxOpenFlags.cs")).Text;
        Assert.IsTrue(cameraAgentFacade.Contains("HVO.SkyMonitor.Storage.FileSystem.LinuxOpenFlags", StringComparison.Ordinal));
        Assert.IsFalse(cameraAgentFacade.Contains("Architecture.Arm64", StringComparison.Ordinal),
            "CameraAgent's public compatibility facade must not regain a private ABI table.");

        foreach (var table in tableFiles.Where(table => !File.Exists(Path.Combine(root, table))))
        {
            violations.Add($"{table}: the declared flag table file is missing");
        }

        foreach (var file in snapshot.CSharpFiles)
        {
            var text = file.Text;
            var relative = file.RelativePath;
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

        // Seven importers: the GPS serial device, the authenticated CameraAgent callers, deployment CLI, SQLite catalog
        // resolver, and Storage.FileSystem's DurableSync plus HardLinkPublisher.
        Assert.AreEqual(7, callers, "the set of libc open importers changed; update this guard deliberately");
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
    public void CameraAgentAccountFormsReachTheirHandlers()
        => AssertAccountFormsReachTheirHandlers("HVO.SkyMonitor.CameraAgent");

    // The account pages render statically, so only a form post reaches them. A posted input
    // reaches the page only through a [SupplyParameterFromForm] property, and Blazor names each
    // input after its binding expression: an input bound through any other member (such as a
    // lazily created wrapper over the supplied property) posts a name nothing maps back, and the
    // form silently submits empty. A browser event handler other than a form's @onsubmit never
    // runs, so a control wired through one does nothing.
    private static void AssertAccountFormsReachTheirHandlers(string projectDirectoryName)
    {
        var root = snapshot.Root;
        var accountRoot = Path.Combine(root, "src", projectDirectoryName, "Components", "Account");
        var suppliedPattern = new System.Text.RegularExpressions.Regex(
            """\[SupplyParameterFromForm[^\]]*\]\s*(?:(?:public|private|protected|internal)\s+)?[\w<>?.,]+\s+(?<name>\w+)\s*\{""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var bindPattern = new System.Text.RegularExpressions.Regex(
            @"@bind-Value(?::get)?=""(?<expression>[^""]+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var eventPattern = new System.Text.RegularExpressions.Regex(
            @"@on(?<event>[a-z]+)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var violations = new List<string>();
        var bindings = 0;

        foreach (var page in Directory.EnumerateFiles(accountRoot, "*.razor", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var markup = snapshot.Source(page).Text;
            var codeBehind = page + ".cs";
            var code = markup + (File.Exists(codeBehind) ? snapshot.Source(codeBehind).Text : string.Empty);
            var supplied = suppliedPattern.Matches(code).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in bindPattern.Matches(markup))
            {
                bindings++;
                var expression = match.Groups["expression"].Value.Trim();
                if (!supplied.Contains(expression.Split('.')[0]))
                {
                    violations.Add($"{Path.GetRelativePath(root, page)} binds {expression}; form-supplied: [{string.Join(", ", supplied.Order(StringComparer.Ordinal))}]");
                }
            }

            foreach (System.Text.RegularExpressions.Match match in eventPattern.Matches(markup))
            {
                if (match.Groups["event"].Value != "submit")
                {
                    violations.Add($"{Path.GetRelativePath(root, page)} handles @on{match.Groups["event"].Value}, which a static page never receives");
                }
            }
        }

        Assert.IsGreaterThan(0, bindings, $"No account form bindings were found under {Path.GetRelativePath(root, accountRoot)}.");
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static System.Text.RegularExpressions.Regex LogEventIdDeclarationPattern()
        => new(
            """\b(?:EventId\s*=\s*|new\s+EventId\s*\(\s*|LoggerMessage\s*\(\s*|EventId\b[^;=]*=\s*new\s*\(\s*)(?<id>\d+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static ImmutableArray<(int Id, string Path, int Line)> ScanLogEventDeclarations(
        ImmutableArray<SolutionSnapshot.SourceFile> files)
    {
        var declarationPattern = LogEventIdDeclarationPattern();
        var declarations = ImmutableArray.CreateBuilder<(int Id, string Path, int Line)>();

        foreach (var file in files)
        {
            var source = file.MaskedText;
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
                    file.RelativePath,
                    line));
            }
        }

        return declarations.ToImmutable();
    }

    private static void AssertHostOwnedLogEventIdsAreUnique(string hostName, string projectDirectoryName)
    {
        var ownedPrefix = Path.Combine("src", projectDirectoryName);
        var duplicates = snapshot.LogEventDeclarations
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
            [CameraAgentCommon] = Project(CameraAgentCommon, ProjectKind.Production, ProcessingRunner),
            [ProcessingRunner] = Project(ProcessingRunner, ProjectKind.Production, Common),
            [Common] = Project(Common, ProjectKind.Production, TestSupport),
            [TestSupport] = Project(TestSupport, ProjectKind.Test)
        };
        var graph = new RepositoryGraph(string.Empty, projects);

        var violations = graph.ValidateTransitiveBoundaries(HostProjects);

        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"{CameraAgent} -> {CameraAgentCommon} -> {ProcessingRunner}", StringComparison.Ordinal)));
        Assert.IsTrue(violations.Any(violation => violation.Contains(
            $"{ProcessingRunner} -> {Common} -> {TestSupport}", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void AgentCoreHasNoHostInfrastructureOrImagingDependencies()
    {
        var repository = snapshot.Graph;
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

            var releaseEvaluation = RepositoryGraph.EvaluateProjectAsync(sourceProject, "Release").GetAwaiter().GetResult();
            var debugEvaluation = RepositoryGraph.EvaluateProjectAsync(sourceProject, "Debug").GetAwaiter().GetResult();

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
    public async Task ProjectEvaluationProcessTimeoutKillsProcessTreeAndPreservesDiagnostics()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(2), RepositoryGraph.DefaultEvaluationTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(5), RepositoryGraph.PostKillGracePeriod);

        const string stdoutMarker = "ARCH-TIMEOUT-STDOUT-MARKER";
        const string stderrMarker = "ARCH-TIMEOUT-STDERR-MARKER";
        const string operationDescription = "SyntheticHungProject.csproj";

        var fastResult = await RepositoryGraph.RunProcessWithTimeoutAsync(
            CreateQuickOutputStartInfo("ARCH-OK-STDOUT", "ARCH-OK-STDERR"),
            "SyntheticFastProject.csproj",
            TimeSpan.FromSeconds(10),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, fastResult.ExitCode);
        Assert.Contains("ARCH-OK-STDOUT", fastResult.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARCH-OK-STDERR", fastResult.StandardError, StringComparison.Ordinal);

        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-hang-{Guid.NewGuid():N}.pid");
        int? grandchildPid = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateHangingChildStartInfo(stdoutMarker, stderrMarker, pidFile),
                    operationDescription,
                    TimeSpan.FromMilliseconds(750),
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            grandchildPid = TryExtractPid(exception.Message, "child_pid=") ?? TryReadPidFile(pidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(15),
                elapsed.Elapsed,
                $"Timed-out child process took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s to terminate.");
            Assert.Contains("ARCH-EVALUATE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(operationDescription, exception.Message, StringComparison.Ordinal);
            if (OperatingSystem.IsLinux())
            {
                Assert.Contains(
                    "and process-tree termination was requested; the root process and all tracked tagged descendants exited and redirected output streams closed.",
                    exception.Message,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("process-tree termination was requested", exception.Message, StringComparison.Ordinal);
            }
            Assert.Contains(stdoutMarker, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stderrMarker, exception.Message, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.IsTrue(
                    grandchildPid.HasValue,
                    $"Expected grandchild PID marker in timeout message:{Environment.NewLine}{exception.Message}");
                Assert.IsTrue(
                    await WaitForProcessGoneAsync(grandchildPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                    $"Expected Kill(entireProcessTree: true) to terminate grandchild PID {grandchildPid.Value.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
        finally
        {
            await CleanupSyntheticProcessAsync(grandchildPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectEvaluationTimeoutWhenRootExitsWithPipeHoldingDescendantBoundsDrainAndClosesPipes()
    {
        const string stdoutMarker = "ARCH-EXITED-ROOT-STDOUT-MARKER";
        const string stderrMarker = "ARCH-EXITED-ROOT-STDERR-MARKER";
        const string operationDescription = "SyntheticExitedRootProject.csproj";
        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-exited-root-{Guid.NewGuid():N}.pid");
        int? childPid = null;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateExitedParentWithPipeHoldingChildStartInfo(stdoutMarker, stderrMarker, pidFile, stripEvaluationRunId: true),
                    operationDescription,
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(300),
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            childPid = TryExtractPid(exception.Message, "child_pid=") ?? TryReadPidFile(pidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(5),
                elapsed.Elapsed,
                $"Exited-parent pipe drain took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.Contains("ARCH-EVALUATE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(operationDescription, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stdoutMarker, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stderrMarker, exception.Message, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain(
                    "the root process and all tracked tagged descendants exited",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "while draining redirected output after the root process exited",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.Contains("descendant", exception.Message, StringComparison.Ordinal);
                Assert.IsTrue(
                    childPid.HasValue,
                    $"Expected pipe-holding child PID marker in timeout message:{Environment.NewLine}{exception.Message}");

                if (OperatingSystem.IsLinux())
                {
                    AssertRedirectedPipeReadersClosedForProcess(childPid.Value);
                }
            }
        }
        finally
        {
            await CleanupSyntheticProcessAsync(childPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectEvaluationTimeoutBoundsPostKillDrainWhenEscapedDescendantHoldsPipesOpen()
    {
        const string stdoutMarker = "ARCH-ESCAPED-PIPE-STDOUT-MARKER";
        const string stderrMarker = "ARCH-ESCAPED-PIPE-STDERR-MARKER";
        const string operationDescription = "SyntheticEscapedPipeProject.csproj";
        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-escaped-{Guid.NewGuid():N}.pid");
        int? escapedPid = null;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateHangingRootWithEscapedPipeHolderStartInfo(stdoutMarker, stderrMarker, pidFile, stripEvaluationRunId: true),
                    operationDescription,
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(300),
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            escapedPid = TryExtractPid(exception.Message, "escaped_pid=") ?? TryReadPidFile(pidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(5),
                elapsed.Elapsed,
                $"Post-kill drain with escaped pipe holder took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.Contains("ARCH-EVALUATE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(operationDescription, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stdoutMarker, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stderrMarker, exception.Message, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain(
                    "the root process and all tracked tagged descendants exited",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "and the root process exited after termination was requested, but redirected output draining timed out",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.IsTrue(
                    escapedPid.HasValue,
                    $"Expected escaped pipe-holder PID marker in timeout message:{Environment.NewLine}{exception.Message}");

                if (OperatingSystem.IsLinux())
                {
                    AssertRedirectedPipeReadersClosedForProcess(escapedPid.Value);
                }
            }
        }
        finally
        {
            await CleanupSyntheticProcessAsync(escapedPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectEvaluationTimeoutWhenRootExitsTerminatesReparentedPipeHoldingDescendant()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string stdoutMarker = "ARCH-REPARENTED-OPEN-STDOUT-MARKER";
        const string stderrMarker = "ARCH-REPARENTED-OPEN-STDERR-MARKER";
        const string operationDescription = "SyntheticReparentedPipeHolderProject.csproj";
        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-reparented-open-{Guid.NewGuid():N}.pid");
        int? childPid = null;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateExitedParentWithPipeHoldingChildStartInfo(stdoutMarker, stderrMarker, pidFile),
                    operationDescription,
                    TimeSpan.FromMilliseconds(500),
                    RepositoryGraph.PostKillGracePeriod,
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            childPid = TryExtractPid(exception.Message, "child_pid=") ?? TryReadPidFile(pidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(3.5),
                elapsed.Elapsed,
                $"Reparented pipe-holding descendant cleanup should not stall for the full post-kill grace period; took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.Contains("ARCH-EVALUATE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(operationDescription, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stdoutMarker, exception.Message, StringComparison.Ordinal);
            Assert.Contains(stderrMarker, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "the root process and all tracked tagged descendants exited",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "while draining redirected output after the root process exited",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "surviving tagged descendants were terminated",
                exception.Message,
                StringComparison.Ordinal);
            Assert.IsTrue(
                childPid.HasValue,
                $"Expected reparented pipe-holding child PID marker in timeout message:{Environment.NewLine}{exception.Message}");
            Assert.IsTrue(
                await WaitForProcessGoneAsync(childPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                $"Expected timeout cleanup to terminate reparented pipe-holding descendant PID {childPid.Value.ToString(CultureInfo.InvariantCulture)}.");
        }
        finally
        {
            await CleanupSyntheticProcessAsync(childPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectEvaluationTimeoutTerminatesReparentedPipeClosedDescendants()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Case 1: Root hangs while a reparented descendant closes its redirected pipes and stays alive.
        var hangingPidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-reparented-closed-{Guid.NewGuid():N}.pid");
        int? hangingEscapedPid = null;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateHangingRootWithReparentedPipeClosedDescendantStartInfo(
                        "ARCH-REPARENTED-CLOSED-STDOUT",
                        "ARCH-REPARENTED-CLOSED-STDERR",
                        hangingPidFile),
                    "SyntheticReparentedPipeClosedProject.csproj",
                    TimeSpan.FromMilliseconds(500),
                    RepositoryGraph.PostKillGracePeriod,
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            hangingEscapedPid = TryExtractPid(exception.Message, "escaped_pid=") ?? TryReadPidFile(hangingPidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(3.5),
                elapsed.Elapsed,
                $"Reparented pipe-closed descendant cleanup took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.Contains(
                "and process-tree termination was requested; the root process and all tracked tagged descendants exited and redirected output streams closed.",
                exception.Message,
                StringComparison.Ordinal);
            Assert.IsTrue(
                hangingEscapedPid.HasValue,
                $"Expected reparented pipe-closed PID marker in timeout message:{Environment.NewLine}{exception.Message}");
            Assert.IsTrue(
                await WaitForProcessGoneAsync(hangingEscapedPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                $"Expected timeout cleanup to terminate reparented pipe-closed descendant PID {hangingEscapedPid.Value.ToString(CultureInfo.InvariantCulture)} before reporting verified cleanup.");
        }
        finally
        {
            await CleanupSyntheticProcessAsync(hangingEscapedPid, hangingPidFile).ConfigureAwait(false);
        }

        // Case 2: Root exits while one reparented descendant holds pipes open and another reparented descendant closes its pipes.
        var closedPidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-exited-closed-{Guid.NewGuid():N}.pid");
        var openPidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-exited-open-{Guid.NewGuid():N}.pid");
        int? closedPid = null;
        int? openPid = null;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunProcessWithTimeoutAsync(
                    CreateExitedRootWithPipeHoldingAndPipeClosedDescendantsStartInfo(
                        "ARCH-EXITED-MIXED-STDOUT",
                        "ARCH-EXITED-MIXED-STDERR",
                        closedPidFile,
                        openPidFile),
                    "SyntheticExitedRootMixedDescendantsProject.csproj",
                    TimeSpan.FromMilliseconds(500),
                    RepositoryGraph.PostKillGracePeriod,
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            closedPid = TryExtractPid(exception.Message, "closed_pid=") ?? TryReadPidFile(closedPidFile);
            openPid = TryExtractPid(exception.Message, "open_pid=") ?? TryReadPidFile(openPidFile);

            Assert.IsLessThan(
                TimeSpan.FromSeconds(3.5),
                elapsed.Elapsed,
                $"Exited-root mixed descendant cleanup took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.DoesNotContain(
                "the root process and all tracked tagged descendants exited",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "while draining redirected output after the root process exited",
                exception.Message,
                StringComparison.Ordinal);
            Assert.IsTrue(
                closedPid.HasValue,
                $"Expected pipe-closed descendant PID marker in timeout message:{Environment.NewLine}{exception.Message}");
            Assert.IsTrue(
                openPid.HasValue,
                $"Expected pipe-holding descendant PID marker in timeout message:{Environment.NewLine}{exception.Message}");
            Assert.IsTrue(
                await WaitForProcessGoneAsync(closedPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                $"Expected timeout cleanup to terminate reparented pipe-closed descendant PID {closedPid.Value.ToString(CultureInfo.InvariantCulture)}.");
            Assert.IsTrue(
                await WaitForProcessGoneAsync(openPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                $"Expected timeout cleanup to terminate reparented pipe-holding descendant PID {openPid.Value.ToString(CultureInfo.InvariantCulture)}.");
        }
        finally
        {
            await CleanupSyntheticProcessAsync(closedPid, closedPidFile).ConfigureAwait(false);
            await CleanupSyntheticProcessAsync(openPid, openPidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task WaitForProcessGoneAsyncRecognizesLiveLinuxZombieProcess()
    {
        var expectedEntry = Encoding.UTF8.GetBytes($"{RepositoryGraph.EvaluationRunIdEnvironmentVariable}=abc123");
        Assert.IsTrue(RepositoryGraph.ContainsEnvironmentEntry(
            Encoding.UTF8.GetBytes($"PATH=/usr/bin\0{RepositoryGraph.EvaluationRunIdEnvironmentVariable}=abc123\0HOME=/tmp\0"),
            expectedEntry));
        Assert.IsFalse(RepositoryGraph.ContainsEnvironmentEntry(
            Encoding.UTF8.GetBytes($"OTHER_{RepositoryGraph.EvaluationRunIdEnvironmentVariable}=abc123\0{RepositoryGraph.EvaluationRunIdEnvironmentVariable}=abc1234\0"),
            expectedEntry));

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("sleep 0.1 & zombie_pid=$!; printf '%s\\n' \"$zombie_pid\"; exec sleep 30");

        using var parentProcess = new Process { StartInfo = startInfo };
        Assert.IsTrue(parentProcess.Start(), "Expected synthetic zombie parent process to start.");

        int? zombiePid = null;
        try
        {
            var pidLine = await parentProcess.StandardOutput.ReadLineAsync(TestContext.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsTrue(
                int.TryParse(pidLine?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedZombiePid),
                $"Expected synthetic zombie child PID on stdout, got '{pidLine}'.");
            zombiePid = parsedZombiePid;

            var becameZombie = false;
            var zombieWait = Stopwatch.StartNew();
            while (zombieWait.Elapsed < TimeSpan.FromSeconds(5))
            {
                var stat = TryReadLinuxProcessStat(parsedZombiePid);
                if (stat is not null && TryGetLinuxStatState(stat) == 'Z')
                {
                    becameZombie = true;
                    break;
                }

                await Task.Delay(20, TestContext.CancellationToken).ConfigureAwait(false);
            }

            Assert.IsTrue(
                becameZombie,
                $"Expected child PID {parsedZombiePid.ToString(CultureInfo.InvariantCulture)} to enter persistent Linux zombie 'Z' state while parent sleep stays alive.");
            Assert.IsTrue(IsLinuxZombieOrDeadProcess(parsedZombiePid));
            Assert.IsTrue(
                await WaitForProcessGoneAsync(parsedZombiePid, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                $"Expected WaitForProcessGoneAsync to recognize live Linux zombie PID {parsedZombiePid.ToString(CultureInfo.InvariantCulture)} as gone.");

            var statAfterCheck = TryReadLinuxProcessStat(parsedZombiePid);
            Assert.IsNotNull(statAfterCheck, "Expected zombie /proc/<pid>/stat entry to remain present while unreaped by parent.");
            Assert.AreEqual('Z', TryGetLinuxStatState(statAfterCheck), "Expected zombie process to still be in state 'Z' before parent cleanup.");
        }
        finally
        {
            KillProcessBestEffort(parentProcess.Id);
            var parentExited = await WaitForProcessGoneAsync(parentProcess.Id, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            if (parentExited)
            {
                using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await parentProcess.WaitForExitAsync(reapCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (reapCts.IsCancellationRequested)
                {
                }
            }

            Assert.IsTrue(
                parentExited,
                $"Synthetic zombie parent PID {parentProcess.Id.ToString(CultureInfo.InvariantCulture)} was still alive after cleanup kill.");

            if (zombiePid.HasValue)
            {
                var zombieGone = await WaitForProcessGoneAsync(zombiePid.Value, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(
                    zombieGone,
                    $"Synthetic zombie child PID {zombiePid.Value.ToString(CultureInfo.InvariantCulture)} was still reported live after parent cleanup.");
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectEvaluationCallerCancellationKillsProcessTreeBeforeThrowing()
    {
        const string stdoutMarker = "ARCH-CANCEL-STDOUT-MARKER";
        const string stderrMarker = "ARCH-CANCEL-STDERR-MARKER";
        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-cancel-{Guid.NewGuid():N}.pid");
        int? grandchildPid = null;

        try
        {
            using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            var runTask = RepositoryGraph.RunProcessWithTimeoutAsync(
                CreateHangingChildStartInfo(stdoutMarker, stderrMarker, pidFile),
                "SyntheticCancelledProject.csproj",
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(500),
                callerCts.Token);

            if (!OperatingSystem.IsWindows())
            {
                grandchildPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(grandchildPid.HasValue, "Expected synthetic hanging child to record its grandchild PID before cancellation.");
            }
            else
            {
                await Task.Delay(250, TestContext.CancellationToken).ConfigureAwait(false);
            }

            var cancelElapsed = Stopwatch.StartNew();
            await callerCts.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask).ConfigureAwait(false);
            cancelElapsed.Stop();

            Assert.IsLessThan(
                TimeSpan.FromSeconds(5),
                cancelElapsed.Elapsed,
                $"Caller cancellation took {cancelElapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s to unwind.");

            if (!OperatingSystem.IsWindows())
            {
                Assert.IsTrue(
                    await WaitForProcessGoneAsync(grandchildPid!.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                    $"Expected caller cancellation to kill grandchild PID {grandchildPid.Value.ToString(CultureInfo.InvariantCulture)} before throwing.");
            }
        }
        finally
        {
            await CleanupSyntheticProcessAsync(grandchildPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RepositoryGraphWorkersCancelSiblingsFailFastAndRecognizeLinuxZombies()
    {
        Assert.IsTrue(IsLinuxStatZombieOrDead("4242 (sleep) Z 1 4242 4242 0 -1 0"));
        Assert.IsTrue(IsLinuxStatZombieOrDead("4242 (weird (proc) name) Z 1 4242 4242 0 -1 0"));
        Assert.IsTrue(IsLinuxStatZombieOrDead("4242 (exiting) X 1 4242 4242 0 -1 0"));
        Assert.IsFalse(IsLinuxStatZombieOrDead("4242 (sleep) S 1 4242 4242 0 -1 0"));
        Assert.IsFalse(IsLinuxStatZombieOrDead("4242 (dotnet) R 1 4242 4242 0 -1 0"));

        var pidFile = Path.Combine(Path.GetTempPath(), $"skymonitor-arch-failfast-{Guid.NewGuid():N}.pid");
        int? siblingGrandchildPid = null;
        var queuedStartedCount = 0;

        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                RepositoryGraph.RunWithFailFastWorkersAsync(
                    [0, 1, 2, 3],
                    maxConcurrency: 2,
                    async (workIndex, ct) =>
                    {
                        if (workIndex == 0)
                        {
                            await RepositoryGraph.RunProcessWithTimeoutAsync(
                                CreateHangingChildStartInfo("ARCH-SIBLING-OUT", "ARCH-SIBLING-ERR", pidFile),
                                "SyntheticSiblingProject.csproj",
                                TimeSpan.FromSeconds(30),
                                TimeSpan.FromMilliseconds(500),
                                ct).ConfigureAwait(false);
                            return workIndex;
                        }

                        if (workIndex == 1)
                        {
                            if (!OperatingSystem.IsWindows())
                            {
                                siblingGrandchildPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await Task.Delay(250, ct).ConfigureAwait(false);
                            }

                            throw new TimeoutException("ARCH-EVALUATE: synthetic first failure");
                        }

                        Interlocked.Increment(ref queuedStartedCount);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        return workIndex;
                    },
                    TestContext.CancellationToken)).ConfigureAwait(false);
            elapsed.Stop();

            Assert.Contains("ARCH-EVALUATE: synthetic first failure", exception.Message, StringComparison.Ordinal);
            Assert.IsLessThan(
                TimeSpan.FromSeconds(5),
                elapsed.Elapsed,
                $"Fail-fast worker cancellation took {elapsed.Elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s.");
            Assert.AreEqual(0, Volatile.Read(ref queuedStartedCount), "Queued evaluations should not start after a sibling fails.");

            if (!OperatingSystem.IsWindows())
            {
                siblingGrandchildPid ??= TryReadPidFile(pidFile);
                Assert.IsTrue(siblingGrandchildPid.HasValue, "Expected sibling worker to start its synthetic child before failing.");
                Assert.IsTrue(
                    await WaitForProcessGoneAsync(siblingGrandchildPid.Value, TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false),
                    $"Expected fail-fast cancellation to terminate sibling grandchild PID {siblingGrandchildPid.Value.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
        finally
        {
            await CleanupSyntheticProcessAsync(siblingGrandchildPid, pidFile).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DescribeTimeoutTerminationCoversAllCleanupOutcomes()
    {
        var cases = new (
            string Name,
            bool RootExitedBeforeKill,
            bool KillSucceeded,
            bool RootExitedAfterKill,
            RepositoryGraph.TaggedDescendantCleanupResult TaggedCleanup,
            bool PipesDrained,
            string Expected)[]
        {
            (
                "VerifiedCleanExitNoReparentedDescendants",
                false,
                true,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                true,
                "and process-tree termination was requested; the root process and all tracked tagged descendants exited and redirected output streams closed."
            ),
            (
                "VerifiedCleanExitAfterSignalingReparentedDescendants",
                false,
                true,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 2, SurvivingDescendantsDetected: false),
                true,
                "and process-tree termination was requested; the root process and all tracked tagged descendants exited and redirected output streams closed."
            ),
            (
                "RootExitedBeforeKillAndPipesTimedOut",
                true,
                false,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                false,
                "while draining redirected output after the root process exited; pipe readers were closed after the bounded drain grace period and descendant processes may still be running."
            ),
            (
                "RootExitedBeforeKillWithSurvivingTaggedDescendants",
                true,
                false,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 1, SurvivingDescendantsDetected: true),
                true,
                "while draining redirected output after the root process exited; tagged descendant processes remained active after the post-kill grace period."
            ),
            (
                "RootExitedBeforeKillWithTerminatedTaggedDescendants",
                true,
                false,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 2, SurvivingDescendantsDetected: false),
                true,
                "while draining redirected output after the root process exited; surviving tagged descendants were terminated and redirected pipes closed during cleanup, though full process-tree termination could not be established because the root process had already exited."
            ),
            (
                "RootExitedBeforeKillWithNoTaggedDescendantsSignaled",
                true,
                false,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                true,
                "while draining redirected output after the root process exited; redirected pipes closed during cleanup, but process-tree termination could not be established because the root process had already exited."
            ),
            (
                "RootDidNotExitAfterKill",
                false,
                true,
                false,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                false,
                "and termination was requested, but the root process did not exit within the post-kill grace period; pipe readers were closed and descendant processes may still be running."
            ),
            (
                "RootExitedAfterKillWithSurvivingTaggedDescendants",
                false,
                true,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 1, SurvivingDescendantsDetected: true),
                true,
                "and the root process exited after termination was requested, but tagged descendant processes remained active after the post-kill grace period."
            ),
            (
                "RootExitedAfterKillAndPipesTimedOut",
                false,
                true,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                false,
                "and the root process exited after termination was requested, but redirected output draining timed out; pipe readers were closed and descendant processes may still be running."
            ),
            (
                "RootExitedBeforeKillSucceeded",
                false,
                false,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: true, SignaledCount: 0, SurvivingDescendantsDetected: false),
                true,
                "and the root process exited before process-tree termination could be established."
            ),
            (
                "TaggedDescendantCleanupUnsupportedOnPlatform",
                false,
                true,
                true,
                new RepositoryGraph.TaggedDescendantCleanupResult(Supported: false, SignaledCount: 0, SurvivingDescendantsDetected: false),
                true,
                "and process-tree termination was requested and the root process exited with drained pipes, but reparented descendants could not be verified on this platform."
            ),
        };

        foreach (var testCase in cases)
        {
            var actual = RepositoryGraph.DescribeTimeoutTermination(
                testCase.RootExitedBeforeKill,
                testCase.KillSucceeded,
                testCase.RootExitedAfterKill,
                testCase.TaggedCleanup,
                testCase.PipesDrained);

            Assert.AreEqual(
                testCase.Expected,
                actual,
                $"Unexpected timeout termination description for case '{testCase.Name}'.");
        }
    }

    private static ProcessStartInfo CreateQuickOutputStartInfo(string stdoutText, string stderrText)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsStartInfo = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            windowsStartInfo.ArgumentList.Add("/c");
            windowsStartInfo.ArgumentList.Add($"echo {stdoutText}& echo {stderrText} 1>&2");
            return windowsStartInfo;
        }

        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add("printf '%s\\n' \"$1\"; printf '%s\\n' \"$2\" >&2");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(stdoutText);
        unixStartInfo.ArgumentList.Add(stderrText);
        return unixStartInfo;
    }

    private static ProcessStartInfo CreateHangingChildStartInfo(string stdoutMarker, string stderrMarker, string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsStartInfo = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            windowsStartInfo.ArgumentList.Add("/c");
            windowsStartInfo.ArgumentList.Add($"echo {stdoutMarker}& echo {stderrMarker} 1>&2& ping -n 61 127.0.0.1 >nul");
            return windowsStartInfo;
        }

        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(
            "sleep 60 & child_pid=$!; printf '%s\\n' \"$child_pid\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\"; printf '%s child_pid=%s\\n' \"$2\" \"$child_pid\"; printf '%s\\n' \"$3\" >&2; wait \"$child_pid\"");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(pidFile);
        unixStartInfo.ArgumentList.Add(stdoutMarker);
        unixStartInfo.ArgumentList.Add(stderrMarker);
        return unixStartInfo;
    }

    private static ProcessStartInfo CreateExitedParentWithPipeHoldingChildStartInfo(
        string stdoutMarker,
        string stderrMarker,
        string pidFile,
        bool stripEvaluationRunId = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHangingChildStartInfo(stdoutMarker, stderrMarker, pidFile);
        }

        var childLaunch = stripEvaluationRunId
            ? $"(unset {RepositoryGraph.EvaluationRunIdEnvironmentVariable}; exec sleep 60)"
            : "sleep 60";
        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(
            $"{childLaunch} & child_pid=$!; printf '%s\\n' \"$child_pid\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\"; printf '%s child_pid=%s\\n' \"$2\" \"$child_pid\"; printf '%s\\n' \"$3\" >&2; exit 0");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(pidFile);
        unixStartInfo.ArgumentList.Add(stdoutMarker);
        unixStartInfo.ArgumentList.Add(stderrMarker);
        return unixStartInfo;
    }

    private static ProcessStartInfo CreateHangingRootWithEscapedPipeHolderStartInfo(
        string stdoutMarker,
        string stderrMarker,
        string pidFile,
        bool stripEvaluationRunId = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHangingChildStartInfo(stdoutMarker, stderrMarker, pidFile);
        }

        var escapedLaunch = stripEvaluationRunId
            ? $"(unset {RepositoryGraph.EvaluationRunIdEnvironmentVariable}; exec sleep 60)"
            : "sleep 60";
        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(
            $"({escapedLaunch} & escaped_pid=$!; printf '%s\\n' \"$escaped_pid\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\") & wait $!; escaped_pid=$(cat \"$1\"); printf '%s escaped_pid=%s\\n' \"$2\" \"$escaped_pid\"; printf '%s\\n' \"$3\" >&2; exec sleep 60");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(pidFile);
        unixStartInfo.ArgumentList.Add(stdoutMarker);
        unixStartInfo.ArgumentList.Add(stderrMarker);
        return unixStartInfo;
    }

    private static ProcessStartInfo CreateHangingRootWithReparentedPipeClosedDescendantStartInfo(
        string stdoutMarker,
        string stderrMarker,
        string pidFile)
    {
        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(
            "(sleep 60 </dev/null >/dev/null 2>&1 & escaped_pid=$!; printf '%s\\n' \"$escaped_pid\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\") & wait $!; escaped_pid=$(cat \"$1\"); printf '%s escaped_pid=%s\\n' \"$2\" \"$escaped_pid\"; printf '%s\\n' \"$3\" >&2; exec sleep 60");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(pidFile);
        unixStartInfo.ArgumentList.Add(stdoutMarker);
        unixStartInfo.ArgumentList.Add(stderrMarker);
        return unixStartInfo;
    }

    private static ProcessStartInfo CreateExitedRootWithPipeHoldingAndPipeClosedDescendantsStartInfo(
        string stdoutMarker,
        string stderrMarker,
        string closedPidFile,
        string openPidFile)
    {
        var unixStartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(
            "(sleep 60 </dev/null >/dev/null 2>&1 & closed_pid=$!; printf '%s\\n' \"$closed_pid\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\") & wait $!; closed_pid=$(cat \"$1\"); sleep 60 & open_pid=$!; printf '%s\\n' \"$open_pid\" > \"$2.tmp\" && mv \"$2.tmp\" \"$2\"; printf '%s closed_pid=%s open_pid=%s\\n' \"$3\" \"$closed_pid\" \"$open_pid\"; printf '%s\\n' \"$4\" >&2; exit 0");
        unixStartInfo.ArgumentList.Add("--");
        unixStartInfo.ArgumentList.Add(closedPidFile);
        unixStartInfo.ArgumentList.Add(openPidFile);
        unixStartInfo.ArgumentList.Add(stdoutMarker);
        unixStartInfo.ArgumentList.Add(stderrMarker);
        return unixStartInfo;
    }

    private static int? TryExtractPid(string text, string prefix)
    {
        var pidStart = text.IndexOf(prefix, StringComparison.Ordinal);
        if (pidStart < 0)
        {
            return null;
        }

        var digitsStart = pidStart + prefix.Length;
        var digitsEnd = digitsStart;
        while (digitsEnd < text.Length && char.IsAsciiDigit(text[digitsEnd]))
        {
            digitsEnd++;
        }

        return int.TryParse(
            text.AsSpan(digitsStart, digitsEnd - digitsStart),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var pid)
            ? pid
            : null;
    }

    private static int? TryReadPidFile(string pidFile)
    {
        try
        {
            if (!File.Exists(pidFile))
            {
                return null;
            }

            var content = File.ReadAllText(pidFile).Trim();
            return int.TryParse(content, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<int?> WaitForPidFileAsync(string pidFile, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < maxWait)
        {
            var pid = TryReadPidFile(pidFile);
            if (pid.HasValue)
            {
                return pid;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return TryReadPidFile(pidFile);
    }

    private static async Task CleanupSyntheticProcessAsync(int? knownPid, string pidFile)
    {
        try
        {
            var pid = knownPid ?? TryReadPidFile(pidFile);
            if (pid.HasValue)
            {
                KillProcessBestEffort(pid.Value);
                var exited = await WaitForProcessGoneAsync(pid.Value, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(
                    exited,
                    $"Synthetic test process PID {pid.Value.ToString(CultureInfo.InvariantCulture)} remained alive after cleanup kill.");
            }
        }
        finally
        {
            TryDeleteFile(pidFile);
            TryDeleteFile($"{pidFile}.tmp");
        }
    }

    private static void KillProcessBestEffort(int pid)
    {
        if (IsLinuxZombieOrDeadProcess(pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (AggregateException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AssertRedirectedPipeReadersClosedForProcess(int childPid)
    {
        var pidText = childPid.ToString(CultureInfo.InvariantCulture);
        var childStdoutPipe = TryReadLinkTarget($"/proc/{pidText}/fd/1");
        var childStderrPipe = TryReadLinkTarget($"/proc/{pidText}/fd/2");

        Assert.IsNotNull(childStdoutPipe, $"Expected child PID {pidText} to keep stdout fd open.");
        Assert.IsNotNull(childStderrPipe, $"Expected child PID {pidText} to keep stderr fd open.");
        Assert.IsTrue(
            childStdoutPipe.StartsWith("pipe:[", StringComparison.Ordinal),
            $"Expected child stdout fd to reference an anonymous pipe, got '{childStdoutPipe}'.");
        Assert.IsTrue(
            childStderrPipe.StartsWith("pipe:[", StringComparison.Ordinal),
            $"Expected child stderr fd to reference an anonymous pipe, got '{childStderrPipe}'.");

        var selfOpenTargets = GetCurrentProcessOpenLinkTargets();
        Assert.IsFalse(
            selfOpenTargets.Contains(childStdoutPipe),
            $"Parent test process still holds stdout pipe reader '{childStdoutPipe}' open after timeout.");
        Assert.IsFalse(
            selfOpenTargets.Contains(childStderrPipe),
            $"Parent test process still holds stderr pipe reader '{childStderrPipe}' open after timeout.");
    }

    private static string? TryReadLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashSet<string> GetCurrentProcessOpenLinkTargets()
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            var target = TryReadLinkTarget(entry);
            if (target is not null)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    private static bool IsLinuxZombieOrDeadProcess(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            return IsLinuxStatZombieOrDead(stat);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? TryReadLinuxProcessStat(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static char? TryGetLinuxStatState(string statContent)
    {
        var closeParenIndex = statContent.LastIndexOf(')');
        if (closeParenIndex < 0 || closeParenIndex + 2 >= statContent.Length)
        {
            return null;
        }

        return statContent[closeParenIndex + 2];
    }

    private static bool IsLinuxStatZombieOrDead(string statContent) =>
        TryGetLinuxStatState(statContent) is 'Z' or 'X' or 'x';

    private static async Task<bool> WaitForProcessGoneAsync(int pid, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (IsLinuxZombieOrDeadProcess(pid))
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            if (deadline.Elapsed >= maxWait)
            {
                return false;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
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
        var repository = snapshot.Graph;
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_PUBLISH_EVIDENCE_ROOT");
        var testAssemblies = repository.Projects.Values
            .Where(project => project.Kind == ProjectKind.Test)
            .Select(project => project.AssemblyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hostName in HostProjects.Order(StringComparer.Ordinal))
        {
            var output = string.IsNullOrWhiteSpace(evidenceRoot)
                ? Path.Combine(Path.GetTempPath(), $"skymonitor-architecture-{hostName}-{Guid.NewGuid():N}")
                : hostName switch
                {
                    CameraAgent => Path.Combine(evidenceRoot, "cameraagent"),
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
                foreach (var forbiddenHost in HostProjects.Where(
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
    public void CameraAgentDockerfileDoesNotPackageTestCatalogFixtures()
    {
        var dockerfile = snapshot.Source(Path.Combine(snapshot.Graph.Root, "src", CameraAgent, "Dockerfile")).Text;
        Assert.IsFalse(dockerfile.Contains("tests/fixtures/catalog", StringComparison.OrdinalIgnoreCase),
            $"ARCH-PUBLISH: '{CameraAgent}' Dockerfile must not package a test catalog fixture.");
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
                Path.Combine(snapshot.Graph.Root, "tests", "fixtures", "catalog", "hyg-v42-bright-stars.sqlite"),
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
        IReadOnlyList<string> FrameworkReferences)
    {
        internal string ReleaseAssemblyPath { get; init; } = "";
    }

    private sealed class RepositoryGraph
    {
        internal RepositoryGraph(string root, IReadOnlyDictionary<string, ProjectInfo> projects)
        {
            Root = root;
            Projects = projects;
        }

        public string Root { get; }

        public IReadOnlyDictionary<string, ProjectInfo> Projects { get; }

        public static RepositoryGraph Load(string root)
        {
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
            var evaluationRequests = projectPaths
                .SelectMany(path => SupportedConfigurations.Select(configuration => (Path: path, Configuration: configuration)))
                .ToArray();

            // Evaluation runs no targets. Wait for every worker, cancelling remaining workers promptly on the first failure,
            // then surface results and failures in project and configuration order.
            var allEvaluations = RunWithFailFastWorkersAsync(
                evaluationRequests,
                Math.Clamp(Environment.ProcessorCount, 1, 8),
                static (request, cancellationToken) => EvaluateProjectAsync(request.Path, request.Configuration, cancellationToken))
                .GetAwaiter().GetResult();

            var configurationCount = SupportedConfigurations.Length;
            for (var index = 0; index < projectPaths.Length; index++)
            {
                var path = projectPaths[index];
                var name = Path.GetFileNameWithoutExtension(path)!;
                var relativePath = Path.GetRelativePath(root, path);
                var evaluations = allEvaluations.AsSpan(index * configurationCount, configurationCount).ToArray();
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
                    evaluations.SelectMany(item => item.FrameworkReferences).Distinct(StringComparer.Ordinal).ToArray())
                { ReleaseAssemblyPath = evaluation.TargetPath });
            }

            return new RepositoryGraph(root, projects);
        }

        internal static async Task<TResult[]> RunWithFailFastWorkersAsync<TWork, TResult>(
            IReadOnlyList<TWork> workItems,
            int maxConcurrency,
            Func<TWork, CancellationToken, Task<TResult>> workerAction,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

            using var workers = new SemaphoreSlim(maxConcurrency);
            using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ExceptionDispatchInfo? firstFailure = null;
            var tasks = workItems.Select(work => ExecuteAsync(work)).ToArray();

            await Task.WhenAll(tasks.Cast<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            foreach (var task in tasks)
            {
                if (task.IsFaulted)
                {
                    task.GetAwaiter().GetResult();
                }
            }

            firstFailure?.Throw();

            return tasks.Select(static task => task.GetAwaiter().GetResult()).ToArray();

            async Task<TResult> ExecuteAsync(TWork work)
            {
                await workers.WaitAsync(failFastCts.Token).ConfigureAwait(false);
                try
                {
                    failFastCts.Token.ThrowIfCancellationRequested();
                    return await workerAction(work, failFastCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !failFastCts.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(ex), null);
                    await failFastCts.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    workers.Release();
                }
            }
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

        public string[] ValidateTransitiveBoundaries(IReadOnlySet<string> hosts)
        {
            var violations = new List<string>();
            foreach (var source in Projects.Values.Where(project => project.Kind == ProjectKind.Production))
            {
                foreach (var path in FindReachablePaths(source.Name))
                {
                    var target = Projects[path[^1]];
                    var crossesHostBoundary = hosts.Contains(source.Name) && hosts.Contains(target.Name);
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
            IReadOnlySet<string> cameraAgentProjects)
        {
            var violations = new List<string>();
            var classified = sharedProjects
                .Concat(cameraAgentProjects)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var projectName in classified.Where(name => !Projects.ContainsKey(name)))
            {
                violations.Add($"ARCH-TEST-MISSING: Documented test project '{projectName}' was not found.");
            }

            foreach (var project in Projects.Values.Where(project =>
                project.Kind == ProjectKind.Test &&
                project.Name != TestSupport &&
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
                    reference != TestSupport))
                {
                    violations.Add($"ARCH-TEST-ASSEMBLY: Test project '{projectName}' references test assembly '{reference}'.");
                }

                var reachable = FindReachablePaths(projectName)
                    .Select(path => path[^1])
                    .ToHashSet(StringComparer.Ordinal);
                if (sharedProjects.Contains(projectName) && reachable.Overlaps(CameraAgentImplementations))
                {
                    violations.Add($"ARCH-TEST-SHARED: Shared test project '{projectName}' reaches a host implementation.");
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

        // Tagged descendant cleanup relies on descendants inheriting this environment variable;
        // processes that scrub their environment (for example via `env -i`) and close pipes
        // cannot be enumerated without cgroups or a subreaper, which is why timeout diagnostics
        // report only the root exit, tracked tagged descendants, and pipe closure actually verified.
        internal const string EvaluationRunIdEnvironmentVariable = "HVO_ARCH_EVAL_RUN_ID";
        internal static readonly TimeSpan DefaultEvaluationTimeout = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan PostKillGracePeriod = TimeSpan.FromSeconds(5);

        internal static Task<ProjectEvaluation> EvaluateProjectAsync(
            string projectPath,
            string configuration,
            CancellationToken cancellationToken = default) =>
            EvaluateProjectAsync(projectPath, configuration, DefaultEvaluationTimeout, cancellationToken);

        internal static async Task<ProjectEvaluation> EvaluateProjectAsync(
            string projectPath,
            string configuration,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-nodeReuse:false");
            startInfo.ArgumentList.Add($"-property:Configuration={configuration}");
            startInfo.ArgumentList.Add("-getProperty:AssemblyName,TargetPath");
            startInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference,FrameworkReference");
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

            var result = await RunProcessWithTimeoutAsync(startInfo, projectPath, timeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ARCH-EVALUATE: MSBuild evaluation failed for '{projectPath}'.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
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

            return new ProjectEvaluation(assemblyName, projectReferences, packageReferences, frameworkReferences)
            { TargetPath = root.GetProperty("Properties").GetProperty("TargetPath").GetString()! };
        }

        internal static Task<ProcessExecutionResult> RunProcessWithTimeoutAsync(
            ProcessStartInfo startInfo,
            string operationDescription,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            RunProcessWithTimeoutAsync(startInfo, operationDescription, timeout, PostKillGracePeriod, cancellationToken);

        internal static async Task<ProcessExecutionResult> RunProcessWithTimeoutAsync(
            ProcessStartInfo startInfo,
            string operationDescription,
            TimeSpan timeout,
            TimeSpan postKillGracePeriod,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(postKillGracePeriod, TimeSpan.Zero);
            var effectivePostKillGracePeriod = postKillGracePeriod;
            cancellationToken.ThrowIfCancellationRequested();

            var evaluationRunId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            startInfo.Environment[EvaluationRunIdEnvironmentVariable] = evaluationRunId;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"ARCH-EVALUATE: Failed to start process for '{operationDescription}'.");
            }

            // Drain both pipes concurrently so a failing or timed-out process can write its full diagnostics.
            using var drainCts = new CancellationTokenSource();
            var outputBuffer = new StringBuilder();
            var errorBuffer = new StringBuilder();
            var outputTask = DrainStreamAsync(process.StandardOutput, outputBuffer, drainCts.Token);
            var errorTask = DrainStreamAsync(process.StandardError, errorBuffer, drainCts.Token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var rootExitedBeforeCancellation = false;

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                rootExitedBeforeCancellation = true;
                await Task.WhenAll(outputTask, errorTask).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var timedOut = !cancellationToken.IsCancellationRequested;
                var rootExitedBeforeKill = rootExitedBeforeCancellation || IsProcessExitedSafe(process);
                var killSucceeded = TerminateProcessTree(process);
                var rootExitedAfterKill = await WaitForExitAfterKillAsync(process, effectivePostKillGracePeriod).ConfigureAwait(false);
                var taggedCleanup = await TerminateTaggedDescendantsAsync(evaluationRunId, effectivePostKillGracePeriod).ConfigureAwait(false);
                var pipesDrained = await DrainOutputsAfterKillAsync(
                    process,
                    outputTask,
                    errorTask,
                    drainCts,
                    effectivePostKillGracePeriod).ConfigureAwait(false);

                if (!timedOut)
                {
                    throw;
                }

                var timedOutOutput = SnapshotBuffer(outputBuffer);
                var timedOutError = SnapshotBuffer(errorBuffer);
                var terminationStatus = DescribeTimeoutTermination(
                    rootExitedBeforeKill,
                    killSucceeded,
                    rootExitedAfterKill,
                    taggedCleanup,
                    pipesDrained);
                throw new TimeoutException(
                    $"ARCH-EVALUATE: Process timed out after {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s for '{operationDescription}' {terminationStatus}" +
                    $"{Environment.NewLine}StandardOutput:{Environment.NewLine}{timedOutOutput}" +
                    $"{Environment.NewLine}StandardError:{Environment.NewLine}{timedOutError}");
            }
            finally
            {
                if (!drainCts.IsCancellationRequested)
                {
                    await drainCts.CancelAsync().ConfigureAwait(false);
                }

                CloseRedirectedStreams(process);
            }

            return new ProcessExecutionResult(
                process.ExitCode,
                SnapshotBuffer(outputBuffer),
                SnapshotBuffer(errorBuffer));
        }

        internal readonly record struct TaggedDescendantCleanupResult(
            bool Supported,
            int SignaledCount,
            bool SurvivingDescendantsDetected);

        internal static string DescribeTimeoutTermination(
            bool rootExitedBeforeKill,
            bool killSucceeded,
            bool rootExitedAfterKill,
            TaggedDescendantCleanupResult taggedCleanup,
            bool pipesDrained)
        {
            if (!rootExitedBeforeKill &&
                killSucceeded &&
                rootExitedAfterKill &&
                pipesDrained &&
                taggedCleanup.Supported &&
                !taggedCleanup.SurvivingDescendantsDetected)
            {
                return "and process-tree termination was requested; the root process and all tracked tagged descendants exited and redirected output streams closed.";
            }

            if (rootExitedBeforeKill && !pipesDrained)
            {
                return "while draining redirected output after the root process exited; pipe readers were closed after the bounded drain grace period and descendant processes may still be running.";
            }

            if (rootExitedBeforeKill && taggedCleanup.SurvivingDescendantsDetected)
            {
                return "while draining redirected output after the root process exited; tagged descendant processes remained active after the post-kill grace period.";
            }

            if (rootExitedBeforeKill && taggedCleanup.SignaledCount > 0)
            {
                return "while draining redirected output after the root process exited; surviving tagged descendants were terminated and redirected pipes closed during cleanup, though full process-tree termination could not be established because the root process had already exited.";
            }

            if (rootExitedBeforeKill)
            {
                return "while draining redirected output after the root process exited; redirected pipes closed during cleanup, but process-tree termination could not be established because the root process had already exited.";
            }

            if (!rootExitedAfterKill)
            {
                return "and termination was requested, but the root process did not exit within the post-kill grace period; pipe readers were closed and descendant processes may still be running.";
            }

            if (taggedCleanup.SurvivingDescendantsDetected)
            {
                return "and the root process exited after termination was requested, but tagged descendant processes remained active after the post-kill grace period.";
            }

            if (!pipesDrained)
            {
                return "and the root process exited after termination was requested, but redirected output draining timed out; pipe readers were closed and descendant processes may still be running.";
            }

            if (!killSucceeded)
            {
                return "and the root process exited before process-tree termination could be established.";
            }

            return "and process-tree termination was requested and the root process exited with drained pipes, but reparented descendants could not be verified on this platform.";
        }

        // Linux best-effort cleanup scans /proc/<pid>/environ for descendants that inherited
        // HVO_ARCH_EVAL_RUN_ID. Descendants that clear their environment (for example via `env -i`)
        // cannot be enumerated without cgroups or a subreaper, which is why
        // DescribeTimeoutTermination qualifies its success message to the cleanup actually verified.
        private static async Task<TaggedDescendantCleanupResult> TerminateTaggedDescendantsAsync(
            string evaluationRunId,
            TimeSpan gracePeriod)
        {
            if (!OperatingSystem.IsLinux())
            {
                return new TaggedDescendantCleanupResult(
                    Supported: false,
                    SignaledCount: 0,
                    SurvivingDescendantsDetected: false);
            }

            var expectedEntry = Encoding.UTF8.GetBytes($"{EvaluationRunIdEnvironmentVariable}={evaluationRunId}");
            var signaledPids = new HashSet<int>();
            var deadline = Stopwatch.StartNew();

            while (true)
            {
                var livePids = FindLiveTaggedLinuxProcessIds(expectedEntry);
                if (livePids.Count == 0)
                {
                    return new TaggedDescendantCleanupResult(
                        Supported: true,
                        SignaledCount: signaledPids.Count,
                        SurvivingDescendantsDetected: false);
                }

                foreach (var pid in livePids)
                {
                    signaledPids.Add(pid);
                    KillProcessBestEffort(pid);
                }

                if (deadline.Elapsed >= gracePeriod)
                {
                    var remaining = FindLiveTaggedLinuxProcessIds(expectedEntry);
                    return new TaggedDescendantCleanupResult(
                        Supported: true,
                        SignaledCount: signaledPids.Count,
                        SurvivingDescendantsDetected: remaining.Count > 0);
                }

                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        private static List<int> FindLiveTaggedLinuxProcessIds(ReadOnlySpan<byte> expectedEnvironmentEntry)
        {
            var matches = new List<int>();
            var currentPid = Environment.ProcessId;

            foreach (var entry in Directory.EnumerateDirectories("/proc"))
            {
                var fileName = Path.GetFileName(entry.AsSpan());
                if (!int.TryParse(fileName, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                    pid <= 0 ||
                    pid == currentPid)
                {
                    continue;
                }

                if (IsLinuxZombieOrDeadProcess(pid))
                {
                    continue;
                }

                var environBytes = TryReadProcFileBytes($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/environ");
                if (environBytes is not null && ContainsEnvironmentEntry(environBytes, expectedEnvironmentEntry))
                {
                    matches.Add(pid);
                }
            }

            return matches;
        }

        private static byte[]? TryReadProcFileBytes(string path)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        internal static bool ContainsEnvironmentEntry(ReadOnlySpan<byte> environBytes, ReadOnlySpan<byte> expectedEntry)
        {
            if (expectedEntry.IsEmpty || environBytes.Length < expectedEntry.Length)
            {
                return false;
            }

            var offset = 0;
            while (offset < environBytes.Length)
            {
                var nulIndex = environBytes[offset..].IndexOf((byte)0);
                var entry = nulIndex < 0 ? environBytes[offset..] : environBytes.Slice(offset, nulIndex);
                if (entry.SequenceEqual(expectedEntry))
                {
                    return true;
                }

                if (nulIndex < 0)
                {
                    break;
                }

                offset += nulIndex + 1;
            }

            return false;
        }

        private static bool IsProcessExitedSafe(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        private static bool TerminateProcessTree(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Process already exited before Kill could signal it.
                return false;
            }
            catch (Win32Exception) when (IsProcessExitedSafe(process))
            {
                // Process exited concurrently while terminating the tree.
                return false;
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is InvalidOperationException or Win32Exception) && IsProcessExitedSafe(process))
            {
                // Processes in the tree exited concurrently while terminating.
                return false;
            }
        }

        private static async Task<bool> WaitForExitAfterKillAsync(Process process, TimeSpan gracePeriod)
        {
            using var exitCts = new CancellationTokenSource(gracePeriod);
            try
            {
                await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (exitCts.IsCancellationRequested)
            {
                // Bounded post-kill wait expired; continue so the timeout error is still surfaced.
                return IsProcessExitedSafe(process);
            }
        }

        private static async Task<bool> DrainOutputsAfterKillAsync(
            Process process,
            Task outputTask,
            Task errorTask,
            CancellationTokenSource drainCts,
            TimeSpan gracePeriod)
        {
            try
            {
                await Task.WhenAll(outputTask, errorTask).WaitAsync(gracePeriod).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                await drainCts.CancelAsync().ConfigureAwait(false);
                CloseRedirectedStreams(process);
                try
                {
                    await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Pipe readers were closed and cancellation was requested; do not block indefinitely.
                }

                return false;
            }
        }

        private static void CloseRedirectedStreams(Process process)
        {
            if (process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    process.StandardOutput.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (process.StartInfo.RedirectStandardError)
            {
                try
                {
                    process.StandardError.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static async Task DrainStreamAsync(
            StreamReader reader,
            StringBuilder destination,
            CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    lock (destination)
                    {
                        destination.Append(buffer, 0, read);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Post-kill drain window expired; preserve everything captured so far.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // Process stream closed when Process was disposed after drain cancellation.
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                // Pipe closed after drain cancellation.
            }
        }

        private static string SnapshotBuffer(StringBuilder buffer)
        {
            lock (buffer)
            {
                return buffer.ToString();
            }
        }

        internal sealed record ProcessExecutionResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);

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
            string[] FrameworkReferences)
        {
            internal string TargetPath { get; init; } = "";
        }
    }
}
