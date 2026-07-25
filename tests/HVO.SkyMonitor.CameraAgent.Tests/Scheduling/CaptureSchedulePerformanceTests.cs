using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CaptureSchedulePerformanceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int WarmupOperations = 1_000;
    private const int MeasuredOperations = 10_000;
    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();

    [TestMethod]
    public async Task RepresentativeFixedSolarDstAndNoEventCases_WritePerformanceEvidence()
    {
        if (!string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Issue #207 performance evidence requires a Release build.");
        }
        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision();
        var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
        var calculator = new AstronomyEngineSolarEventCalculator();
        var scenarios = CreateScenarios().Select(scenario => PrepareScenario(scenario, calculator)).ToArray();
        var cases = new List<CaseEvidence>(scenarios.Length);

        foreach (var prepared in scenarios)
        {
            var scenario = prepared.Scenario;
            var previewMeasurement = Measure(
                () => CaptureScheduleIntervalExpander.Expand(
                    scenario.Definition,
                    scenario.FirstLocalDate,
                    scenario.PreviewDayCount,
                    scenario.TimeZone,
                    scenario.Observer,
                    calculator),
                actual => EnsureEquivalent(prepared.Preview, actual),
                prepared.Preview.ExpansionSha256);
            var request = new CaptureScheduleEvaluationRequest(
                prepared.EvaluationUtc,
                CaptureScheduleSafetyState.Available,
                ManualPaused: false);
            var evaluationMeasurement = Measure(
                () => CaptureScheduleEvaluator.Evaluate(scenario.Definition, prepared.Preview, request),
                actual => EnsureEquivalent(prepared.Decision, actual),
                prepared.DecisionSha256);

            cases.Add(new CaseEvidence(
                scenario.Id,
                scenario.Description,
                scenario.TimeZone.Id,
                scenario.Observer,
                scenario.FirstLocalDate,
                scenario.PreviewDayCount,
                scenario.Definition,
                prepared.EvaluationUtc,
                prepared.Preview.ScheduleRevisionSha256,
                prepared.Preview.ExpansionSha256,
                prepared.PreviewSha256,
                prepared.Preview.TimeZoneRuleSha256,
                prepared.Preview.SolarAlgorithmVersion,
                prepared.Preview.Intervals.Count,
                prepared.Preview.UnavailableWindows.Count,
                prepared.DecisionSha256,
                prepared.Decision.Admitted,
                prepared.Decision.Reason.ToString(),
                prepared.Decision.Interval?.Source.ToString(),
                prepared.Decision.SetpointProfileId,
                prepared.Decision.NextTransitionUtc,
                previewMeasurement,
                evaluationMeasurement));
        }

        var exactCommand = $"DOTNET_gcServer=1 HVO_EVIDENCE_REVISION={revision} dotnet test " +
            "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build " +
            "--configuration Release --filter \"FullyQualifiedName=" +
            "HVO.SkyMonitor.CameraAgent.Tests.Scheduling.CaptureSchedulePerformanceTests." +
            "RepresentativeFixedSolarDstAndNoEventCases_WritePerformanceEvidence\"";
        var evidence = new
        {
            Issue = 207,
            Scope = "Pure capture schedule preview and evaluator performance",
            Revision = revision,
            Source = new
            {
                Git = git,
                Assemblies = new[]
                {
                    ReadAssemblyEvidence(typeof(CaptureSchedulePerformanceTests)),
                    ReadAssemblyEvidence(typeof(CaptureScheduleIntervalExpander)),
                    ReadAssemblyEvidence(typeof(CaptureScheduleDefinition)),
                    ReadAssemblyEvidence(typeof(AstronomyEngineSolarEventCalculator))
                }
            },
            GeneratedUtc = DateTimeOffset.UtcNow,
            ExactCommand = exactCommand,
            Baseline = new
            {
                Revision = "N/A",
                Measurements = "N/A",
                Reason = "The local capture schedule preview and evaluator are net-new production paths in issue #207."
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                RuntimeVersion = Environment.Version.ToString(),
                Configuration = BuildConfiguration,
                PinnedSdk = ReadPinnedSdkVersion(repositoryRoot),
                ServerGc = GCSettings.IsServerGC,
                GcServerEnvironment = Environment.GetEnvironmentVariable("DOTNET_gcServer"),
                ProcessorCount = Environment.ProcessorCount,
                Cpu = ReadCpuModel(),
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                InitialWorkingSetBytes = Environment.WorkingSet,
                StopwatchFrequency = Stopwatch.Frequency,
                ExecutionMode = "Native dotnet test process; no application container or external service"
            },
            WorkloadManifest = new
            {
                WarmupOperationsPerOperationPerCase = WarmupOperations,
                MeasuredOperationsPerOperationPerCase = MeasuredOperations,
                DeterminismVerificationOperationsPerOperationPerCase = MeasuredOperations,
                Operations = new[] { "CaptureScheduleIntervalExpander.Expand", "CaptureScheduleEvaluator.Evaluate" },
                PreviewAndEvaluationMeasuredSeparately = true,
                Concurrency = 1,
                Calculator = nameof(AstronomyEngineSolarEventCalculator),
                SolarAlgorithmVersion = AstronomyEngineSolarEventCalculator.Version,
                TimeZoneProvider = "System TimeZoneInfo; exact rule identity is recorded per case",
                MeasurementBoundary = "Per-call latency, batch throughput, process CPU, current-thread allocations, and RSS measure only production calls plus timing/loop bookkeeping. A separate equal-size pass structurally verifies every result.",
                Cases = cases.Select(static item => new
                {
                    item.Id,
                    item.Description,
                    item.TimeZoneId,
                    item.Observer,
                    item.FirstLocalDate,
                    item.PreviewDayCount,
                    item.Definition,
                    item.EvaluationUtc
                }).ToArray()
            },
            Correctness = new
            {
                ResultVerification = "Every warmup result and every result from a separate 10,000-operation verification pass is structurally compared with the prepared deterministic result.",
                PreviewIdentity = "Schedule revision, expansion, timezone-rule, solar-version, bounds, intervals, and unavailable windows are compared.",
                EvaluationIdentity = "The complete decision record is compared and its canonical JSON SHA-256 is recorded.",
                ExpectedSemantics = "Phoenix fixed and solar windows admit; New York skipped and ambiguous boundaries resolve to pinned UTC intervals; Tromso midnight sun reports NoSolarEvent and remains closed."
            },
            Cases = cases
        };

        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-207", revision);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "capture-schedule-evaluator-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions) + Environment.NewLine).ConfigureAwait(false);
    }

    private static PreparedScenario PrepareScenario(
        PerformanceScenario scenario,
        AstronomyEngineSolarEventCalculator calculator)
    {
        var preview = CaptureScheduleIntervalExpander.Expand(
            scenario.Definition,
            scenario.FirstLocalDate,
            scenario.PreviewDayCount,
            scenario.TimeZone,
            scenario.Observer,
            calculator);
        var evaluationUtc = scenario.EvaluationUtc ?? Midpoint(preview.Intervals.Single());
        var decision = CaptureScheduleEvaluator.Evaluate(
            scenario.Definition,
            preview,
            new CaptureScheduleEvaluationRequest(
                evaluationUtc,
                CaptureScheduleSafetyState.Available,
                ManualPaused: false));

        AssertExpectedSemantics(scenario, preview, decision, evaluationUtc);
        return new PreparedScenario(
            scenario,
            preview,
            evaluationUtc,
            decision,
            CaptureContractJson.ComputeCanonicalJsonSha256(preview),
            CaptureContractJson.ComputeCanonicalJsonSha256(decision));
    }

    private static OperationMeasurement Measure<T>(
        Func<T> operation,
        Action<T> verify,
        string deterministicIdentitySha256)
    {
        for (var index = 0; index < WarmupOperations; index++)
        {
            verify(operation());
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var samples = new double[MeasuredOperations];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var batchStarted = Stopwatch.GetTimestamp();
        T? lastResult = default;
        for (var index = 0; index < MeasuredOperations; index++)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            lastResult = operation();
            samples[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds * 1_000d;
        }
        var batchElapsed = Stopwatch.GetElapsedTime(batchStarted);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        process.Refresh();
        var cpuElapsed = process.TotalProcessorTime - cpuBefore;
        var rssAfter = process.WorkingSet64;

        verify(lastResult!);
        for (var index = 0; index < MeasuredOperations; index++)
        {
            verify(operation());
        }

        Array.Sort(samples);
        return new OperationMeasurement(
            WarmupOperations,
            MeasuredOperations,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            batchElapsed.TotalMilliseconds,
            MeasuredOperations / batchElapsed.TotalSeconds,
            cpuElapsed.TotalMilliseconds,
            cpuElapsed.TotalMilliseconds * 1_000d / MeasuredOperations,
            allocatedBytes,
            allocatedBytes / (double)MeasuredOperations,
            rssBefore,
            rssAfter,
            rssAfter - rssBefore,
            deterministicIdentitySha256);
    }

    private static void AssertExpectedSemantics(
        PerformanceScenario scenario,
        CaptureSchedulePreview preview,
        CaptureScheduleDecision decision,
        DateTimeOffset evaluationUtc)
    {
        Assert.AreEqual(CaptureScheduleContract.ComputeSha256(scenario.Definition), preview.ScheduleRevisionSha256);
        Assert.AreEqual(CaptureScheduleIntervalExpander.AlgorithmVersion, preview.ExpansionAlgorithmVersion);
        Assert.AreEqual(scenario.ExpectedSolarAlgorithmVersion, preview.SolarAlgorithmVersion);
        Assert.AreEqual(scenario.ExpectedIntervalCount, preview.Intervals.Count);
        Assert.AreEqual(scenario.ExpectedUnavailableWindowCount, preview.UnavailableWindows.Count);
        Assert.AreEqual(evaluationUtc, decision.DecisionUtc);
        Assert.AreEqual(scenario.ExpectedAdmitted, decision.Admitted);
        Assert.AreEqual(scenario.ExpectedReason, decision.Reason);
        Assert.AreEqual(scenario.ExpectedIntervalSource, decision.Interval?.Source);
        Assert.AreEqual(scenario.ExpectedAdmitted ? "night" : null, decision.SetpointProfileId);
        Assert.IsNotNull(decision.NextTransitionUtc);

        if (scenario.ExpectedIntervalStartUtc is { } expectedStart)
        {
            Assert.AreEqual(expectedStart, preview.Intervals.Single().StartUtc);
        }
        if (scenario.ExpectedIntervalEndUtc is { } expectedEnd)
        {
            Assert.AreEqual(expectedEnd, preview.Intervals.Single().EndUtc);
        }
        if (scenario.ExpectedUnavailableWindowCount > 0)
        {
            Assert.IsTrue(preview.UnavailableWindows.All(static item =>
                string.Equals(item.ReasonCode, "schedule.solar-event-unavailable", StringComparison.Ordinal)));
        }
    }

    private static void EnsureEquivalent(CaptureSchedulePreview expected, CaptureSchedulePreview actual)
    {
        if (!string.Equals(expected.ScheduleRevisionSha256, actual.ScheduleRevisionSha256, StringComparison.Ordinal) ||
            !string.Equals(expected.ExpansionSha256, actual.ExpansionSha256, StringComparison.Ordinal) ||
            !string.Equals(expected.ExpansionAlgorithmVersion, actual.ExpansionAlgorithmVersion, StringComparison.Ordinal) ||
            !string.Equals(expected.TimeZoneRuleSha256, actual.TimeZoneRuleSha256, StringComparison.Ordinal) ||
            !string.Equals(expected.SolarAlgorithmVersion, actual.SolarAlgorithmVersion, StringComparison.Ordinal) ||
            expected.PreviewStartUtc != actual.PreviewStartUtc ||
            expected.PreviewEndUtc != actual.PreviewEndUtc ||
            !expected.Intervals.SequenceEqual(actual.Intervals) ||
            !expected.UnavailableWindows.SequenceEqual(actual.UnavailableWindows))
        {
            throw new InvalidOperationException("A capture schedule preview changed during deterministic measurement.");
        }
    }

    private static void EnsureEquivalent(CaptureScheduleDecision expected, CaptureScheduleDecision actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException("A capture schedule decision changed during deterministic measurement.");
        }
    }

    private static IReadOnlyList<PerformanceScenario> CreateScenarios()
    {
        var phoenix = new ObservatoryLocation(35.347, -113.878, 1_000, "America/Phoenix");
        var newYork = new ObservatoryLocation(40.7128, -74.0060, 10, "America/New_York");
        var tromso = new ObservatoryLocation(69.6492, 18.9553, 0, "Europe/Oslo");
        return
        [
            Scenario(
                "phoenix-fixed",
                "Phoenix fixed local overnight window",
                phoenix,
                new DateOnly(2025, 1, 15),
                previewDayCount: 2,
                Fixed(18),
                Fixed(6, dayOffset: 1),
                new DateTimeOffset(2025, 1, 16, 3, 0, 0, TimeSpan.Zero),
                expectedIntervalStartUtc: new DateTimeOffset(2025, 1, 16, 1, 0, 0, TimeSpan.Zero),
                expectedIntervalEndUtc: new DateTimeOffset(2025, 1, 16, 13, 0, 0, TimeSpan.Zero)),
            Scenario(
                "phoenix-solar",
                "Phoenix astronomical dusk to astronomical dawn window",
                phoenix,
                new DateOnly(2025, 6, 21),
                previewDayCount: 2,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.AstronomicalDusk),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.AstronomicalDawn, DayOffset: 1),
                evaluationUtc: null,
                expectedSolarAlgorithmVersion: AstronomyEngineSolarEventCalculator.Version),
            Scenario(
                "new-york-spring-dst",
                "New York 2024 spring skipped local end advances to the first valid instant",
                newYork,
                new DateOnly(2024, 3, 10),
                previewDayCount: 1,
                Fixed(1, minute: 30),
                Fixed(2, minute: 30),
                new DateTimeOffset(2024, 3, 10, 6, 45, 0, TimeSpan.Zero),
                expectedIntervalStartUtc: new DateTimeOffset(2024, 3, 10, 6, 30, 0, TimeSpan.Zero),
                expectedIntervalEndUtc: new DateTimeOffset(2024, 3, 10, 7, 0, 0, TimeSpan.Zero)),
            Scenario(
                "new-york-fall-dst",
                "New York 2024 fall ambiguous start uses earlier UTC and end uses later UTC",
                newYork,
                new DateOnly(2024, 11, 3),
                previewDayCount: 1,
                Fixed(1, minute: 30),
                Fixed(1, minute: 30),
                new DateTimeOffset(2024, 11, 3, 6, 0, 0, TimeSpan.Zero),
                expectedIntervalStartUtc: new DateTimeOffset(2024, 11, 3, 5, 30, 0, TimeSpan.Zero),
                expectedIntervalEndUtc: new DateTimeOffset(2024, 11, 3, 6, 30, 0, TimeSpan.Zero)),
            Scenario(
                "tromso-summer-solstice-no-event",
                "Tromso summer solstice sunset and next-day sunrise are unavailable",
                tromso,
                new DateOnly(2025, 6, 21),
                previewDayCount: 1,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunset),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunrise, DayOffset: 1),
                new DateTimeOffset(2025, 6, 21, 10, 0, 0, TimeSpan.Zero),
                expectedIntervalCount: 0,
                expectedUnavailableWindowCount: 1,
                expectedAdmitted: false,
                expectedReason: CaptureScheduleAdmissionReason.NoSolarEvent,
                expectedIntervalSource: null,
                expectedSolarAlgorithmVersion: AstronomyEngineSolarEventCalculator.Version)
        ];
    }

    private static PerformanceScenario Scenario(
        string id,
        string description,
        ObservatoryLocation observer,
        DateOnly firstLocalDate,
        int previewDayCount,
        CaptureScheduleBoundary start,
        CaptureScheduleBoundary end,
        DateTimeOffset? evaluationUtc,
        int expectedIntervalCount = 1,
        int expectedUnavailableWindowCount = 0,
        bool expectedAdmitted = true,
        CaptureScheduleAdmissionReason expectedReason = CaptureScheduleAdmissionReason.WeeklyWindow,
        CaptureScheduleIntervalSource? expectedIntervalSource = CaptureScheduleIntervalSource.WeeklyWindow,
        string expectedSolarAlgorithmVersion = "none",
        DateTimeOffset? expectedIntervalStartUtc = null,
        DateTimeOffset? expectedIntervalEndUtc = null)
    {
        var definition = new CaptureScheduleDefinition(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 100, TimeSpan.FromSeconds(10))],
            [new CaptureWeeklyScheduleWindow("night-window", firstLocalDate.DayOfWeek, start, end, "night")]);
        return new PerformanceScenario(
            id,
            description,
            observer,
            TimeZoneInfo.FindSystemTimeZoneById(observer.TimeZoneId),
            firstLocalDate,
            previewDayCount,
            definition,
            evaluationUtc,
            expectedIntervalCount,
            expectedUnavailableWindowCount,
            expectedAdmitted,
            expectedReason,
            expectedIntervalSource,
            expectedSolarAlgorithmVersion,
            expectedIntervalStartUtc,
            expectedIntervalEndUtc);
    }

    private static CaptureScheduleBoundary Fixed(int hour, int minute = 0, int dayOffset = 0)
        => new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(hour, minute), DayOffset: dayOffset);

    private static DateTimeOffset Midpoint(ExpandedScheduleInterval interval)
        => interval.StartUtc + TimeSpan.FromTicks((interval.EndUtc - interval.StartUtc).Ticks / 2);

    private static double Percentile(double[] sortedSamples, double percentile)
        => sortedSamples[(int)Math.Ceiling(percentile * sortedSamples.Length) - 1];

    private static string GetEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        if (string.IsNullOrWhiteSpace(revision) || revision is "." or ".." ||
            revision.Any(static character => !IsPathSafeRevisionCharacter(character)))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION must contain only ASCII letters, digits, period, underscore, or hyphen.");
        }
        return revision;
    }

    private static bool IsPathSafeRevisionCharacter(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static string ReadCpuModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var model = File.ReadLines(cpuInfo).FirstOrDefault(
                static line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                var separator = model.IndexOf(':', StringComparison.Ordinal);
                if (separator >= 0)
                {
                    return model[(separator + 1)..].Trim();
                }
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static AssemblyEvidence ReadAssemblyEvidence(Type type)
    {
        var location = type.Assembly.Location;
        using var stream = File.OpenRead(location);
        return new AssemblyEvidence(
            type.Assembly.GetName().Name ?? type.FullName ?? "unknown",
            location,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var head = (await RunProcessAsync(repositoryRoot, "git", "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunProcessAsync(
            repositoryRoot, "git", "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunProcessAsync(
            repositoryRoot, "git", "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunProcessAsync(
            repositoryRoot, "git", "diff", "--binary", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
        var untracked = await RunProcessAsync(
            repositoryRoot, "git", "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(status));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(diff));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(untracked));
        foreach (var relativePath in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            fingerprint.AppendData(await File.ReadAllBytesAsync(
                Path.Combine(repositoryRoot, relativePath)).ConfigureAwait(false));
        }
        return new GitEvidence(
            head,
            branch,
            !string.IsNullOrWhiteSpace(status),
            Convert.ToHexString(fingerprint.GetHashAndReset()));
    }

    private static async Task<string> RunProcessAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName} for performance evidence.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed while collecting performance evidence: {error}");
        }
        return output;
    }

    private static string GetRepositoryRoot()
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

    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record PerformanceScenario(
        string Id,
        string Description,
        ObservatoryLocation Observer,
        TimeZoneInfo TimeZone,
        DateOnly FirstLocalDate,
        int PreviewDayCount,
        CaptureScheduleDefinition Definition,
        DateTimeOffset? EvaluationUtc,
        int ExpectedIntervalCount,
        int ExpectedUnavailableWindowCount,
        bool ExpectedAdmitted,
        CaptureScheduleAdmissionReason ExpectedReason,
        CaptureScheduleIntervalSource? ExpectedIntervalSource,
        string ExpectedSolarAlgorithmVersion,
        DateTimeOffset? ExpectedIntervalStartUtc,
        DateTimeOffset? ExpectedIntervalEndUtc);

    private sealed record PreparedScenario(
        PerformanceScenario Scenario,
        CaptureSchedulePreview Preview,
        DateTimeOffset EvaluationUtc,
        CaptureScheduleDecision Decision,
        string PreviewSha256,
        string DecisionSha256);

    private sealed record OperationMeasurement(
        int WarmupOperations,
        int MeasuredOperations,
        double MedianMicroseconds,
        double P95Microseconds,
        double MinimumMicroseconds,
        double MaximumMicroseconds,
        double BatchWallMilliseconds,
        double ThroughputOperationsPerSecond,
        double ProcessCpuMilliseconds,
        double ProcessCpuMicrosecondsPerOperation,
        long ThreadAllocatedBytes,
        double ThreadAllocatedBytesPerOperation,
        long RssBeforeBytes,
        long RssAfterBytes,
        long RssDeltaBytes,
        string DeterministicIdentitySha256);

    private sealed record CaseEvidence(
        string Id,
        string Description,
        string TimeZoneId,
        ObservatoryLocation Observer,
        DateOnly FirstLocalDate,
        int PreviewDayCount,
        CaptureScheduleDefinition Definition,
        DateTimeOffset EvaluationUtc,
        string ScheduleRevisionSha256,
        string ExpansionSha256,
        string PreviewCanonicalSha256,
        string TimeZoneRuleSha256,
        string SolarAlgorithmVersion,
        int IntervalCount,
        int UnavailableWindowCount,
        string DecisionSha256,
        bool Admitted,
        string AdmissionReason,
        string? IntervalSource,
        string? SetpointProfileId,
        DateTimeOffset? NextTransitionUtc,
        OperationMeasurement Preview,
        OperationMeasurement Evaluation);

    private sealed record GitEvidence(
        string Head,
        string Branch,
        bool Dirty,
        string DirtyDiffSha256);

    private sealed record AssemblyEvidence(
        string Name,
        string Path,
        string Sha256);
}
