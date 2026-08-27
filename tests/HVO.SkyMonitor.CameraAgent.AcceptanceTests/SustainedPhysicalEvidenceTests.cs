using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SustainedPhysicalEvidenceTests
{
    private const int WarmupCount = 5;
    private const int MeasuredCount = 100;
    private const int TotalCount = WarmupCount + MeasuredCount;
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    [Timeout(3_900_000)]
    public async Task AcquireSustainedPhysicalTrialAndStopAsync()
    {
        RequireOptIn();
        var evidenceRoot = RequiredPath("HVO_ISSUE_268_EVIDENCE_ROOT");
        Directory.CreateDirectory(evidenceRoot);
        var container = Required("HVO_ISSUE_268_CAMERA_CONTAINER");
        var baseUri = new Uri(Required("HVO_ISSUE_268_BASE_URI"), UriKind.Absolute);
        var deadline = DateTimeOffset.FromUnixTimeSeconds(long.Parse(
            Required("HVO_ISSUE_268_DEADLINE_UNIX_SECONDS"), CultureInfo.InvariantCulture));
        var identities = ReadExpectedProfileIdentities(RequiredPath("HVO_ISSUE_268_RESOLVED_CONFIG"));
        await WriteJsonAsync(Path.Combine(evidenceRoot, "profile-identities.json"), identities).ConfigureAwait(false);
        using var client = await LoginAsync(baseUri, RequiredPath("HVO_ISSUE_268_OWNER_PASSWORD_FILE"), deadline)
            .ConfigureAwait(false);
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client).ConfigureAwait(false);

        var startHealth = await WaitForHealthyAsync(client, deadline).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "health-start.json"), startHealth).ConfigureAwait(false);

        await WaitForPayloadCountAsync(container, 1, deadline).ConfigureAwait(false);
        var setpointBarrier = await SetCaptureStateAsync(
            client, pause: true, antiforgeryToken, "setpoint-validation", deadline).ConfigureAwait(false);
        await WaitForDrainedAsync(client, deadline).ConfigureAwait(false);
        Assert.AreEqual(1, await CountPayloadsAsync(container).ConfigureAwait(false));
        var firstSidecar = await CopyFirstSidecarAsync(container, evidenceRoot).ConfigureAwait(false);
        AssertCaptureSetpoint(ParseManifest(firstSidecar).Descriptor, ExpectedProfile.Create(), identities);

        var warmupResume = await SetCaptureStateAsync(
            client, pause: false, antiforgeryToken, "warmups-resume", deadline).ConfigureAwait(false);
        await WaitForPayloadCountAsync(container, WarmupCount, deadline).ConfigureAwait(false);
        var measuredBarrier = await SetCaptureStateAsync(
            client, pause: true, antiforgeryToken, "measured-boundary", deadline).ConfigureAwait(false);
        await WaitForDrainedAsync(client, deadline).ConfigureAwait(false);
        Assert.AreEqual(WarmupCount, await CountPayloadsAsync(container).ConfigureAwait(false));
        await AssertWarmupPayloadsVaryAsync(container).ConfigureAwait(false);
        // Keep the admission barrier closed across a complete two-second OTLP export interval.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        EnsureBeforeDeadline(deadline);
        var acquisitionStart = await CaptureBoundarySnapshotAsync(
            "acquisition-start", client, container, evidenceRoot).ConfigureAwait(false);
        var filesystemBytesBeforeMeasured = acquisitionStart.RuntimeFilesystemBytes;

        var measuredResume = await SetCaptureStateAsync(
            client, pause: false, antiforgeryToken, "measured-resume", deadline).ConfigureAwait(false);
        await WaitForPayloadCountAsync(container, TotalCount, deadline).ConfigureAwait(false);
        var acquisitionEndBarrier = await SetCaptureStateAsync(
            client, pause: true, antiforgeryToken, "measured-complete", deadline).ConfigureAwait(false);
        Assert.AreEqual(TotalCount, await CountPayloadsAsync(container).ConfigureAwait(false));
        var acquisitionEnd = await CaptureBoundarySnapshotAsync(
            "acquisition-end", client, container, evidenceRoot).ConfigureAwait(false);
        var filesystemBytesAfterMeasured = acquisitionEnd.RuntimeFilesystemBytes;
        var measuredFilesystemGrowthBytes = filesystemBytesAfterMeasured - filesystemBytesBeforeMeasured;
        var expectedPayloadBytes = (long)MeasuredCount * ExpectedProfile.Create().PayloadBytes;
        Assert.IsTrue(measuredFilesystemGrowthBytes >= expectedPayloadBytes);
        Assert.IsTrue(measuredFilesystemGrowthBytes <= expectedPayloadBytes + 67_108_864);
        await WaitForDrainedAsync(client, deadline).ConfigureAwait(false);
        var totalEnd = await CaptureBoundarySnapshotAsync(
            "total-end", client, container, evidenceRoot).ConfigureAwait(false);

        var countBeforeStop = await CountPayloadsAsync(container).ConfigureAwait(false);
        Assert.AreEqual(TotalCount, countBeforeStop, "A capture outside the declared window was retained.");
        Assert.AreEqual("Paused", totalEnd.Summary.CaptureControl.Value.State);
        AssertDrained(totalEnd.Summary);
        EnsureBeforeDeadline(deadline);
        await DockerAsync(Remaining(deadline), "stop", "--signal", "SIGTERM", "--timeout", "45", container).ConfigureAwait(false);
        var exitCode = int.Parse(
            (await DockerAsync(TimeSpan.FromSeconds(15), "inspect", "--format", "{{.State.ExitCode}}", container).ConfigureAwait(false)).Trim(),
            CultureInfo.InvariantCulture);
        Assert.AreEqual(0, exitCode, "CameraAgent did not exit cleanly after SIGTERM.");

        var acquisition = new
        {
            schemaVersion = "issue-268-physical-acquisition-v1",
            profile = Required("HVO_ISSUE_268_PROFILE"),
            trial = Required("HVO_ISSUE_268_TRIAL"),
            warmupCount = WarmupCount,
            measuredCount = MeasuredCount,
            countBeforeStop,
            filesystemBytesBeforeMeasured,
            filesystemBytesAfterMeasured,
            measuredFilesystemGrowthBytes,
            gracefulSignal = "SIGTERM",
            exitCode,
            startHealthStatus = HealthStatus(startHealth),
            beforeHealthStatus = acquisitionStart.HealthStatus,
            finalHealthStatus = totalEnd.HealthStatus,
            monotonicFrequency = Stopwatch.Frequency,
            barriers = new[] { setpointBarrier, warmupResume, measuredBarrier, measuredResume, acquisitionEndBarrier },
            boundarySnapshots = new { acquisitionStart, acquisitionEnd, totalEnd },
            stoppedUtc = DateTimeOffset.UtcNow,
            stoppedMonotonicTimestamp = Stopwatch.GetTimestamp()
        };
        await WriteJsonAsync(Path.Combine(evidenceRoot, "acquisition.json"), acquisition).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(900_000)]
    public async Task InspectRetainedPhysicalTrialAsync()
    {
        RequireOptIn();
        var evidenceRoot = RequiredPath("HVO_ISSUE_268_EVIDENCE_ROOT");
        var runtimeRoot = RequiredPath("HVO_ISSUE_268_EXPORTED_RUNTIME_ROOT");
        var profile = ExpectedProfile.Create();
        var identities = JsonSerializer.Deserialize<ExpectedProfileIdentities>(
            await File.ReadAllTextAsync(Path.Combine(evidenceRoot, "profile-identities.json")).ConfigureAwait(false),
            EvidenceJson)!;
        var observations = ReadRawManifests(runtimeRoot)
            .OrderBy(static item => item.Manifest.Descriptor.Capture.CaptureSequence)
            .ToArray();
        Assert.HasCount(TotalCount, observations);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, TotalCount).Select(static value => (long)value).ToArray(),
            observations.Select(static item => item.Manifest.Descriptor.Capture.CaptureSequence).ToArray(),
            "Fresh trial sequences must be contiguous and start at one.");

        var journalPath = Path.Combine(runtimeRoot, "journal", "raw-ingress.db");
        Assert.IsTrue(File.Exists(journalPath));
        var journal = ReadJournal(journalPath);
        Assert.AreEqual(TotalCount, journal.Rows.Count);
        Assert.AreEqual("ok", journal.Integrity, ignoreCase: true);
        Assert.AreEqual(SqliteRawCaptureJournal.CurrentSchemaVersion, journal.UserVersion);
        Assert.AreEqual(0, journal.NonCommittedRawCount);
        Assert.AreEqual(0, journal.ReconciliationFailureCount);
        Assert.AreEqual(0, journal.NonTerminalLaneCount);
        Assert.AreEqual(0, journal.LaneFailureCount);
        Assert.AreEqual(TotalCount, journal.CompletedLaneCount);
        Assert.AreEqual(0, journal.ProcessingNodeCount);
        Assert.AreEqual(0, journal.ProcessingOutputCount);
        AssertClosedWorldRuntime(runtimeRoot, observations, journal);

        var samples = new List<CaptureSample>(TotalCount);
        foreach (var observation in observations)
        {
            var descriptor = observation.Manifest.Descriptor;
            AssertCaptureSetpoint(descriptor, profile, identities);
            var validation = observation.Manifest.Validate();
            Assert.IsTrue(validation.IsValid, validation.ReasonCode);
            var sequence = descriptor.Capture.CaptureSequence;
            var row = journal.Rows.Single(item => item.CaptureSequence == sequence);
            var payloadPath = ResolveRelativeFile(runtimeRoot, observation.Manifest.RelativeArtifactPath);
            Assert.IsTrue(File.Exists(payloadPath), payloadPath);
            var payloadLength = new FileInfo(payloadPath).Length;
            Assert.AreEqual(profile.PayloadBytes, payloadLength);
            using var payloadStream = File.OpenRead(payloadPath);
            var payloadSha256 = Convert.ToHexString(await SHA256.HashDataAsync(payloadStream).ConfigureAwait(false));
            Assert.AreEqual(descriptor.Artifact.ChecksumSha256, payloadSha256, ignoreCase: true);
            Assert.AreEqual(row.PayloadSha256, payloadSha256, ignoreCase: true);
            Assert.AreEqual(row.PayloadLength, payloadLength);
            Assert.AreEqual(row.PayloadRelativePath, observation.Manifest.RelativeArtifactPath);
            Assert.AreEqual(row.SidecarRelativePath, Path.GetRelativePath(runtimeRoot, observation.Path));
            Assert.AreEqual(row.ManifestSha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(observation.Path).ConfigureAwait(false))), ignoreCase: true);
            Assert.AreEqual(row.DescriptorSha256, CaptureContractJson.ComputeDescriptorSha256(descriptor), ignoreCase: true);

            var timing = descriptor.Timing;
            var startInterval = samples.Count == 0
                ? (double?)null
                : (timing.ExposureStartedUtc - samples[^1].ExposureStartedUtc).TotalSeconds;
            samples.Add(new CaptureSample(
                sequence,
                timing.ExposureStartedUtc,
                timing.DurableIngressUtc,
                startInterval,
                (timing.ExposureEndedUtc - timing.ExposureStartedUtc).TotalSeconds,
                (timing.ReadoutCompletedUtc - timing.ExposureEndedUtc).TotalSeconds,
                (timing.DurableIngressUtc - timing.ReadoutCompletedUtc).TotalSeconds,
                (timing.DurableIngressUtc - timing.ExposureStartedUtc).TotalSeconds,
                descriptor.CycleEvidence?.MonotonicStartJitter.TotalSeconds,
                descriptor.CycleEvidence?.StartReason.ToString(),
                descriptor.Controls.EffectiveExposure.TotalSeconds,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveOffset,
                descriptor.Controls.EffectiveTemperatureC,
                payloadLength,
                payloadSha256,
                descriptor.Profiles.Rig.Sha256,
                descriptor.Profiles.Processing.Sha256));
        }

        var measured = samples.Skip(WarmupCount).ToArray();
        Assert.HasCount(MeasuredCount, measured);
        Assert.HasCount(MeasuredCount, measured.Select(static item => item.PayloadSha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.HasCount(1, measured.Select(static item => item.RigProfileSha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.HasCount(1, measured.Select(static item => item.ProcessingProfileSha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.IsTrue(measured.All(static item => double.IsFinite(item.TemperatureC ?? double.NaN)));
        Assert.IsTrue(measured.All(static item => item.StartToDurableSeconds < 25));
        Assert.IsTrue(measured.All(static item => item.ExposureSeconds is >= 20 and <= 30));
        var intervals = measured.Skip(1).Select(static item => item.StartIntervalSeconds!.Value).ToArray();
        Assert.HasCount(99, intervals);
        Assert.IsTrue(intervals.All(static value => value >= 24.9));
        Assert.IsLessThanOrEqualTo(25.5, Median(intervals));
        Assert.IsLessThanOrEqualTo(27.5, NearestRank(intervals, 0.95));
        Assert.IsLessThanOrEqualTo(30, intervals.Max());
        Assert.IsLessThanOrEqualTo(1, NearestRank(measured.Select(static item => item.ReadoutSeconds), 0.95));
        Assert.IsLessThanOrEqualTo(2, measured.Max(static item => item.ReadoutSeconds));
        Assert.IsLessThanOrEqualTo(2, NearestRank(measured.Select(static item => item.ReadoutToDurableSeconds), 0.95));
        Assert.IsLessThanOrEqualTo(4, measured.Max(static item => item.ReadoutToDurableSeconds));

        var measuredObservations = observations.Skip(WarmupCount).ToArray();
        var rawGrowthBytes = measuredObservations.Sum(static item => new FileInfo(item.Path).Length) +
            measuredObservations.Sum(item => new FileInfo(Path.Combine(runtimeRoot, item.Manifest.RelativeArtifactPath)).Length);
        var measuredPayloadBytes = checked((long)MeasuredCount * profile.PayloadBytes);
        Assert.IsTrue(rawGrowthBytes >= measuredPayloadBytes);
        Assert.IsTrue(rawGrowthBytes <= measuredPayloadBytes + 67_108_864);
        using var acquisition = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidenceRoot, "acquisition.json")).ConfigureAwait(false));
        var evidence = new
        {
            schemaVersion = "issue-268-physical-capture-evidence-v1",
            profile = profile.Id,
            trial = Required("HVO_ISSUE_268_TRIAL"),
            result = new
            {
                passed = true,
                warmupCount = WarmupCount,
                measuredCount = MeasuredCount,
                contiguousSequences = true,
                distinctMeasuredPayloads = true,
                finalHealthStatus = acquisition.RootElement.GetProperty("finalHealthStatus").GetString(),
                gracefulExitCode = acquisition.RootElement.GetProperty("exitCode").GetInt32(),
                measuredFilesystemGrowthBytes = acquisition.RootElement.GetProperty("measuredFilesystemGrowthBytes").GetInt64()
            },
            workload = profile,
            boundaries = new
            {
                acquisitionStartUnixMilliseconds = measured[0].ExposureStartedUtc.ToUnixTimeMilliseconds(),
                acquisitionEndUnixMilliseconds = measured[^1].DurableIngressUtc.ToUnixTimeMilliseconds(),
                totalEndUnixMilliseconds = acquisition.RootElement.GetProperty("boundarySnapshots").GetProperty("totalEnd")
                    .GetProperty("capturedUnixMilliseconds").GetInt64()
            },
            statistics = new
            {
                startIntervalSeconds = Statistics(intervals),
                readoutSeconds = Statistics(measured.Select(static item => item.ReadoutSeconds)),
                synchronousSdkReadCallBytesPerSecond = Statistics(measured.Select(static item => item.PayloadBytes / item.ReadoutSeconds)),
                readoutToDurableSeconds = Statistics(measured.Select(static item => item.ReadoutToDurableSeconds)),
                startToDurableSeconds = Statistics(measured.Select(static item => item.StartToDurableSeconds)),
                startJitterSeconds = Statistics(measured.Select(static item => item.StartJitterSeconds ?? 0)),
                cameraTemperatureC = TemperatureStatistics(measured),
                measuredPayloadBytes,
                retainedRawAndSidecarBytes = rawGrowthBytes,
                deadlineOverrunCaptureCount = measured.Count(static item => item.StartReason == CaptureStartReason.DeadlineOverrun.ToString())
            },
            expectedProfileIdentities = identities,
            monotonicFrequency = acquisition.RootElement.GetProperty("monotonicFrequency").GetInt64(),
            barriers = acquisition.RootElement.GetProperty("barriers").Clone(),
            boundarySnapshots = acquisition.RootElement.GetProperty("boundarySnapshots").Clone(),
            sqlite = journal,
            ioLimitations = new[]
            {
                new { name = "sqlite.statement-count", disposition = "N/A", reason = "The ordinary production host exposes no low-overhead external per-statement counter." },
                new { name = "fsync-and-directory-sync-count", disposition = "N/A", reason = "Tracing these syscalls would perturb the sustained physical campaign." }
            },
            samples = measured
        };
        await WriteJsonAsync(Path.Combine(evidenceRoot, "capture-evidence.json"), evidence).ConfigureAwait(false);
    }

    private static void AssertCaptureSetpoint(
        ReconstructionDescriptor descriptor,
        ExpectedProfile profile,
        ExpectedProfileIdentities identities)
    {
        Assert.AreEqual("cameraagent-physical-268", descriptor.Capture.AgentId);
        Assert.AreEqual(FrameArtifactRole.Raw, descriptor.Artifact.Role);
        Assert.AreEqual($"ZWO {profile.Model}", descriptor.Artifact.SourceId);
        Assert.IsEmpty(descriptor.Artifact.SourceArtifactIds);
        Assert.AreEqual("source", descriptor.Artifact.Variant);
        Assert.AreEqual(profile.Width, descriptor.Layout.Width);
        Assert.AreEqual(profile.Height, descriptor.Layout.Height);
        Assert.AreEqual(profile.StrideBytes, descriptor.Layout.StrideBytes);
        Assert.AreEqual(profile.PayloadBytes, descriptor.Layout.ByteLength);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, descriptor.Layout.PixelFormat);
        Assert.AreEqual(FrameByteOrder.LittleEndian, descriptor.Layout.ByteOrder);
        Assert.AreEqual(profile.SampleDepthBits, descriptor.Layout.SampleDepthBits);
        Assert.AreEqual(16, descriptor.Layout.ContainerDepthBits);
        Assert.AreEqual(FrameSamplePacking.ByteAligned, descriptor.Layout.Packing);
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, descriptor.Layout.CfaPattern);
        Assert.AreEqual(FrameStoredCodeTransform.OpaqueContainerV1, descriptor.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, descriptor.Layout.LevelCodeSpace);
        Assert.AreEqual(20, descriptor.Controls.EffectiveExposure.TotalSeconds, 0.001);
        Assert.AreEqual(profile.Gain, descriptor.Controls.EffectiveGain, 0.001);
        Assert.AreEqual(profile.Offset, descriptor.Controls.EffectiveOffset);
        Assert.AreEqual(profile.ProfileVersion, descriptor.Profiles.Rig.Version);
        Assert.AreEqual("processing", descriptor.Profiles.Processing.Name);
        Assert.AreEqual(identities.RigSha256, descriptor.Profiles.Rig.Sha256, ignoreCase: true);
        Assert.AreEqual(identities.ProcessingSha256, descriptor.Profiles.Processing.Sha256, ignoreCase: true);
        Assert.IsNotNull(descriptor.CycleEvidence);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, descriptor.CycleEvidence.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, descriptor.CycleEvidence.GainControl);
        Assert.IsNull(descriptor.CycleEvidence.Metering);
    }

    private static ExpectedProfileIdentities ReadExpectedProfileIdentities(string configPath)
    {
        var document = JsonSerializer.Deserialize<CameraModuleDocument>(File.ReadAllText(configPath), ConfigurationJson);
        Assert.IsNotNull(document);
        var configuration = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            document.Module,
            document.Rig,
            document.Pipeline,
            document.AgentId);
        return new ExpectedProfileIdentities(
            CameraRigProfileIdentity.ComputeSha256(document.Rig),
            RawCaptureDescriptorFactory.CreateProcessingProfile(configuration).Sha256);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned client owns its cookie handler.")]
    private static async Task<HttpClient> LoginAsync(Uri baseUri, string passwordFile, DateTimeOffset deadline)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
            login.EnsureSuccessStatusCode();
            var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
            var token = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
                RegexOptions.CultureInvariant);
            Assert.IsTrue(token.Success);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
                ["Input.Email"] = "physical-268@cameraagent.test",
                ["Input.Password"] = (await File.ReadAllTextAsync(passwordFile).ConfigureAwait(false)).Trim(),
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            EnsureBeforeDeadline(deadline);
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.IsTrue(token.Success);
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private static async Task<CaptureBarrierEvidence> SetCaptureStateAsync(
        HttpClient client,
        bool pause,
        string antiforgeryToken,
        string phase,
        DateTimeOffset deadline)
    {
        EnsureBeforeDeadline(deadline);
        var before = await ReadOperationsSummaryAsync(client).ConfigureAwait(false);
        var startedUtc = DateTimeOffset.UtcNow;
        var startedMonotonic = Stopwatch.GetTimestamp();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/operations/capture/{(pause ? "pause" : "resume")}", UriKind.Relative));
        request.Headers.Add(
            "Idempotency-Key",
            $"issue-268-{Required("HVO_ISSUE_268_TRIAL")}-{phase}-{Required("HVO_ISSUE_268_REVISION")}");
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        request.Content = JsonContent.Create(new
        {
            expectedVersion = before.CaptureControl.Value.Version,
            reason = $"issue-268 authenticated {phase} barrier"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<CaptureControlCommandResult>().ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(pause ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running, receipt.State);
        var after = await ReadOperationsSummaryAsync(client).ConfigureAwait(false);
        Assert.AreEqual(pause ? "Paused" : "Running", after.CaptureControl.Value.State);
        Assert.AreEqual(receipt.Version, after.CaptureControl.Value.Version);
        return new CaptureBarrierEvidence(
            phase,
            pause ? "Paused" : "Running",
            receipt.Version,
            startedUtc,
            DateTimeOffset.UtcNow,
            startedMonotonic,
            Stopwatch.GetTimestamp(),
            true);
    }

    private static async Task WaitForDrainedAsync(HttpClient client, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var summary = await ReadOperationsSummaryAsync(client).ConfigureAwait(false);
            if (IsDrained(summary))
            {
                return;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail("The authenticated capture barrier did not reach terminal durable state before the trial deadline.");
    }

    private static async Task<CameraAgentOperationsSummary> ReadOperationsSummaryAsync(HttpClient client)
    {
        var summary = await client.GetFromJsonAsync<CameraAgentOperationsSummary>(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.IsNotNull(summary);
        return summary;
    }

    private static bool IsDrained(CameraAgentOperationsSummary summary)
        => summary.RawIngress.Value.PendingCount == 0 &&
           summary.CaptureLanes.Value.PendingCount == 0 &&
           summary.CaptureLanes.Value.LeasedCount == 0 &&
           summary.CaptureProcessing.Value.PendingCount == 0 &&
           summary.CaptureProcessing.Value.RetryCount == 0 &&
           summary.CaptureProcessing.Value.TerminalCount == 0;

    private static void AssertDrained(CameraAgentOperationsSummary summary)
        => Assert.IsTrue(IsDrained(summary), "The total-end boundary was not durably drained.");

    private static async Task<BoundarySnapshot> CaptureBoundarySnapshotAsync(
        string name,
        HttpClient client,
        string container,
        string evidenceRoot)
    {
        var startedMonotonic = Stopwatch.GetTimestamp();
        var capturedUtc = DateTimeOffset.UtcNow;
        var metrics = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        var health = await GetHealthyAsync(client).ConfigureAwait(false);
        var summary = await ReadOperationsSummaryAsync(client).ConfigureAwait(false);
        var dockerStats = await DockerAsync(
            TimeSpan.FromSeconds(15), "stats", "--no-stream", "--format", "{{json .}}", container).ConfigureAwait(false);
        var processStatus = await DockerAsync(
            TimeSpan.FromSeconds(15), "exec", container, "/bin/sh", "-c",
            "cat /proc/1/stat /proc/1/status /proc/1/io").ConfigureAwait(false);
        var sqliteFileSizes = await DockerAsync(
            TimeSpan.FromSeconds(15), "exec", container, "/bin/sh", "-c",
            "for path in /var/lib/hvo/data/agent/journal/raw-ingress.db /var/lib/hvo/data/agent/journal/raw-ingress.db-wal /var/lib/hvo/data/agent/journal/raw-ingress.db-shm; do if [ -e \"$path\" ]; then stat -c %s \"$path\"; else printf '0\\n'; fi; done")
            .ConfigureAwait(false);
        var sqliteSizes = sqliteFileSizes.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.HasCount(3, sqliteSizes);
        var blockDevice = Required("HVO_ISSUE_268_BLOCK_DEVICE");
        Assert.IsTrue(Regex.IsMatch(blockDevice, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant));
        var blockDeviceStat = await File.ReadAllTextAsync($"/sys/class/block/{blockDevice}/stat").ConfigureAwait(false);
        var captureCount = await CountPayloadsAsync(container).ConfigureAwait(false);
        var runtimeFilesystemBytes = await RuntimeFilesystemBytesAsync(container).ConfigureAwait(false);
        var dockerStatsElement = JsonDocument.Parse(dockerStats).RootElement.Clone();
        var processBoundary = ParseProcessBoundary(processStatus);
        var processSnapshotSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(processStatus)));
        var prometheusCounters = ReadPrometheusCounters(metrics);
        var completedMonotonic = Stopwatch.GetTimestamp();
        var completedUtc = DateTimeOffset.UtcNow;
        var snapshot = new BoundarySnapshot(
            name,
            capturedUtc,
            capturedUtc.ToUnixTimeMilliseconds(),
            completedUtc,
            completedUtc.ToUnixTimeMilliseconds(),
            startedMonotonic,
            completedMonotonic,
            HealthStatus(health),
            captureCount,
            runtimeFilesystemBytes,
            summary,
            dockerStatsElement,
            processBoundary,
            processSnapshotSha256,
            new HostBlockDeviceBoundary("runtime", blockDeviceStat.Trim()),
            new SqliteFileBoundary(sqliteSizes[0], sqliteSizes[1], sqliteSizes[2]),
            prometheusCounters);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, $"metrics-{name}.txt"), metrics).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(evidenceRoot, $"boundary-{name}.json"), snapshot).ConfigureAwait(false);
        return snapshot;
    }

    private static ProcessBoundary ParseProcessBoundary(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsNotEmpty(lines);
        var fields = lines[0][(lines[0].LastIndexOf(')') + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rss = Regex.Match(value, "^VmRSS:[ \\t]+([0-9]+)[ \\t]+kB$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        long Io(string name)
        {
            var match = Regex.Match(value, $"^{Regex.Escape(name)}:[ \\t]+([0-9]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Assert.IsTrue(match.Success, $"Missing process boundary field {name}.");
            return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        Assert.IsTrue(rss.Success, "Missing process boundary RSS.");
        return new ProcessBoundary(
            checked((long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture))),
            checked(long.Parse(rss.Groups[1].Value, CultureInfo.InvariantCulture) * 1024),
            Io("read_bytes"),
            Io("write_bytes"),
            Io("syscr"),
            Io("syscw"));
    }

    private static Dictionary<string, double> ReadPrometheusCounters(string metrics)
    {
        var names = new[]
        {
            "camera_agent_capture_control_cycles",
            "camera_agent_capture_control_decisions",
            "camera_agent_ingress_committed",
            "camera_agent_ingress_committed_bytes",
            "camera_agent_ingress_sqlite_transactions",
            "camera_agent_ingress_sqlite_checkpoints",
            "camera_agent_ingress_sqlite_lock_wait_duration_seconds_count",
            "camera_agent_ingress_sqlite_lock_wait_duration_seconds_sum",
            "camera_agent_lanes_work_created",
            "camera_agent_lanes_claims",
            "camera_agent_lanes_completed",
            "camera_agent_processing_graphs"
        };
        return names.ToDictionary(
            name => name,
            name => SumPrometheus(metrics, name, name == "camera_agent_lanes_claims" ? ("result", "claimed") : null),
            StringComparer.Ordinal);
    }

    private static double SumPrometheus(string metrics, string name, (string Key, string Value)? requiredLabel)
    {
        double sum = 0;
        var found = false;
        foreach (var line in metrics.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }
            var sampleName = line.Split(['{', ' '], 2)[0];
            if (!string.Equals(sampleName, name, StringComparison.Ordinal) &&
                !string.Equals(sampleName, name + "_total", StringComparison.Ordinal))
            {
                continue;
            }
            if (requiredLabel is { } label && !Regex.IsMatch(
                line,
                $"(?:\\{{|,){Regex.Escape(label.Key)}=\"{Regex.Escape(label.Value)}\"(?:,|\\}})",
                RegexOptions.CultureInvariant))
            {
                continue;
            }
            var sample = line;
            var exemplarIndex = sample.IndexOf(" # ", StringComparison.Ordinal);
            if (exemplarIndex >= 0)
            {
                sample = sample[..exemplarIndex];
            }
            var value = sample[(sample.LastIndexOf(' ') + 1)..];
            sum += double.Parse(value, CultureInfo.InvariantCulture);
            found = true;
        }
        Assert.IsTrue(found, $"Required Prometheus boundary counter was absent: {name}");
        return sum;
    }

    private static async Task<string> CopyFirstSidecarAsync(string container, string evidenceRoot)
    {
        var path = (await DockerAsync(
            TimeSpan.FromSeconds(15),
            "exec", container, "/bin/sh", "-c",
            "find /var/lib/hvo/data/agent/frames -type f -name '*.json' | sort | head -n 1").ConfigureAwait(false)).Trim();
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        var destination = Path.Combine(evidenceRoot, "first-warmup-sidecar.json");
        await DockerAsync(TimeSpan.FromSeconds(30), "cp", $"{container}:{path}", destination).ConfigureAwait(false);
        return destination;
    }

    private static async Task AssertWarmupPayloadsVaryAsync(string container)
    {
        var output = await DockerAsync(
            TimeSpan.FromSeconds(15),
            "exec", container, "/bin/sh", "-c",
            "find /var/lib/hvo/data/agent/frames -type f -name '*.json' | sort").ConfigureAwait(false);
        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.HasCount(WarmupCount, paths);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-268-warmups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var checksums = new string[WarmupCount];
            for (var index = 0; index < paths.Length; index++)
            {
                var destination = Path.Combine(temporaryRoot, $"warmup-{index + 1}.json");
                await DockerAsync(TimeSpan.FromSeconds(30), "cp", $"{container}:{paths[index]}", destination)
                    .ConfigureAwait(false);
                checksums[index] = ParseManifest(destination).Descriptor.Artifact.ChecksumSha256;
            }
            AssertWarmupChecksumsVary(checksums);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    internal static void AssertWarmupChecksumsVary(IReadOnlyCollection<string> checksums)
    {
        Assert.HasCount(WarmupCount, checksums);
        Assert.HasCount(
            WarmupCount,
            checksums.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            "Warmup payloads must vary before the measured physical window begins.");
    }

    private static async Task WaitForPayloadCountAsync(string container, int expected, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var count = await CountPayloadsAsync(container).ConfigureAwait(false);
            if (count == expected)
            {
                return;
            }
            Assert.IsLessThanOrEqualTo(expected, count, "The trial retained more captures than declared.");
            await Task.Delay(250).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for exactly {expected} physical payloads.");
    }

    private static async Task<int> CountPayloadsAsync(string container)
    {
        var output = await DockerAsync(
            TimeSpan.FromSeconds(15),
            "exec", container, "/bin/sh", "-c",
            "find /var/lib/hvo/data/agent/frames -type f -name '*.json' | wc -l").ConfigureAwait(false);
        return int.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> RuntimeFilesystemBytesAsync(string container)
    {
        var output = await DockerAsync(
            TimeSpan.FromSeconds(30),
            "exec", container, "/usr/bin/du", "--summarize", "--bytes", "/var/lib/hvo/data/agent").ConfigureAwait(false);
        return long.Parse(output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
    }

    private static async Task<string> WaitForHealthyAsync(HttpClient client, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK && HealthStatus(body) == "Healthy")
                {
                    return body;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail("CameraAgent did not reach Healthy state.");
        return string.Empty;
    }

    private static async Task<string> GetHealthyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", HealthStatus(body));
        return body;
    }

    private static string HealthStatus(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString() ?? string.Empty;

    private static void EnsureBeforeDeadline(DateTimeOffset deadline)
        => Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "The absolute 80-minute trial deadline expired.");

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        EnsureBeforeDeadline(deadline);
        return deadline - DateTimeOffset.UtcNow;
    }

    private static List<ManifestObservation> ReadRawManifests(string root)
    {
        var observations = new List<ManifestObservation>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "frames"), "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
            Assert.IsTrue(parsed.IsValid, $"Invalid retained JSON file {path}: {parsed.Validation.ReasonCode}");
            Assert.IsNotNull(parsed.Document?.Manifest, $"Retained JSON file is not an artifact manifest: {path}");
            var manifest = parsed.Document!.Manifest!;
            Assert.AreEqual(FrameArtifactRole.Raw, manifest.Descriptor.Artifact.Role, $"Extraneous non-raw manifest: {path}");
            observations.Add(new ManifestObservation(path, manifest));
        }
        return observations;
    }

    private static void AssertClosedWorldRuntime(
        string runtimeRoot,
        IReadOnlyCollection<ManifestObservation> observations,
        JournalEvidence journal)
    {
        var manifestSidecars = observations.Select(item => RelativeFile(runtimeRoot, item.Path)).ToArray();
        var manifestPayloads = observations
            .Select(item => RelativeFile(runtimeRoot, ResolveRelativeFile(runtimeRoot, item.Manifest.RelativeArtifactPath)))
            .ToArray();
        Assert.HasCount(observations.Count, manifestSidecars.Distinct(StringComparer.Ordinal).ToArray());
        Assert.HasCount(observations.Count, manifestPayloads.Distinct(StringComparer.Ordinal).ToArray());

        CollectionAssert.AreEqual(
            manifestSidecars.Order(StringComparer.Ordinal).ToArray(),
            journal.Rows.Select(static row => NormalizeRelativePath(row.SidecarRelativePath)).Order(StringComparer.Ordinal).ToArray(),
            "Journal sidecars must exactly match retained raw manifests.");
        CollectionAssert.AreEqual(
            manifestPayloads.Order(StringComparer.Ordinal).ToArray(),
            journal.Rows.Select(static row => NormalizeRelativePath(row.PayloadRelativePath)).Order(StringComparer.Ordinal).ToArray(),
            "Journal payloads must exactly match retained raw artifacts.");

        var expected = manifestSidecars.Concat(manifestPayloads).ToHashSet(StringComparer.Ordinal);
        expected.Add("journal/raw-ingress.db");
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var relative = "journal/raw-ingress.db" + suffix;
            if (File.Exists(Path.Combine(runtimeRoot, "journal", "raw-ingress.db" + suffix)))
            {
                expected.Add(relative);
            }
        }
        var actual = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
            .Select(path => RelativeFile(runtimeRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual.Order(StringComparer.Ordinal).ToArray(),
            "Retained runtime contains an extraneous or unreferenced file.");
    }

    private static string ResolveRelativeFile(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        Assert.IsFalse(Path.IsPathRooted(normalized));
        Assert.IsFalse(normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal));
        var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        Assert.AreEqual(normalized, RelativeFile(root, path));
        return path;
    }

    private static string RelativeFile(string root, string path)
    {
        var relative = NormalizeRelativePath(Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)));
        Assert.IsFalse(relative == ".." || relative.StartsWith("../", StringComparison.Ordinal));
        return relative;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static ArtifactManifestV2 ParseManifest(string path)
    {
        var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        Assert.IsNotNull(parsed.Document?.Manifest);
        return parsed.Document!.Manifest!;
    }

    private static JournalEvidence ReadJournal(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var rows = new List<JournalRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT capture_sequence, descriptor_sha256, manifest_sha256, payload_sha256, payload_length, payload_relative_path, sidecar_relative_path, state FROM raw_captures ORDER BY capture_sequence;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new JournalRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
        }
        return new JournalEvidence(
            ScalarString(connection, "PRAGMA integrity_check;"),
            ScalarLong(connection, "PRAGMA user_version;"),
            rows,
            ScalarLong(connection, "SELECT COUNT(*) FROM raw_captures WHERE state != 'committed';"),
            ScalarLong(connection, "SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined' OR operation_state != 'completed';"),
            ScalarLong(connection, "SELECT COUNT(*) FROM capture_lane_work WHERE state NOT IN ('completed', 'abandoned');"),
            ScalarLong(connection, "SELECT COUNT(*) FROM capture_lane_work WHERE attempt_count != 1 OR state != 'completed';"),
            ScalarLong(connection, "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'completed';"),
            ScalarLong(connection, "SELECT COUNT(*) FROM processing_nodes;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM processing_outputs;"),
            FileBytes(path),
            FileBytes(path + "-wal"),
            FileBytes(path + "-shm"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All callers pass fixed test-owned SQL constants.")]
    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All callers pass fixed test-owned SQL constants.")]
    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static object Statistics(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return new { count = values.Length, minimum = values[0], median = Median(values), p95 = NearestRank(values, 0.95), maximum = values[^1] };
    }

    private static object TemperatureStatistics(IReadOnlyCollection<CaptureSample> samples)
    {
        var values = samples.Select(static item => item.TemperatureC!.Value).Order().ToArray();
        var first = samples.First();
        var last = samples.Last();
        var hours = (last.ExposureStartedUtc - first.ExposureStartedUtc).TotalHours;
        return new
        {
            start = first.TemperatureC,
            end = last.TemperatureC,
            minimum = values[0],
            median = Median(values),
            maximum = values[^1],
            theilSenDegreesCPerHour = TheilSen(samples, hours)
        };
    }

    private static double TheilSen(IReadOnlyCollection<CaptureSample> samples, double totalHours)
    {
        Assert.IsGreaterThan(0, totalHours);
        var ordered = samples.OrderBy(static item => item.ExposureStartedUtc).ToArray();
        var slopes = new List<double>(ordered.Length * (ordered.Length - 1) / 2);
        for (var left = 0; left < ordered.Length - 1; left++)
        {
            for (var right = left + 1; right < ordered.Length; right++)
            {
                var hours = (ordered[right].ExposureStartedUtc - ordered[left].ExposureStartedUtc).TotalHours;
                slopes.Add((ordered[right].TemperatureC!.Value - ordered[left].TemperatureC!.Value) / hours);
            }
        }
        return Median(slopes);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
    }

    private static double NearestRank(IEnumerable<double> source, double percentile)
    {
        var values = source.Order().ToArray();
        return values[Math.Max(0, (int)Math.Ceiling(values.Length * percentile) - 1)];
    }

    private static async Task<string> DockerAsync(TimeSpan timeout, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            var timeoutError = await error.ConfigureAwait(false);
            _ = await output.ConfigureAwait(false);
            throw new AssertFailedException(
                $"docker {string.Join(' ', arguments)} timed out after {timeout}: {timeoutError}");
        }
        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"docker {string.Join(' ', arguments)} failed: {standardError}");
        return standardOutput;
    }

    private static Task WriteJsonAsync(string path, object value)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, EvidenceJson));

    private static void RequireOptIn()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("HVO_ISSUE_268_PHYSICAL_EVIDENCE") != "1")
        {
            Assert.Inconclusive("Run only through scripts/test:cameraagent-physical-268 on a declared physical host.");
        }
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new AssertFailedException($"{name} is required.");

    private static string RequiredPath(string name) => Path.GetFullPath(Required(name));

    private sealed record ManifestObservation(string Path, ArtifactManifestV2 Manifest);

    private sealed record CaptureSample(
        long Sequence,
        DateTimeOffset ExposureStartedUtc,
        DateTimeOffset DurableIngressUtc,
        double? StartIntervalSeconds,
        double ExposureSeconds,
        double ReadoutSeconds,
        double ReadoutToDurableSeconds,
        double StartToDurableSeconds,
        double? StartJitterSeconds,
        string? StartReason,
        double EffectiveExposureSeconds,
        double EffectiveGain,
        double? EffectiveOffset,
        double? TemperatureC,
        long PayloadBytes,
        string PayloadSha256,
        string RigProfileSha256,
        string ProcessingProfileSha256);

    private sealed record ExpectedProfileIdentities(string RigSha256, string ProcessingSha256);

    private sealed record CaptureBarrierEvidence(
        string Phase,
        string State,
        long Version,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        long StartedMonotonicTimestamp,
        long CompletedMonotonicTimestamp,
        bool Authenticated);

    private sealed record BoundarySnapshot(
        string Name,
        DateTimeOffset CapturedUtc,
        long CapturedUnixMilliseconds,
        DateTimeOffset CompletedUtc,
        long CompletedUnixMilliseconds,
        long StartedMonotonicTimestamp,
        long CompletedMonotonicTimestamp,
        string HealthStatus,
        int CaptureCount,
        long RuntimeFilesystemBytes,
        CameraAgentOperationsSummary Summary,
        JsonElement DockerStats,
        ProcessBoundary Process,
        string ProcessSnapshotSha256,
        HostBlockDeviceBoundary HostBlockDevice,
        SqliteFileBoundary SqliteFiles,
        IReadOnlyDictionary<string, double> PrometheusCounters);

    private sealed record ProcessBoundary(
        long CpuTicks,
        long RssBytes,
        long ReadBytes,
        long WriteBytes,
        long ReadSyscalls,
        long WriteSyscalls);

    private sealed record HostBlockDeviceBoundary(string Name, string Stat);

    private sealed record SqliteFileBoundary(long DatabaseBytes, long WalBytes, long ShmBytes);

    private sealed record JournalRow(
        long CaptureSequence,
        string DescriptorSha256,
        string ManifestSha256,
        string PayloadSha256,
        long PayloadLength,
        string PayloadRelativePath,
        string SidecarRelativePath,
        string State);

    private sealed record JournalEvidence(
        string Integrity,
        long UserVersion,
        IReadOnlyList<JournalRow> Rows,
        long NonCommittedRawCount,
        long ReconciliationFailureCount,
        long NonTerminalLaneCount,
        long LaneFailureCount,
        long CompletedLaneCount,
        long ProcessingNodeCount,
        long ProcessingOutputCount,
        long DatabaseBytes,
        long WalBytes,
        long ShmBytes);

    private sealed record ExpectedProfile(
        string Id,
        string Model,
        int Width,
        int Height,
        int StrideBytes,
        int SampleDepthBits,
        long PayloadBytes,
        double Gain,
        double Offset,
        string ProfileVersion)
    {
        public static ExpectedProfile Create()
            => Required("HVO_ISSUE_268_PROFILE") switch
            {
                "asi178mc" => new("asi178mc", "ASI178MC", 3096, 2080, 6192, 14, 12_879_360, 150, 10,
                    "physical-asi178mc-fullframe-raw16-sample-provisional-v1"),
                "asi676mc" => new("asi676mc", "ASI676MC", 3552, 3552, 7104, 12, 25_233_408, 82, 1,
                    "physical-asi676mc-fullframe-raw16-sample-provisional-v1"),
                var value => throw new AssertFailedException($"Unsupported issue #268 profile: {value}")
            };
    }
}

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SustainedPhysicalEvidenceUnitTests
{
    [TestMethod]
    public void WarmupPayloadVariationAcceptsDistinctChecksums()
        => SustainedPhysicalEvidenceTests.AssertWarmupChecksumsVary(["A", "B", "C", "D", "E"]);

    [TestMethod]
    public void WarmupPayloadVariationRejectsDuplicateChecksums()
        => Assert.ThrowsExactly<AssertFailedException>(() =>
            SustainedPhysicalEvidenceTests.AssertWarmupChecksumsVary(["A", "B", "C", "D", "D"]));
}
