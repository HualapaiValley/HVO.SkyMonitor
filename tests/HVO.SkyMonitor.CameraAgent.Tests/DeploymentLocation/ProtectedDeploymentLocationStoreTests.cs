using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProtectedDeploymentLocationStoreTests
{
    private string _root = null!;
    private DataProtectionDeploymentLocationProtector _protector = null!;
    private IOptions<CameraAgentHostOptions> _options = null!;
    private MutableTimeProvider _timeProvider = null!;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-07-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int[] ExpectedLogEventIds = [7301, 7302];
    private static readonly string[] ExpectedCanonicalHistoryProperties =
    [
        "schemaVersion",
        "locationId",
        "snapshots",
        "supersededAtUtc",
        "staged",
        "centrallyActivatedCanonicalSha256",
        "candidate",
        "configurationSeed",
        "activatedAtUtc",
        "sourceKinds"
    ];

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-location-tests", Guid.NewGuid().ToString("N"));
        var keyDirectory = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keyDirectory);
        _protector = new DataProtectionDeploymentLocationProtector(DataProtectionProvider.Create(keyDirectory));
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _timeProvider = new MutableTimeProvider(Now);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeRestartAndChange_PreservesProtectedImmutableVersions()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        CaptureLocationProvenance firstProvenance;
        using (var first = CreateStore())
        {
            var snapshot = await first.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, snapshot.Version);
            firstProvenance = snapshot.ToProvenance();
        }

        var statePath = Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var protectedPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var protectedText = Encoding.UTF8.GetString(protectedPayload);
        Assert.IsFalse(protectedText.Contains("35.347", StringComparison.Ordinal));
        Assert.IsFalse(protectedText.Contains("-113.878", StringComparison.Ordinal));
        Assert.IsFalse(protectedText.Contains("America/Phoenix", StringComparison.Ordinal));
        var canonical = JsonNode.Parse(_protector.Unprotect(protectedPayload))!.AsObject();
        CollectionAssert.AreEquivalent(
            ExpectedCanonicalHistoryProperties,
            canonical.Select(static pair => pair.Key).ToArray());
        Assert.AreEqual("Unspecified", canonical["sourceKinds"]!["1"]!.GetValue<string>());
        Assert.AreEqual("Unspecified", canonical["configurationSeed"]!["sourceKind"]!.GetValue<string>());
        Assert.IsNotNull(canonical["activatedAtUtc"]!["1"]);
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        var markerPayload = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);

        using var restarted = CreateStore();
        var same = await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(firstProvenance, same.ToProvenance());
        Assert.AreEqual(firstProvenance, restarted.Resolve(firstProvenance).ToProvenance());
        CollectionAssert.AreEqual(protectedPayload, await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        CollectionAssert.AreEqual(markerPayload, await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false));

        _timeProvider.UtcNow = Now.AddSeconds(1);
        var changed = await restarted.InitializeAsync(
            CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, changed.Version);
        Assert.AreNotEqual(firstProvenance.IdentitySha256, changed.ToProvenance().IdentitySha256);
        Assert.AreEqual(firstProvenance, restarted.Resolve(firstProvenance, Now).ToProvenance());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            restarted.Resolve(firstProvenance, Now.AddSeconds(1)));
    }

    [TestMethod]
    [DataRow("sourceKinds", false)]
    [DataRow("sourceKinds", true)]
    [DataRow("configurationSeed", false)]
    [DataRow("configurationSeed.sourceKind", false)]
    [DataRow("activatedAtUtc", false)]
    [DataRow("staged", false)]
    [DataRow("centrallyActivatedCanonicalSha256", false)]
    [DataRow("candidate", false)]
    public async Task InitializeAsync_RejectsIncompleteCanonicalHistoryWithoutMutation(
        string missingProperty,
        bool centralIntegration)
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix") with
        {
            SourceKind = DeploymentLocationSourceKind.Gps,
            EffectiveFromUtc = Now.AddMinutes(-1)
        };
        using (var initial = CreateStore())
        {
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        var statePath = Path.Combine(
            _options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        var plaintext = _protector.Unprotect(await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        var incomplete = JsonNode.Parse(plaintext)!.AsObject();
        if (missingProperty == "configurationSeed.sourceKind")
        {
            incomplete["configurationSeed"]!.AsObject().Remove("sourceKind");
        }
        else
        {
            incomplete.Remove(missingProperty);
        }
        await File.WriteAllBytesAsync(
            statePath,
            _protector.Protect(Encoding.UTF8.GetBytes(incomplete.ToJsonString()))).ConfigureAwait(false);
        var incompleteProtectedPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var markerPayload = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);
        if (centralIntegration)
        {
            _options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = _options.Value.RawIngressRoot,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
            });
        }

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsNull(restarted.Active);
        Assert.IsNull(restarted.Candidate);
        CollectionAssert.AreEqual(
            incompleteProtectedPayload,
            await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        CollectionAssert.AreEqual(markerPayload, await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("unsupported-schema")]
    [DataRow("malformed-chronology")]
    [DataRow("inconsistent-configuration-seed")]
    [DataRow("inconsistent-configuration-end")]
    [DataRow("unknown-field")]
    public async Task InitializeAsync_RejectsNoncanonicalHistoryWithoutMutation(string corruption)
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix") with
        {
            SourceKind = DeploymentLocationSourceKind.Gps,
            EffectiveFromUtc = Now.AddMinutes(-1),
            EffectiveUntilUtc = corruption == "inconsistent-configuration-end" ? Now.AddMinutes(1) : null
        };
        using (var initial = CreateStore())
        {
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        var statePath = Path.Combine(
            _options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var plaintext = _protector.Unprotect(await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        var noncanonical = JsonNode.Parse(plaintext)!.AsObject();
        switch (corruption)
        {
            case "unsupported-schema":
                noncanonical["schemaVersion"] = 2;
                break;
            case "malformed-chronology":
                noncanonical["activatedAtUtc"]!.AsObject()["1"] = JsonValue.Create(Now.AddMinutes(-2));
                break;
            case "inconsistent-configuration-seed":
                noncanonical["configurationSeed"]!["source"] = "different-source";
                break;
            case "inconsistent-configuration-end":
                noncanonical["configurationSeed"]!["effectiveUntilUtc"] = null;
                break;
            case "unknown-field":
                noncanonical["legacyClassification"] = "gps";
                break;
        }
        await File.WriteAllBytesAsync(
            statePath,
            _protector.Protect(Encoding.UTF8.GetBytes(noncanonical.ToJsonString()))).ConfigureAwait(false);
        var noncanonicalPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            noncanonicalPayload,
            await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("activation-boundary")]
    [DataRow("missing-supersession")]
    public async Task InitializeAsync_RejectsNoncanonicalChronologyWithoutMutation(string corruption)
    {
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        var secondSeed = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await store.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
        }
        var statePath = Path.Combine(
            _options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var plaintext = _protector.Unprotect(await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        var noncanonical = JsonNode.Parse(plaintext)!.AsObject();
        if (corruption == "missing-supersession")
        {
            noncanonical["supersededAtUtc"]!.AsObject().Remove("1");
        }
        else
        {
            noncanonical["activatedAtUtc"]!.AsObject()["2"] = JsonValue.Create(Now.AddMilliseconds(1500));
        }
        await File.WriteAllBytesAsync(
            statePath,
            _protector.Protect(Encoding.UTF8.GetBytes(noncanonical.ToJsonString()))).ConfigureAwait(false);
        var noncanonicalPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddSeconds(2);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(
            noncanonicalPayload,
            await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StageAsync_DoesNotChangeLiveGeometryAndActivatesOnlyOnRestart()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        DeploymentLocationSnapshot staged;
        using (var running = CreateStore())
        {
            var active = await running.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            var configuredSuccessor = seed with
            {
                Source = "centrally-approved",
                HorizontalAccuracyMeters = 2,
                Coordinates = seed.Coordinates with { LatitudeDegrees = 35.348, ElevationMeters = 521 }
            };
            var stillActive = await running.InitializeAsync(
                configuredSuccessor, CancellationToken.None).ConfigureAwait(false);
            staged = running.Candidate!;

            await running.StageAsync(staged, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(active, stillActive);
            Assert.AreEqual(active, running.Active);
            Assert.AreEqual(staged, running.Staged);
        }

        _timeProvider.UtcNow = Now.AddSeconds(10);
        var successorSeed = seed with
        {
            Source = "centrally-approved",
            HorizontalAccuracyMeters = 2,
            Coordinates = seed.Coordinates with { LatitudeDegrees = 35.348, ElevationMeters = 521 }
        };
        using (var restarted = CreateStore())
        {
            var activated = await restarted.InitializeAsync(successorSeed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(staged, activated);
            Assert.IsNull(restarted.Staged);
        }

        using var repeatedRestart = CreateStore();
        var retained = await repeatedRestart.InitializeAsync(successorSeed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(staged, retained);

        _timeProvider.UtcNow = Now.AddSeconds(11);
        var newerSeed = successorSeed with
        {
            Coordinates = successorSeed.Coordinates with { LatitudeDegrees = 35.349 }
        };
        var unchangedUntilApproval = await repeatedRestart.InitializeAsync(
            newerSeed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(staged, unchangedUntilApproval);
        Assert.AreEqual(3L, repeatedRestart.Candidate!.Version);

        var sourceKindCorrection = newerSeed with { SourceKind = DeploymentLocationSourceKind.Gps };
        _ = await repeatedRestart.InitializeAsync(sourceKindCorrection, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(4L, repeatedRestart.Candidate!.Version);
        Assert.AreEqual(
            DeploymentLocationSourceKind.Gps,
            repeatedRestart.ResolveSourceKind(repeatedRestart.Candidate));
        Assert.AreEqual(
            DeploymentLocationSourceKind.Unspecified,
            repeatedRestart.ResolveSourceKind(repeatedRestart.Active!));
        await repeatedRestart.StageAsync(repeatedRestart.Candidate, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddSeconds(12);
        using var classificationRestart = CreateStore();
        var classificationActive = await classificationRestart.InitializeAsync(
            sourceKindCorrection, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(4L, classificationActive.Version);
        Assert.AreEqual(DeploymentLocationSourceKind.Gps, classificationRestart.ResolveSourceKind(classificationActive));
    }

    [TestMethod]
    public async Task InitializeAsync_RevertingCandidateRemovesItsClassificationAndRemainsRestartable()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var activeSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var running = CreateStore())
        {
            var active = await running.InitializeAsync(activeSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await running.InitializeAsync(
                activeSeed with
                {
                    SourceKind = DeploymentLocationSourceKind.Gps,
                    Coordinates = activeSeed.Coordinates with { LatitudeDegrees = 35.348 }
                },
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(running.Candidate);

            var reverted = await running.InitializeAsync(activeSeed, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(active, reverted);
            Assert.IsNull(running.Candidate);
        }

        using var restarted = CreateStore();
        var retained = await restarted.InitializeAsync(activeSeed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, retained.Version);
        Assert.IsNull(restarted.Candidate);
        Assert.AreEqual(DeploymentLocationSourceKind.Unspecified, restarted.ResolveSourceKind(retained));
    }

    [TestMethod]
    public async Task StagedActivation_ReconcilesConfigurationChangedBeforeRestart()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var initialSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix") with
        {
            SourceKind = DeploymentLocationSourceKind.Manual
        };
        var approvedSeed = initialSeed with
        {
            Source = "approved",
            Coordinates = initialSeed.Coordinates with { LatitudeDegrees = 35.348 }
        };
        DeploymentLocationSnapshot approved;
        using (var running = CreateStore())
        {
            _ = await running.InitializeAsync(initialSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await running.InitializeAsync(approvedSeed, CancellationToken.None).ConfigureAwait(false);
            approved = running.Candidate!;
            await running.StageAsync(approved, CancellationToken.None).ConfigureAwait(false);
        }
        var changedBeforeRestart = approvedSeed with
        {
            Coordinates = approvedSeed.Coordinates with { LatitudeDegrees = 35.349 }
        };
        _timeProvider.UtcNow = Now.AddSeconds(10);

        using var restarted = CreateStore();
        var active = await restarted.InitializeAsync(changedBeforeRestart, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(approved, active);
        Assert.AreEqual(3L, restarted.Candidate!.Version);
        Assert.AreEqual(35.349, restarted.Candidate.LatitudeDegrees);
        Assert.IsNull(restarted.Staged);
    }

    [TestMethod]
    public async Task StagedActivation_FutureOrExpiredIntervalDoesNotPreventStartup()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var initialSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix") with
        {
            SourceKind = DeploymentLocationSourceKind.Manual
        };
        var futureSeed = initialSeed with
        {
            Source = "scheduled",
            EffectiveFromUtc = Now.AddSeconds(10),
            EffectiveUntilUtc = Now.AddSeconds(20),
            Coordinates = initialSeed.Coordinates with { LatitudeDegrees = 35.348 }
        };
        DeploymentLocationSnapshot initial;
        DeploymentLocationSnapshot staged;
        using (var running = CreateStore())
        {
            initial = await running.InitializeAsync(initialSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await running.InitializeAsync(futureSeed, CancellationToken.None).ConfigureAwait(false);
            staged = running.Candidate!;
            await running.StageAsync(staged, CancellationToken.None).ConfigureAwait(false);
        }

        _timeProvider.UtcNow = Now.AddSeconds(5);
        using (var beforeInterval = CreateStore())
        {
            Assert.AreEqual(initial, await beforeInterval.InitializeAsync(futureSeed, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(staged, beforeInterval.Staged);
        }

        _timeProvider.UtcNow = Now.AddSeconds(21);
        var replacementSeed = futureSeed with
        {
            EffectiveFromUtc = null,
            EffectiveUntilUtc = null,
            Coordinates = futureSeed.Coordinates with { LatitudeDegrees = 35.349 }
        };
        using var afterInterval = CreateStore();
        Assert.AreEqual(initial, await afterInterval.InitializeAsync(
            replacementSeed, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(afterInterval.Staged);
        Assert.AreEqual(3L, afterInterval.Candidate!.Version);
        Assert.AreEqual(35.349, afterInterval.Candidate.LatitudeDegrees);
    }

    [TestMethod]
    public async Task StagedActivation_PreservesOldCaptureEvidenceUntilActualRestartBoundary()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        var nextSeed = firstSeed with
        {
            Source = "approved-successor",
            Coordinates = firstSeed.Coordinates with { LatitudeDegrees = 35.348 }
        };
        DeploymentLocationSnapshot active;
        using (var running = CreateStore())
        {
            active = await running.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await running.InitializeAsync(nextSeed, CancellationToken.None).ConfigureAwait(false);
            await running.StageAsync(running.Candidate!, CancellationToken.None).ConfigureAwait(false);
        }
        var captureTime = Now.AddSeconds(5);
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Timing = manifest.Descriptor.Timing with { ExposureStartedUtc = captureTime },
                Location = active.ToProvenance()
            }
        };
        var evidenceDirectory = Path.Combine(_options.Value.RawIngressRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(evidenceDirectory, "pre-restart-capture.json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);

        _timeProvider.UtcNow = Now.AddSeconds(10);
        using var restarted = CreateStore();
        var activated = await restarted.InitializeAsync(nextSeed, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2L, activated.Version);
        Assert.AreEqual(active, restarted.Resolve(active.ToProvenance(), captureTime));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            restarted.Resolve(activated.ToProvenance(), captureTime));
    }

    [TestMethod]
    public async Task Initialize_MissingHistoryOrWrongKeyFailsClosedForExistingEvidence()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        DeploymentLocationSnapshot location;
        using (var store = CreateStore())
        {
            location = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        var statePath = Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        var otherKeys = Path.Combine(_root, "other-keys");
        Directory.CreateDirectory(otherKeys);
        var wrongProtector = new DataProtectionDeploymentLocationProtector(DataProtectionProvider.Create(otherKeys));
        using (var wrongKeyStore = new ProtectedDeploymentLocationStore(
            _options, wrongProtector, _timeProvider, NullLogger<ProtectedDeploymentLocationStore>.Instance))
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await wrongKeyStore.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        File.Delete(statePath);
        using (var missingStore = CreateStore())
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await missingStore.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        File.Delete(markerPath);
        var evidenceDirectory = Path.Combine(_options.Value.RawIngressRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Location = location.ToProvenance() with { EffectiveFromUtc = DateTimeOffset.UnixEpoch }
            }
        };
        await File.WriteAllBytesAsync(
            Path.Combine(evidenceDirectory, "capture.json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        using var evidenceStore = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await evidenceStore.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        File.Delete(Path.Combine(evidenceDirectory, "capture.json"));
        var journalDirectory = Path.Combine(_options.Value.RawIngressRoot, "journal");
        Directory.CreateDirectory(journalDirectory);
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(journalDirectory, "raw-ingress.db")};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE raw_captures(manifest_json BLOB NOT NULL);
                INSERT INTO raw_captures(manifest_json) VALUES ($manifest);
                """;
            command.Parameters.AddWithValue("$manifest", CaptureContractJson.Serialize(manifest));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var journalStore = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await journalStore.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Initialize_RestoredOlderHistoryFailsForNewerRetainedEvidence()
    {
        var captureTime = DateTimeOffset.Parse(
            "2026-01-02T03:04:06Z", System.Globalization.CultureInfo.InvariantCulture);
        _timeProvider.UtcNow = captureTime.AddSeconds(-1);
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        var secondSeed = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney");
        var statePath = Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        byte[] firstHistory;
        byte[] firstMarker;
        DeploymentLocationSnapshot secondLocation;
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
            firstHistory = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
            firstMarker = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);
            _timeProvider.UtcNow = captureTime;
            secondLocation = await store.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
        }

        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with { Location = secondLocation.ToProvenance() }
        };
        var evidenceDirectory = Path.Combine(_options.Value.RawIngressRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(evidenceDirectory, "capture.json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        await File.WriteAllBytesAsync(statePath, firstHistory).ConfigureAwait(false);
        await File.WriteAllBytesAsync(markerPath, firstMarker).ConfigureAwait(false);

        using var restored = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restored.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Initialize_BackdatedChangeCannotInvalidateRetainedCapture()
    {
        var captureTime = DateTimeOffset.Parse(
            "2026-01-02T03:04:06Z", System.Globalization.CultureInfo.InvariantCulture);
        _timeProvider.UtcNow = captureTime.AddMinutes(-4);
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using var store = CreateStore();
        var firstLocation = await store.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with { Location = firstLocation.ToProvenance() }
        };
        var evidenceDirectory = Path.Combine(_options.Value.RawIngressRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(evidenceDirectory, "capture.json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);

        _timeProvider.UtcNow = captureTime.AddHours(1);
        var backdated = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney") with
        {
            EffectiveFromUtc = captureTime.AddMinutes(-2)
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.InitializeAsync(backdated, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(1L, store.Active!.Version);
    }

    [TestMethod]
    public async Task Initialize_RecoversInterruptedMarkerPublication()
    {
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        var secondSeed = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        byte[] firstMarker;
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
            firstMarker = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await store.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(markerPath, firstMarker).ConfigureAwait(false);
        using (var staleMarkerStore = CreateStore())
        {
            var recovered = await staleMarkerStore.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, recovered.Version);
        }
        var repairedMarker = await File.ReadAllTextAsync(markerPath).ConfigureAwait(false);
        StringAssert.Contains(repairedMarker, "\"version\":2", StringComparison.Ordinal);

        File.Delete(markerPath);
        using var missingMarkerStore = CreateStore();
        var recoveredMissing = await missingMarkerStore.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, recoveredMissing.Version);
        Assert.IsTrue(File.Exists(markerPath));
    }

    [TestMethod]
    public async Task Initialize_MarkerMismatchFailsWithoutMutation()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        var statePath = Path.Combine(
            _options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        var historyPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var marker = JsonNode.Parse(await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false))!.AsObject();
        marker["locationId"] = "different-deployment";
        await File.WriteAllBytesAsync(
            markerPath,
            Encoding.UTF8.GetBytes(marker.ToJsonString())).ConfigureAwait(false);
        var mismatchedMarkerPayload = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        CollectionAssert.AreEqual(historyPayload, await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        CollectionAssert.AreEqual(
            mismatchedMarkerPayload,
            await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Initialize_SkippedVersionMarkerFailsWithoutMutation()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var firstSeed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        var secondSeed = firstSeed with
        {
            Source = "second-candidate",
            Coordinates = firstSeed.Coordinates with { LatitudeDegrees = 35.348 }
        };
        var thirdSeed = secondSeed with
        {
            Source = "third-candidate",
            Coordinates = secondSeed.Coordinates with { LatitudeDegrees = 35.349 }
        };
        using (var running = CreateStore())
        {
            _ = await running.InitializeAsync(firstSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(1);
            _ = await running.InitializeAsync(secondSeed, CancellationToken.None).ConfigureAwait(false);
            _timeProvider.UtcNow = Now.AddSeconds(2);
            _ = await running.InitializeAsync(thirdSeed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3L, running.Candidate!.Version);
            await running.StageAsync(running.Candidate, CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(3);
        using (var activating = CreateStore())
        {
            var active = await activating.InitializeAsync(thirdSeed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3L, active.Version);
        }
        var statePath = Path.Combine(
            _options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var markerPath = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
        var historyPayload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var marker = JsonNode.Parse(await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false))!.AsObject();
        marker["version"] = 2;
        await File.WriteAllBytesAsync(
            markerPath,
            Encoding.UTF8.GetBytes(marker.ToJsonString())).ConfigureAwait(false);
        var skippedMarkerPayload = await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(thirdSeed, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(historyPayload, await File.ReadAllBytesAsync(statePath).ConfigureAwait(false));
        CollectionAssert.AreEqual(
            skippedMarkerPayload,
            await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Initialize_SymbolicLinkDataRootFailsBeforeWritingState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var physical = Path.Combine(_root, "physical-data");
        var link = Path.Combine(_root, "linked-data");
        Directory.CreateDirectory(physical);
        Directory.CreateSymbolicLink(link, physical);
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = link });
        try
        {
            using var store = new ProtectedDeploymentLocationStore(
                options, _protector, _timeProvider, NullLogger<ProtectedDeploymentLocationStore>.Instance);
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await store.InitializeAsync(
                    CreateSeed(35.347, -113.878, 0, "America/Phoenix"),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(Path.Combine(physical, ".location")));
        }
        finally
        {
            Directory.Delete(link);
        }

        var dataRoot = _options.Value.RawIngressRoot;
        var locationDirectory = Path.Combine(dataRoot, ".location");
        var externalKeys = Path.Combine(_root, "external-location-keys");
        Directory.CreateDirectory(locationDirectory);
        Directory.CreateDirectory(externalKeys);
        var keyLink = Path.Combine(locationDirectory, "keys");
        Directory.CreateSymbolicLink(keyLink, externalKeys);
        try
        {
            var productionProtector = new DataProtectionDeploymentLocationProtector(_options);
            using var store = new ProtectedDeploymentLocationStore(
                _options, productionProtector, _timeProvider, NullLogger<ProtectedDeploymentLocationStore>.Instance);
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await store.InitializeAsync(
                    CreateSeed(35.347, -113.878, 0, "America/Phoenix"),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(Path.Combine(locationDirectory, "deployment-location.v1.protected")));
        }
        finally
        {
            Directory.Delete(keyLink);
        }
    }

    [TestMethod]
    public async Task Initialize_EmitsBoundedSignalsWithoutCoordinates()
    {
        var measurements = new List<(string Name, string Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DeploymentLocationTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, FormatTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, FormatTags(tags))));
        meterListener.Start();
        var stopped = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeploymentLocationTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        using var telemetry = new DeploymentLocationTelemetry();
        var logger = new RecordingLogger<ProtectedDeploymentLocationStore>();
        using var store = new ProtectedDeploymentLocationStore(
            _options, _protector, _timeProvider, logger, telemetry);

        var sidingSpring = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney");
        _ = await store.InitializeAsync(
            sidingSpring,
            CancellationToken.None).ConfigureAwait(false);
        _ = await store.InitializeAsync(sidingSpring, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddSeconds(1);
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"),
            CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.InitializeAsync(
                sidingSpring with { Source = string.Empty }, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsTrue(measurements.Any(item =>
            item.Name == "skymonitor.cameraagent.deployment_location.operations" &&
            item.Tags.Contains("operation=initialize", StringComparison.Ordinal) &&
            item.Tags.Contains("outcome=applied", StringComparison.Ordinal)));
        Assert.IsTrue(measurements.Any(item => item.Tags.Contains("operation=load", StringComparison.Ordinal) &&
            item.Tags.Contains("outcome=existing", StringComparison.Ordinal)));
        Assert.IsTrue(measurements.Any(item => item.Tags.Contains("operation=change", StringComparison.Ordinal) &&
            item.Tags.Contains("outcome=applied", StringComparison.Ordinal)));
        Assert.IsTrue(measurements.Any(item => item.Tags.Contains("outcome=failed", StringComparison.Ordinal)));
        Assert.HasCount(4, stopped);
        Assert.IsTrue(stopped.All(static activity => activity.OperationName == "deployment-location.initialize"));
        CollectionAssert.IsSubsetOf(ExpectedLogEventIds, logger.EventIds.Distinct().ToArray());
        var signals = string.Join('\n', measurements.Select(static item => $"{item.Name}:{item.Tags}")) +
            string.Join('\n', logger.Messages) +
            string.Join('\n', stopped.SelectMany(static activity => activity.TagObjects)
                .Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.IsFalse(signals.Contains("-31.2733", StringComparison.Ordinal));
        Assert.IsFalse(signals.Contains("149.07", StringComparison.Ordinal));
        Assert.IsFalse(signals.Contains("Australia/Sydney", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Initialize_RejectsOverlapAndProtectedPayloadTampering()
    {
        var first = CreateSeed(35.347, -113.878, 0, "America/Phoenix") with
        {
            EffectiveFromUtc = Now.AddDays(-1),
            EffectiveUntilUtc = Now.AddDays(1)
        };
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(first, CancellationToken.None).ConfigureAwait(false);
            var overlapping = CreateSeed(-31.2733, 149.0700, 1165, "Australia/Sydney") with
            {
                EffectiveFromUtc = Now,
                EffectiveUntilUtc = Now.AddDays(2)
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await store.InitializeAsync(overlapping, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        var statePath = Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
        var payload = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        payload[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(statePath, payload).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(first, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private ProtectedDeploymentLocationStore CreateStore()
        => new(
            _options,
            _protector,
            _timeProvider,
            NullLogger<ProtectedDeploymentLocationStore>.Instance);

    private static DeploymentLocationSeed CreateSeed(
        double latitude,
        double longitude,
        double elevation,
        string timeZoneId)
        => new(
            "cameraagent-deployment",
            "operator-local-configuration",
            null,
            null,
            null,
            new ObservatoryLocation(latitude, longitude, elevation, timeZoneId));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => string.Join(',', tags.ToArray().Select(static tag => $"{tag.Key}={tag.Value}"));

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        internal List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventIds.Add(eventId.Id);
            Messages.Add(formatter(state, exception));
        }
    }
}
