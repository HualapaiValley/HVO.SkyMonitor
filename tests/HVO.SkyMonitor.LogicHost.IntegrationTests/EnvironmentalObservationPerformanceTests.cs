using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class EnvironmentalObservationPerformanceTests
{
    private const int IngestCount = 10_000;
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse(
        "2026-01-15T06:00:00Z",
        CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset BenchmarkNow = DateTimeOffset.Parse(
        "2026-07-17T06:00:00Z",
        CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue103_E1ToE5_RecordsCanonicalEvidence()
    {
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal,
            "Canonical issue #103 evidence must be collected from a Release build.");
        var repositoryRoot = FindRepositoryRoot();
        var candidateRevision = RunGit(repositoryRoot, "rev-parse", "HEAD");
        var branch = RunGit(repositoryRoot, "branch", "--show-current");
        var dirtyStatus = RunGit(repositoryRoot, "status", "--porcelain");
        var dirty = !string.IsNullOrWhiteSpace(dirtyStatus);
        var untrackedHashes = RunGit(repositoryRoot, "ls-files", "--others", "--exclude-standard")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, path))))}");
        var dirtyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            dirtyStatus + "\n" + RunGit(repositoryRoot, "diff", "--binary", "HEAD") +
            "\n" + string.Join('\n', untrackedHashes))));
        var evidenceRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ??
            (dirty ? "local-dirty" : candidateRevision);
        var evidenceDirectoryRevision = evidenceRevision.Length > 12 ? evidenceRevision[..12] : evidenceRevision;
        var evidenceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "HVO.SkyMonitor.LogicHost.IntegrationTests",
            "TestResults",
            "issue-103",
            evidenceDirectoryRevision);
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "environmental-observation-performance.json");
        File.Delete(evidencePath);
        var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorEnvironmentPerformance_{Guid.NewGuid():N}"
        };
        var interceptor = new CountingCommandInterceptor();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connection.ConnectionString)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var telemetry = new EnvironmentalObservationTelemetry();
        await using var setup = new ApplicationDbContext(dbOptions);
        try
        {
            await setup.Database.MigrateAsync().ConfigureAwait(false);
            var sqlServerVersion = await ReadSqlServerVersionAsync(setup).ConfigureAwait(false);
            var target = await SeedTargetAsync(setup).ConfigureAwait(false);
            var e1 = MeasureContracts(target.SiteId, target.AgentId);
            var ingestRun = await MeasureIngestAsync(dbOptions, interceptor, telemetry, target).ConfigureAwait(false);
            var ingest = ingestRun.Ingest;
            var correlation = new List<CorrelationEvidence>();
            correlation.AddRange(ingestRun.OneThousandCorrelation);
            correlation.AddRange(await MeasureCorrelationScaleAsync(
                dbOptions,
                interceptor,
                telemetry,
                target,
                10_000,
                Epoch.AddSeconds(9_999),
                DeterministicGuid(9_999)).ConfigureAwait(false));

            var finalObservation = await SeedToOneHundredThousandAsync(
                setup,
                dbOptions,
                telemetry,
                target).ConfigureAwait(false);
            correlation.AddRange(await MeasureCorrelationScaleAsync(
                dbOptions,
                interceptor,
                telemetry,
                target,
                100_000,
                finalObservation.ObservedAtUtc,
                finalObservation.ObservationId).ConfigureAwait(false));
            var captureInstants = await MeasureCaptureInstantMatrixAsync(
                dbOptions,
                telemetry,
                target).ConfigureAwait(false);
            var temporalScenarios = await MeasureTemporalScenariosAsync(
                dbOptions,
                telemetry,
                target).ConfigureAwait(false);
            var retention = await MeasureRetentionRestartAsync(
                connection.ConnectionString,
                interceptor,
                target.SiteId,
                target.AgentId,
                telemetry).ConfigureAwait(false);
            var concurrency = await MeasureDuplicateConcurrencyAsync(
                dbOptions,
                telemetry,
                target,
                finalObservation).ConfigureAwait(false);
            var faults = await MeasureFaultRecoveryAsync(
                dbOptions,
                interceptor,
                telemetry,
                finalObservation).ConfigureAwait(false);
            await using var verification = new ApplicationDbContext(dbOptions);
            var hashes = await verification.EnvironmentalObservations
                .AsNoTracking()
                .Where(record => record.Source!.SiteId == target.SiteId &&
                    record.Source.Provider.StartsWith("provider-") &&
                    record.ObservedAtUtc <= finalObservation.ObservedAtUtc)
                .OrderBy(record => record.ObservationId)
                .Select(record => record.PayloadSha256)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var durableHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(hashes))));
            var canonicalHashCount = hashes.Count(hash => !string.Equals(hash, new string('A', 64), StringComparison.Ordinal));
            var evidence = new
            {
                Schema = "hvo-environmental-observation-performance-v1",
                Issue = 103,
                Revision = new
                {
                    EvidenceDirectoryRevision = evidenceRevision,
                    Candidate = candidateRevision,
                    Branch = branch,
                    Dirty = dirty,
                    DirtyFingerprintSha256 = dirtyFingerprint
                },
                RecordedAtUtc = DateTimeOffset.UtcNow,
                Command = "dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~EnvironmentalObservationPerformanceTests\"",
                Environment = new
                {
                    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Cpu = ReadCpuIdentity(),
                    DotnetSdk = RunCommand("dotnet", repositoryRoot, "--version"),
                    SqlServerVersion = sqlServerVersion,
                    Build = "Release",
                    Storage = "Docker-backed SQL Server Testcontainer",
                    Environment.ProcessorCount,
                    AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
                },
                Workload = new
                {
                    Ids = new[] { "W3M", "W4", "issue-103-temporal" },
                    Fixture = "deterministic environmental-observation-v1",
                    Seed = "DeterministicGuid(int), Epoch=2026-01-15T06:00:00Z",
                    ObservationRows = hashes.Length,
                    Providers = 10,
                    Concurrency = new[] { 1, 4, 8 },
                    CorrelationHistoryRows = new[] { 1_000, 10_000, 100_000 },
                    InitialBacklog = 10_000,
                    ArrivalRatePerSecond = 1
                },
                Baseline = new
                {
                    Revision = "N/A",
                    Result = "N/A",
                    Reason = "Net-new environmental contract and SQL path; this run establishes the absolute non-regression baseline."
                },
                E1 = e1,
                E2 = ingest,
                E3 = new { Scaling = correlation, CaptureInstants = captureInstants, Scenarios = temporalScenarios },
                E4 = retention,
                E5 = new { DuplicateAndConflictConcurrency = concurrency, Faults = faults },
                Durable = new
                {
                    ObservationRows = hashes.Length,
                    CanonicalIngestRows = canonicalHashCount,
                    SyntheticIndexScaleRows = hashes.Length - canonicalHashCount,
                    OrderedStoredPayloadHashChecksumSha256 = durableHash,
                    Verification = "Canonical rows passed contract validation and content hashing during ingest; synthetic rows exercise only temporal SQL index scale."
                },
                Result = new
                {
                    Comparison = "N/A for a net-new path; candidate values establish the baseline.",
                    Noise = "Thirty measured ingest batches and 200 correlation samples per concurrency level; no cross-machine comparison claimed.",
                    AcceptedRegressions = "None.",
                    ResidualRisk = "SQL latency and logical reads remain environment-specific; CI reruns enforce correctness, not a portable timing budget.",
                    Backlog = "No durable worker queue is introduced; retention restart convergence is reported in E4."
                },
                Exclusions = "W1/W2 frame CPU and memory are N/A: #103 adds metadata contracts and SQL paths only; #104/#105 own rendering and image cloud assessment."
            };
            ingest.Observations.Should().Be(IngestCount);
            ingest.ObservationsPerSecond.Should().BeGreaterThan(10);
            correlation.Should().HaveCount(9);
            correlation.Should().OnlyContain(result => result.SelectedCorrectly && result.TemporalIndexUsed);
            correlation.Should().OnlyContain(result => result.P95Milliseconds < 1_000);
            correlation.Should().OnlyContain(result => result.FreshLogicalReads > 0 &&
                result.StaleLogicalReads > 0 && result.HistoryLogicalReads > 0);
            correlation.Where(result => result.HistoryRows == 100_000).Max(result => result.FreshLogicalReads)
                .Should().BeLessThanOrEqualTo(
                    correlation.Where(result => result.HistoryRows == 1_000).Max(result => result.FreshLogicalReads) * 3,
                    "indexed fresh-correlation reads must scale with the relevant time window rather than total history");
            correlation.Where(result => result.HistoryRows == 100_000).Max(result => result.StaleLogicalReads)
                .Should().BeLessThanOrEqualTo(
                    correlation.Where(result => result.HistoryRows == 1_000).Max(result => result.StaleLogicalReads) * 3,
                    "indexed stale-correlation reads must scale with the relevant time window rather than total history");
            correlation.Where(result => result.HistoryRows == 100_000).Max(result => result.HistoryLogicalReads)
                .Should().BeLessThanOrEqualTo(
                    correlation.Where(result => result.HistoryRows == 1_000).Max(result => result.HistoryLogicalReads) * 3,
                    "indexed bounded-history reads must scale with the requested time window rather than total history");
            temporalScenarios.Should().Match<TemporalScenarioEvidence>(result =>
                result.ExactBoundaryFresh && result.StaleBoundaryExplicit && result.GapReturnsStale &&
                result.BeyondMaximumMissing && result.OverlapDeterministic && result.ClockSkewSelected &&
                result.OutOfOrderReceiptIgnored && result.HashesVerified);
            captureInstants.CorrectSelections.Should().Be(captureInstants.Instants);
            retention.InterruptedRows.Should().Be(4_000);
            retention.ResumedRows.Should().Be(5_900);
            retention.RetainedRows.Should().Be(10_100);
            retention.PinnedRows.Should().Be(100);
            concurrency.Should().OnlyContain(result => result.DurableRows == 1 && result.Conflicts == 1);
            faults.Should().Match<FaultEvidence>(result => result.PreCommitFailureObserved &&
                result.RetryAccepted && result.RestartDuplicate && result.PostCommitAcknowledgementLost &&
                result.PostCommitRetryDuplicate && result.InvalidRejected == 5 && result.DurableRows == 2);
            hashes.Should().HaveCount(100_000);
            canonicalHashCount.Should().Be(10_001);
            durableHash.Should().HaveLength(64);

            var temporaryPath = evidencePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            File.Move(temporaryPath, evidencePath, overwrite: true);
            TestContext.WriteLine($"Issue #103 performance evidence: {evidencePath}");
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static ContractEvidence MeasureContracts(Guid siteId, Guid agentId)
    {
        var source = CreateObservation(0, siteId, agentId);
        var fixtures = Enum.GetValues<EnvironmentalObservationSourceKind>()
            .SelectMany((sourceKind, sourceIndex) => ContractValues().Select((value, valueIndex) => source with
            {
                ObservationId = DeterministicGuid(300_000 + sourceIndex * 100 + valueIndex),
                Source = source.Source with { Kind = sourceKind },
                Value = value,
                Lineage = sourceKind == EnvironmentalObservationSourceKind.Derived
                    ? [new EnvironmentalObservationReference(new string('A', 64), DeterministicGuid(400_000 + valueIndex))]
                    : []
            }))
            .Concat(Enum.GetValues<EnvironmentalObservationQuality>().SelectMany((quality, qualityIndex) =>
                new double?[] { null, 0.1 }.Select((uncertainty, uncertaintyIndex) => source with
                {
                    ObservationId = DeterministicGuid(350_000 + qualityIndex * 10 + uncertaintyIndex),
                    Value = source.Value with { Quality = quality, Uncertainty = uncertainty }
                })))
            .ToArray();
        for (var index = 0; index < 5; index++)
        {
            foreach (var fixture in fixtures)
            {
                _ = EnvironmentalObservationJson.Parse(EnvironmentalObservationJson.Serialize(fixture));
            }
        }
        var samples = new double[30];
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            foreach (var fixture in fixtures)
            {
                var bytes = EnvironmentalObservationJson.Serialize(fixture);
                var parsed = EnvironmentalObservationJson.Parse(bytes);
                Assert.IsTrue(parsed.Validation.IsValid);
                Assert.AreEqual(
                    EnvironmentalObservationJson.ComputeContentSha256(fixture),
                    EnvironmentalObservationJson.ComputeContentSha256(parsed.Observation!));
            }
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var invalid = new[]
        {
            source with { Value = source.Value with { Unit = EnvironmentalObservationUnit.Pascals } },
            source with { Value = source.Value with { NumericValue = double.NaN } },
            source with { ValidThroughUtc = source.ValidFromUtc },
            source with { Source = source.Source with { Kind = EnvironmentalObservationSourceKind.Derived } },
            source with { Lineage = [new EnvironmentalObservationReference(new string('B', 64), DeterministicGuid(500_000))] },
            source with { Value = source.Value with { Quality = (EnvironmentalObservationQuality)999 } },
            source with { Value = source.Value with { Kind = (EnvironmentalObservationKind)999 } },
            source with { Value = source.Value with { Unit = (EnvironmentalObservationUnit)999 } },
            source with { Value = source.Value with { Uncertainty = -0.1 } },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with { ParametersSha256 = new string('C', 64) }
                }
            }
        };
        Assert.IsTrue(invalid.All(item => !EnvironmentalObservationJson.Validate(item).IsValid));
        var hashes = fixtures.Select(EnvironmentalObservationJson.ComputeContentSha256).Order(StringComparer.Ordinal);
        return new(
            Warmups: 5,
            Samples: samples.Length,
            Fixtures: fixtures.Length,
            InvalidFixtures: invalid.Length,
            PayloadBytes: fixtures.Sum(fixture => EnvironmentalObservationJson.Serialize(fixture).Length),
            MedianMilliseconds: Percentile(samples, 0.5),
            P95Milliseconds: Percentile(samples, 0.95),
            AllocatedBytes: GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            ContentSha256: Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(hashes)))),
            ExtensionPolicy: "Unknown kinds/units fail v1; new semantics require an additive schema version.");
    }

    private static IReadOnlyList<EnvironmentalObservationValue> ContractValues()
        =>
        [
            ContractValue(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, 12.5),
            ContractValue(EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationUnit.Percent, 45),
            ContractValue(EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationUnit.Pascals, 101_325),
            ContractValue(EnvironmentalObservationKind.WindSpeed, EnvironmentalObservationUnit.MetersPerSecond, 4),
            ContractValue(EnvironmentalObservationKind.WindDirection, EnvironmentalObservationUnit.DegreesTrue, 180),
            ContractValue(EnvironmentalObservationKind.WindGust, EnvironmentalObservationUnit.MetersPerSecond, 6),
            ContractValue(EnvironmentalObservationKind.PrecipitationRate, EnvironmentalObservationUnit.MillimetersPerHour, 0),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RainState,
                EnvironmentalObservationUnit.Boolean,
                null,
                false,
                EnvironmentalObservationQuality.Good),
            ContractValue(EnvironmentalObservationKind.SkyBrightness, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond, 20),
            ContractValue(EnvironmentalObservationKind.SkyQuality, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond, 21),
            ContractValue(EnvironmentalObservationKind.CloudCover, EnvironmentalObservationUnit.Fraction, 0.25)
        ];

    private static EnvironmentalObservationValue ContractValue(
        EnvironmentalObservationKind kind,
        EnvironmentalObservationUnit unit,
        double value)
        => new(kind, unit, value, null, EnvironmentalObservationQuality.Good, 0.1);

    private static async Task<IngestRunEvidence> MeasureIngestAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        CountingCommandInterceptor interceptor,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target)
    {
        var measured = new List<double>();
        var measuredCommands = 0L;
        var measuredBytes = 0L;
        var process = Process.GetCurrentProcess();
        var cpuBefore = TimeSpan.Zero;
        var allocatedBefore = 0L;
        var rssBefore = 0L;
        IReadOnlyList<CorrelationEvidence>? oneThousandCorrelation = null;
        for (var batch = 0; batch < 100; batch++)
        {
            await using var db = new ApplicationDbContext(dbOptions);
            var service = new EnvironmentalObservationIngestService(
                db,
                new FixedTimeProvider(BenchmarkNow),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            var measure = batch >= 70;
            if (batch == 70)
            {
                cpuBefore = process.TotalProcessorTime;
                allocatedBefore = GC.GetTotalAllocatedBytes(true);
                rssBefore = process.WorkingSet64;
            }
            if (measure)
            {
                interceptor.Reset();
            }
            var started = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < 100; offset++)
            {
                var observation = CreateObservation(batch * 100 + offset, target.SiteId, target.AgentId);
                var result = await service.IngestAsync(observation).ConfigureAwait(false);
                Assert.AreEqual(EnvironmentalObservationIngestDisposition.Accepted, result.Disposition);
                Assert.AreEqual(
                    EnvironmentalObservationJson.ComputeContentSha256(observation),
                    result.ContentSha256);
                if (measure)
                {
                    measuredBytes += EnvironmentalObservationJson.Serialize(observation).Length;
                }
            }
            if (measure)
            {
                measured.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                measuredCommands += interceptor.Count;
            }
            if (batch == 9)
            {
                oneThousandCorrelation = await MeasureCorrelationScaleAsync(
                    dbOptions,
                    interceptor,
                    telemetry,
                    target,
                    1_000,
                    Epoch.AddSeconds(999),
                    DeterministicGuid(999)).ConfigureAwait(false);
            }
        }
        var totalMeasuredSeconds = measured.Sum() / 1_000d;
        return new(
            new IngestEvidence(
                Observations: IngestCount,
                Providers: 10,
                WarmupBatches: 5,
                SetupBatches: 65,
                MeasuredBatches: 30,
                BatchSize: 100,
                ArrivalRatePerSecond: 1,
                ObservationsPerSecond: 3_000 / totalMeasuredSeconds,
                MedianBatchMilliseconds: Percentile(measured, 0.5),
                P95BatchMilliseconds: Percentile(measured, 0.95),
                SqlCommands: measuredCommands,
                Transactions: 3_000,
                PayloadBytes: measuredBytes,
                CpuMilliseconds: (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                AllocatedBytes: GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                WorkingSetDeltaBytes: process.WorkingSet64 - rssBefore),
            oneThousandCorrelation ?? throw new InvalidOperationException("The 1K correlation checkpoint was not measured."));
    }

    private static async Task<EnvironmentalObservationV1> SeedToOneHundredThousandAsync(
        ApplicationDbContext setup,
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target)
    {
        var sourceObservation = CreateObservation(0, target.SiteId, target.AgentId);
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(sourceObservation);
        var sourceId = await setup.EnvironmentalObservationSources
            .Where(source => source.IdentitySha256 == sourceIdentity)
            .Select(source => source.Id)
            .SingleAsync()
            .ConfigureAwait(false);
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            WITH [Numbers] AS
            (
                SELECT TOP (89999) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS [N]
                FROM [sys].[all_objects] AS [a]
                CROSS JOIN [sys].[all_objects] AS [b]
            )
            INSERT INTO [EnvironmentalObservations]
                ([Id], [SourceRecordId], [SiteId], [AgentId], [RigId], [SourceKind], [SourceIdentitySha256],
                 [ObservationId], [SchemaVersion], [Kind], [Unit], [NumericValue],
                 [BooleanValue], [Quality], [Uncertainty], [SubmittedNumericValue], [SubmittedUnit],
                 [ObservedAtUtc], [ObservedFromUtc], [ObservedThroughUtc], [ValidFromUtc], [ValidThroughUtc],
                 [StaleAfterUtc], [ReceivedAtUtc], [ApparentClockOffsetSeconds], [ClockDiagnostic], [PayloadSha256])
            SELECT NEWID(), {sourceId}, {target.SiteId}, {target.AgentId}, {"rig-1"},
                   {EnvironmentalObservationSourceKind.Measured.ToString()}, {sourceIdentity},
                   NEWID(), {EnvironmentalObservationV1.CurrentSchemaVersion},
                   {EnvironmentalObservationKind.RelativeHumidity.ToString()}, {EnvironmentalObservationUnit.Percent.ToString()},
                   CONVERT(float, 40 + ([N] % 50)), NULL, {EnvironmentalObservationQuality.Good.ToString()}, 0.5, NULL, NULL,
                   DATEADD(second, 10000 + [N], {Epoch}), NULL, NULL,
                   DATEADD(second, 9880 + [N], {Epoch}), DATEADD(second, 10120 + [N], {Epoch}),
                   DATEADD(second, 10060 + [N], {Epoch}), {BenchmarkNow}, 0,
                   {EnvironmentalClockDiagnostic.ClockBehindOrDeliveryDelayed.ToString()}, {new string('A', 64)}
            FROM [Numbers];
            """).ConfigureAwait(false);
        var final = CreateObservation(99_999, target.SiteId, target.AgentId);
        await using var db = new ApplicationDbContext(dbOptions);
        var service = new EnvironmentalObservationIngestService(
            db,
            new FixedTimeProvider(BenchmarkNow),
            Options.Create(new EnvironmentalObservationOptions()),
            telemetry,
            NullLogger<EnvironmentalObservationIngestService>.Instance);
        await service.IngestAsync(final).ConfigureAwait(false);
        return final;
    }

    private static async Task<IReadOnlyList<CorrelationEvidence>> MeasureCorrelationScaleAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        CountingCommandInterceptor interceptor,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target,
        int historySize,
        DateTimeOffset instant,
        Guid expectedObservationId)
    {
        var request = new EnvironmentalObservationCorrelationRequest(
            new EnvironmentalObservationTarget(target.SiteId, target.AgentId, "rig-1"),
            instant,
            instant,
            new EnvironmentalObservationSelector(
                EnvironmentalObservationKind.RelativeHumidity,
                [EnvironmentalObservationSourceKind.Measured],
                [EnvironmentalObservationQuality.Good],
                TimeSpan.FromDays(7)));
        var results = new List<CorrelationEvidence>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            for (var warmup = 0; warmup < 20; warmup++)
            {
                _ = await CorrelateOnceAsync(dbOptions, telemetry, request).ConfigureAwait(false);
            }
            interceptor.Reset();
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var rssBefore = process.WorkingSet64;
            var samples = new ConcurrentBag<double>();
            var selected = new ConcurrentBag<Guid>();
            var started = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < 200; offset += concurrency)
            {
                var count = Math.Min(concurrency, 200 - offset);
                await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
                {
                    var callStarted = Stopwatch.GetTimestamp();
                    var match = await CorrelateOnceAsync(dbOptions, telemetry, request).ConfigureAwait(false);
                    samples.Add(Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds);
                    selected.Add(match.Observation?.ObservationId ?? Guid.Empty);
                })).ConfigureAwait(false);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var plans = await ReadTemporalPlansAsync(dbOptions, telemetry, request).ConfigureAwait(false);
            var planSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                plans.Fresh.Xml + plans.Stale.Xml + plans.History.Xml)));
            results.Add(new(
                HistoryRows: historySize,
                Concurrency: concurrency,
                Warmups: 20,
                Samples: 200,
                MedianMilliseconds: Percentile(samples, 0.5),
                P95Milliseconds: Percentile(samples, 0.95),
                QueriesPerSecond: 200 / elapsed.TotalSeconds,
                SqlCommands: interceptor.Count,
                CpuMilliseconds: (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                AllocatedBytes: GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                WorkingSetDeltaBytes: process.WorkingSet64 - rssBefore,
                SelectedCorrectly: selected.All(id => id == expectedObservationId),
                TemporalIndexUsed: plans.Fresh.Xml.Contains(
                        "IX_EnvironmentalObservations_TargetKindValidityEnd",
                        StringComparison.Ordinal) &&
                    plans.Stale.Xml.Contains(
                        "IX_EnvironmentalObservations_TargetScopeKindObserved",
                        StringComparison.Ordinal) &&
                    plans.History.Xml.Contains(
                        "IX_EnvironmentalObservations_TargetKindObserved",
                        StringComparison.Ordinal),
                FreshLogicalReads: plans.Fresh.LogicalReads,
                StaleLogicalReads: plans.Stale.LogicalReads,
                HistoryLogicalReads: plans.History.LogicalReads,
                PlanSha256: planSha256));
        }
        return results;
    }

    private static async Task<CaptureInstantEvidence> MeasureCaptureInstantMatrixAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target)
    {
        const int instantCount = 1_000;
        const int maximumInstantOffset = 99_879;
        var selectedIds = new Guid[instantCount];
        var correct = 0;
        var instants = Enumerable.Range(0, instantCount)
            .Select(index => Epoch.AddSeconds(index * maximumInstantOffset / (instantCount - 1)))
            .ToArray();
        var expectedTimes = instants.Select(instant => instant.AddSeconds(120)).ToArray();
        Dictionary<DateTimeOffset, Guid> expected;
        await using (var db = new ApplicationDbContext(dbOptions))
        {
            expected = (await db.EnvironmentalObservations
                    .AsNoTracking()
                    .Where(observation => expectedTimes.Contains(observation.ObservedAtUtc))
                    .Select(observation => new { observation.ObservedAtUtc, observation.Id })
                    .ToArrayAsync()
                    .ConfigureAwait(false))
                .ToDictionary(item => item.ObservedAtUtc, item => item.Id);
        }
        for (var offset = 0; offset < instantCount; offset += 8)
        {
            var count = Math.Min(8, instantCount - offset);
            await Task.WhenAll(Enumerable.Range(offset, count).Select(async index =>
            {
                await using var db = new ApplicationDbContext(dbOptions);
                var service = new EnvironmentalObservationQueryService(
                    db,
                    Options.Create(new EnvironmentalObservationOptions()),
                    new FixedTimeProvider(BenchmarkNow),
                    telemetry);
                var instant = instants[index];
                var request = new EnvironmentalObservationCorrelationRequest(
                    new EnvironmentalObservationTarget(target.SiteId, target.AgentId, "rig-1"),
                    instant,
                    instant,
                    new EnvironmentalObservationSelector(
                        EnvironmentalObservationKind.RelativeHumidity,
                        [EnvironmentalObservationSourceKind.Measured],
                        [EnvironmentalObservationQuality.Good],
                        TimeSpan.FromDays(7)));
                var selectedId = await service
                    .BuildFreshSelectionIdQuery(request, EnvironmentalObservationSourceKind.Measured, specificity: 2)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
                selectedIds[index] = selectedId;
                if (expected.TryGetValue(instant.AddSeconds(120), out var expectedId) && selectedId == expectedId)
                {
                    Interlocked.Increment(ref correct);
                }
            })).ConfigureAwait(false);
        }
        return new(instantCount, correct, selectedIds);
    }

    private static async Task<EnvironmentalObservationMatch> CorrelateOnceAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationCorrelationRequest request)
    {
        await using var db = new ApplicationDbContext(dbOptions);
        var service = new EnvironmentalObservationQueryService(
            db,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(BenchmarkNow),
            telemetry);
        return await service.CorrelateAsync(request).ConfigureAwait(false);
    }

    private static async Task<TemporalScenarioEvidence> MeasureTemporalScenariosAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target)
    {
        var instant = Epoch.AddDays(20);
        var temperature = ScenarioObservation(
            600_000,
            target,
            "scenario-temperature",
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationUnit.DegreesCelsius,
            12,
            instant,
            instant,
            instant.AddSeconds(10),
            instant.AddSeconds(5));
        var pressureOlder = ScenarioObservation(
            600_001,
            target,
            "scenario-pressure",
            EnvironmentalObservationKind.AtmosphericPressure,
            EnvironmentalObservationUnit.Pascals,
            101_000,
            instant,
            instant.AddSeconds(-1),
            instant.AddSeconds(10),
            instant.AddSeconds(10));
        var pressureNewer = pressureOlder with
        {
            ObservationId = DeterministicGuid(600_002),
            ObservedAtUtc = instant.AddSeconds(1),
            Value = pressureOlder.Value with { NumericValue = 101_100 }
        };
        var cloudSkew = ScenarioObservation(
            600_003,
            target,
            "scenario-cloud-skew",
            EnvironmentalObservationKind.CloudCover,
            EnvironmentalObservationUnit.Fraction,
            0.2,
            instant.AddSeconds(60),
            instant.AddSeconds(-1),
            instant.AddSeconds(5),
            instant.AddSeconds(5));
        await IngestScenarioAsync(dbOptions, telemetry, temperature, BenchmarkNow).ConfigureAwait(false);
        await IngestScenarioAsync(dbOptions, telemetry, pressureNewer, BenchmarkNow).ConfigureAwait(false);
        await IngestScenarioAsync(dbOptions, telemetry, pressureOlder, BenchmarkNow.AddSeconds(1)).ConfigureAwait(false);
        await IngestScenarioAsync(dbOptions, telemetry, cloudSkew, BenchmarkNow).ConfigureAwait(false);

        var boundary = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.AirTemperature, instant, TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var staleBoundary = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.AirTemperature, instant.AddSeconds(5), TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var gap = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.AirTemperature, instant.AddSeconds(11), TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var missing = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.AirTemperature, instant.AddDays(1), TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var overlap = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.AtmosphericPressure, instant.AddSeconds(2), TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var skew = await CorrelateOnceAsync(
            dbOptions,
            telemetry,
            ScenarioRequest(target, EnvironmentalObservationKind.CloudCover, instant, TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        var selected = new[] { boundary, staleBoundary, gap, overlap, skew }
            .Where(match => match.Observation is not null)
            .Select(match => match.Observation!)
            .ToArray();
        var persisted = new List<ReceivedEnvironmentalObservationV1>();
        await using (var verificationDb = new ApplicationDbContext(dbOptions))
        {
            var verificationService = new EnvironmentalObservationQueryService(
                verificationDb,
                Options.Create(new EnvironmentalObservationOptions()),
                new FixedTimeProvider(BenchmarkNow),
                telemetry);
            foreach (var kind in selected.Select(observation => observation.Value.Kind).Distinct())
            {
                persisted.AddRange(await verificationService.QueryAsync(new EnvironmentalObservationQuery(
                    target.SiteId,
                    kind,
                    instant.AddHours(-1),
                    instant.AddDays(2),
                    Take: 100,
                    AgentId: target.AgentId,
                    RigId: "rig-1")).ConfigureAwait(false));
            }
        }
        var hashesVerified = selected.All(observation => persisted.Any(received =>
            received.Observation.ObservationId == observation.ObservationId &&
            EnvironmentalObservationJson.Validate(received).IsValid &&
            received.ContentSha256 == EnvironmentalObservationJson.ComputeContentSha256(observation)));
        return new(
            ExactBoundaryFresh: boundary.Status == EnvironmentalObservationMatchStatus.Fresh,
            StaleBoundaryExplicit: staleBoundary.Status == EnvironmentalObservationMatchStatus.Stale,
            GapReturnsStale: gap.Status == EnvironmentalObservationMatchStatus.Stale,
            BeyondMaximumMissing: missing.Status == EnvironmentalObservationMatchStatus.Missing,
            OverlapDeterministic: overlap.HadOverlap && overlap.Observation?.ObservationId == pressureNewer.ObservationId,
            ClockSkewSelected: skew.Observation?.ObservationId == cloudSkew.ObservationId && skew.Age == TimeSpan.Zero,
            OutOfOrderReceiptIgnored: overlap.Observation?.ObservationId == pressureNewer.ObservationId,
            HashesVerified: hashesVerified,
            SelectedObservationIds: selected.Select(observation => observation.ObservationId).ToArray());
    }

    private static EnvironmentalObservationCorrelationRequest ScenarioRequest(
        PerformanceTarget target,
        EnvironmentalObservationKind kind,
        DateTimeOffset instant,
        TimeSpan maximumStaleness)
        => new(
            new EnvironmentalObservationTarget(target.SiteId, target.AgentId, "rig-1"),
            instant,
            instant,
            new EnvironmentalObservationSelector(
                kind,
                [EnvironmentalObservationSourceKind.Measured],
                [EnvironmentalObservationQuality.Good],
                maximumStaleness));

    private static async Task IngestScenarioAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationV1 observation,
        DateTimeOffset receivedAtUtc)
    {
        await using var db = new ApplicationDbContext(dbOptions);
        var service = new EnvironmentalObservationIngestService(
            db,
            new FixedTimeProvider(receivedAtUtc),
            Options.Create(new EnvironmentalObservationOptions()),
            telemetry,
            NullLogger<EnvironmentalObservationIngestService>.Instance);
        await service.IngestAsync(observation).ConfigureAwait(false);
    }

    private static EnvironmentalObservationV1 ScenarioObservation(
        int identity,
        PerformanceTarget target,
        string sourceId,
        EnvironmentalObservationKind kind,
        EnvironmentalObservationUnit unit,
        double value,
        DateTimeOffset observedAt,
        DateTimeOffset validFrom,
        DateTimeOffset validThrough,
        DateTimeOffset staleAfter)
    {
        var parameters = Json("""{"scenario":"temporal-correlation-v1"}""");
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            DeterministicGuid(identity),
            new EnvironmentalObservationTarget(target.SiteId, target.AgentId, "rig-1"),
            new EnvironmentalObservationSource(
                "scenario-provider",
                sourceId,
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("scenario-normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            observedAt,
            null,
            null,
            validFrom,
            validThrough,
            staleAfter,
            new EnvironmentalObservationValue(
                kind,
                unit,
                value,
                null,
                EnvironmentalObservationQuality.Good,
                0.1),
            []);
    }

    private static async Task<CorrelationPlanEvidence> ReadTemporalPlansAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationCorrelationRequest request)
    {
        await using var db = new ApplicationDbContext(dbOptions);
        var service = new EnvironmentalObservationQueryService(
            db,
            Options.Create(new EnvironmentalObservationOptions()),
            new FixedTimeProvider(BenchmarkNow),
            telemetry);
        var freshSql = service
            .BuildFreshSelectionIdQuery(request, EnvironmentalObservationSourceKind.Measured, specificity: 2)
            .Take(1)
            .ToQueryString();
        var staleInstant = request.ThroughUtc.AddSeconds(121);
        var staleRequest = request with
        {
            FromUtc = staleInstant,
            ThroughUtc = staleInstant,
            Selector = request.Selector with { MaximumStaleness = TimeSpan.FromMinutes(10) }
        };
        var staleSql = service
            .BuildStaleSelectionIdQuery(staleRequest, EnvironmentalObservationSourceKind.Measured, specificity: 2)
            .Take(1)
            .ToQueryString();
        var historySql = service.BuildHistorySelectionIdQuery(new EnvironmentalObservationQuery(
                request.Target.SiteId,
                request.Selector.Kind,
                request.FromUtc.AddMinutes(-10),
                request.ThroughUtc.AddSeconds(1),
                Take: 100,
                AgentId: request.Target.AgentId,
                RigId: request.Target.RigId))
            .ToQueryString();
        var connectionString = db.Database.GetConnectionString() ??
            throw new InvalidOperationException("The performance SQL connection string is unavailable.");
        return new(
            await ReadPlanAsync(connectionString, freshSql).ConfigureAwait(false),
            await ReadPlanAsync(connectionString, staleSql).ConfigureAwait(false),
            await ReadPlanAsync(connectionString, historySql).ConfigureAwait(false));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command is generated entirely by EF Core from test-owned production query builders.")]
    private static async Task<PlanEvidence> ReadPlanAsync(string connectionString, string productionSql)
    {
        productionSql = string.Join('\n', productionSql.Split('\n').Where(line =>
            !line.Contains("This LINQ query is being executed in split-query mode", StringComparison.Ordinal) &&
            !line.Contains("The SQL shown is for the first query", StringComparison.Ordinal) &&
            !line.Contains("Additional queries may also be executed", StringComparison.Ordinal)));
        var sqlStart = productionSql.IndexOf("DECLARE ", StringComparison.Ordinal);
        if (sqlStart < 0)
        {
            sqlStart = productionSql.IndexOf("SELECT ", StringComparison.Ordinal);
        }
        productionSql = productionSql[sqlStart..];
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET STATISTICS XML ON; SET STATISTICS IO ON; {productionSql} SET STATISTICS IO OFF; SET STATISTICS XML OFF;";
            var plan = string.Empty;
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.FieldCount == 1 && !await reader.IsDBNullAsync(0).ConfigureAwait(false) &&
                        reader.GetValue(0) is string value && value.Contains("ShowPlanXML", StringComparison.Ordinal))
                    {
                        plan = value;
                    }
                }
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));
            var logicalReads = Regex.Matches(messages.ToString(), @"logical reads (?<value>\d+)", RegexOptions.IgnoreCase)
                .Sum(match => int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture));
            return new(plan, logicalReads);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<RetentionEvidence> MeasureRetentionRestartAsync(
        string connectionString,
        CountingCommandInterceptor interceptor,
        Guid siteId,
        Guid agentId,
        EnvironmentalObservationTelemetry telemetry)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(builder => builder
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor));
        await using var provider = services.BuildServiceProvider();
        Guid sourceId;
        string sourceIdentity;
        Guid[] pinnedIds;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = CreateSource(siteId, agentId, "retention-performance");
            db.EnvironmentalObservationSources.Add(source);
            await db.SaveChangesAsync().ConfigureAwait(false);
            sourceId = source.Id;
            sourceIdentity = source.IdentitySha256;
            var cutoff = BenchmarkNow.AddDays(-30);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH [Numbers] AS
                (
                    SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS [N]
                    FROM [sys].[all_objects] AS [a]
                    CROSS JOIN [sys].[all_objects] AS [b]
                )
                INSERT INTO [EnvironmentalObservations]
                    ([Id], [SourceRecordId], [SiteId], [AgentId], [RigId], [SourceKind], [SourceIdentitySha256],
                     [ObservationId], [SchemaVersion], [Kind], [Unit], [NumericValue],
                     [BooleanValue], [Quality], [Uncertainty], [SubmittedNumericValue], [SubmittedUnit],
                     [ObservedAtUtc], [ObservedFromUtc], [ObservedThroughUtc], [ValidFromUtc], [ValidThroughUtc],
                     [StaleAfterUtc], [ReceivedAtUtc], [ApparentClockOffsetSeconds], [ClockDiagnostic], [PayloadSha256])
                SELECT NEWID(), {sourceId}, {siteId}, {agentId}, {"rig-1"},
                       {EnvironmentalObservationSourceKind.Measured.ToString()}, {sourceIdentity},
                       NEWID(), {EnvironmentalObservationV1.CurrentSchemaVersion},
                       {EnvironmentalObservationKind.RelativeHumidity.ToString()}, {EnvironmentalObservationUnit.Percent.ToString()},
                       50, NULL, {EnvironmentalObservationQuality.Good.ToString()}, NULL, NULL, NULL,
                       DATEADD(day, -40, {BenchmarkNow}), NULL, NULL, DATEADD(day, -41, {BenchmarkNow}),
                       DATEADD(day, -39, {BenchmarkNow}), DATEADD(day, -39, {BenchmarkNow}),
                       CASE WHEN [N] < 10000 THEN DATEADD(second, -1 - [N], {cutoff})
                            WHEN [N] < 11000 THEN {cutoff}
                            ELSE DATEADD(second, 1 + [N], {cutoff}) END,
                       0, {EnvironmentalClockDiagnostic.ClockBehindOrDeliveryDelayed.ToString()}, {new string('B', 64)}
                FROM [Numbers];
                """).ConfigureAwait(false);
            pinnedIds = await db.EnvironmentalObservations
                .Where(observation => observation.SourceRecordId == sourceId && observation.ReceivedAtUtc < cutoff)
                .OrderBy(observation => observation.Id)
                .Select(observation => observation.Id)
                .Take(100)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var pinOwners = await db.EnvironmentalObservations
                .Where(observation => observation.SourceRecordId == sourceId && observation.ReceivedAtUtc > cutoff)
                .OrderByDescending(observation => observation.Id)
                .Select(observation => observation.Id)
                .Take(100)
                .ToArrayAsync()
                .ConfigureAwait(false);
            db.EnvironmentalObservationLineage.AddRange(pinnedIds.Select((pinnedId, ordinal) =>
                new EnvironmentalObservationLineageRecord
                {
                    DerivedObservationRecordId = pinOwners[ordinal],
                    Ordinal = 0,
                    SourceObservationRecordId = pinnedId
                }));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var retentionOptions = Options.Create(new EnvironmentalObservationOptions
        {
            ReceiptRetentionDays = 30,
            RetentionBatchSize = 1_000
        });
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var rssBefore = process.WorkingSet64;
        interceptor.Reset();
        var worker = new EnvironmentalObservationRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            retentionOptions,
            new FixedTimeProvider(BenchmarkNow),
            new EnvironmentalRetentionState(),
            telemetry,
            NullLogger<EnvironmentalObservationRetentionWorker>.Instance);
        var started = Stopwatch.GetTimestamp();
        var interrupted = 0;
        for (var batch = 0; batch < 4; batch++)
        {
            interrupted += await worker.SweepBatchAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var restartedWorker = new EnvironmentalObservationRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            retentionOptions,
            new FixedTimeProvider(BenchmarkNow),
            new EnvironmentalRetentionState(),
            telemetry,
            NullLogger<EnvironmentalObservationRetentionWorker>.Instance);
        var resumed = await restartedWorker.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        await using var verificationScope = provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var retained = await verification.EnvironmentalObservations.CountAsync(record => record.SourceRecordId == sourceId)
            .ConfigureAwait(false);
        var retainedPins = await verification.EnvironmentalObservations
            .Where(record => pinnedIds.Contains(record.Id))
            .OrderBy(record => record.Id)
            .Select(record => record.Id)
            .ToArrayAsync()
            .ConfigureAwait(false);
        var pinnedIdentityHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
            string.Concat(retainedPins.Select(id => id.ToString("N", CultureInfo.InvariantCulture))))));
        return new(
            SeedRows: 20_000,
            ExpiredRows: 10_000,
            EligibleRows: 9_900,
            BoundaryRows: 1_000,
            PinnedRows: retainedPins.Length,
            BatchSize: 1_000,
            InterruptedRows: interrupted,
            ResumedRows: resumed,
            RetainedRows: retained,
            SqlCommands: interceptor.Count,
            SqlCommandTextBytes: interceptor.CommandTextBytes,
            LockBehavior: "One transaction-owned exclusive environmental retention application lock per batch.",
            PinnedObservationIdsSha256: pinnedIdentityHash,
            ElapsedMilliseconds: elapsed.TotalMilliseconds,
            RowsPerSecond: 9_900 / elapsed.TotalSeconds,
            CpuMilliseconds: (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            AllocatedBytes: GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            WorkingSetDeltaBytes: process.WorkingSet64 - rssBefore);
    }

    private static async Task<IReadOnlyList<ConcurrencyEvidence>> MeasureDuplicateConcurrencyAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        EnvironmentalObservationTelemetry telemetry,
        PerformanceTarget target,
        EnvironmentalObservationV1 template)
    {
        var results = new List<ConcurrencyEvidence>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            var observation = template with
            {
                ObservationId = DeterministicGuid(200_000 + concurrency),
                ObservedAtUtc = template.ObservedAtUtc.AddSeconds(concurrency),
                ValidFromUtc = template.ValidFromUtc.AddSeconds(concurrency),
                ValidThroughUtc = template.ValidThroughUtc.AddSeconds(concurrency),
                StaleAfterUtc = template.StaleAfterUtc.AddSeconds(concurrency)
            };
            var started = Stopwatch.GetTimestamp();
            var dispositions = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                await using var db = new ApplicationDbContext(dbOptions);
                var service = new EnvironmentalObservationIngestService(
                    db,
                    new FixedTimeProvider(BenchmarkNow),
                    Options.Create(new EnvironmentalObservationOptions()),
                    telemetry,
                    NullLogger<EnvironmentalObservationIngestService>.Instance);
                return (await service.IngestAsync(observation).ConfigureAwait(false)).Disposition;
            })).ConfigureAwait(false);
            await using var verification = new ApplicationDbContext(dbOptions);
            var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
            var durableRows = await verification.EnvironmentalObservations.CountAsync(record =>
                record.Source!.IdentitySha256 == sourceIdentity && record.ObservationId == observation.ObservationId)
                .ConfigureAwait(false);
            var conflicts = 0;
            await using (var conflictDb = new ApplicationDbContext(dbOptions))
            {
                var conflictService = new EnvironmentalObservationIngestService(
                    conflictDb,
                    new FixedTimeProvider(BenchmarkNow),
                    Options.Create(new EnvironmentalObservationOptions()),
                    telemetry,
                    NullLogger<EnvironmentalObservationIngestService>.Instance);
                try
                {
                    await conflictService.IngestAsync(observation with
                    {
                        Value = observation.Value with { NumericValue = observation.Value.NumericValue + 1 }
                    }).ConfigureAwait(false);
                }
                catch (EnvironmentalObservationConflictException)
                {
                    conflicts++;
                }
            }
            results.Add(new(
                Concurrency: concurrency,
                Accepted: dispositions.Count(item => item == EnvironmentalObservationIngestDisposition.Accepted),
                Duplicates: dispositions.Count(item => item == EnvironmentalObservationIngestDisposition.Duplicate),
                Conflicts: conflicts,
                DurableRows: durableRows,
                ElapsedMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        return results;
    }

    private static async Task<FaultEvidence> MeasureFaultRecoveryAsync(
        DbContextOptions<ApplicationDbContext> dbOptions,
        CountingCommandInterceptor interceptor,
        EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationV1 template)
    {
        var observation = template with
        {
            ObservationId = DeterministicGuid(700_000),
            ObservedAtUtc = template.ObservedAtUtc.AddMinutes(1),
            ValidFromUtc = template.ValidFromUtc.AddMinutes(1),
            ValidThroughUtc = template.ValidThroughUtc.AddMinutes(1),
            StaleAfterUtc = template.StaleAfterUtc.AddMinutes(1)
        };
        interceptor.FailNextEnvironmentalInsert();
        var failureObserved = false;
        await using (var failedDb = new ApplicationDbContext(dbOptions))
        {
            var failedService = new EnvironmentalObservationIngestService(
                failedDb,
                new FixedTimeProvider(BenchmarkNow),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            try
            {
                await failedService.IngestAsync(observation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
            {
                failureObserved = true;
            }
        }
        EnvironmentalObservationIngestResult retry;
        await using (var retryDb = new ApplicationDbContext(dbOptions))
        {
            var retryService = new EnvironmentalObservationIngestService(
                retryDb,
                new FixedTimeProvider(BenchmarkNow),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            retry = await retryService.IngestAsync(observation).ConfigureAwait(false);
        }
        EnvironmentalObservationIngestResult duplicate;
        var invalidRejected = 0;
        await using (var restartDb = new ApplicationDbContext(dbOptions))
        {
            var restartService = new EnvironmentalObservationIngestService(
                restartDb,
                new FixedTimeProvider(BenchmarkNow.AddSeconds(1)),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            duplicate = await restartService.IngestAsync(observation).ConfigureAwait(false);
            var invalid = new[]
            {
                observation with
                {
                    ObservationId = DeterministicGuid(700_001),
                    Value = observation.Value with { Unit = EnvironmentalObservationUnit.Pascals }
                },
                observation with
                {
                    ObservationId = DeterministicGuid(700_002),
                    Value = observation.Value with { NumericValue = 101 }
                },
                observation with
                {
                    ObservationId = DeterministicGuid(700_003),
                    ValidThroughUtc = observation.ValidFromUtc
                },
                observation with
                {
                    ObservationId = DeterministicGuid(700_004),
                    Source = observation.Source with
                    {
                        Provenance = observation.Source.Provenance with { ParametersSha256 = new string('D', 64) }
                    }
                },
                observation with
                {
                    ObservationId = DeterministicGuid(700_005),
                    Source = observation.Source with { Kind = EnvironmentalObservationSourceKind.Derived }
                }
            };
            foreach (var invalidObservation in invalid)
            {
                try
                {
                    await restartService.IngestAsync(invalidObservation).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    invalidRejected++;
                }
            }
        }
        var postCommitObservation = observation with
        {
            ObservationId = DeterministicGuid(700_006),
            ObservedAtUtc = observation.ObservedAtUtc.AddMinutes(2),
            ValidFromUtc = observation.ValidFromUtc.AddMinutes(2),
            ValidThroughUtc = observation.ValidThroughUtc.AddMinutes(2),
            StaleAfterUtc = observation.StaleAfterUtc.AddMinutes(2)
        };
        var acknowledgementLost = false;
        DateTimeOffset committedReceipt = default;
        try
        {
            await using var commitDb = new ApplicationDbContext(dbOptions);
            var commitService = new EnvironmentalObservationIngestService(
                commitDb,
                new FixedTimeProvider(BenchmarkNow.AddSeconds(2)),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            var committed = await commitService.IngestAsync(postCommitObservation).ConfigureAwait(false);
            committedReceipt = committed.ReceivedAtUtc;
            throw new IOException("Injected acknowledgement loss after durable commit.");
        }
        catch (IOException)
        {
            acknowledgementLost = true;
        }
        EnvironmentalObservationIngestResult postCommitRetry;
        await using (var restartedDb = new ApplicationDbContext(dbOptions))
        {
            var restartedService = new EnvironmentalObservationIngestService(
                restartedDb,
                new FixedTimeProvider(BenchmarkNow.AddSeconds(3)),
                Options.Create(new EnvironmentalObservationOptions()),
                telemetry,
                NullLogger<EnvironmentalObservationIngestService>.Instance);
            postCommitRetry = await restartedService.IngestAsync(postCommitObservation).ConfigureAwait(false);
        }
        await using var verification = new ApplicationDbContext(dbOptions);
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
        var durableRows = await verification.EnvironmentalObservations.CountAsync(record =>
            record.Source!.IdentitySha256 == sourceIdentity &&
            (record.ObservationId == observation.ObservationId ||
                record.ObservationId == postCommitObservation.ObservationId))
            .ConfigureAwait(false);
        return new(
            PreCommitFailureObserved: failureObserved,
            RetryAccepted: retry.Disposition == EnvironmentalObservationIngestDisposition.Accepted,
            RestartDuplicate: duplicate.Disposition == EnvironmentalObservationIngestDisposition.Duplicate &&
                duplicate.ReceivedAtUtc == retry.ReceivedAtUtc,
            PostCommitAcknowledgementLost: acknowledgementLost,
            PostCommitRetryDuplicate: postCommitRetry.Disposition == EnvironmentalObservationIngestDisposition.Duplicate &&
                postCommitRetry.ReceivedAtUtc == committedReceipt,
            InvalidRejected: invalidRejected,
            DurableRows: durableRows);
    }

    private static async Task<PerformanceTarget> SeedTargetAsync(ApplicationDbContext db)
    {
        var site = new Observatory
        {
            OwnerUserId = "environment-performance-owner",
            Name = "Environment performance site",
            TimeZoneId = "UTC",
            CreatedAtUtc = BenchmarkNow
        };
        var agentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var registration = new DeviceRegistration
        {
            DeviceId = "environment-performance-agent",
            Observatory = site,
            ObservatoryId = site.Id,
            FriendlyName = "Environment performance agent",
            ObservatoryName = site.Name,
            OwnerUserId = site.OwnerUserId,
            OwnerDisplayName = "Performance",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = agentId,
            DeviceKeyHash = new string('B', 64),
            IssuedAtUtc = BenchmarkNow.AddDays(-1),
            ExpiresAtUtc = BenchmarkNow.AddDays(1)
        };
        db.DeviceRegistrations.Add(registration);
        db.CentralFrames.Add(new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = agentId,
            ObservatoryId = site.Id,
            AgentId = agentId.ToString("D", CultureInfo.InvariantCulture),
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Epoch,
            FirstReceivedAtUtc = Epoch,
            RigId = "rig-1"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(site.Id, registration.Id, agentId);
    }

    private static EnvironmentalObservationV1 CreateObservation(int index, Guid siteId, Guid agentId)
    {
        var provider = index % 10;
        var parameters = Json($$"""{"provider":{{provider}},"normalization":"canonical-v1"}""");
        var observed = Epoch.AddSeconds(index);
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            DeterministicGuid(index),
            new EnvironmentalObservationTarget(siteId, agentId, "rig-1"),
            new EnvironmentalObservationSource(
                $"provider-{provider}",
                $"weather-{provider}",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("environment-normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            observed,
            null,
            null,
            observed.AddSeconds(-120),
            observed.AddSeconds(120),
            observed.AddSeconds(60),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                30 + index % 60,
                null,
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private static EnvironmentalObservationSourceRecord CreateSource(
        Guid siteId,
        Guid agentId,
        string sourceId)
        => new()
        {
            IdentitySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + ":identity"))),
            ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + ":content"))),
            SiteId = siteId,
            AgentId = agentId,
            RigId = "rig-1",
            Provider = "performance",
            SourceId = sourceId,
            Version = "1.0.0",
            Kind = EnvironmentalObservationSourceKind.Measured,
            MethodName = "performance-seed",
            MethodVersion = "1.0.0",
            ParametersJson = "{}",
            ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(Json("{}")),
            CreatedAtUtc = BenchmarkNow
        };

    private static Guid DeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        bytes[15] = 1;
        return new Guid(bytes);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        return ordered[Math.Max(0, (int)Math.Ceiling(ordered.Length * percentile) - 1)];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
        => RunCommand("git", workingDirectory, arguments);

    private static string RunCommand(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    private static async Task<string> ReadSqlServerVersionAsync(ApplicationDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))";
            return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? "unknown");
        }
        finally
        {
            if (close)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static string ReadCpuIdentity()
    {
        const string cpuInfoPath = "/proc/cpuinfo";
        if (File.Exists(cpuInfoPath))
        {
            var model = File.ReadLines(cpuInfoPath)
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                var separator = model.IndexOf(':', StringComparison.Ordinal);
                return separator >= 0 ? model[(separator + 1)..].Trim() : model.Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private long count;
        private long commandTextBytes;
        private int failNextInsert;
        public long Count => Interlocked.Read(ref count);
        public long CommandTextBytes => Interlocked.Read(ref commandTextBytes);
        public void Reset()
        {
            Interlocked.Exchange(ref count, 0);
            Interlocked.Exchange(ref commandTextBytes, 0);
        }
        public void FailNextEnvironmentalInsert() => Interlocked.Exchange(ref failNextInsert, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            ThrowInjectedFailure(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            ThrowInjectedFailure(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            Interlocked.Increment(ref count);
            Interlocked.Add(ref commandTextBytes, Encoding.UTF8.GetByteCount(command.CommandText));
        }

        private void ThrowInjectedFailure(DbCommand command)
        {
            if (Volatile.Read(ref failNextInsert) == 1 &&
                command.CommandText.Contains("INSERT INTO [EnvironmentalObservations]", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref failNextInsert, 0) == 1)
            {
                throw new InvalidOperationException("Injected pre-commit environmental observation failure.");
            }
        }
    }

    private sealed record PerformanceTarget(Guid SiteId, Guid RegistrationId, Guid AgentId);
    private sealed record IngestRunEvidence(IngestEvidence Ingest, IReadOnlyList<CorrelationEvidence> OneThousandCorrelation);
    private sealed record ContractEvidence(int Warmups, int Samples, int Fixtures, int InvalidFixtures, int PayloadBytes, double MedianMilliseconds, double P95Milliseconds, long AllocatedBytes, string ContentSha256, string ExtensionPolicy);
    private sealed record IngestEvidence(int Observations, int Providers, int WarmupBatches, int SetupBatches, int MeasuredBatches, int BatchSize, int ArrivalRatePerSecond, double ObservationsPerSecond, double MedianBatchMilliseconds, double P95BatchMilliseconds, long SqlCommands, int Transactions, long PayloadBytes, double CpuMilliseconds, long AllocatedBytes, long WorkingSetDeltaBytes);
    private sealed record CorrelationEvidence(int HistoryRows, int Concurrency, int Warmups, int Samples, double MedianMilliseconds, double P95Milliseconds, double QueriesPerSecond, long SqlCommands, double CpuMilliseconds, long AllocatedBytes, long WorkingSetDeltaBytes, bool SelectedCorrectly, bool TemporalIndexUsed, int FreshLogicalReads, int StaleLogicalReads, int HistoryLogicalReads, string PlanSha256);
    private sealed record CaptureInstantEvidence(int Instants, int CorrectSelections, IReadOnlyList<Guid> SelectedObservationIds);
    private sealed record TemporalScenarioEvidence(bool ExactBoundaryFresh, bool StaleBoundaryExplicit, bool GapReturnsStale, bool BeyondMaximumMissing, bool OverlapDeterministic, bool ClockSkewSelected, bool OutOfOrderReceiptIgnored, bool HashesVerified, IReadOnlyList<Guid> SelectedObservationIds);
    private sealed record RetentionEvidence(int SeedRows, int ExpiredRows, int EligibleRows, int BoundaryRows, int PinnedRows, int BatchSize, int InterruptedRows, int ResumedRows, int RetainedRows, long SqlCommands, long SqlCommandTextBytes, string LockBehavior, string PinnedObservationIdsSha256, double ElapsedMilliseconds, double RowsPerSecond, double CpuMilliseconds, long AllocatedBytes, long WorkingSetDeltaBytes);
    private sealed record ConcurrencyEvidence(int Concurrency, int Accepted, int Duplicates, int Conflicts, int DurableRows, double ElapsedMilliseconds);
    private sealed record FaultEvidence(bool PreCommitFailureObserved, bool RetryAccepted, bool RestartDuplicate, bool PostCommitAcknowledgementLost, bool PostCommitRetryDuplicate, int InvalidRejected, int DurableRows);
    private sealed record PlanEvidence(string Xml, int LogicalReads);
    private sealed record CorrelationPlanEvidence(PlanEvidence Fresh, PlanEvidence Stale, PlanEvidence History);
}
