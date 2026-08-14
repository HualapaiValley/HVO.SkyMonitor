using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.TestSupport;

public static partial class Phase14ScenarioEvidence
{
    private const int MaximumAssertions = 64;
    private const int MaximumMeasurements = 64;

    public static async Task RecordAsync(
        string scenarioId,
        string observationId,
        string? caseSelector,
        IReadOnlyList<string> passedAssertions,
        IReadOnlyList<Phase14EvidenceMeasurement>? measurements = null)
    {
        var root = Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        ValidateIdentifier(scenarioId, nameof(scenarioId));
        observationId = NormalizeIdentifier(observationId, nameof(observationId));
        var observationPrefix = Environment.GetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX");
        if (observationPrefix is not null)
        {
            ValidateIdentifier(observationPrefix, "HVO_PHASE14_OBSERVATION_PREFIX");
            observationId = $"{observationPrefix}-{observationId}";
            ValidateIdentifier(observationId, nameof(observationId));
        }
        caseSelector = NormalizeCaseSelector(caseSelector, nameof(caseSelector));
        ArgumentNullException.ThrowIfNull(passedAssertions);
        if (passedAssertions.Count is < 1 or > MaximumAssertions)
        {
            throw new ArgumentException("Phase 14 evidence requires one to 64 assertions.", nameof(passedAssertions));
        }
        var normalizedAssertions = passedAssertions
            .Select(assertion => NormalizeIdentifier(assertion, nameof(passedAssertions)))
            .ToArray();
        if (normalizedAssertions.Distinct(StringComparer.Ordinal).Count() != normalizedAssertions.Length)
        {
            throw new ArgumentException("Phase 14 evidence assertions must remain unique after normalization.", nameof(passedAssertions));
        }

        measurements ??= [];
        if (measurements.Count > MaximumMeasurements)
        {
            throw new ArgumentException("Phase 14 evidence permits at most 64 measurements.", nameof(measurements));
        }
        var normalizedMeasurements = measurements.Select(measurement => measurement with
        {
            Id = NormalizeIdentifier(measurement.Id, nameof(measurements))
        }).ToArray();
        if (normalizedMeasurements.Select(static measurement => measurement.Id).Distinct(StringComparer.Ordinal).Count() !=
            normalizedMeasurements.Length)
        {
            throw new ArgumentException("Phase 14 evidence measurement identifiers must remain unique after normalization.", nameof(measurements));
        }
        foreach (var measurement in normalizedMeasurements)
        {
            if (!AllowedUnits.Contains(measurement.Unit, StringComparer.Ordinal) || measurement.Value < 0)
            {
                throw new ArgumentException("Phase 14 evidence measurements require a supported unit and non-negative value.", nameof(measurements));
            }
        }

        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException("HVO_PHASE14_EVIDENCE_ROOT must be an absolute path.");
        }
        var allowedRoot = Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT");
        if (string.IsNullOrWhiteSpace(allowedRoot) || !Path.IsPathFullyQualified(allowedRoot))
        {
            throw new InvalidOperationException("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT must be an absolute path.");
        }
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!root.StartsWith(string.Concat(allowedRoot, Path.DirectorySeparatorChar), pathComparison))
        {
            throw new InvalidOperationException("HVO_PHASE14_EVIDENCE_ROOT must be a child of HVO_PHASE14_EVIDENCE_ALLOWED_ROOT.");
        }
        var revision = RequireDigest("HVO_PHASE14_SOURCE_REVISION", 40);
        var tree = RequireDigest("HVO_PHASE14_SOURCE_TREE", 40);
        var fragments = Path.Combine(root, "fragments");
        ValidatePrivateDirectory(allowedRoot);
        ValidatePrivateDirectory(root);
        ValidatePrivateDirectory(fragments);
        ValidatePathComponents(allowedRoot, fragments);

        var observationHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observationId)));
        var path = Path.Combine(fragments, $"{scenarioId}--{observationHash}.json");
        var fragment = new Phase14EvidenceFragment(
            1,
            scenarioId,
            observationId,
            caseSelector,
            revision,
            tree,
            normalizedAssertions.Select(static assertion => new Phase14EvidenceAssertion(assertion, true)).ToArray(),
            normalizedMeasurements);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            fragment,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var temporaryPath = Path.Combine(fragments, $".{scenarioId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            var stream = new FileStream(temporaryPath, fileOptions);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch (IOException)
        {
            if (!File.Exists(path) || !(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
            {
                throw;
            }
            // Repeated identical data-row observations are idempotent.
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static readonly string[] AllowedUnits =
    [
        "milliseconds",
        "seconds",
        "bytes",
        "captures",
        "captures-per-second",
        "count",
        "percent"
    ];

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new ArgumentException("Phase 14 evidence identifiers must be lowercase kebab-case and at most 128 characters.", parameterName);
        }
    }

    private static string NormalizeIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = IdentifierSeparatorRegex().Replace(value, "-");
        normalized = PascalCaseBoundaryRegex().Replace(normalized, "$1-$2");
#pragma warning disable CA1308 // The evidence contract deliberately requires lowercase kebab-case identifiers.
        normalized = normalized.Trim('-').ToLowerInvariant();
#pragma warning restore CA1308
        ValidateIdentifier(normalized, parameterName);
        return normalized;
    }

    private static string? NormalizeCaseSelector(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        if (!CaseSelectorRegex().IsMatch(value))
        {
            throw new ArgumentException(
                "Phase 14 evidence case selectors must contain only letters, numbers, periods, underscores, or hyphens and be at most 128 characters.",
                parameterName);
        }
        return value;
    }

    private static string RequireDigest(string name, int length)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"{name} must be a {length}-character hexadecimal Git identity.");
        }
        return Convert.ToHexStringLower(Convert.FromHexString(value));
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void ValidatePrivateDirectory(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
        {
            throw new InvalidOperationException("Phase 14 evidence directories must be pre-created regular directories.");
        }
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) !=
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            throw new InvalidOperationException("Phase 14 evidence directories must use mode 0700.");
        }
    }

    private static void ValidatePathComponents(string allowedRoot, string path)
    {
        var relative = Path.GetRelativePath(allowedRoot, path);
        var current = allowedRoot;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsReparsePoint(current))
            {
                throw new InvalidOperationException("Phase 14 evidence paths must not contain symbolic links or reparse points.");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierSeparatorRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex PascalCaseBoundaryRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseSelectorRegex();
}

public sealed record Phase14EvidenceMeasurement(string Id, long Value, string Unit);

public sealed record Phase14EvidenceAssertion(string Id, bool Passed);

public sealed record Phase14EvidenceFragment(
    int SchemaVersion,
    string ScenarioId,
    string ObservationId,
    string? CaseSelector,
    string SourceRevision,
    string SourceTree,
    IReadOnlyList<Phase14EvidenceAssertion> Assertions,
    IReadOnlyList<Phase14EvidenceMeasurement> Measurements);
