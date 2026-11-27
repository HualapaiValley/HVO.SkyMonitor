using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Where one group revision stands: its staged state, any reason the writer stopped, and its catalog state.</summary>
internal sealed record SiloPublicationOutcome(
    string GroupId, string RevisionId, SiloStagedState State, string? BlockedCode, string? CatalogState, string? CatalogOutcome);

/// <summary>
/// Group publication (#1965 5.3). Payloads are qualified first (P1), then the manifest that names only qualified
/// locators (P2), then the <c>publish</c> record that commits it (P3), and only then does the catalog make the revision
/// browsable (P4). Before P2 and again before P3 every named version is read back by exact GET; a version lost before
/// the commit is recreated from its retained spooled bytes, and a commit never names a version that is gone. The writer
/// releases spooled bytes only after its own first P4 has verified every required member in that same operation.
/// </summary>
internal sealed class SiloPublisher(
    SiloPublicationState state,
    SiloObjectSpool spool,
    SiloJournalWriter journal,
    SiloCatalog catalog,
    ISiloPayloadSpool payloads,
    SiloStorageBinding binding,
    TimeProvider clock,
    ISiloFaults? faults = null) : IDisposable
{
    internal const string StagedEvidenceMissing = "silo-staged-evidence-missing";
    internal const string StoreUnavailable = "silo.store-unavailable";
    internal const string ManifestSpoolLost = "silo.manifest-spool-lost";
    private const string PayloadChecksumRejected = "silo.payload-checksum-rejected";
    private const int MaximumSteps = 12;
    private const int SweepBatch = 256;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ISiloObjectStore Local => journal.Store;

    /// <summary>
    /// The stable operation key of one logical revision's commit: bounded for every valid group and revision identity,
    /// and unambiguous because the group identity is length-prefixed.
    /// </summary>
    public static string PublishOperation(string groupId, string revisionId) =>
        "publish:" + SiloHash.Sha256(System.Text.Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{groupId.Length}:{groupId}{revisionId}")));

    /// <summary>
    /// Stage a group revision. The same draft again is idempotent. A different draft for the same revision, or an
    /// artifact revision already staged or published with other bytes or other producer and sidecar facts, is a conflict
    /// and stages nothing.
    /// </summary>
    public ValueTask<bool> StageAsync(SiloGroupDraft draft, CancellationToken cancellationToken) =>
        StageAsync(draft, null, cancellationToken);

    /// <summary>Durably stage each source and claim all of them under the same gate used to release staged files.</summary>
    public async ValueTask<bool> StageAsync(SiloGroupDraft draft,
        Func<SiloDraftMember, CancellationToken, ValueTask<Stream>>? openContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        var json = System.Text.Encoding.UTF8.GetString(SiloJson.Serialize(draft));
        var sha = SiloHash.Sha256(System.Text.Encoding.UTF8.GetBytes(json));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                RequireMatchingDraft(draft, existing.DraftSha256, sha);
                if (existing.State == SiloStagedState.Staged && draft.GroupKind == SiloFixtureInventory.GroupKind)
                    payloads.RequireWithinCapacity();
                return false;
            }
            if (await catalog.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new SiloConflictException("silo.revision-published",
                    $"Group revision {draft.GroupId}/{draft.RevisionId} is already published; a changed fact is a new revision.");
            }
            foreach (var member in draft.Members)
            {
                var identity = new SiloObjectIdentity(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
                if (await catalog.Index.ConflictsWithPublishedAsync(identity, member.Facts().Sha256(), cancellationToken).ConfigureAwait(false))
                {
                    throw new SiloConflictException("silo.artifact-revision-conflict",
                        $"Artifact revision {member.ArtifactId}/{member.ArtifactRevision} is published with other bytes or facts; it is never replaced.");
                }
            }
            var stagedTokens = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (openContent is not null)
                {
                    foreach (var member in draft.Members)
                    {
                        await payloads.StageAsync(member, openContent, cancellationToken).ConfigureAwait(false);
                        stagedTokens.Add(member.SourceToken);
                    }
                }
                return await state.StageAsync(draft, json, sha, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (stagedTokens.Count > 0)
                {
                    // State is authoritative if the staging transaction committed before reporting failure.
                    var claimed = await state.ListClaimedPayloadTokensAsync(CancellationToken.None).ConfigureAwait(false);
                    foreach (var token in stagedTokens)
                    {
                        if (!claimed.Contains(token))
                            await payloads.ReleaseAsync(token, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static void RequireMatchingDraft(SiloGroupDraft draft, string existingSha256, string sha256)
    {
        if (!string.Equals(existingSha256, sha256, StringComparison.Ordinal))
            throw new SiloConflictException("silo.revision-conflict",
                $"Group revision {draft.GroupId}/{draft.RevisionId} is already staged with different content.");
    }

    internal static void ValidateDraft(SiloGroupDraft draft)
    {
        try
        {
            var placeholder = new SiloExactLocator("staged", "staged", "staged", "staged");
            SiloJson.ValidateManifest(BuildManifest(draft, new SiloStorageBinding("staged", "staged", "staged"), 1,
                draft.Members.Select(member => placeholder with { Key = SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256) })
                    .ToArray()), "draft " + draft.GroupId + "/" + draft.RevisionId);
            foreach (var member in draft.Members)
            {
                SiloText.RequireText(member.SourceToken, nameof(member.SourceToken), 1024);
            }
            if (draft.Capture is { } capture)
            {
                SiloText.RequireText(capture.AgentId, nameof(capture.AgentId), 256);
                ArgumentOutOfRangeException.ThrowIfNegative(capture.CaptureSequence);
            }
        }
        catch (SiloFormatRefusedException exception)
        {
            throw new ArgumentException(exception.Message, nameof(draft), exception);
        }
    }

    internal static SiloGroupManifest BuildManifest(
        SiloGroupDraft draft, SiloStorageBinding binding, long writerGeneration, IReadOnlyList<SiloExactLocator> locators) =>
        new(SiloFormats.GroupManifest, binding.InstanceId, binding.BindingId, writerGeneration, draft.GroupKind, draft.GroupId,
            draft.RevisionId, draft.PublishedUtc, draft.FromUtc, draft.ToUtc,
            draft.Members.Select((member, index) => new SiloManifestMember(member.ArtifactId, member.ArtifactRevision, member.Role,
                member.RetentionClass, member.MediaType, member.Length, member.Sha256, member.Required, locators[index],
                member.ContentIdentitySha256, member.OutputIdentitySha256, member.Producer, member.RecipeIdentitySha256)).ToArray(),
            draft.Dependencies, draft.Provenance, draft.PreviousRevisionId, draft.CameraId, draft.View);

    /// <summary>Advance one group revision as far as it can go now.</summary>
    public async ValueTask<SiloPublicationOutcome> PublishAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        var started = CameraAgentWorkloadTelemetry.Start();
        var outcome = CameraAgentWorkloadTelemetry.Outcome.Failure;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await AdvanceLockedAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
                outcome = result.State == SiloStagedState.Visible && result.BlockedCode is null
                    ? CameraAgentWorkloadTelemetry.Outcome.Success
                    : CameraAgentWorkloadTelemetry.Outcome.Incomplete;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = CameraAgentWorkloadTelemetry.Outcome.Cancelled;
            throw;
        }
        finally
        {
            CameraAgentWorkloadTelemetry.Record(started, CameraAgentWorkloadTelemetry.Operation.SiloPublish, outcome);
        }
    }

    internal async ValueTask PublishLiveViewAsync(SiloLiveIssueRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = "centered-live:" + record.ChangeOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await journal.AppendAsync([record], operation, cancellationToken).ConfigureAwait(false);
            var end = await catalog.CatchUpAsync(await state.ReadWriterEvidenceAsync(cancellationToken).ConfigureAwait(false), null,
                cancellationToken).ConfigureAwait(false);
            if (end.Reason != SiloPrefixEnd.Tail)
                throw new SiloStoreUnavailableException(binding.StoreId, "index centered live view", writeMayHaveLanded: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Startup: replay any pending segment, bring the catalog to the writer's committed head with P3 recovery, finish any
    /// interrupted release, then advance staged work. The work is bounded by unreleased staged groups and the journal
    /// after the cursor (#1965 F5).
    /// </summary>
    public ValueTask<IReadOnlyList<SiloPublicationOutcome>> ResumeAsync(int limit, CancellationToken cancellationToken) =>
        ResumeAsync(limit, skipRawAdmissions: false, cancellationToken);

    internal async ValueTask<IReadOnlyList<SiloPublicationOutcome>> ResumeAsync(
        int limit, bool skipRawAdmissions, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await payloads.ReconcileAsync(await state.ListClaimedPayloadTokensAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
            // A pending segment is reconciled before any cleanup or new commit.
            await journal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
            await CleanupSpoolLockedAsync(cancellationToken).ConfigureAwait(false);
            var end = await catalog.CatchUpAsync(await state.ReadWriterEvidenceAsync(cancellationToken).ConfigureAwait(false), null,
                cancellationToken).ConfigureAwait(false);
            if (end.Reason == SiloPrefixEnd.SourceUnavailable)
                throw new SiloStoreUnavailableException(binding.StoreId, "recover committed publication", writeMayHaveLanded: false);
            foreach (var (groupId, revisionId) in await state.ListUnreleasedAsync(SweepBatch, cancellationToken).ConfigureAwait(false))
            {
                await ReleaseAsync((await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false))!, cancellationToken)
                    .ConfigureAwait(false);
            }
            var outcomes = new List<SiloPublicationOutcome>();
            foreach (var (groupId, revisionId) in await state.ListOpenAsync(limit, skipRawAdmissions, cancellationToken).ConfigureAwait(false))
            {
                outcomes.Add(await AdvanceLockedAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false));
            }
            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Abandon staged work before P3 and hand the versions it created, or may have created, to retention as orphan
    /// candidates. A pending commit is replayed first, so work that reached P3 is never abandoned.
    /// </summary>
    public async ValueTask AbandonAsync(string groupId, string revisionId, string reason, CancellationToken cancellationToken) =>
        await AbandonCoreAsync(groupId, revisionId, reason, false, cancellationToken).ConfigureAwait(false);

    internal ValueTask<bool> AbandonIfUncommittedAsync(string groupId, string revisionId, string reason,
        CancellationToken cancellationToken) => AbandonCoreAsync(groupId, revisionId, reason, true, cancellationToken);

    private async ValueTask<bool> AbandonCoreAsync(string groupId, string revisionId, string reason, bool allowSettledOrUnstaged,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await journal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
            var staged = await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
            if (allowSettledOrUnstaged && staged?.State is SiloStagedState.Recorded or SiloStagedState.Visible)
                return false;
            if (allowSettledOrUnstaged && staged?.State is null or SiloStagedState.Abandoned)
                return true;
            if (staged is null)
                throw new KeyNotFoundException($"Group revision {groupId}/{revisionId} is not staged.");

            await state.AbandonAsync(groupId, revisionId, reason, await ResolveUnresolvedAsync(staged, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            var group = await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(group!, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<SiloStagedState?> ReconcileGroupStateAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await journal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
            return (await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false))?.State;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<SiloPublicationOutcome> AdvanceLockedAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        // Proof never crosses the commit boundary or outlives its phase. P1 and P2 share the readbacks they just made; P3
        // reads every version it names again, after the manifest was written; the first P4 reads everything again after
        // the commit. After an interruption the bytes are read again.
        var fresh = new SiloFreshEvidence();
        for (var step = 0; step < MaximumSteps; step++)
        {
            var group = await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Group revision {groupId}/{revisionId} is not staged.");
            try
            {
                switch (group.State)
                {
                    case SiloStagedState.Staged:
                        await QualifyPayloadsAsync(group, fresh, cancellationToken).ConfigureAwait(false);
                        break;
                    case SiloStagedState.PayloadsQualified:
                        await QualifyManifestAsync(group, fresh, cancellationToken).ConfigureAwait(false);
                        break;
                    case SiloStagedState.ManifestQualified:
                        await RecordAsync(group, new SiloFreshEvidence(), cancellationToken).ConfigureAwait(false);
                        break;
                    case SiloStagedState.Recorded:
                        await IndexAsync(group, new SiloFreshEvidence(), cancellationToken).ConfigureAwait(false);
                        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                    case SiloStagedState.Visible:
                        await ReleaseAsync(group, cancellationToken).ConfigureAwait(false);
                        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                    default:
                        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
                or SiloConflictException { Code: StagedEvidenceMissing })
            {
                return await OutcomeAsync(groupId, revisionId, StagedEvidenceMissing, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                // Nothing is concluded: an uncertain write is retried later with the same exact bytes.
                return await OutcomeAsync(groupId, revisionId, StoreUnavailable, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SiloConflictException or SiloStoreNotQualifiedException or SiloChecksumRejectedException)
            {
                var code = exception switch
                {
                    SiloConflictException conflict => conflict.Code,
                    SiloStoreNotQualifiedException => "silo.store-not-qualified",
                    _ => PayloadChecksumRejected
                };
                await state.BlockAsync(groupId, revisionId, code, exception.Message, cancellationToken).ConfigureAwait(false);
                return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
            }
        }
        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SiloPublicationOutcome> OutcomeAsync(
        string groupId, string revisionId, string? transientCode, CancellationToken cancellationToken)
    {
        var group = await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
        var indexed = await catalog.Index.ReadPublicationAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
        return new(groupId, revisionId, group!.State, transientCode ?? group.BlockedCode, indexed?.State, indexed?.Outcome);
    }

    internal static SiloExactLocator PayloadLocator(ISiloObjectStore store, SiloDraftMember member, string versionId) =>
        new(store.Descriptor.StoreId, store.Descriptor.Bucket, SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256), versionId);

    internal static async ValueTask<SiloProbe> ProbeQualifiedVersionAsync(ISiloObjectStore store, SiloExactLocator locator,
        long length, string sha256, TimeProvider clock, CancellationToken cancellationToken, bool captureBytes = false)
    {
        var probe = await SiloVerifier.ProbeAsync(store, locator, length, sha256, clock, captureBytes,
            cancellationToken).ConfigureAwait(false);
        return probe.Status switch
        {
            SiloProbeStatus.Verified or SiloProbeStatus.Absent => probe,
            SiloProbeStatus.Damaged => throw new SiloConflictException("silo.qualified-version-damaged",
                $"The qualified version {locator} no longer reads back with its exact bytes ({probe.Detail}); it is never replaced."),
            _ => throw new SiloStoreUnavailableException(store.Descriptor.StoreId, "exact readback of " + locator.Key, writeMayHaveLanded: false)
        };
    }

    private SiloExactLocator ManifestLocator(SiloGroupDraft draft, string manifestSha256, string versionId) =>
        ManifestLocator(Local, draft, manifestSha256, versionId);

    internal static SiloExactLocator ManifestLocator(ISiloObjectStore store, SiloGroupDraft draft, string manifestSha256, string versionId) =>
        new(store.Descriptor.StoreId, store.Descriptor.Bucket,
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, manifestSha256), versionId);

    /// <summary>Create, read back and verify one payload from its exact spooled bytes, recording the attempt first.</summary>
    private async ValueTask<SiloCreated> PutPayloadAsync(
        SiloStagedGroup group, SiloStagedMember member, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var draft = member.Draft;
        await state.MarkMemberPutUnresolvedAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, cancellationToken).ConfigureAwait(false);
        var created = await SiloCreateOnly.PutVerifiedAsync(Local, SiloObjectNamespace.PayloadKey(draft.RetentionClass, draft.Sha256), draft.Length,
            draft.Sha256, token => OpenPayloadAsync(group.Draft.GroupKind, draft, token), null, binding.BindingId, clock,
            () => faults?.Hit(SiloFaultPoint.AfterPayloadPut), cancellationToken).ConfigureAwait(false);
        fresh.Verified(PayloadLocator(Local, draft, created.VersionId), draft.Length, draft.Sha256, created.VerifiedUtc);
        return created;
    }

    private async ValueTask<Stream> OpenPayloadAsync(string groupKind, SiloDraftMember member, CancellationToken cancellationToken)
    {
        var input = await payloads.OpenAsync(member.SourceToken, cancellationToken).ConfigureAwait(false);
        if (groupKind != SiloFixtureInventory.GroupKind)
            return input;

        // A fixture retry must prove local evidence even if the store already has a current object at the key.
        try
        {
            if (!await SiloFilePayloadSpool.MatchesAsync(input, member.Sha256, member.Length, cancellationToken).ConfigureAwait(false))
                throw new SiloConflictException(StagedEvidenceMissing, "A required staged payload differs from its identity.");
            input.Position = 0;
            return input;
        }
        catch
        {
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>P1: create, read back and qualify each payload's content-addressed locator from its exact spooled bytes.</summary>
    private async ValueTask QualifyPayloadsAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        foreach (var member in group.Members.Where(static member => member.VersionId is null))
        {
            var created = await PutPayloadAsync(group, member, fresh, cancellationToken).ConfigureAwait(false);
            await state.SetMemberLocatorAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, created.VersionId, created.VerifiedUtc,
                resolvesPut: true, cancellationToken).ConfigureAwait(false);
            faults?.Hit(SiloFaultPoint.AfterPayloadVerified);
        }
        await state.MoveAsync(group.Draft.GroupId, group.Draft.RevisionId, SiloStagedState.Staged, SiloStagedState.PayloadsQualified,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read every named payload version back now. A version that is gone is recreated from its retained spooled bytes, and
    /// the member is re-named in the same transaction that supersedes any manifest built from the old locator (#1965 5.3).
    /// A version that holds other bytes stops the group. A pending commit of this revision is reconciled before this runs.
    /// </summary>
    private async ValueTask RevalidatePayloadsAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        foreach (var member in group.Members)
        {
            if (member.VersionId is not { } version)
            {
                throw new InvalidOperationException("Every payload is qualified before its manifest is written.");
            }
            var locator = PayloadLocator(Local, member.Draft, version);
            if (fresh.TryGet(locator, member.Draft.Length, member.Draft.Sha256, out _, out _))
            {
                continue;
            }
            var probe = await ProbeQualifiedVersionAsync(Local, locator, member.Draft.Length, member.Draft.Sha256, clock,
                cancellationToken).ConfigureAwait(false);
            switch (probe.Status)
            {
                case SiloProbeStatus.Verified:
                    fresh.Verified(locator, member.Draft.Length, member.Draft.Sha256, probe.ObservedUtc);
                    await state.SetMemberLocatorAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, version, probe.ObservedUtc,
                        resolvesPut: false, cancellationToken).ConfigureAwait(false);
                    break;
                case SiloProbeStatus.Absent:
                    // Resolve an uncertain manifest write before anything changes, so its exact version can be handed over.
                    var resolved = await ResolveUncertainManifestAsync(group, cancellationToken).ConfigureAwait(false);
                    var created = await PutPayloadAsync(group, member, fresh, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(created.VersionId, version, StringComparison.Ordinal))
                    {
                        // The version reads back after all: nothing was replaced.
                        await state.SetMemberLocatorAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, version, created.VerifiedUtc,
                            resolvesPut: true, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    await state.ReplaceMemberLocatorAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, version, created.VersionId,
                        created.VerifiedUtc, resolved, cancellationToken).ConfigureAwait(false);
                    faults?.Hit(SiloFaultPoint.AfterPayloadReplaced);
                    group = (await state.ReadGroupAsync(group.Draft.GroupId, group.Draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
                    break;
            }
        }
    }

    /// <summary>
    /// The exact version an uncertain write of the current manifest left at its digest key, if one is there now. No
    /// committed record can name that key, because it is fixed by bytes that were never committed, so any version found
    /// there is that write's. When none is found a late landing is still possible, and the key alone is handed over.
    /// </summary>
    private async ValueTask<string?> ResolveUncertainManifestAsync(SiloStagedGroup group, CancellationToken cancellationToken) =>
        group is { ManifestPutUnresolved: true, ManifestSha256: { } sha }
            ? await Local.LocateCurrentVersionAsync(
                SiloObjectNamespace.ManifestKey(group.Draft.FromUtc, group.Draft.GroupId, group.Draft.RevisionId, sha), cancellationToken)
                .ConfigureAwait(false)
            : null;

    /// <summary>
    /// The exact versions that unresolved writes of a group's keys left there, as far as they can be resolved now. A key
    /// that cannot be read, or holds nothing, stays unresolved and is handed over as the key itself.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<string, string>> ResolveUnresolvedAsync(SiloStagedGroup group, CancellationToken cancellationToken)
    {
        var keys = group.Members.Where(static member => member.PutUnresolved)
            .Select(static member => SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256)).ToList();
        if (group is { ManifestPutUnresolved: true, ManifestSha256: { } sha })
        {
            keys.Add(SiloObjectNamespace.ManifestKey(group.Draft.FromUtc, group.Draft.GroupId, group.Draft.RevisionId, sha));
        }
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            try
            {
                if (await Local.LocateCurrentVersionAsync(key, cancellationToken).ConfigureAwait(false) is { } version)
                {
                    resolved[key] = version;
                }
            }
            catch (SiloStoreUnavailableException)
            {
                // Unresolved: the key itself is handed over.
            }
        }
        return resolved;
    }

    /// <summary>
    /// P2: the manifest names only payload locators verified now; its exact bytes are spooled before the first PUT, under
    /// the immutable key fixed by those bytes. A manifest superseded by a replaced payload is never reused: the next one
    /// is built from the current locators and so has its own digest key.
    /// </summary>
    private async ValueTask QualifyManifestAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var draft = group.Draft;
        await RevalidatePayloadsAsync(group, fresh, cancellationToken).ConfigureAwait(false);
        group = (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
        byte[] bytes;
        if (group.ManifestSha256 is { } spooledSha)
        {
            bytes = await spool.ReadAsync(SiloObjectSpool.NameOf(spooledSha), spooledSha, cancellationToken).ConfigureAwait(false)
                ?? await RebuildManifestBytesAsync(group, spooledSha, cancellationToken).ConfigureAwait(false)
                ?? throw new SiloConflictException(ManifestSpoolLost,
                    $"The spooled manifest of {draft.GroupId}/{draft.RevisionId} is lost and cannot be reproduced exactly.");
        }
        else
        {
            var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            bytes = SiloJson.Serialize(BuildManifest(draft, binding, head.WriterGeneration, Locators(group)));
            await spool.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await state.RecordManifestBytesAsync(draft.GroupId, draft.RevisionId, SiloHash.Sha256(bytes), bytes.Length, cancellationToken)
                .ConfigureAwait(false);
            faults?.Hit(SiloFaultPoint.AfterManifestSpooled);
            group = (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
        }
        await PutManifestAsync(group, bytes, fresh, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterManifestQualified);
    }

    /// <summary>
    /// Create the manifest once under its digest key, from its exact bytes. A retry after an uncertain write targets the
    /// same key with the same bytes, so a landed earlier attempt is found and named rather than duplicated.
    /// </summary>
    private async ValueTask PutManifestAsync(SiloStagedGroup group, byte[] bytes, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var draft = group.Draft;
        var sha = SiloHash.Sha256(bytes);
        if (!string.Equals(sha, group.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the recorded manifest bytes are written.");
        }
        var key = SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, sha);
        await state.MarkManifestPutUnresolvedAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        var created = await SiloCreateOnly.PutVerifiedAsync(Local, key, bytes.Length, sha, SiloCreateOnly.FromBytes(bytes),
            SiloFormats.GroupManifest, binding.BindingId, clock, () => faults?.Hit(SiloFaultPoint.AfterManifestPut), cancellationToken)
            .ConfigureAwait(false);
        fresh.Verified(ManifestLocator(draft, sha, created.VersionId), bytes.Length, sha, created.VerifiedUtc, bytes);
        await state.SetManifestVersionAsync(draft.GroupId, draft.RevisionId, created.VersionId, created.VerifiedUtc, resolvesPut: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The manifest is a pure function of the draft, binding, generation and locators; rebuild it only if it matches exactly.</summary>
    private async ValueTask<byte[]?> RebuildManifestBytesAsync(SiloStagedGroup group, string expectedSha, CancellationToken cancellationToken)
    {
        var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        for (var generation = head.WriterGeneration; generation >= 1 && generation > head.WriterGeneration - 2; generation--)
        {
            var bytes = SiloJson.Serialize(BuildManifest(group.Draft, binding, generation, Locators(group)));
            if (string.Equals(SiloHash.Sha256(bytes), expectedSha, StringComparison.Ordinal))
            {
                await spool.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                return bytes;
            }
        }
        return null;
    }

    private SiloExactLocator[] Locators(SiloStagedGroup group) =>
        group.Members.Select(member => PayloadLocator(Local, member.Draft,
            member.VersionId ?? throw new InvalidOperationException("A manifest names only qualified locators."))).ToArray();

    /// <summary>
    /// P3: reconcile any uncertain commit first, with its exact bytes, so this revision is never committed twice. Then read
    /// every named version back now; a lost payload supersedes the manifest and returns the group to P2, and a lost
    /// manifest is recreated from its exact spooled bytes under the same digest key. Only then is the publish record
    /// committed, naming exactly the versions verified, with their qualification.
    /// </summary>
    private async ValueTask RecordAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var draft = group.Draft;
        // A pending commit of this revision is reconciled first: one that may have been written commits here with its exact
        // bytes, moving this group to recorded exactly once; one that provably never reached the store is withdrawn.
        await journal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
        var current = (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
        if (current.State != SiloStagedState.ManifestQualified)
        {
            return;
        }
        await RevalidatePayloadsAsync(current, fresh, cancellationToken).ConfigureAwait(false);
        current = (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
        if (current.State != SiloStagedState.ManifestQualified)
        {
            // Superseded: the next step writes the new manifest under its own digest key.
            return;
        }
        var manifest = ManifestLocator(draft, current.ManifestSha256!, current.ManifestVersionId!);
        if (!fresh.TryGet(manifest, current.ManifestLength!.Value, current.ManifestSha256!, out _, out _))
        {
            var probe = await ProbeQualifiedVersionAsync(Local, manifest, current.ManifestLength!.Value, current.ManifestSha256!, clock,
                cancellationToken).ConfigureAwait(false);
            switch (probe.Status)
            {
                case SiloProbeStatus.Verified:
                    fresh.Verified(manifest, current.ManifestLength!.Value, current.ManifestSha256!, probe.ObservedUtc);
                    await state.SetManifestVersionAsync(draft.GroupId, draft.RevisionId, manifest.VersionId, probe.ObservedUtc, resolvesPut: false,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case SiloProbeStatus.Absent:
                    var bytes = await spool.ReadAsync(SiloObjectSpool.NameOf(current.ManifestSha256!), current.ManifestSha256!, cancellationToken)
                        .ConfigureAwait(false)
                        ?? await RebuildManifestBytesAsync(current, current.ManifestSha256!, cancellationToken).ConfigureAwait(false)
                        ?? throw new SiloConflictException(ManifestSpoolLost,
                            $"The manifest of {draft.GroupId}/{draft.RevisionId} is lost and cannot be reproduced exactly.");
                    await PutManifestAsync(current, bytes, fresh, cancellationToken).ConfigureAwait(false);
                    break;
            }
            current = (await state.ReadGroupAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false))!;
        }
        var qualified = Local.Descriptor.PersistenceQualified;
        var locators = Locators(current);
        var members = current.Members.Select((member, index) => new SiloPublishedMember(member.Draft.ArtifactId,
            member.Draft.ArtifactRevision, member.Draft.Length, member.Draft.Sha256, member.Draft.Facts().Sha256(), member.Draft.Required,
            locators[index], new(member.VerifiedUtc!.Value, SiloFormats.ExactGetSha256, qualified))).ToArray();
        var published = new SiloPublishedObject(ManifestLocator(draft, current.ManifestSha256!, current.ManifestVersionId!), current.ManifestLength!.Value,
            current.ManifestSha256!, new(current.ManifestVerifiedUtc!.Value, SiloFormats.ExactGetSha256, qualified));
        var publish = new SiloPublishRecord(draft.GroupKind, draft.GroupId, draft.RevisionId, published, members,
            draft.PreviousRevisionId, draft.Capture);
        await journal.AppendAsync([publish], PublishOperation(draft.GroupId, draft.RevisionId), draft.GroupId, draft.RevisionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The writer's first P4: bring the catalog to the committed head, then confirm this revision by P3 recovery in this
    /// operation. Only a confirmed revision becomes visible to the writer and releases its spooled bytes.
    /// </summary>
    private async ValueTask IndexAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        if (group.Draft.GroupKind == SiloFixtureInventory.GroupKind)
            await VerifyRecordedFixtureAsync(group, fresh, cancellationToken).ConfigureAwait(false);
        await catalog.CatchUpAsync(await state.ReadWriterEvidenceAsync(cancellationToken).ConfigureAwait(false), fresh, cancellationToken)
            .ConfigureAwait(false);
        var confirmed = await catalog.ConfirmP4Async(group.Draft.GroupId, group.Draft.RevisionId, fresh, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterIndexApplied);
        if (confirmed != "visible")
        {
            return;
        }
        await state.MoveAsync(group.Draft.GroupId, group.Draft.RevisionId, SiloStagedState.Recorded, SiloStagedState.Visible,
            cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterVisibleCommitted);
        await ReleaseAsync(group, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask VerifyRecordedFixtureAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var missingPayload = false;
        foreach (var member in group.Members)
        {
            var draft = member.Draft;
            var locator = PayloadLocator(Local, draft, member.VersionId!);
            var probe = await ProbeQualifiedVersionAsync(Local, locator, draft.Length, draft.Sha256, clock, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Status == SiloProbeStatus.Absent)
            {
                var staged = await OpenPayloadAsync(group.Draft.GroupKind, draft, cancellationToken).ConfigureAwait(false);
                await staged.DisposeAsync().ConfigureAwait(false);
                missingPayload = true;
            }
            else
            {
                fresh.Verified(locator, draft.Length, draft.Sha256, probe.ObservedUtc);
            }
        }

        var manifest = ManifestLocator(group.Draft, group.ManifestSha256!, group.ManifestVersionId!);
        var length = group.ManifestLength!.Value;
        var proof = await ProbeQualifiedVersionAsync(Local, manifest, length, group.ManifestSha256!, clock, cancellationToken, captureBytes: true)
            .ConfigureAwait(false);
        // First P4 must use the exact locators sealed by P3, even if staged bytes could create a new version.
        if (missingPayload || proof.Status == SiloProbeStatus.Absent)
            throw new SiloConflictException(SiloFixtureImport.ReopenFailed, "An exact version named by the committed fixture is missing.");
        fresh.Verified(manifest, length, group.ManifestSha256!, proof.ObservedUtc, proof.Bytes);
    }

    /// <summary>Release every unreleased member and the manifest spool. Idempotent: a resume finishes an interrupted release.</summary>
    private async ValueTask ReleaseAsync(SiloStagedGroup group, CancellationToken cancellationToken)
    {
        foreach (var member in group.Members.Where(static member => !member.Released))
        {
            // Staging and release share the publisher gate. Each member gives up its claim, but only the final
            // claimant unlinks the bytes. If interrupted between unlink and mark, the unlink is safe to retry.
            if (!await state.HasOtherPayloadOwnerAsync(group.Draft.GroupId, group.Draft.RevisionId, member, cancellationToken)
                .ConfigureAwait(false))
            {
                await payloads.ReleaseAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
            }
            await state.MarkReleasedAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, cancellationToken).ConfigureAwait(false);
        }
        if (group.ManifestSha256 is { } sha)
        {
            spool.Delete(SiloObjectSpool.NameOf(sha));
        }
    }

    /// <summary>Remove spool files no pending segment or open publication still needs, without racing an append.</summary>
    private async ValueTask<int> CleanupSpoolLockedAsync(CancellationToken cancellationToken) =>
        await journal.ExclusiveAsync(async () =>
        {
            var referenced = await state.ReferencedSpoolNamesAsync(cancellationToken).ConfigureAwait(false);
            var removed = 0;
            foreach (var name in spool.Names().Where(name => !referenced.Contains(name)))
            {
                spool.Delete(name);
                removed++;
            }
            return removed;
        }, cancellationToken).ConfigureAwait(false);

    public void Dispose() => _gate.Dispose();
}
