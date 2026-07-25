using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed class Issue170PerformanceEvidence
{
    private static FileStream? exclusiveProcessLock;
    private static readonly string[] HarnessOnlyFiles =
    [
        "tests/HVO.SkyMonitor.IntegrationTests/AssemblyHooks.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/DeviceBootstrapPerformanceTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/DeploymentLocationAuthorityPerformanceTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/IntegrationTestFixture.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/Issue170AllocationSampler.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/Issue170PerformanceEvidence.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/Issue170PerformanceSummaryTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/LogicHostIngestPerformanceTests.cs"
    ];
    private static readonly string[] ComparableHarnessFiles = HarnessOnlyFiles
        .Where(path => !path.EndsWith("DeploymentLocationAuthorityPerformanceTests.cs", StringComparison.Ordinal))
        .ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private Issue170PerformanceEvidence(
        string repositoryRoot,
        string commit,
        string productionCommit,
        string branch,
        int trial,
        IReadOnlyList<AssemblyEvidence> assemblies,
        EnvironmentEvidence environment,
        string harnessSourceSha256)
    {
        RepositoryRoot = repositoryRoot;
        Commit = commit;
        ProductionCommit = productionCommit;
        Branch = branch;
        Trial = trial;
        Assemblies = assemblies;
        Environment = environment;
        HarnessSourceSha256 = harnessSourceSha256;
        RunId = Guid.NewGuid();
        using var process = Process.GetCurrentProcess();
        ProcessId = process.Id;
        ProcessStartUtc = process.StartTime.ToUniversalTime();
    }

    public string RepositoryRoot { get; }
    public string Commit { get; }
    public string ProductionCommit { get; }
    public string Branch { get; }
    public int Trial { get; }
    public IReadOnlyList<AssemblyEvidence> Assemblies { get; }
    public EnvironmentEvidence Environment { get; }
    public string HarnessSourceSha256 { get; }
    public Guid RunId { get; }
    public int ProcessId { get; }
    public DateTime ProcessStartUtc { get; }
    public static string BuildCommand => "dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release --no-incremental -warnaserror";

    public string CreateTestCommand(string fullyQualifiedName)
        => $"DOTNET_gcServer=1 HVO_EVIDENCE_REVISION={Commit} "
            + $"HVO_EVIDENCE_PRODUCTION_REVISION={ProductionCommit} HVO_EVIDENCE_TRIAL={Trial} "
            + "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj "
            + $"--no-build --configuration Release --filter FullyQualifiedName~{fullyQualifiedName}";

    public object Revision => new
    {
        Commit,
        ProductionCommit,
        Branch,
        Dirty = false,
        Trial,
        HarnessSourceSha256,
        RunId,
        ProcessId,
        ProcessStartUtc,
        EvidenceCompletedUtc = DateTimeOffset.UtcNow,
        Assemblies
    };

    public static Issue170PerformanceEvidence Create()
    {
        var root = FindRepositoryRoot();
        AcquireExclusiveProcessLock();
        var commit = RunGit(root, "rev-parse", "HEAD");
        var requestedRevision = System.Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        if (string.IsNullOrWhiteSpace(requestedRevision))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION must identify the exact committed revision under test.");
        }
        var resolvedRevision = RunGit(root, "rev-parse", $"{requestedRevision}^{{commit}}");
        if (!string.Equals(resolvedRevision, commit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION does not resolve to the checked-out commit.");
        }
        if (!string.IsNullOrWhiteSpace(RunGit(root, "status", "--porcelain", "--untracked-files=all")))
        {
            throw new InvalidOperationException("Reviewed performance evidence requires a clean worktree.");
        }
        var requestedProductionRevision = System.Environment.GetEnvironmentVariable(
            "HVO_EVIDENCE_PRODUCTION_REVISION");
        if (string.IsNullOrWhiteSpace(requestedProductionRevision))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_PRODUCTION_REVISION must identify the exact production revision under test.");
        }
        var productionCommit = RunGit(root, "rev-parse", $"{requestedProductionRevision}^{{commit}}");
        _ = RunGit(root, "merge-base", "--is-ancestor", productionCommit, commit);
        var changedFiles = RunGit(root, "diff", "--name-only", productionCommit, commit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (changedFiles.Any(path => !HarnessOnlyFiles.Contains(path, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "The evidence revision differs from its production revision outside the explicit harness-only file set.");
        }
        if (!int.TryParse(
                System.Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var trial)
            || trial is < 1 or > 5)
        {
            throw new InvalidOperationException("HVO_EVIDENCE_TRIAL must be an integer from 1 through 5.");
        }

        var assemblies = Directory.EnumerateFiles(
                AppContext.BaseDirectory, "HVO.SkyMonitor.*.dll", SearchOption.TopDirectoryOnly)
            .Select(Assembly.LoadFrom)
            .DistinctBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .Select(CreateAssemblyEvidence)
            .ToArray();
        if (assemblies.Any(assembly => !string.Equals(assembly.Configuration, "Release", StringComparison.Ordinal)
            || !assembly.InformationalVersion.Contains(commit, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Every evidence assembly must be a Release binary built from the exact evidence harness commit.");
        }
        var environment = CreateEnvironmentEvidence(root);
        var branch = RunGit(root, "branch", "--show-current");
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new InvalidOperationException("Reviewed performance evidence requires a named branch.");
        }
        return new Issue170PerformanceEvidence(
            root,
            commit,
            productionCommit,
            branch,
            trial,
            assemblies,
            environment,
            CreateHarnessSourceSha256(root));
    }

    public async Task WriteTrialAsync(string fileName, object evidence)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new ArgumentException("Evidence file name must be one JSON file name.", nameof(fileName));
        }
        var issueRoot = Path.GetFullPath(Path.Combine(RepositoryRoot, "TestResults", "issue-170"));
        var outputDirectory = Path.GetFullPath(Path.Combine(
            issueRoot,
            Commit,
            $"trial-{Trial:D2}"));
        var issuePrefix = string.Concat(Path.TrimEndingDirectorySeparator(issueRoot), Path.DirectorySeparatorChar);
        if (!outputDirectory.StartsWith(issuePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Evidence output escaped the issue-170 result directory.");
        }
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, fileName);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static AssemblyEvidence CreateAssemblyEvidence(Assembly assembly)
    {
        var path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"Assembly {assembly.GetName().Name} has no attributable file.");
        }
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? "unknown";
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        return new AssemblyEvidence(
            assembly.GetName().Name ?? "unknown",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            assembly.ManifestModule.ModuleVersionId,
            configuration,
            informationalVersion);
    }

    private static EnvironmentEvidence CreateEnvironmentEvidence(string root)
    {
        var declaredSdk = ReadSdk(root);
        var executingSdk = RunCommand(root, "dotnet", "--version");
        var storage = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat;
        var storageType = new DriveInfo(Path.GetPathRoot(root)!).DriveType.ToString();
        var runtimeVersion = System.Environment.Version.ToString();
        var dockerVersion = RunOptionalCommand(root, "docker", "version", "--format", "{{.Server.Version}}");
        var cpuLimit = ReadOptionalText("/sys/fs/cgroup/cpu.max");
        var memoryLimit = ReadOptionalText("/sys/fs/cgroup/memory.max");
        var containerImages = ReadContainerImages(root);
        var observedContainers = RunOptionalCommand(
                root, "docker", "ps", "--format", "{{.Image}}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(declaredSdk, executingSdk, StringComparison.Ordinal)
            || !GCSettings.IsServerGC
            || string.Equals(dockerVersion, "unavailable", StringComparison.Ordinal)
            || containerImages.Any(image => !observedContainers.Contains(image, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "Reviewed evidence requires the pinned SDK, server GC, Docker, and all declared service containers.");
        }
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["configuration"] = "Release",
            ["cpu"] = ReadCpuModel(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["runtimeVersion"] = runtimeVersion,
            ["memoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ["memoryLimit"] = memoryLimit,
            ["operatingSystem"] = RuntimeInformation.OSDescription,
            ["processorCount"] = System.Environment.ProcessorCount,
            ["cpuLimit"] = cpuLimit,
            ["declaredSdk"] = declaredSdk,
            ["dockerVersion"] = dockerVersion,
            ["executingSdk"] = executingSdk,
            ["serverGc"] = GCSettings.IsServerGC,
            ["storageFormat"] = storage,
            ["storageType"] = storageType,
            ["containerImages"] = containerImages,
            ["observedContainers"] = observedContainers
        };
        var canonical = JsonSerializer.Serialize(values);
        return new EnvironmentEvidence(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadCpuModel(),
            System.Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.FrameworkDescription,
            runtimeVersion,
            GCSettings.IsServerGC,
            declaredSdk,
            executingSdk,
            dockerVersion,
            cpuLimit,
            memoryLimit,
            storage,
            storageType,
            containerImages,
            observedContainers,
            "Release",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static string ReadSdk(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not specify an SDK version.");
    }

    private static string ReadCpuModel()
    {
        const string path = "/proc/cpuinfo";
        if (!File.Exists(path))
        {
            return "unavailable";
        }
        var line = File.ReadLines(path).FirstOrDefault(static candidate =>
            candidate.StartsWith("model name", StringComparison.Ordinal));
        return line?.Split(':', 2)[1].Trim() ?? "unavailable";
    }

    private static string CreateHarnessSourceSha256(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in ComparableHarnessFiles.Order(StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            hash.AppendData(pathBytes);
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(Path.Combine(root, relativePath)));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ResolveCommit(string root, string revision)
        => RunGit(root, "rev-parse", $"{revision}^{{commit}}");

    internal static void RequireAncestor(string root, string ancestor, string descendant)
        => _ = RunGit(root, "merge-base", "--is-ancestor", ancestor, descendant);

    internal static void AcquireExclusiveProcessLock()
    {
        if (exclusiveProcessLock is not null)
        {
            return;
        }
        try
        {
            exclusiveProcessLock = new FileStream(
                Path.Combine(Path.GetTempPath(), "hvo-issue-170-performance.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another issue #170 performance evidence process is already running on this machine.", exception);
        }
    }

    private static string[] ReadContainerImages(string root)
        => System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(Path.Combine(
                    root, "tests", "HVO.SkyMonitor.IntegrationTests", "IntegrationTestFixture.cs")),
                "\\.WithImage\\(\"(?<image>[^\"]+)\"\\)")
            .Select(match => match.Groups["image"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ReadOptionalText(string path)
        => File.Exists(path) ? File.ReadAllText(path).Trim() : "unavailable";

    private static string RunOptionalCommand(string root, string fileName, params string[] arguments)
    {
        try
        {
            return RunCommand(root, fileName, arguments);
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static string RunCommand(string root, string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string RunGit(string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    internal sealed record AssemblyEvidence(
        string Name,
        string Sha256,
        Guid ModuleVersionId,
        string Configuration,
        string InformationalVersion);

    internal sealed record EnvironmentEvidence(
        string OperatingSystem,
        string Architecture,
        string Cpu,
        int ProcessorCount,
        long TotalAvailableMemoryBytes,
        string Framework,
        string RuntimeVersion,
        bool ServerGarbageCollection,
        string DeclaredSdk,
        string ExecutingSdk,
        string DockerVersion,
        string CpuLimit,
        string MemoryLimit,
        string StorageFormat,
        string StorageType,
        IReadOnlyList<string> DeclaredContainerImages,
        IReadOnlyList<string> ObservedContainers,
        string Configuration,
        string FingerprintSha256);
}
