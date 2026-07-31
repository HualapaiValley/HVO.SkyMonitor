using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.TestSupport;

public static class EvidenceSourceIdentity
{
    public static async Task WriteJsonAsync(
        string path,
        object evidence,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            stream, evidence, evidence.GetType(), options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<EvidenceSourceSnapshot> CaptureAsync(
        string repositoryRoot,
        params Type[] assemblyMarkers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(assemblyMarkers);

        var head = (await RunGitAsync(repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var requestedRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        if (!string.IsNullOrWhiteSpace(requestedRevision))
        {
            var resolved = (await RunGitAsync(
                repositoryRoot, "rev-parse", $"{requestedRevision}^{{commit}}").ConfigureAwait(false)).Trim();
            if (!string.Equals(resolved, head, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "HVO_EVIDENCE_REVISION does not resolve to the checked-out commit.");
            }
        }

        var branch = (await RunGitAsync(
            repositoryRoot, "branch", "--show-current").ConfigureAwait(false)).Trim();
        var status = await RunGitAsync(
            repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunGitAsync(
            repositoryRoot, "diff", "--binary", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
        var untrackedOutput = await RunGitAsync(
            repositoryRoot, "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
        var untrackedPaths = untrackedOutput.Split(
                '\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(fingerprint, status);
        Append(fingerprint, diff);
        foreach (var relativePath in untrackedPaths)
        {
            Append(fingerprint, relativePath);
            fingerprint.AppendData(await File.ReadAllBytesAsync(
                Path.Combine(repositoryRoot, relativePath)).ConfigureAwait(false));
        }
        var dirtyFingerprint = Convert.ToHexString(fingerprint.GetHashAndReset());
        var dirty = !string.IsNullOrWhiteSpace(status);
        var assemblies = assemblyMarkers
            .Append(typeof(EvidenceSourceIdentity))
            .Select(static marker => marker.Assembly)
            .Distinct()
            .OrderBy(static assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .Select(assembly => CreateAssemblySnapshot(repositoryRoot, assembly))
            .ToArray();
        var invalidAssemblies = assemblies.Where(assembly =>
            !string.Equals(assembly.Configuration, "Release", StringComparison.Ordinal)
            || !assembly.InformationalVersion.Contains(head, StringComparison.OrdinalIgnoreCase)
            || assembly.AssemblyWrittenUtc < assembly.LatestSourceWriteUtc).ToArray();
        if (invalidAssemblies.Length > 0)
        {
            throw new InvalidOperationException(
                "Evidence assemblies must be Release binaries rebuilt from the current source at the checked-out HEAD: "
                + string.Join(", ", invalidAssemblies.Select(assembly =>
                    $"{assembly.Name} (configuration={assembly.Configuration}, informational={assembly.InformationalVersion}, "
                    + $"assembly_utc={assembly.AssemblyWrittenUtc:O}, latest_source_utc={assembly.LatestSourceWriteUtc:O})")));
        }

        var runId = CreateRunId();
        var claimability = dirty
            ? "dirty-development-not-claimable"
            : string.IsNullOrWhiteSpace(requestedRevision)
                ? "clean-unrequested-not-claimable"
                : "clean-source-attributed-review-required";

        return new EvidenceSourceSnapshot(
            requestedRevision,
            head,
            branch,
            dirty,
            dirtyFingerprint,
            dirty ? $"{head}-dirty-{dirtyFingerprint[..12]}" : head,
            runId.Id,
            runId.Trial,
            claimability,
            assemblies);
    }

    private static EvidenceAssemblySnapshot CreateAssemblySnapshot(string repositoryRoot, Assembly assembly)
    {
        var location = assembly.Location;
        var name = assembly.GetName().Name ?? "unknown";
        var sourceDirectory = new[]
        {
            Path.Combine(repositoryRoot, "src", name),
            Path.Combine(repositoryRoot, "tests", name)
        }.SingleOrDefault(Directory.Exists) ?? throw new InvalidOperationException(
            $"Unable to locate the source project for evidence assembly '{name}'.");
        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetExtension(path) is ".cs" or ".razor" or ".csproj" or ".props" or ".targets")
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var sourceFingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sourceFile in sourceFiles)
        {
            Append(sourceFingerprint, Path.GetRelativePath(repositoryRoot, sourceFile));
            sourceFingerprint.AppendData(File.ReadAllBytes(sourceFile));
        }
        using var stream = File.OpenRead(location);
        return new EvidenceAssemblySnapshot(
            name,
            Convert.ToHexString(SHA256.HashData(stream)),
            assembly.ManifestModule.ModuleVersionId,
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Convert.ToHexString(sourceFingerprint.GetHashAndReset()),
            File.GetLastWriteTimeUtc(location),
            sourceFiles.Max(File.GetLastWriteTimeUtc));
    }

    private static void Append(IncrementalHash fingerprint, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fingerprint.AppendData(BitConverter.GetBytes(bytes.Length));
        fingerprint.AppendData(bytes);
    }

    private static (string Id, int? Trial) CreateRunId()
    {
        var trialValue = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        if (string.IsNullOrWhiteSpace(trialValue))
        {
            return ($"run-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}", null);
        }
        if (!int.TryParse(
                trialValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var trial)
            || trial is < 1 or > 5)
        {
            throw new InvalidOperationException("HVO_EVIDENCE_TRIAL must be an integer from 1 through 5.");
        }
        return ($"trial-{trial}", trial);
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start git for evidence provenance.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        }
        return output;
    }
}

public sealed record EvidenceSourceSnapshot(
    string? RequestedRevision,
    string Head,
    string Branch,
    bool Dirty,
    string DirtyDiffSha256,
    string OutputDirectoryName,
    string RunId,
    int? Trial,
    string Claimability,
    IReadOnlyList<EvidenceAssemblySnapshot> Assemblies);

public sealed record EvidenceAssemblySnapshot(
    string Name,
    string Sha256,
    Guid ModuleVersionId,
    string Configuration,
    string InformationalVersion,
    string SourceSha256,
    DateTime AssemblyWrittenUtc,
    DateTime LatestSourceWriteUtc);

public static class EvidenceTaskCleanup
{
    public static async Task<T> AwaitAsync<T>(
        Task<T> task,
        CancellationTokenSource cancellation,
        TimeSpan timeout)
    {
        try
        {
            return await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            _ = await task.ConfigureAwait(false);
            throw;
        }
    }

    public static async Task DrainAsync(
        IEnumerable<Task> tasks,
        CancellationTokenSource cancellation,
        TimeSpan timeout)
    {
        var completion = Task.WhenAll(tasks);
        try
        {
            await completion.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
            throw;
        }
    }
}
