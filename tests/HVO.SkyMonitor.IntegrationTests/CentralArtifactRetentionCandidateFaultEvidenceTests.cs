using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CentralArtifactRetentionCandidateFaultEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] RequiredDeleteDelaysMilliseconds = [0, 250, 2000];
    private static readonly string[] RuntimeSignalAssertionNames =
    [
        "required retention log event IDs and bounded structured fields",
        "required meter instruments and bounded metric tag keys",
        "required retention activities and bounded activity tag keys",
        "log payload excludes the exact object key and operation token",
        "health data exposes exactly four bounded fields",
        "health payload excludes the exact object key and operation token"
    ];

    [TestMethod]
    [Timeout(600_000)]
    public async Task DurableBoundaryMatrix_WritesClaimableCandidateEvidence()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HVO_ISSUE_246_RETENTION_FAULT_EVIDENCE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set HVO_ISSUE_246_RETENTION_FAULT_EVIDENCE=1 on a committed clean candidate.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(CentralArtifactRetentionCandidateFaultEvidenceTests),
            typeof(CentralArtifactRetentionProcessor),
            typeof(CentralArtifactRetentionWorker)).ConfigureAwait(false);
        var development = string.Equals(
            Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE"),
            "development",
            StringComparison.Ordinal);
        if (!development && (source.Dirty || !string.Equals(
                source.Claimability,
                "clean-source-attributed-review-required",
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Candidate fault evidence requires a clean explicitly attributed revision.");
        }
        if (development && !string.Equals(
                source.Claimability,
                "dirty-development-not-claimable",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Development fault evidence must remain dirty and non-claimable.");
        }

        var retention = new CentralArtifactRetentionIntegrationTests();
        var transient = new CentralDerivativeWindowIntegrationTests();
        var requiredScenarioNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "reservation-rollback-cancellation",
            "delete-critical-section-recovery",
            "object-store-retry-terminal",
            "delete-applied-response-lost",
            "finalize-pre-commit-rollback",
            "finalize-commit-ambiguity",
            "concurrent-request-worker",
            "runtime-signal-collector",
            "stale-token-replay",
            "forced-lock-loss-stale-publisher",
            "fresh-provider-worker-drain",
            "worker-item-isolation",
            "health-state-matrix"
        };
        var scenarios = new ScenarioDefinition[]
        {
            new("reservation-rollback-cancellation", "before reservation and during DELETE cancellation",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.CancellationAndExactOwnership_LeaveDurablePendingAndRespectBinaryKeys)),
                retention.CancellationAndExactOwnership_LeaveDurablePendingAndRespectBinaryKeys),
            new("delete-critical-section-recovery", "blocked DELETE has no SQL transaction and fresh processor converges",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.DelayedDelete_HasNoSqlTransactionAllowsWriterAndRecoversStaleRowVersion)),
                retention.DelayedDelete_HasNoSqlTransactionAllowsWriterAndRecoversStaleRowVersion),
            new("object-store-retry-terminal", "503 backoff, non-caller timeout, and terminal authorization",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.ObjectStoreFaults_RetryTransientAndPersistTerminalFailure)),
                retention.ObjectStoreFaults_RetryTransientAndPersistTerminalFailure),
            new("delete-applied-response-lost", "DELETE applied, response lost, and missing-object replay",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.AppliedDeleteWithLostResponse_ReplaysMissingObjectToCompletion)),
                retention.AppliedDeleteWithLostResponse_ReplaysMissingObjectToCompletion),
            new("finalize-pre-commit-rollback", "finalization rollback before commit and fresh processor recovery",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.FinalizationPreCommitRollback_FreshProcessorRecoversMissingObject)),
                retention.FinalizationPreCommitRollback_FreshProcessorRecoversMissingObject),
            new("finalize-commit-ambiguity", "commit applied then exception and post-unwind completion probe",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.FinalizationCommitAppliedThenThrows_ProbesAfterUnwindAndReturnsReleased)),
                retention.FinalizationCommitAppliedThenThrows_ProbesAfterUnwindAndReturnsReleased),
            new("concurrent-request-worker", "duplicate concurrent request and worker share one physical DELETE",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.ConcurrentRequestAndWorker_UseOneDeleteAndBothObserveCompletion)),
                retention.ConcurrentRequestAndWorker_UseOneDeleteAndBothObserveCompletion),
            new("runtime-signal-collector", "terminal replay plus collected logs, metrics, activities, health, and privacy assertions",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.Release_ReservesDeletesFinalizesAndDuplicateRemainsReleased)),
                retention.Release_ReservesDeletesFinalizesAndDuplicateRemainsReleased),
            new("stale-token-replay", "stale token blocks finalization and current token converges",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.StaleTokenCannotFinalizeDeleteAndFreshWorkerConverges)),
                retention.StaleTokenCannotFinalizeDeleteAndFreshWorkerConverges),
            new("forced-lock-loss-stale-publisher", "killed session locks, reserve-versus-intent, partial and full verified replay",
                TestName<CentralDerivativeWindowIntegrationTests>(
                    nameof(CentralDerivativeWindowIntegrationTests.CentralTransientRuntime_UsesDelayedExactWindowAndPersistsCanonicalOutcome)) + "(true)",
                () => transient.CentralTransientRuntime_UsesDelayedExactWindowAndPersistsCanonicalOutcome(true)),
            new("fresh-provider-worker-drain", "due filtering and fresh service-provider drain",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.Worker_FreshProvidersDrainOnlyDueRowsAndRecordActualOutcome)),
                retention.Worker_FreshProvidersDrainOnlyDueRowsAndRecordActualOutcome),
            new("worker-item-isolation", "first item lock timeout and second-item convergence",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.Worker_FirstItemLockTimeoutDoesNotStarveSecondDueDeletion)),
                retention.Worker_FirstItemLockTimeoutDoesNotStarveSecondDueDeletion),
            new("health-state-matrix", "fresh, stale, failed, and drained bounded health data",
                TestName<CentralArtifactRetentionIntegrationTests>(
                    nameof(CentralArtifactRetentionIntegrationTests.Health_ReportsFreshStaleFailedAndDrainedRetentionWork)),
                retention.Health_ReportsFreshStaleFailedAndDrainedRetentionWork)
        };
        var definedScenarioNames = scenarios.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (definedScenarioNames.Count != scenarios.Length
            || !definedScenarioNames.SetEquals(requiredScenarioNames))
        {
            throw new InvalidOperationException("Candidate fault evidence does not define the exact required scenario matrix.");
        }
        var results = new List<ScenarioEvidence>(scenarios.Length);
        foreach (var scenario in scenarios)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await scenario.Execute().ConfigureAwait(false);
                results.Add(new(
                    scenario.Name,
                    scenario.Boundary,
                    scenario.ConcreteIntegrationTest,
                    "passed",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (Exception exception)
            {
                results.Add(new(
                    scenario.Name,
                    scenario.Boundary,
                    scenario.ConcreteIntegrationTest,
                    "failed",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                throw new InvalidOperationException(
                    $"Candidate fault scenario '{scenario.Name}' failed. Evidence contains no exception detail.",
                    exception);
            }
        }
        if (results.Count != requiredScenarioNames.Count
            || results.Any(item => !string.Equals(item.Outcome, "passed", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Candidate fault evidence requires every named scenario to pass.");
        }

        var assertionClaims = new[]
        {
            CreateClaim("exact-sql-object-convergence", "runtime-signal-collector", results),
            CreateClaim("one-logical-terminal-completion", "concurrent-request-worker", results),
            CreateClaim("no-sql-transaction-during-delete", "delete-critical-section-recovery", results),
            CreateClaim("finalize-pre-commit-fresh-recovery", "finalize-pre-commit-rollback", results),
            CreateClaim("delete-response-loss-recovery", "delete-applied-response-lost", results),
            CreateClaim("per-item-worker-isolation", "worker-item-isolation", results)
        };
        var runtimeSignalClaims = RuntimeSignalAssertionNames
            .Select(assertion => CreateClaim(assertion, "runtime-signal-collector", results)).ToArray();
        var workerDrain = results.Single(item => item.Name == "fresh-provider-worker-drain");
        var workerDrainOutcome = string.Equals(workerDrain.Outcome, "passed", StringComparison.Ordinal)
            && workerDrain.DurationMilliseconds <= 10_000
            ? "passed"
            : "failed";
        if (!string.Equals(workerDrainOutcome, "passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fresh-provider worker drain exceeded the ten-second evidence limit.");
        }

        var evidence = new
        {
            Schema = "hvo-issue-246-central-artifact-retention-fault-evidence-v1",
            Source = source,
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                SqlProvider = "SQL Server Testcontainers",
                ObjectProvider = "MinIO Testcontainers"
            },
            Matrix = results,
            Assertions = assertionClaims,
            WorkerDrain = new
            {
                Scenario = workerDrain.Name,
                workerDrain.ConcreteIntegrationTest,
                ElapsedMilliseconds = workerDrain.DurationMilliseconds,
                LimitMilliseconds = 10_000,
                Outcome = workerDrainOutcome
            },
            RuntimeSignalCollectorAssertions = runtimeSignalClaims,
            Privacy = new
            {
                RuntimeSignalScenario = CreateClaim("runtime-signal privacy assertions", "runtime-signal-collector", results),
                SerializedEvidenceScan = "required on the exact UTF-8 evidence bytes before manifest emission"
            },
            SeparateRequiredEvidence = new
            {
                Owner = TestName<CentralArtifactRetentionPerformanceTests>(
                    nameof(CentralArtifactRetentionPerformanceTests.Release_W2W4AndDelayedDelete_RecordsEvidence)),
                RequiredDeleteDelayMilliseconds = RequiredDeleteDelaysMilliseconds,
                Status = "not executed or claimed by this candidate fault matrix; required from the immutable after-performance trial"
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
        var evidencePrivacy = ScanPrivacy(evidenceBytes);
        if (!string.Equals(evidencePrivacy.Outcome, "passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Candidate evidence privacy scan failed.");
        }

        var output = Path.Combine(
            repositoryRoot,
            "TestResults",
            "issue-246",
            source.OutputDirectoryName,
            source.RunId,
            "fault-matrix");
        Directory.CreateDirectory(output);
        var evidencePath = Path.Combine(output, "central-artifact-retention-fault-evidence.json");
        await WriteNewAsync(evidencePath, evidenceBytes).ConfigureAwait(false);
        var manifest = new
        {
            Schema = "hvo-evidence-file-manifest-v1",
            SourceHead = source.Head,
            Files = new[]
            {
                new
                {
                    Name = Path.GetFileName(evidencePath),
                    Length = evidenceBytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes))
                }
            },
            Privacy = new
            {
                RuntimeSignalCollectorAssertions = runtimeSignalClaims,
                FinalSerializedEvidenceScan = evidencePrivacy
            }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (!string.Equals(ScanPrivacy(manifestBytes).Outcome, "passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Candidate evidence manifest privacy scan failed.");
        }
        await WriteNewAsync(
            Path.Combine(output, "central-artifact-retention-fault-evidence.manifest.json"),
            manifestBytes).ConfigureAwait(false);
        TestContext.WriteLine("issue246 candidate fault evidence: {0}", output);
    }

    public TestContext TestContext { get; set; } = null!;

    private static ScenarioClaim CreateClaim(
        string assertion,
        string scenarioName,
        IReadOnlyCollection<ScenarioEvidence> results)
    {
        var scenario = results.Single(item => item.Name == scenarioName);
        return new(
            assertion,
            scenario.Name,
            scenario.ConcreteIntegrationTest,
            scenario.Outcome,
            scenario.DurationMilliseconds);
    }

    private static PrivacyScanEvidence ScanPrivacy(byte[] serialized)
    {
        var text = Encoding.UTF8.GetString(serialized);
        var findings = new List<string>();
        var forbiddenValues = new[]
        {
            IntegrationTestFixture.MinioAccessKey,
            IntegrationTestFixture.MinioSecretKey,
            AssemblyHooks.Fixture.SqlServerConnectionString
        };
        foreach (var value in forbiddenValues)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && text.Contains(value, StringComparison.Ordinal))
            {
                findings.Add("credential-value");
            }
        }
        var forbiddenNames = new[]
        {
            "ArtifactId", "ObjectKey", "StorageReference", "OperationToken", "ChecksumSha256", "ConnectionString"
        };
        foreach (var name in forbiddenNames)
        {
            if (text.Contains($"\"{name}\"", StringComparison.Ordinal))
            {
                findings.Add($"forbidden-field:{name}");
            }
        }
        return new(
            findings.Count == 0 ? "passed" : "failed",
            findings.Count,
            serialized.LongLength,
            Convert.ToHexString(SHA256.HashData(serialized)));
    }

    private static async Task WriteNewAsync(string path, byte[] contents)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await stream.WriteAsync(contents).ConfigureAwait(false);
    }

    private static string TestName<T>(string method) => $"{typeof(T).FullName}.{method}";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record ScenarioDefinition(
        string Name,
        string Boundary,
        string ConcreteIntegrationTest,
        Func<Task> Execute);

    private sealed record ScenarioEvidence(
        string Name,
        string Boundary,
        string ConcreteIntegrationTest,
        string Outcome,
        double DurationMilliseconds);

    private sealed record ScenarioClaim(
        string Assertion,
        string Scenario,
        string ConcreteIntegrationTest,
        string Outcome,
        double ScenarioDurationMilliseconds);

    private sealed record PrivacyScanEvidence(
        string Outcome,
        int FindingCount,
        long ScannedByteLength,
        string ScannedSha256);
}
