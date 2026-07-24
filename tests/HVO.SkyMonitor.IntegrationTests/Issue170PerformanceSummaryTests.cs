using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue170PerformanceSummaryTests
{
    private const string RequiredBaselineProductionCommit = "57098c0f04e88e2a1a85b3ac1b18a8b675fa6257";
    private static readonly string[] EvidenceFiles =
    [
        "device-bootstrap-performance.json",
        "deployment-location-authority-performance.json",
        "logichost-ingest-performance.json"
    ];
    private static readonly string[] ThroughputMetricSuffixes =
    [
        "operationsPerSecond", "queriesPerSecond", "capturesPerSecond", "bytesPerSecond",
        "convergencesPerSecond", "payloadBytesPerSecond"
    ];
    private static readonly string[] MillisecondMetricSuffixes =
    [
        "elapsedMilliseconds", "migrationMilliseconds", "backfillMilliseconds",
        "restartConvergenceMilliseconds", "recoveryLatencyMilliseconds",
        "totalTransitionMilliseconds", "cpuMilliseconds", "oldestAgeMilliseconds"
    ];
    private static readonly string[] ByteMetricSuffixes =
    [
        "allocatedBytes", "workingSetDeltaBytes", "workingSetObservedPeakBytes",
        "workingSetBeforeBytes", "workingSetAfterBytes", "rssStartBytes", "rssPeakBytes", "rssEndBytes",
        "runtimeAllocationCounterDeltaBytes",
        "dataAllocatedGrowthBytes", "logAllocatedGrowthBytes", "dataUsedGrowthBytes",
        "logUsedGrowthBytes", "requestBodyBytes", "responseBodyBytes", "multipartRequestBodyBytes",
        "multipartPayloadBytes", "statusRequestBodyBytes", "logicalPayloadBytes",
        "requestContentLengthBytesObserved", "responseContentLengthBytesObserved"
    ];
    private static readonly string[] CountMetricSuffixes =
    [
        "logicalReads", "sqlCommands", "firstPageSqlCommands", "traversalSqlCommands",
        "deadlockRetries", "serverErrorRetries", "pendingReferenceRetries", "requests",
        "multipartPosts", "statusPosts", "minioRequestsObserved", "minioGetObserved",
        "minioPutObserved", "minioPostObserved", "minioDeleteObserved", "minioHeadObserved",
        "requestsWithoutContentLength", "responsesWithoutContentLength",
        "sqlTransactionsStartedObserved", "sqlTransactionsCommittedObserved",
        "sqlTransactionsRolledBackObserved", "sqlTransactionsFailedObserved"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task FiveTrialEvidence_WritesDeterministicReviewedSummary()
    {
        var run = Issue170PerformanceEvidence.Create();
        var candidateCommit = RequiredCommit("HVO_EVIDENCE_CANDIDATE_REVISION");
        if (!string.Equals(candidateCommit, run.Commit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The candidate summary must run from the candidate commit.");
        }
        var baselineCommit = RequiredCommit("HVO_EVIDENCE_BASELINE_REVISION");
        var candidateProductionCommit = RequiredCommit("HVO_EVIDENCE_CANDIDATE_PRODUCTION_REVISION");
        var baselineProductionCommit = RequiredCommit("HVO_EVIDENCE_BASELINE_PRODUCTION_REVISION");
        if (!string.Equals(
                baselineProductionCommit, RequiredBaselineProductionCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The issue #170 baseline production revision must be {RequiredBaselineProductionCommit}.");
        }
        var workloads = new SortedDictionary<string, WorkloadSummary>(StringComparer.Ordinal);
        foreach (var fileName in EvidenceFiles)
        {
            var candidate = LoadTrials(run.RepositoryRoot, candidateCommit, fileName, required: true)!;
            var baseline = LoadTrials(
                run.RepositoryRoot,
                baselineCommit,
                fileName,
                required: fileName != "deployment-location-authority-performance.json");
            if (fileName == "logichost-ingest-performance.json"
                && (!candidate.LocationModes.SequenceEqual(["location-bound"], StringComparer.Ordinal)
                    || baseline is null
                    || !baseline.LocationModes.SequenceEqual(["location-null"], StringComparer.Ordinal)))
            {
                candidate.Dispose();
                baseline?.Dispose();
                throw new InvalidDataException(
                    "Ingest comparison requires an explicit location-null baseline and location-bound candidate.");
            }
            if (!string.Equals(candidate.ProductionCommit, candidateProductionCommit, StringComparison.OrdinalIgnoreCase)
                || baseline is not null && !string.Equals(
                    baseline.ProductionCommit, baselineProductionCommit, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                baseline?.Dispose();
                throw new InvalidDataException("Evidence production revisions do not match the reviewed revisions.");
            }
            workloads.Add(fileName, Summarize(
                candidate,
                baseline,
                requireComparable: fileName != "deployment-location-authority-performance.json"));
        }
        var summary = new ReviewedSummary(
            "hvo-issue-170-five-trial-summary-v1",
            170,
            candidateCommit,
            baselineCommit,
            candidateProductionCommit,
            baselineProductionCommit,
            "dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror",
            $"HVO_EVIDENCE_REVISION={candidateCommit} HVO_EVIDENCE_PRODUCTION_REVISION={candidateProductionCommit} HVO_EVIDENCE_TRIAL=1 HVO_EVIDENCE_CANDIDATE_REVISION={candidateCommit} HVO_EVIDENCE_BASELINE_REVISION={baselineCommit} HVO_EVIDENCE_CANDIDATE_PRODUCTION_REVISION={candidateProductionCommit} HVO_EVIDENCE_BASELINE_PRODUCTION_REVISION={baselineProductionCommit} dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~Issue170PerformanceSummaryTests.FiveTrialEvidence_WritesDeterministicReviewedSummary",
            "Minimum/median/maximum use five independent processes. Pooled latency uses all retained samples. Paired deltas require matching immutable harness, normalized invariant workload, method, environment, scenario, metric, and sample shapes. The ingest workload normalization excludes only the explicitly recorded location-null baseline versus location-bound candidate mode required by issue #170.",
            workloads);
        var path = Path.Combine(run.RepositoryRoot, "docs", "validation", "issue-170-performance-summary.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (!(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("The reviewed summary already exists with different content.");
            }
        }
    }

    private static TrialSet? LoadTrials(
        string root,
        string commit,
        string fileName,
        bool required)
    {
        var documents = new List<JsonDocument>(5);
        var hashes = new List<string>(5);
        for (var trial = 1; trial <= 5; trial++)
        {
            var path = Path.Combine(
                root, "TestResults", "issue-170", commit, $"trial-{trial:D2}", fileName);
            if (!File.Exists(path))
            {
                foreach (var document in documents)
                {
                    document.Dispose();
                }
                if (!required && trial == 1)
                {
                    return null;
                }
                throw new InvalidDataException($"Exactly five trials are required; missing {path}.");
            }
            var bytes = File.ReadAllBytes(path);
            hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
            documents.Add(JsonDocument.Parse(bytes));
            var recordedTrial = Property(Property(documents[^1].RootElement, "revision"), "trial").GetInt32();
            if (recordedTrial != trial)
            {
                throw new InvalidDataException($"{path} records trial {recordedTrial}, expected {trial}.");
            }
        }

        var roots = documents.Select(document => document.RootElement).ToArray();
        foreach (var rootElement in roots)
        {
            var recordedCommit = Property(Property(rootElement, "revision"), "commit").GetString();
            if (!string.Equals(recordedCommit, commit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{fileName} contains evidence for a different commit.");
            }
        }
        var environmentFingerprint = Property(
            Property(Property(roots[0], "environment"), "observed"), "fingerprintSha256").GetString()!;
        var workloadFingerprint = WorkloadFingerprint(Property(roots[0], "workload"));
        var assemblies = Property(Property(roots[0], "revision"), "assemblies").Clone();
        var assemblyFingerprint = Fingerprint(assemblies);
        var productionCommit = Property(Property(roots[0], "revision"), "productionCommit").GetString()!;
        var harnessSourceSha256 = Property(
            Property(roots[0], "revision"), "harnessSourceSha256").GetString()!;
        var topologyFingerprint = Fingerprint(Property(roots[0], "environment"));
        var methodFingerprint = Fingerprint(Property(roots[0], "method"));
        var locationModes = ReadStringProperties(Property(roots[0], "workload"), "intentionalLocationMode");
        var runIds = roots.Select(rootElement => Property(
            Property(rootElement, "revision"), "runId").GetGuid()).ToArray();
        var runs = roots.Select(rootElement => new RunEvidence(
            Property(Property(rootElement, "revision"), "runId").GetGuid(),
            Property(Property(rootElement, "revision"), "processId").GetInt32(),
            Property(Property(rootElement, "revision"), "processStartUtc").GetDateTime())).ToArray();
        if (runIds.Distinct().Count() != roots.Length
            || runs.Select(run => (run.ProcessId, run.ProcessStartUtc)).Distinct().Count() != roots.Length
            || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count)
        {
            throw new InvalidDataException($"{fileName} trials are not five independent process runs.");
        }
        if (roots.Skip(1).Any(rootElement =>
                Property(Property(Property(rootElement, "environment"), "observed"), "fingerprintSha256").GetString()
                    != environmentFingerprint
                || WorkloadFingerprint(Property(rootElement, "workload")) != workloadFingerprint
                || Fingerprint(Property(Property(rootElement, "revision"), "assemblies")) != assemblyFingerprint
                || Property(Property(rootElement, "revision"), "productionCommit").GetString() != productionCommit
                || Property(Property(rootElement, "revision"), "harnessSourceSha256").GetString()
                    != harnessSourceSha256
                || Fingerprint(Property(rootElement, "environment")) != topologyFingerprint
                || Fingerprint(Property(rootElement, "method")) != methodFingerprint
                || !ReadStringProperties(Property(rootElement, "workload"), "intentionalLocationMode")
                    .SequenceEqual(locationModes, StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"{fileName} trials do not share environment, workload, and assembly fingerprints.");
        }
        ValidateMeasurementShapes(roots, fileName);
        return new TrialSet(
            documents,
            hashes,
            roots.Select(rootElement => Property(rootElement, "correctness").Clone()).ToArray(),
            roots.Select(rootElement => Property(rootElement, "io").Clone()).ToArray(),
            environmentFingerprint,
            workloadFingerprint,
            assemblyFingerprint,
            assemblies,
            productionCommit,
            harnessSourceSha256,
            runs,
            locationModes,
            topologyFingerprint,
            methodFingerprint);
    }

    private static WorkloadSummary Summarize(
        TrialSet candidate,
        TrialSet? baseline,
        bool requireComparable)
    {
        try
        {
            var candidateScalars = ReadScalars(candidate);
            var candidateLatencies = ReadLatencySamples(candidate);
            var baselineScalars = baseline is null ? null : ReadScalars(baseline);
            var baselineLatencies = baseline is null ? null : ReadLatencySamples(baseline);
            var comparable = baseline is not null
                && baseline.EnvironmentFingerprint == candidate.EnvironmentFingerprint
                && baseline.WorkloadFingerprint == candidate.WorkloadFingerprint
                && baseline.HarnessSourceSha256 == candidate.HarnessSourceSha256
                && baseline.TopologyFingerprint == candidate.TopologyFingerprint
                && baseline.MethodFingerprint == candidate.MethodFingerprint;
            if (requireComparable && (!comparable
                || !candidateScalars.Keys.SequenceEqual(baselineScalars!.Keys, StringComparer.Ordinal)
                || !candidateLatencies.Keys.SequenceEqual(baselineLatencies!.Keys, StringComparer.Ordinal)
                || candidateLatencies.Any(pair => pair.Value.Length != baselineLatencies[pair.Key].Length)))
            {
                throw new InvalidDataException(
                    "Required baseline and candidate evidence are not directly comparable by environment, workload, method, scenario, metric, and sample shape.");
            }
            var scalarComparisons = new SortedDictionary<string, PairedComparison>(StringComparer.Ordinal);
            var latencyComparisons = new SortedDictionary<string, DistributionComparison>(StringComparer.Ordinal);
            if (comparable)
            {
                foreach (var (path, candidateValues) in candidateScalars)
                {
                    if (baselineScalars!.TryGetValue(path, out var baselineValues))
                    {
                        scalarComparisons.Add(path, ComparePaired(baselineValues, candidateValues));
                    }
                }
                foreach (var (path, candidateValues) in candidateLatencies)
                {
                    if (baselineLatencies!.TryGetValue(path, out var baselineValues))
                    {
                        latencyComparisons.Add(path, CompareDistributions(baselineValues, candidateValues));
                    }
                }
            }
            return new WorkloadSummary(
                candidate.EnvironmentFingerprint,
                candidate.WorkloadFingerprint,
                candidate.AssemblyFingerprint,
                candidate.Assemblies,
                candidate.ProductionCommit,
                candidate.HarnessSourceSha256,
                candidate.Runs,
                candidate.LocationModes,
                candidate.TopologyFingerprint,
                candidate.MethodFingerprint,
                candidate.FileSha256,
                candidate.Correctness,
                candidate.Io,
                baseline?.EnvironmentFingerprint,
                baseline?.WorkloadFingerprint,
                baseline?.AssemblyFingerprint,
                baseline?.Assemblies,
                baseline?.ProductionCommit,
                baseline?.HarnessSourceSha256,
                baseline?.Runs,
                baseline?.LocationModes,
                baseline?.TopologyFingerprint,
                baseline?.MethodFingerprint,
                baseline?.FileSha256,
                baseline?.Correctness,
                baseline?.Io,
                comparable,
                SummarizeScalars(candidateScalars),
                candidateScalars.Keys.ToDictionary(
                    path => path, MetricMetadataFor, StringComparer.Ordinal),
                baselineScalars is null ? null : SummarizeScalars(baselineScalars),
                scalarComparisons,
                SummarizeDistributions(candidateLatencies),
                baselineLatencies is null ? null : SummarizeDistributions(baselineLatencies),
                latencyComparisons);
        }
        finally
        {
            candidate.Dispose();
            baseline?.Dispose();
        }
    }

    private static SortedDictionary<string, double[]> ReadScalars(TrialSet trials)
    {
        var byTrial = trials.Documents.Select(document =>
        {
            var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
            Flatten(Property(document.RootElement, "measurements"), "measurements", values, null);
            return values;
        }).ToArray();
        var common = byTrial[0].Keys.Where(key => byTrial.Skip(1).All(values => values.ContainsKey(key)));
        return new SortedDictionary<string, double[]>(common.ToDictionary(
            key => key,
            key => byTrial.Select(values => values[key]).ToArray(),
            StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static SortedDictionary<string, double[]> ReadLatencySamples(TrialSet trials)
    {
        var pooled = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var document in trials.Documents)
        {
            Flatten(Property(document.RootElement, "measurements"), "measurements", null, pooled);
        }
        return new SortedDictionary<string, double[]>(pooled.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Order().ToArray(),
            StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static void ValidateMeasurementShapes(JsonElement[] roots, string fileName)
    {
        var scalarShapes = new List<string[]>(roots.Length);
        var latencyShapes = new List<SortedDictionary<string, int>>(roots.Length);
        foreach (var root in roots)
        {
            var defaultSamples = fileName == "device-bootstrap-performance.json"
                ? Property(Property(root, "workload"), "measurements").GetInt32()
                : (int?)null;
            ValidateLatencyEvidence(Property(root, "measurements"), defaultSamples, "measurements");
            var scalars = new SortedDictionary<string, double>(StringComparer.Ordinal);
            var latencies = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
            Flatten(Property(root, "measurements"), "measurements", scalars, latencies);
            scalarShapes.Add(scalars.Keys.ToArray());
            latencyShapes.Add(new SortedDictionary<string, int>(
                latencies.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal),
                StringComparer.Ordinal));
        }
        if (scalarShapes.Skip(1).Any(shape => !shape.SequenceEqual(scalarShapes[0], StringComparer.Ordinal))
            || latencyShapes.Skip(1).Any(shape => shape.Count != latencyShapes[0].Count
                || shape.Any(pair => !latencyShapes[0].TryGetValue(pair.Key, out var count) || count != pair.Value)))
        {
            throw new InvalidDataException($"{fileName} trials do not contain identical metric/scenario/sample sets.");
        }
        if (fileName == "logichost-ingest-performance.json")
        {
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                var marker = $"scenario=W4-W1-C{concurrency},concurrency={concurrency}";
                var sample = latencyShapes[0].SingleOrDefault(pair =>
                    pair.Key.Contains(marker, StringComparison.Ordinal));
                if (sample.Key is null || sample.Value != 200)
                {
                    throw new InvalidDataException(
                        $"Ingest evidence must retain exactly 200 W4-C{concurrency} latency samples per trial.");
                }
            }
        }
    }

    private static void ValidateLatencyEvidence(JsonElement element, int? inheritedSamples, string path)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateLatencyEvidence(item, inheritedSamples, $"{path}[{ObjectIdentity(item, index)}]");
                index++;
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var expectedSamples = ExpectedSamples(element) ?? inheritedSamples;
        if (TryProperty(element, "latencySamplesMilliseconds", out var samplesElement))
        {
            if (samplesElement.ValueKind != JsonValueKind.Array || expectedSamples is null
                || samplesElement.GetArrayLength() != expectedSamples.Value)
            {
                throw new InvalidDataException(
                    $"{path} latency evidence does not contain its declared sample count.");
            }
            var samples = samplesElement.EnumerateArray().Select(item => item.GetDouble()).Order().ToArray();
            var distribution = TryProperty(element, "latencyMilliseconds", out var nestedDistribution)
                ? nestedDistribution
                : element;
            ValidatePercentile(distribution, "median", Percentile(samples, 0.50), path);
            ValidatePercentile(distribution, "p95", Percentile(samples, 0.95), path);
            ValidatePercentile(distribution, "p99", Percentile(samples, 0.99), path);
            ValidatePercentile(distribution, "maximum", samples[^1], path);
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals("latencySamplesMilliseconds", StringComparison.OrdinalIgnoreCase))
            {
                ValidateLatencyEvidence(property.Value, expectedSamples, $"{path}.{property.Name}");
            }
        }
    }

    private static int? ExpectedSamples(JsonElement element)
    {
        foreach (var name in new[] { "measuredOperations", "measuredQueries", "measuredTransitions", "measurements" })
        {
            if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt32();
            }
        }
        return null;
    }

    private static void ValidatePercentile(JsonElement distribution, string name, double expected, string path)
    {
        var propertyName = TryProperty(distribution, name, out var value)
            ? name
            : $"{name}Milliseconds";
        var actual = Property(distribution, propertyName).GetDouble();
        if (Math.Abs(actual - expected) > Math.Max(1e-9, Math.Abs(expected) * 1e-12))
        {
            throw new InvalidDataException($"{path} published {name} does not match retained samples.");
        }
    }

    private static void Flatten(
        JsonElement element,
        string path,
        IDictionary<string, double>? scalars,
        IDictionary<string, List<double>>? latencySamples)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                Flatten(property.Value, $"{path}.{property.Name}", scalars, latencySamples);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (path.EndsWith("latencySamplesMilliseconds", StringComparison.OrdinalIgnoreCase))
            {
                if (latencySamples is not null)
                {
                    if (!latencySamples.TryGetValue(path, out var samples))
                    {
                        samples = [];
                        latencySamples.Add(path, samples);
                    }
                    samples.AddRange(element.EnumerateArray().Select(item => item.GetDouble()));
                }
                return;
            }
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var identity = item.ValueKind == JsonValueKind.Object
                    ? ObjectIdentity(item, index)
                    : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Flatten(item, $"{path}[{identity}]", scalars, latencySamples);
                index++;
            }
            return;
        }
        if (scalars is not null && element.ValueKind == JsonValueKind.Number
            && TryMetricMetadata(path, out _) && element.TryGetDouble(out var value))
        {
            scalars[path] = value;
        }
    }

    private static bool TryMetricMetadata(string path, out MetricMetadata metadata)
    {
        if (path.Contains("latencyMilliseconds.", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("allocationCounterStart", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("allocationCounterEnd", StringComparison.OrdinalIgnoreCase))
        {
            metadata = default!;
            return false;
        }
        if (ThroughputMetricSuffixes.Any(
                suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            metadata = new MetricMetadata("operations/second", "higher");
            return true;
        }
        if (MillisecondMetricSuffixes.Any(
                suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            metadata = new MetricMetadata("milliseconds", "lower");
            return true;
        }
        if (path.EndsWith("oldestAgeSeconds", StringComparison.OrdinalIgnoreCase))
        {
            metadata = new MetricMetadata("seconds", "lower");
            return true;
        }
        if (ByteMetricSuffixes.Any(
                suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || path.Contains("backlog", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            metadata = new MetricMetadata("bytes", "lower");
            return true;
        }
        if (CountMetricSuffixes.Any(
                suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || path.Contains("backlog", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("count", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".sql.transactions.", StringComparison.OrdinalIgnoreCase))
        {
            metadata = new MetricMetadata("count", "lower");
            return true;
        }
        metadata = default!;
        return false;
    }

    private static MetricMetadata MetricMetadataFor(string path)
        => TryMetricMetadata(path, out var metadata)
            ? metadata
            : throw new InvalidOperationException($"Metric metadata is missing for {path}.");

    private static string ObjectIdentity(JsonElement item, int index)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "scenario", "concurrency" })
        {
            if (TryProperty(item, name, out var value))
            {
                parts.Add($"{name}={value.ToString()}");
            }
        }
        return parts.Count == 0
            ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Join(',', parts);
    }

    private static SortedDictionary<string, TrialRange> SummarizeScalars(
        IReadOnlyDictionary<string, double[]> values)
        => new(values.ToDictionary(pair => pair.Key, pair => Range(pair.Value), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static SortedDictionary<string, PooledDistribution> SummarizeDistributions(
        IReadOnlyDictionary<string, double[]> values)
        => new(values.ToDictionary(pair => pair.Key, pair => Distribution(pair.Value), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static PairedComparison ComparePaired(double[] baseline, double[] candidate)
    {
        var baselineRange = Range(baseline);
        var candidateRange = Range(candidate);
        var pairedPercent = baseline.Zip(candidate, static (before, after) =>
            before == 0 ? double.NaN : (after / before - 1) * 100).Where(double.IsFinite).ToArray();
        return new PairedComparison(
            baselineRange.Median,
            candidateRange.Median,
            candidateRange.Median - baselineRange.Median,
            baselineRange.Median == 0 ? null : (candidateRange.Median / baselineRange.Median - 1) * 100,
            pairedPercent.Length == 0 ? null : Range(pairedPercent));
    }

    private static DistributionComparison CompareDistributions(double[] baseline, double[] candidate)
    {
        var before = Distribution(baseline);
        var after = Distribution(candidate);
        return new DistributionComparison(
            before,
            after,
            after.P50 - before.P50,
            before.P50 == 0 ? null : (after.P50 / before.P50 - 1) * 100,
            after.P95 - before.P95,
            before.P95 == 0 ? null : (after.P95 / before.P95 - 1) * 100,
            after.P99 - before.P99,
            before.P99 == 0 ? null : (after.P99 / before.P99 - 1) * 100);
    }

    private static TrialRange Range(IEnumerable<double> source)
    {
        var ordered = source.Order().ToArray();
        return new TrialRange(ordered[0], ordered[ordered.Length / 2], ordered[^1]);
    }

    private static PooledDistribution Distribution(double[] ordered)
        => new(ordered.Length, Percentile(ordered, 0.50), Percentile(ordered, 0.95),
            Percentile(ordered, 0.99), ordered[^1]);

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * percentile) - 1)];

    private static string Fingerprint(JsonElement element)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(element)));

    private static string WorkloadFingerprint(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalWorkload(element, writer);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalWorkload(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (property.Name.Equals("intentionalLocationMode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalWorkload(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalWorkload(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string[] ReadStringProperties(JsonElement element, string propertyName)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        Collect(element);
        return values.ToArray();

        void Collect(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.EnumerateObject())
                {
                    if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        values.Add(property.Value.GetString()!);
                    }
                    else
                    {
                        Collect(property.Value);
                    }
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    Collect(item);
                }
            }
        }
    }

    private static JsonElement Property(JsonElement element, string name)
        => TryProperty(element, name, out var value)
            ? value
            : throw new InvalidDataException($"Evidence property {name} is missing.");

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string RequiredCommit(string name)
        => OptionalCommit(name)
            ?? throw new InvalidOperationException($"{name} must contain a full commit SHA.");

    private static string? OptionalCommit(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException($"{name} must contain one full 40-character commit SHA.");
        }
        return value;
    }

    private sealed class TrialSet(
        IReadOnlyList<JsonDocument> documents,
        IReadOnlyList<string> fileSha256,
        IReadOnlyList<JsonElement> correctness,
        IReadOnlyList<JsonElement> io,
        string environmentFingerprint,
        string workloadFingerprint,
        string assemblyFingerprint,
        JsonElement assemblies,
        string productionCommit,
        string harnessSourceSha256,
        IReadOnlyList<RunEvidence> runs,
        IReadOnlyList<string> locationModes,
        string topologyFingerprint,
        string methodFingerprint) : IDisposable
    {
        public IReadOnlyList<JsonDocument> Documents { get; } = documents;
        public IReadOnlyList<string> FileSha256 { get; } = fileSha256;
        public IReadOnlyList<JsonElement> Correctness { get; } = correctness;
        public IReadOnlyList<JsonElement> Io { get; } = io;
        public string EnvironmentFingerprint { get; } = environmentFingerprint;
        public string WorkloadFingerprint { get; } = workloadFingerprint;
        public string AssemblyFingerprint { get; } = assemblyFingerprint;
        public JsonElement Assemblies { get; } = assemblies;
        public string ProductionCommit { get; } = productionCommit;
        public string HarnessSourceSha256 { get; } = harnessSourceSha256;
        public IReadOnlyList<RunEvidence> Runs { get; } = runs;
        public IReadOnlyList<string> LocationModes { get; } = locationModes;
        public string TopologyFingerprint { get; } = topologyFingerprint;
        public string MethodFingerprint { get; } = methodFingerprint;

        public void Dispose()
        {
            foreach (var document in Documents)
            {
                document.Dispose();
            }
        }
    }

    private sealed record ReviewedSummary(
        string Schema,
        int Issue,
        string CandidateCommit,
        string? BaselineCommit,
        string CandidateProductionCommit,
        string BaselineProductionCommit,
        string ReleaseBuildCommand,
        string SummaryCommand,
        string Method,
        IReadOnlyDictionary<string, WorkloadSummary> Workloads);
    private sealed record WorkloadSummary(
        string CandidateEnvironmentFingerprint,
        string CandidateWorkloadFingerprint,
        string CandidateAssemblyFingerprint,
        JsonElement CandidateAssemblies,
        string CandidateProductionCommit,
        string CandidateHarnessSourceSha256,
        IReadOnlyList<RunEvidence> CandidateRuns,
        IReadOnlyList<string> CandidateLocationModes,
        string CandidateTopologyFingerprint,
        string CandidateMethodFingerprint,
        IReadOnlyList<string> CandidateInputSha256,
        IReadOnlyList<JsonElement> CandidateCorrectness,
        IReadOnlyList<JsonElement> CandidateIo,
        string? BaselineEnvironmentFingerprint,
        string? BaselineWorkloadFingerprint,
        string? BaselineAssemblyFingerprint,
        JsonElement? BaselineAssemblies,
        string? BaselineProductionCommit,
        string? BaselineHarnessSourceSha256,
        IReadOnlyList<RunEvidence>? BaselineRuns,
        IReadOnlyList<string>? BaselineLocationModes,
        string? BaselineTopologyFingerprint,
        string? BaselineMethodFingerprint,
        IReadOnlyList<string>? BaselineInputSha256,
        IReadOnlyList<JsonElement>? BaselineCorrectness,
        IReadOnlyList<JsonElement>? BaselineIo,
        bool Comparable,
        IReadOnlyDictionary<string, TrialRange> CandidateScalars,
        IReadOnlyDictionary<string, MetricMetadata> ScalarMetadata,
        IReadOnlyDictionary<string, TrialRange>? BaselineScalars,
        IReadOnlyDictionary<string, PairedComparison> ScalarComparisons,
        IReadOnlyDictionary<string, PooledDistribution> CandidateLatency,
        IReadOnlyDictionary<string, PooledDistribution>? BaselineLatency,
        IReadOnlyDictionary<string, DistributionComparison> LatencyComparisons);
    private sealed record TrialRange(double Minimum, double Median, double Maximum);
    private sealed record MetricMetadata(string Unit, string PreferredDirection);
    private sealed record RunEvidence(Guid RunId, int ProcessId, DateTime ProcessStartUtc);
    private sealed record PairedComparison(
        double BaselineMedian,
        double CandidateMedian,
        double AbsoluteMedianChange,
        double? MedianChangePercent,
        TrialRange? PairedChangePercent);
    private sealed record PooledDistribution(int Samples, double P50, double P95, double P99, double Maximum);
    private sealed record DistributionComparison(
        PooledDistribution Baseline,
        PooledDistribution Candidate,
        double P50AbsoluteChange,
        double? P50ChangePercent,
        double P95AbsoluteChange,
        double? P95ChangePercent,
        double P99AbsoluteChange,
        double? P99ChangePercent);
}
