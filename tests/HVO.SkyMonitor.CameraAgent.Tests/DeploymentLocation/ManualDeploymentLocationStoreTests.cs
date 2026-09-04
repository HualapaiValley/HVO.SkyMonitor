using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

/// <summary>
/// Covers the audited local manual coordinate contract: append-at-restart semantics, provenance
/// preservation, expected-version conflict, idempotent replay, configuration precedence, central
/// reconciliation, and the rollback-safe separation from the protected history document.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ManualDeploymentLocationStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-07-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
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

    private string _root = null!;
    private DataProtectionDeploymentLocationProtector _protector = null!;
    private IOptions<CameraAgentHostOptions> _options = null!;
    private MutableTimeProvider _timeProvider = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-manual-location-tests", Guid.NewGuid().ToString("N"));
        var keyDirectory = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keyDirectory);
        _protector = new DataProtectionDeploymentLocationProtector(DataProtectionProvider.Create(keyDirectory));
        _options = CreateOptions(CentralIntegrationMode.Disabled);
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
    public async Task ApplyManualAsync_AppendsTheNewVersionOnlyAtTheNextStartAndKeepsEarlierProvenance()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        CaptureLocationProvenance seededProvenance;
        using (var store = CreateStore())
        {
            var seeded = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            seededProvenance = seeded.ToProvenance();
            Assert.AreEqual(1L, seeded.Version);

            var result = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ManualDeploymentLocationStatus.Applied, result.Status);
            Assert.AreEqual(2L, result.State.NextVersion);
            Assert.IsTrue(result.State.Override!.PendingRestart);
            // The live snapshot every capture is stamped with must not move before the restart boundary.
            Assert.AreEqual(seededProvenance, store.Active!.ToProvenance());
            Assert.AreEqual(35.347, store.Active.LatitudeDegrees);
        }

        _timeProvider.UtcNow = Now.AddSeconds(1);
        using var restarted = CreateStore();
        var activated = await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2L, activated.Version);
        Assert.AreEqual(-31.2733, activated.LatitudeDegrees);
        Assert.AreEqual("Australia/Sydney", activated.TimeZoneId);
        Assert.AreEqual(ManualDeploymentLocationContract.SourceLabel, activated.Source);
        Assert.AreEqual(DeploymentLocationSourceKind.Manual, restarted.ResolveSourceKind(activated));
        // Captures bound to version one still resolve against their own immutable version.
        Assert.AreEqual(seededProvenance, restarted.Resolve(seededProvenance, Now).ToProvenance());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            restarted.Resolve(seededProvenance, Now.AddSeconds(1)));
        Assert.IsFalse(restarted.Manual.Override!.PendingRestart);
    }

    [TestMethod]
    public async Task ApplyManualAsync_LeavesTheProtectedHistoryDocumentSchemaRollbackCompatible()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var restarted = CreateStore())
        {
            _ = await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }

        var canonical = JsonNode.Parse(_protector.Unprotect(
            await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false)))!.AsObject();
        CollectionAssert.AreEquivalent(
            ExpectedCanonicalHistoryProperties,
            canonical.Select(static pair => pair.Key).ToArray());
        Assert.AreEqual("Manual", canonical["sourceKinds"]!["2"]!.GetValue<string>());
        // The seed a rolled-back baseline validates against must match its own latest snapshot.
        Assert.AreEqual(
            "Australia/Sydney",
            canonical["configurationSeed"]!["coordinates"]!["timeZoneId"]!.GetValue<string>());

        var manualPayload = await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false);
        var manualText = Encoding.UTF8.GetString(manualPayload);
        Assert.IsFalse(manualText.Contains("149.07", StringComparison.Ordinal));
        Assert.IsFalse(manualText.Contains("Australia/Sydney", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApplyManualAsync_ReplaysTheSameKeyAndRejectsThatKeyWithDifferentCoordinates()
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);
        var first = await store.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        var payload = await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false);

        var replay = await store.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        var reused = await store.ApplyManualAsync(
            Request(10, 20, 30, "UTC", expectedVersion: 1, expectedManualSequence: 1),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Applied, first.Status);
        Assert.AreEqual(ManualDeploymentLocationStatus.Replayed, replay.Status);
        Assert.AreEqual(ManualDeploymentLocationStatus.Conflict, reused.Status);
        Assert.AreEqual(
            ManualDeploymentLocationContract.IdempotencyKeyConflictReasonCode, reused.ReasonCode);
        Assert.HasCount(1, store.Manual.History);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ApplyManualAsync_RejectsAStaleExpectedVersionWithoutWritingState()
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);

        var result = await store.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 7, key: "stale"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Conflict, result.Status);
        Assert.AreEqual(ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode, result.ReasonCode);
        Assert.AreEqual(1L, result.State.KnownVersion);
        Assert.IsFalse(File.Exists(ManualPath()));
        Assert.IsNull(store.Manual.Override);
    }

    [TestMethod]
    public async Task ApplyManualAsync_TreatsUnchangedCoordinatesAsNoNewVersion()
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);

        var result = await store.ApplyManualAsync(
            Request(35.347, -113.878, 0, "America/Phoenix", expectedVersion: 1, key: "same"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Unchanged, result.Status);
        Assert.IsFalse(File.Exists(ManualPath()));
        Assert.IsEmpty(store.Manual.History);
    }

    [TestMethod]
    [DataRow(91d, 0d, 0d, "America/Phoenix", "location.latitudeDegrees")]
    [DataRow(0d, 181d, 0d, "America/Phoenix", "location.longitudeDegrees")]
    [DataRow(0d, 0d, 40000d, "America/Phoenix", "location.elevationMeters")]
    [DataRow(0d, 0d, -900d, "America/Phoenix", "location.elevationMeters")]
    [DataRow(0d, 0d, 0d, "Not/A/Zone", "location.timeZoneId")]
    [DataRow(0d, 0d, 0d, "MST", "location.timeZoneId")]
    public async Task ApplyManualAsync_RejectsOutOfRangeOrUnresolvableEntries(
        double latitude,
        double longitude,
        double elevation,
        string timeZoneId,
        string fieldPath)
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);

        var result = await store.ApplyManualAsync(
            Request(latitude, longitude, elevation, timeZoneId, expectedVersion: 1, key: "invalid"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Invalid, result.Status);
        Assert.AreEqual(fieldPath, result.FieldPath);
        Assert.IsFalse(File.Exists(ManualPath()));
    }

    [TestMethod]
    [DataRow("", "key-1")]
    [DataRow("owner-1", "")]
    public async Task ApplyManualAsync_RejectsAnUnusableCommandIdentity(string actor, string key)
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);

        var result = await store.ApplyManualAsync(
            Request(10, 20, 30, "UTC", expectedVersion: 1, key: key) with { Actor = actor },
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Invalid, result.Status);
        Assert.AreEqual(ManualDeploymentLocationContract.InvalidCommandReasonCode, result.ReasonCode);
        Assert.IsFalse(File.Exists(ManualPath()));
    }

    [TestMethod]
    public async Task ApplyManualAsync_AfterAnActivatedEntry_AppendsASecondVersionFromTheNewExpectedVersion()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var activated = CreateStore())
        {
            var second = await activated.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, second.Version);
            Assert.AreEqual(2L, activated.Manual.KnownVersion);

            var stale = await activated.ApplyManualAsync(
                Request(
                    19.82, -155.47, 4200, "Pacific/Honolulu",
                    expectedVersion: 1, key: "second-stale", expectedManualSequence: 1),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ManualDeploymentLocationStatus.Conflict, stale.Status);
            Assert.AreEqual(
                ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode, stale.ReasonCode);

            var applied = await activated.ApplyManualAsync(
                Request(
                    19.82, -155.47, 4200, "Pacific/Honolulu",
                    expectedVersion: 2, key: "second", expectedManualSequence: 1),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ManualDeploymentLocationStatus.Applied, applied.Status);
            Assert.HasCount(2, applied.State.History);
            // The projection is newest first.
            Assert.AreEqual("Pacific/Honolulu", applied.State.History[0].TimeZoneId);
            Assert.AreEqual("Australia/Sydney", applied.State.History[1].TimeZoneId);
        }

        _timeProvider.UtcNow = Now.AddSeconds(2);
        using var restarted = CreateStore();
        var third = await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3L, third.Version);
        Assert.AreEqual("Pacific/Honolulu", third.TimeZoneId);
        Assert.AreEqual(DeploymentLocationSourceKind.Manual, restarted.ResolveSourceKind(third));
    }

    [TestMethod]
    public async Task ManualOverride_GovernsRestartsUntilTheConfigurationSeedItselfChanges()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var restarted = CreateStore())
        {
            var activated = await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, activated.Version);
        }
        // An unchanged configuration seed must not revert the operator's entry.
        _timeProvider.UtcNow = Now.AddSeconds(2);
        using (var stable = CreateStore())
        {
            var same = await stable.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, same.Version);
            Assert.AreEqual("Australia/Sydney", same.TimeZoneId);
            Assert.IsNotNull(stable.Manual.Override);
        }

        _timeProvider.UtcNow = Now.AddSeconds(3);
        using var reconfigured = CreateStore();
        var configured = await reconfigured.InitializeAsync(
            CreateSeed(20.5, 30.5, 400, "UTC"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3L, configured.Version);
        Assert.AreEqual("UTC", configured.TimeZoneId);
        Assert.IsNull(reconfigured.Manual.Override);
        Assert.IsNotNull(reconfigured.Manual.OverrideSupersededAtUtc);
        // The audit history survives supersession.
        Assert.HasCount(1, reconfigured.Manual.History);
    }

    [TestMethod]
    public async Task ApplyManualAsync_WithCentralIntegration_ProposesTheVersionAsACandidateUntilAcknowledged()
    {
        _options = CreateOptions(CentralIntegrationMode.Enabled);
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            var result = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ManualDeploymentLocationStatus.Applied, result.Status);
            Assert.IsTrue(result.State.CentralAcknowledgementRequired);
        }

        _timeProvider.UtcNow = Now.AddSeconds(1);
        using var proposing = CreateStore();
        var active = await proposing.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);

        // Central integration keeps local geometry stable and exposes the manual entry for proposal.
        Assert.AreEqual(1L, active.Version);
        Assert.AreEqual(2L, proposing.Candidate!.Version);
        Assert.AreEqual("Australia/Sydney", proposing.Candidate.TimeZoneId);
        Assert.AreEqual(DeploymentLocationSourceKind.Manual, proposing.ResolveSourceKind(proposing.Candidate));
        Assert.IsTrue(proposing.Manual.CandidateAwaitingAcknowledgement);
        Assert.AreEqual(2L, proposing.Manual.KnownVersion);

        await proposing.StageAsync(proposing.Candidate, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, proposing.Active!.Version);
        Assert.IsTrue(proposing.Manual.StagedAcknowledgementPending);

        _timeProvider.UtcNow = Now.AddSeconds(2);
        using var activated = CreateStore();
        var acknowledged = await activated.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2L, acknowledged.Version);
        Assert.AreEqual("Australia/Sydney", acknowledged.TimeZoneId);
        Assert.IsFalse(activated.Manual.StagedAcknowledgementPending);
    }

    [TestMethod]
    public async Task ApplyManualAsync_WithADifferentKeyAndTheSameExpectedVersion_ConflictsOnTheManualSequence()
    {
        using var store = CreateStore();
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);
        var first = await store.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1, key: "first"),
            CancellationToken.None).ConfigureAwait(false);

        // A second operator holding the same page state carries the stale manual sequence. The history
        // version cannot detect it, because the command never touches the history.
        var competing = await store.ApplyManualAsync(
            Request(19.82, -155.47, 4200, "Pacific/Honolulu", expectedVersion: 1, key: "second"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Applied, first.Status);
        Assert.AreEqual(1L, first.State.ManualSequence);
        Assert.AreEqual(ManualDeploymentLocationStatus.Conflict, competing.Status);
        Assert.AreEqual(
            ManualDeploymentLocationContract.ExpectedManualSequenceConflictReasonCode, competing.ReasonCode);
        Assert.AreEqual("Australia/Sydney", store.Manual.Override!.TimeZoneId);
        Assert.HasCount(1, store.Manual.History);
    }

    [TestMethod]
    public async Task ApplyManualAsync_ReplayedAfterSupersession_ConflictsInsteadOfReportingSuccess()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using var reconfigured = CreateStore();
        _ = await reconfigured.InitializeAsync(
            CreateSeed(20.5, 30.5, 400, "UTC"), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(reconfigured.Manual.OverrideSupersededAtUtc);

        var replay = await reconfigured.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 2),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualDeploymentLocationStatus.Conflict, replay.Status);
        Assert.AreEqual(ManualDeploymentLocationContract.SupersededEntryReasonCode, replay.ReasonCode);
        Assert.IsNull(reconfigured.Manual.Override);
    }

    [TestMethod]
    public async Task ApplyManualAsync_WhileACentralAcknowledgementIsStaged_ProposesTheEntryAsItsSuccessor()
    {
        _options = CreateOptions(CentralIntegrationMode.Enabled);
        var configured = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        DeploymentLocationSnapshot staged;
        using (var proposing = CreateStore())
        {
            _ = await proposing.InitializeAsync(configured, CancellationToken.None).ConfigureAwait(false);
            _ = await proposing.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1, key: "first"),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var acknowledged = CreateStore())
        {
            _ = await acknowledged.InitializeAsync(configured, CancellationToken.None).ConfigureAwait(false);
            staged = acknowledged.Candidate!;
            await acknowledged.StageAsync(staged, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(acknowledged.Manual.StagedAcknowledgementPending);

            // A second entry made while the acknowledgement is staged must not disturb it.
            var second = await acknowledged.ApplyManualAsync(
                Request(
                    19.82, -155.47, 4200, "Pacific/Honolulu",
                    expectedVersion: 2, key: "second", expectedManualSequence: 1),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ManualDeploymentLocationStatus.Applied, second.Status);
            Assert.AreEqual(1L, acknowledged.Active!.Version);
            Assert.AreEqual(2L, second.State.PendingVersion);
        }

        _timeProvider.UtcNow = Now.AddSeconds(2);
        using var restarted = CreateStore();
        var active = await restarted.InitializeAsync(configured, CancellationToken.None).ConfigureAwait(false);

        // The acknowledged version activates first; the newer entry becomes its proposed successor.
        Assert.AreEqual(2L, active.Version);
        Assert.AreEqual(staged.CanonicalSha256, active.CanonicalSha256);
        Assert.AreEqual("Australia/Sydney", active.TimeZoneId);
        Assert.AreEqual(3L, restarted.Candidate!.Version);
        Assert.AreEqual("Pacific/Honolulu", restarted.Candidate.TimeZoneId);
        Assert.AreEqual(2L, restarted.Manual.ActiveVersion);
        Assert.AreEqual(3L, restarted.Manual.KnownVersion);
        Assert.IsTrue(restarted.Manual.CandidateAwaitingAcknowledgement);
    }

    [TestMethod]
    public async Task ManualOverride_SupersessionIsIdempotentAcrossRepeatedStarts()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        var reconfigured = CreateSeed(20.5, 30.5, 400, "UTC");
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var first = CreateStore())
        {
            _ = await first.InitializeAsync(reconfigured, CancellationToken.None).ConfigureAwait(false);
        }
        var supersededPayload = await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false);

        _timeProvider.UtcNow = Now.AddSeconds(2);
        using var second = CreateStore();
        var unchanged = await second.InitializeAsync(reconfigured, CancellationToken.None).ConfigureAwait(false);

        // A superseded record is never rewritten again, so the deferred write is safe to retry.
        // The manual entry never activated, so the configured seed appended version two directly.
        Assert.AreEqual(2L, unchanged.Version);
        Assert.AreEqual("UTC", unchanged.TimeZoneId);
        CollectionAssert.AreEqual(
            supersededPayload, await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Initialize_AfterAManualActivation_AppendsAConfiguredVersionExactlyAsABaselineWould()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        _timeProvider.UtcNow = Now.AddSeconds(1);
        using (var activated = CreateStore())
        {
            Assert.AreEqual(
                2L, (await activated.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).Version);
        }

        // A rolled-back baseline never reads the sidecar, so remove it and reconcile the configured seed
        // against the manual-activated history exactly as that baseline would.
        File.Delete(ManualPath());
        _timeProvider.UtcNow = Now.AddSeconds(2);
        using var baseline = CreateStore();
        var reverted = await baseline.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3L, reverted.Version);
        Assert.AreEqual("America/Phoenix", reverted.TimeZoneId);
        Assert.AreEqual(35.347, reverted.LatitudeDegrees);
        Assert.IsNull(baseline.Manual.Override);
        Assert.IsEmpty(baseline.Manual.History);
    }

    [TestMethod]
    public async Task Initialize_RejectsATamperedManualRecordWithoutMutatingState()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        var historyPayload = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
        var manual = JsonNode.Parse(_protector.Unprotect(
            await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false)))!.AsObject();
        manual["seed"]!["coordinates"]!["latitudeDegrees"] = 12.5;
        await File.WriteAllBytesAsync(
            ManualPath(), _protector.Protect(Encoding.UTF8.GetBytes(manual.ToJsonString()))).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsNull(restarted.Active);
        CollectionAssert.AreEqual(historyPayload, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Initialize_RejectsAManualRecordProtectedByAnotherKey()
    {
        var seed = CreateSeed(35.347, -113.878, 0, "America/Phoenix");
        using (var store = CreateStore())
        {
            _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ApplyManualAsync(
                Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        var payload = await File.ReadAllBytesAsync(ManualPath()).ConfigureAwait(false);
        payload[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(ManualPath(), payload).ConfigureAwait(false);

        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyManualAsync_EmitsBoundedSignalsWithoutCoordinates()
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
        _ = await store.InitializeAsync(
            CreateSeed(35.347, -113.878, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);

        _ = await store.ApplyManualAsync(
            Request(-31.2733, 149.07, 1165, "Australia/Sydney", expectedVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        _ = await store.ApplyManualAsync(
            Request(0, 0, 0, "Not/A/Zone", expectedVersion: 2, key: "bad"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(measurements.Any(item =>
            item.Name == "skymonitor.cameraagent.deployment_location.operations" &&
            item.Tags.Contains("operation=manual", StringComparison.Ordinal) &&
            item.Tags.Contains("outcome=applied", StringComparison.Ordinal)));
        Assert.IsTrue(measurements.Any(item =>
            item.Tags.Contains("operation=manual", StringComparison.Ordinal) &&
            item.Tags.Contains("outcome=invalid", StringComparison.Ordinal)));
        Assert.HasCount(2, stopped.Where(static activity =>
            activity.OperationName == "deployment-location.manual"));
        Assert.Contains(7305, logger.EventIds);
        var signals = string.Join('\n', measurements.Select(static item => $"{item.Name}:{item.Tags}")) +
            string.Join('\n', logger.Messages) +
            string.Join('\n', stopped.SelectMany(static activity => activity.TagObjects)
                .Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.IsFalse(signals.Contains("-31.2733", StringComparison.Ordinal));
        Assert.IsFalse(signals.Contains("149.07", StringComparison.Ordinal));
        Assert.IsFalse(signals.Contains("Australia/Sydney", StringComparison.Ordinal));
        Assert.IsFalse(signals.Contains("owner-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApplyManualAsync_BeforeInitializationIsRejected()
    {
        using var store = CreateStore();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.ApplyManualAsync(
                Request(10, 20, 30, "UTC", expectedVersion: 1), CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static ManualDeploymentLocationRequest Request(
        double latitude,
        double longitude,
        double elevation,
        string timeZoneId,
        long expectedVersion,
        string key = "key-1",
        long expectedManualSequence = 0)
        => new(
            latitude,
            longitude,
            elevation,
            timeZoneId,
            expectedVersion,
            expectedManualSequence,
            key,
            "owner-1",
            "relocated");

    private string HistoryPath()
        => Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");

    private string ManualPath()
        => Path.Combine(_options.Value.RawIngressRoot, ".location", "manual-deployment-location.v1.protected");

    private IOptions<CameraAgentHostOptions> CreateOptions(CentralIntegrationMode mode)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(_root, "data"),
            CentralIntegration = new CentralIntegrationOptions { Mode = mode }
        });

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

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => string.Join(',', tags.ToArray().Select(static tag => $"{tag.Key}={tag.Value}"));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

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
