using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

// Real endpoint, loader, protected location history and v15 journal; only autonomous workers are removed.
internal sealed class InstallationReadAttributionFixture(CameraAgentKestrelFixture host) : IAsyncDisposable
{
    internal CameraAgentKestrelFixture Host { get; } = host;
    internal DelayedInstallationLoader Loader => (DelayedInstallationLoader)Host.Services.GetRequiredService<ICameraAgentConfigurationLoader>();
    internal string ConfigPath => Path.Combine(Host.Root, "cameraagent.browser.json");
    internal string IngressRoot => Path.Combine(Host.Root, "attribution-ingress");
    internal string JournalPath => Path.Combine(IngressRoot, "journal", "raw-ingress.db");
    internal IDeploymentLocationStore Locations => Host.Services.GetRequiredService<IDeploymentLocationStore>();
    internal static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal const string EvidenceRootGuidance = "Set HVO_INSTALLATION_READ_EVIDENCE_DIR to a short absolute directory registered with hvo scratch. The directory also hosts the recovery Unix socket; copy retained report/TRX files elsewhere after disposal if needed.";

    internal static void ValidateEvidenceRoot(string scratchRoot)
    {
        if (!Path.IsPathFullyQualified(scratchRoot))
            throw new ArgumentException(EvidenceRootGuidance, nameof(scratchRoot));

        try
        {
            // Match the shared fixture's iv-<32hex>/identity/owner.sock path before it creates any host state.
            _ = new UnixDomainSocketEndPoint(Path.Combine(scratchRoot, $"iv-{Guid.Empty:N}", "identity", OwnerRecoveryTransport.SocketFileName));
        }
        catch (ArgumentOutOfRangeException)
        {
            // Do not retain the endpoint exception: it includes the caller's absolute path.
            throw new ArgumentException($"The prospective recovery Unix socket path exceeds the platform limit. {EvidenceRootGuidance}", nameof(scratchRoot));
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Successful construction transfers host ownership to the wrapper; failures dispose it in catch.")]
    internal static async Task<InstallationReadAttributionFixture> CreateAsync(string? scratchRoot, bool solar)
    {
        if (scratchRoot is not null) ValidateEvidenceRoot(scratchRoot);
        string? ingressRoot = null;
        var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            // No acquisition, retention, reconciliation, or worker writes during the measured reads.
            services.RemoveAll<IHostedService>();
            services.Configure<CameraAgentHostOptions>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RawIngressRoot"] = ingressRoot!,
                    ["DeploymentLocation:LocationId"] = "attribution-location",
                    ["DeploymentLocation:Source"] = "issue-1559-disposable-fixture",
                    ["DeploymentLocation:SourceKind"] = "Manual",
                    ["DeploymentLocation:EffectiveFromUtc"] = "2026-01-01T00:00:00Z"
                }).Build());
            services.RemoveAll<ICameraAgentConfigurationLoader>();
            services.AddSingleton<ICameraAgentConfigurationLoader>(provider =>
                new DelayedInstallationLoader(ActivatorUtilities.CreateInstance<FileCameraAgentConfigurationLoader>(provider)));
        }, prepareRoot: async root =>
        {
            ingressRoot = Path.Combine(root, "attribution-ingress");
            var path = Path.Combine(root, "cameraagent.browser.json");
            var ownerConfiguration = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_OWNER_CONFIGURATION");
            if (!string.IsNullOrEmpty(ownerConfiguration))
            {
                var generated = JsonNode.Parse(await File.ReadAllTextAsync(ownerConfiguration).ConfigureAwait(false))!;
                foreach (var step in generated["pipeline"]!["steps"]!.AsArray())
                    if (step!["type"]!.GetValue<string>() == "Storage") step["options"]!["storageRoot"] = step["id"]!.GetValue<string>() == "ArchiveStorage" ? Path.Combine(root, "attribution-archive") : ingressRoot;
                await File.WriteAllTextAsync(path, generated.ToJsonString(EvidenceJson)).ConfigureAwait(false);
            }
            else if (solar)
            {
                var config = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
                foreach (var window in config["schedule"]!["weeklyWindows"]!.AsArray())
                {
                    window!["start"] = new JsonObject { ["kind"] = "AstronomicalDusk" };
                    window["end"] = new JsonObject { ["kind"] = "AstronomicalDawn", ["dayOffset"] = 1 };
                }
                await File.WriteAllTextAsync(path, config.ToJsonString(EvidenceJson)).ConfigureAwait(false);
            }
        }, requireOwnerPasswordReplacement: string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_OWNER_CONFIGURATION")), scratchRoot: scratchRoot).ConfigureAwait(false);
        // Hosted services are intentionally absent; resolve the endpoint's loader explicitly.
        try
        {
            _ = (DelayedInstallationLoader)host.Services.GetRequiredService<ICameraAgentConfigurationLoader>();
            return new InstallationReadAttributionFixture(host);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<FixtureIdentity> SeedAsync(int fileCount, int journalCount)
    {
        var config = await Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var agentId = config.AgentId ?? throw new InvalidDataException("Fixture AgentId is missing.");
        var location = config.DeploymentLocation!;
        var directory = Path.Combine(IngressRoot, "fixtures");
        Directory.CreateDirectory(directory);
        var journal = new SqliteRawCaptureJournal(JournalPath, 10);
        await journal.InitializeAsync([], CancellationToken.None).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        for (var index = 1; index <= fileCount + journalCount; index++)
        {
            var original = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
            var captureId = new Guid(index, 1559, 1, new byte[8]);
            var artifactId = new Guid(index, 1559, 2, new byte[8]);
            var reserved = await journal.ReserveIdentityAsync(agentId, captureId, artifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual((long)index, reserved.CaptureSequence);
            var manifest = original with
            {
                Descriptor = original.Descriptor with
                {
                    Capture = original.Descriptor.Capture with
                    {
                        AgentId = agentId,
                        CaptureSequence = index,
                        CaptureId = captureId
                    },
                    Artifact = original.Descriptor.Artifact with { ArtifactId = artifactId },
                    Location = location.ToProvenance()
                },
                RelativeArtifactPath = $"fixtures/{index:D5}.bin"
            };
            var json = CaptureContractJson.Serialize(manifest);
            Assert.IsTrue(CaptureContractJson.ParseManifest(json).IsValid);
            hash.AppendData(json);
            bytes += json.Length;
            if (index <= fileCount)
            {
#if HVO_BASELINE
                await File.WriteAllBytesAsync(Path.Combine(directory, $"{index:D5}.json"), json).ConfigureAwait(false);
#else
                await HVO.SkyMonitor.CameraAgent.Common.Storage.RetainedFileMutations.WriteAllBytesAsync(Path.Combine(directory, $"{index:D5}.json"), json).ConfigureAwait(false);
#endif
            }
            else
            {
                var entry = new RawIngressJournalEntry(agentId, index, captureId, artifactId,
                    manifest.IdempotencyKey, CaptureContractJson.ComputeManifestSha256(json),
                    manifest.Descriptor.Artifact.ChecksumSha256, 8, manifest.RelativeArtifactPath,
                    $"fixtures/{index:D5}.json", json, manifest.Descriptor.Timing.ExposureStartedUtc,
                    manifest.Descriptor.Timing.DurableIngressUtc);
                Assert.AreEqual(RawIngressOutcome.Committed,
                    await journal.CommitAsync(entry, null, null, [], CancellationToken.None).ConfigureAwait(false));
            }
        }
        return new FixtureIdentity(fileCount, journalCount, bytes, Convert.ToHexString(hash.GetHashAndReset()),
            new FileInfo(JournalPath).Length, await HashAsync(ConfigPath).ConfigureAwait(false),
            location.CanonicalSha256);
    }

    // Bulk disposable metadata construction: real v15 schema and contract bytes, one transaction.
    // No payload files, acquisition, production data, or durability-throughput claim.
    internal async Task<FixtureIdentity> SeedScaleAsync(int count, int productsPerCapture = 1)
    {
        var config = await Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var location = config.DeploymentLocation!;
#if !HVO_BASELINE
        using (var empty = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(IngressRoot, ".location", RetainedIntegrityLedger.DatabaseName)};Mode=ReadOnly;Pooling=False"))
        {
            await empty.OpenAsync().ConfigureAwait(false);
            using var check = empty.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM entries;";
            Assert.AreEqual(0L, await check.ExecuteScalarAsync().ConfigureAwait(false), "Bulk legacy import is allowed only into this empty disposable fixture.");
        }
#endif
        var journal = new SqliteRawCaptureJournal(JournalPath, 10);
        await journal.InitializeAsync([], CancellationToken.None).ConfigureAwait(false);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={JournalPath};Pooling=False;Foreign Keys=True");
        await connection.OpenAsync().ConfigureAwait(false);
        using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_capture_assignments(capture_id,raw_artifact_id,agent_id,capture_sequence)
            VALUES($capture,$artifact,$agent,$sequence);
            INSERT INTO raw_captures(capture_id,raw_artifact_id,agent_id,capture_sequence,
                descriptor_sha256,manifest_sha256,payload_sha256,payload_length,
                payload_relative_path,sidecar_relative_path,manifest_json,
                exposure_started_unix_ms,durable_ingress_unix_ms,committed_unix_ms,state,evidence_origin)
            VALUES($capture,$artifact,$agent,$sequence,$hash,$hash,$payload,8,
                $path||'.bin',$path||'.json',$json,0,0,0,'committed','DeveloperFixture');
            """;
        foreach (var name in new[] { "$capture", "$artifact", "$agent", "$sequence", "$hash", "$payload", "$path", "$json" })
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(name, null));
        var original = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        for (var index = 1; index <= count; index++)
        {
            var captureSequence = (index - 1) / productsPerCapture + 1;
            var product = (index - 1) % productsPerCapture;
            var captureId = new Guid(captureSequence, 1041, 1, new byte[8]);
            var artifactId = new Guid(index, 1041, 2, new byte[8]);
            var rawArtifactId = new Guid((captureSequence - 1) * productsPerCapture + 1, 1041, 2, new byte[8]);
            var role = product == 0 ? FrameArtifactRole.Raw : product == 1 ? FrameArtifactRole.Preview : FrameArtifactRole.AnnotatedPreview;
            var relative = $"frames/2026/11/10/{role}/{index:D6}";
            var manifest = original with
            {
                Descriptor = original.Descriptor with
                {
                    Capture = original.Descriptor.Capture with { AgentId = config.AgentId!, CaptureSequence = captureSequence, CaptureId = captureId },
                    Artifact = original.Descriptor.Artifact with { ArtifactId = artifactId, Role = role, SourceArtifactIds = product == 0 ? [] : [rawArtifactId] },
                    Location = location.ToProvenance()
                },
                RelativeArtifactPath = relative + ".bin"
            };
            var json = CaptureContractJson.Serialize(manifest);
            Assert.IsTrue(CaptureContractJson.ParseManifest(json).IsValid);
            hash.AppendData(json);
            bytes += json.Length * (product == 0 ? 2L : 1L);
            var path = Path.Combine(IngressRoot, relative + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, json).ConfigureAwait(false);
            if (product != 0) continue;
            command.Parameters["$capture"].Value = captureId.ToString();
            command.Parameters["$artifact"].Value = artifactId.ToString();
            command.Parameters["$agent"].Value = config.AgentId!;
            command.Parameters["$sequence"].Value = captureSequence;
            command.Parameters["$hash"].Value = CaptureContractJson.ComputeManifestSha256(json);
            command.Parameters["$payload"].Value = manifest.Descriptor.Artifact.ChecksumSha256;
            command.Parameters["$path"].Value = relative;
            command.Parameters["$json"].Value = json;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        return new FixtureIdentity(count, (count + productsPerCapture - 1) / productsPerCapture, bytes, Convert.ToHexString(hash.GetHashAndReset()),
            new FileInfo(JournalPath).Length, await HashAsync(ConfigPath).ConfigureAwait(false), location.CanonicalSha256);
    }

#if !HVO_BASELINE
    internal async Task QualifyLegacyScaleAsync()
    {
        // SeedScale asserted an empty ledger before importing this synthetic legacy corpus.
        // Reconstruct the signed legacy history shape before discarding only that
        // asserted-empty fixture ledger. This is corpus construction, not recovery.
        ((IDisposable)Locations).Dispose();
        var state = Path.Combine(IngressRoot, ".location");
        var historyPath = Path.Combine(state, "deployment-location.v1.protected");
        var protector = Host.Services.GetRequiredService<IDeploymentLocationProtector>();
        var legacyHistory = System.Text.Json.Nodes.JsonNode.Parse(protector.Unprotect(await File.ReadAllBytesAsync(historyPath).ConfigureAwait(false)))!.AsObject();
        Assert.IsTrue(legacyHistory.Remove("retainedIntegrityNonce"));
        await File.WriteAllBytesAsync(historyPath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(legacyHistory))).ConfigureAwait(false);
        foreach (var name in new[] { RetainedIntegrityLedger.DatabaseName, RetainedIntegrityLedger.CheckpointName })
            File.Delete(Path.Combine(state, name));
        await Host.RestartAsync().ConfigureAwait(false);
        _ = await Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }
#endif

    internal async Task<JsonElement> ReadAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        Assert.AreEqual(Host.BaseAddress, client.BaseAddress, "Only this disposable host may be read.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        if (Activity.Current is { Id: { } id }) request.Headers.Add("traceparent", id);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    internal HttpClient CreateClient() => new() { BaseAddress = Host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };

    internal async Task EditAsync()
    {
        var original = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
        var modifiedUtc = File.GetLastWriteTimeUtc(ConfigPath);
        var edited = original.Replace(CameraAgentKestrelFixture.AgentId, "issue1559-edited-current-agent", StringComparison.Ordinal)
            .Replace("\"gain\": 1.0", "\"gain\": 2.0", StringComparison.Ordinal);
        Assert.AreNotEqual(original, edited);
        Assert.AreEqual(original.Length, edited.Length, "Exercise a real same-length edit.");
        // A timestamp/length-only cache would miss this atomic edit. Content remains the authority.
        var next = ConfigPath + ".next";
        await File.WriteAllTextAsync(next, edited).ConfigureAwait(false);
        File.Move(next, ConfigPath, overwrite: true);
        File.SetLastWriteTimeUtc(ConfigPath, modifiedUtc);
    }

    internal async Task<string> HashRetainedInputsAsync()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var path in Directory.EnumerateFiles(IngressRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith("-shm", StringComparison.Ordinal) &&
                         !Path.GetFileName(path).StartsWith("retained-validation.", StringComparison.Ordinal) &&
                         !Path.GetFileName(path).StartsWith(".retained-validation.", StringComparison.Ordinal) &&
                         !Path.GetFileName(path).StartsWith("retained-integrity.", StringComparison.Ordinal) &&
                         !Path.GetFileName(path).StartsWith(".retained-integrity.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(IngressRoot, path) + "\0"));
            using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static async Task<string> HashAsync(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

internal sealed record FixtureIdentity(int FileCount, int JournalCount, long ManifestBytes, string OrderedManifestSha256,
    long JournalBytes, string ConfigurationFileSha256, string LocationSha256);

internal sealed class DelayedInstallationLoader(FileCameraAgentConfigurationLoader inner) : ICameraAgentConfigurationLoader
{
    private TaskCompletionSource? _entered;
    private TaskCompletionSource? _release;
    internal Task Entered => _entered!.Task;
    internal void DelayNextRead()
    {
        _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void Release() => _release!.TrySetResult();
    public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => LoadCoreAsync(false, cancellationToken);
    public Task<CameraModuleConfig> LoadForInstallationVerificationAsync(CancellationToken cancellationToken) => LoadCoreAsync(true, cancellationToken);
    private async Task<CameraModuleConfig> LoadCoreAsync(bool strict, CancellationToken cancellationToken)
    {
        await WaitBeforeReadAsync(cancellationToken).ConfigureAwait(false);
        // The manual baseline harness also builds against c4069b8c, before the explicit strict-load API.
        var verification = strict ? inner.GetType().GetMethod(nameof(LoadForInstallationVerificationAsync)) : null;
        return verification is null
            ? await inner.LoadAsync(cancellationToken).ConfigureAwait(false)
            : await ((Task<CameraModuleConfig>)verification.Invoke(inner, [cancellationToken])!).ConfigureAwait(false);
    }
#if !HVO_BASELINE
    public async Task<InstallationConfigurationSnapshot> LoadInstallationSnapshotAsync(CancellationToken cancellationToken)
    {
        await WaitBeforeReadAsync(cancellationToken).ConfigureAwait(false);
        return await inner.LoadInstallationSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }
#endif

    private async Task WaitBeforeReadAsync(CancellationToken cancellationToken)
    {
        var entered = Interlocked.Exchange(ref _entered, null);
        if (entered is not null)
        {
            entered.TrySetResult();
            await _release!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

}

internal sealed class InstallationReadStageCollector : IDisposable
{
    private readonly ConcurrentQueue<InstallationReadStage> _stages = new();
    private readonly ActivityListener _listener;
    internal InstallationReadStageCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is FileCameraAgentConfigurationLoader.ActivitySourceName or
                DeploymentLocationTelemetry.ActivitySourceName or DeploymentLocationTelemetry.AttributionActivitySourceName or "HVO.Issue1559",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => activity.SetCustomProperty("allocated", GC.GetTotalAllocatedBytes(precise: true)),
            ActivityStopped = activity => _stages.Enqueue(new InstallationReadStage(activity.TraceId.ToString(),
                activity.SpanId.ToString(), activity.ParentSpanId.ToString(), activity.OperationName,
                activity.Duration.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - (long)activity.GetCustomProperty("allocated")!,
                activity.TagObjects.Where(tag => tag.Value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    .ToDictionary(tag => tag.Key, tag => tag.Value!, StringComparer.Ordinal)))
        };
        ActivitySource.AddActivityListener(_listener);
    }
    internal InstallationReadStage[] ForTrace(ActivityTraceId traceId) => _stages.Where(stage => stage.TraceId == traceId.ToString()).ToArray();
    public void Dispose() => _listener.Dispose();
}

// ProcessAllocatedBytes is process-wide and inclusive; NumericTags also retains
// explicitly named synchronous leaf thread_allocated_bytes. Do not sum either
// into enclosing spans or across concurrent requests.
internal sealed record InstallationReadStage(string TraceId, string SpanId, string ParentSpanId, string Name,
    double ElapsedMs, long ProcessAllocatedBytes, Dictionary<string, object> NumericTags);
