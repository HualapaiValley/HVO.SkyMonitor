namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Where one group revision stands: its staged state, any reason the writer stopped, and its catalog state.</summary>
internal sealed record SiloPublicationOutcome(
    string GroupId, string RevisionId, SiloStagedState State, string? BlockedCode, string? CatalogState, string? CatalogOutcome);

/// <summary>
/// Group publication (#1965 5.3). Payloads are qualified first (P1), then the manifest that names only qualified
/// locators (P2), then the <c>publish</c> record that commits it (P3), and only then does the catalog make the revision
/// browsable (P4). Every step resumes after a crash from durable state, replaying exact bytes, so a revision is never
/// visible early, never committed twice, and never names a locator a crash could lose.
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
    private const int MaximumSteps = 8;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ISiloObjectStore Local => journal.Store;

    /// <summary>
    /// Stage a group revision. The same draft again is idempotent. A different draft for the same revision, or an
    /// artifact revision already staged or published with another digest, is a conflict and stages nothing.
    /// </summary>
    public async ValueTask<bool> StageAsync(SiloGroupDraft draft, CancellationToken cancellationToken)
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
                return string.Equals(existing.DraftSha256, sha, StringComparison.Ordinal)
                    ? false
                    : throw new SiloConflictException("silo.revision-conflict",
                        $"Group revision {draft.GroupId}/{draft.RevisionId} is already staged with different content.");
            }
            if (await catalog.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new SiloConflictException("silo.revision-published",
                    $"Group revision {draft.GroupId}/{draft.RevisionId} is already published; a changed fact is a new revision.");
            }
            foreach (var member in draft.Members)
            {
                var identity = new SiloObjectIdentity(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
                if (await catalog.Index.ConflictsWithPublishedAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    throw new SiloConflictException("silo.artifact-revision-conflict",
                        $"Artifact revision {member.ArtifactId}/{member.ArtifactRevision} is published with another digest; it is never replaced.");
                }
            }
            return await state.StageAsync(draft, json, sha, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceLockedAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Startup: replay any pending segment, bring the catalog to the journal tail with P3 recovery, then advance staged
    /// work. The work is bounded by unreleased staged groups and the journal after the cursor (#1965 F5).
    /// </summary>
    public async ValueTask<IReadOnlyList<SiloPublicationOutcome>> ResumeAsync(int limit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupSpoolLockedAsync(cancellationToken).ConfigureAwait(false);
            await journal.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
            await catalog.CatchUpAsync(null, cancellationToken).ConfigureAwait(false);
            var outcomes = new List<SiloPublicationOutcome>();
            foreach (var (groupId, revisionId) in await state.ListOpenAsync(limit, cancellationToken).ConfigureAwait(false))
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
    /// Abandon staged work before P3 and hand the versions it created to retention as orphan candidates. A pending
    /// commit is replayed first, so work that reached P3 is never abandoned.
    /// </summary>
    public async ValueTask AbandonAsync(string groupId, string revisionId, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await journal.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
            await state.AbandonAsync(groupId, revisionId, reason, cancellationToken).ConfigureAwait(false);
            var group = await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(group!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<SiloPublicationOutcome> AdvanceLockedAsync(string groupId, string revisionId, CancellationToken cancellationToken)
    {
        // Verifications this advance performs itself, so live P4 need not repeat them. Scoped to one advance, so the
        // evidence never outlives the work it describes; after an interruption P3 recovery reads the bytes again.
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
                        if (!await QualifyPayloadsAsync(group, fresh, cancellationToken).ConfigureAwait(false))
                        {
                            return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case SiloStagedState.PayloadsQualified:
                        if (!await QualifyManifestAsync(group, fresh, cancellationToken).ConfigureAwait(false))
                        {
                            return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case SiloStagedState.ManifestQualified:
                        await RecordAsync(group, cancellationToken).ConfigureAwait(false);
                        break;
                    case SiloStagedState.Recorded:
                        await IndexAsync(group, fresh, cancellationToken).ConfigureAwait(false);
                        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                    default:
                        return await OutcomeAsync(groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SiloStoreUnavailableException)
            {
                // Nothing is concluded: an uncertain write is retried later with the same exact bytes.
                return await OutcomeAsync(groupId, revisionId, "silo.store-unavailable", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SiloConflictException or SiloStoreNotQualifiedException or SiloChecksumRejectedException)
            {
                var code = exception switch
                {
                    SiloConflictException conflict => conflict.Code,
                    SiloStoreNotQualifiedException => "silo.store-not-qualified",
                    _ => "silo.payload-checksum-rejected"
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

    /// <summary>P1: create, read back and qualify each payload's content-addressed locator from its exact spooled bytes.</summary>
    private async ValueTask<bool> QualifyPayloadsAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        foreach (var member in group.Members.Where(static member => member.VersionId is null))
        {
            var draft = member.Draft;
            var key = SiloObjectNamespace.PayloadKey(draft.RetentionClass, draft.Sha256);
            var created = await SiloCreateOnly.PutVerifiedAsync(Local, key, draft.Length, draft.Sha256,
                token => payloads.OpenAsync(draft.SourceToken, token), null, binding.BindingId, clock,
                () => faults?.Hit(SiloFaultPoint.AfterPayloadPut), cancellationToken).ConfigureAwait(false);
            fresh.Verified(new(Local.Descriptor.StoreId, Local.Descriptor.Bucket, key, created.VersionId), created.VerifiedUtc);
            await state.RecordMemberLocatorAsync(group.Draft.GroupId, group.Draft.RevisionId, member.Ordinal, created.VersionId,
                created.VerifiedUtc, cancellationToken).ConfigureAwait(false);
            faults?.Hit(SiloFaultPoint.AfterPayloadVerified);
        }
        await state.MoveAsync(group.Draft.GroupId, group.Draft.RevisionId, SiloStagedState.Staged, SiloStagedState.PayloadsQualified,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>P2: the manifest names only qualified payload locators; its exact bytes are spooled before the first PUT.</summary>
    private async ValueTask<bool> QualifyManifestAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        var draft = group.Draft;
        byte[] bytes;
        if (group.ManifestSha256 is { } spooledSha)
        {
            bytes = await spool.ReadAsync(SiloObjectSpool.NameOf(spooledSha), spooledSha, cancellationToken).ConfigureAwait(false)
                ?? await RebuildManifestBytesAsync(group, spooledSha, cancellationToken).ConfigureAwait(false)
                ?? throw new SiloConflictException("silo.manifest-spool-lost",
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
        }
        var sha = SiloHash.Sha256(bytes);
        var key = SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId);
        var created = await SiloCreateOnly.PutVerifiedAsync(Local, key, bytes.Length, sha, SiloCreateOnly.FromBytes(bytes),
            SiloFormats.GroupManifest, binding.BindingId, clock, () => faults?.Hit(SiloFaultPoint.AfterManifestPut), cancellationToken)
            .ConfigureAwait(false);
        fresh.Verified(new(Local.Descriptor.StoreId, Local.Descriptor.Bucket, key, created.VersionId), created.VerifiedUtc, bytes);
        await state.RecordManifestQualifiedAsync(draft.GroupId, draft.RevisionId, created.VersionId, created.VerifiedUtc, cancellationToken)
            .ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterManifestQualified);
        return true;
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
        group.Members.Select(member => new SiloExactLocator(Local.Descriptor.StoreId, Local.Descriptor.Bucket,
            SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256),
            member.VersionId ?? throw new InvalidOperationException("A manifest names only qualified locators."))).ToArray();

    /// <summary>P3: commit the publish record, which names the qualified manifest and members with their original qualification.</summary>
    private async ValueTask RecordAsync(SiloStagedGroup group, CancellationToken cancellationToken)
    {
        // A publish segment spooled before a crash commits here, moving this group to recorded exactly once.
        await journal.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
        var current = await state.ReadGroupAsync(group.Draft.GroupId, group.Draft.RevisionId, cancellationToken).ConfigureAwait(false);
        if (current!.State != SiloStagedState.ManifestQualified)
        {
            return;
        }
        var draft = current.Draft;
        var qualified = Local.Descriptor.PersistenceQualified;
        var locators = Locators(current);
        var members = current.Members.Select((member, index) => new SiloPublishedMember(member.Draft.ArtifactId,
            member.Draft.ArtifactRevision, member.Draft.Length, member.Draft.Sha256, member.Draft.Required, locators[index],
            new(member.VerifiedUtc!.Value, SiloFormats.ExactGetSha256, qualified))).ToArray();
        var manifest = new SiloPublishedObject(
            new(Local.Descriptor.StoreId, Local.Descriptor.Bucket, SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId),
                current.ManifestVersionId!),
            current.ManifestLength!.Value, current.ManifestSha256!, new(current.ManifestVerifiedUtc!.Value, SiloFormats.ExactGetSha256, qualified));
        var publish = new SiloPublishRecord(draft.GroupKind, draft.GroupId, draft.RevisionId, manifest, members,
            draft.PreviousRevisionId, draft.Capture);
        await journal.AppendAsync([publish], draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>P4: the catalog indexes the original P3 sequence. Only a visible group releases its spooled bytes.</summary>
    private async ValueTask IndexAsync(SiloStagedGroup group, SiloFreshEvidence fresh, CancellationToken cancellationToken)
    {
        await catalog.CatchUpAsync(fresh, cancellationToken).ConfigureAwait(false);
        var indexed = await catalog.Index.ReadPublicationAsync(group.Draft.GroupId, group.Draft.RevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (indexed is { State: "closed", Outcome: SiloDisposition.TemporarilyUnavailable or SiloDisposition.Unverified })
        {
            // The P3 record stays authoritative; a later successful verification brings the same revision to P4.
            await catalog.RetryClosedAsync(cancellationToken).ConfigureAwait(false);
            indexed = await catalog.Index.ReadPublicationAsync(group.Draft.GroupId, group.Draft.RevisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        faults?.Hit(SiloFaultPoint.AfterIndexApplied);
        if (indexed?.State != "visible")
        {
            return;
        }
        await state.MoveAsync(group.Draft.GroupId, group.Draft.RevisionId, SiloStagedState.Recorded, SiloStagedState.Visible,
            cancellationToken).ConfigureAwait(false);
        await ReleaseAsync(group, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync(SiloStagedGroup group, CancellationToken cancellationToken)
    {
        foreach (var member in group.Members.Where(static member => !member.Released))
        {
            await payloads.ReleaseAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
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
