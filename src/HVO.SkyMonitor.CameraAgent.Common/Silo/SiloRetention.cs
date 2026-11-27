using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>The managed-copy writer. Every decision runs against a caught-up catalog while publication is excluded.</summary>
internal sealed class SiloRetention(
    SiloPublicationState state, SiloCatalogIndex index, SiloPublisher publisher, SiloCatalog catalog,
    SiloJournalWriter journal, PhysicalRoot root, ISiloObjectStore local, ISiloDiskBudget budget, TimeProvider clock)
    : ISiloObjectLeases
{
    private const int MaximumSharedRoles = 128;
    private const int MaximumLeases = 64;
    private const string LocalCustody = "cameraagent";
    private const string Expire = "expire-role";
    private const string Evict = "verify-survivor";
    private const int MaximumPendingSurvivors = 128;
    private int _orphanOffset;
    private const string Abandoned = "confirmed-abandoned";

    // Called under the publication gate, including on process restart before any P1–P4 work resumes.
    internal SiloExactLocator? ReadUnsettledTarget() =>
        SiloRetentionState.Read(root).Attempt is { DispositionCommitted: false } attempt ? attempt.Target : null;

    internal ValueTask SetPolicyAsync(SiloRetentionPolicy policy, CancellationToken cancellationToken) =>
        WriteControlAsync(policy.ToRecord(), cancellationToken);

    internal ValueTask SetPinAsync(string pinId, SiloRoleKey role, bool active, string reason, CancellationToken cancellationToken) =>
        WriteControlAsync(new SiloPinRecord(pinId, SiloJson.EnumName(role.Kind), role.Id, role.Revision, active, reason), cancellationToken);

    private async ValueTask WriteControlAsync(SiloJournalRecord record, CancellationToken cancellationToken) =>
        await publisher.WithPublicationLockAsync(async () =>
        {
            if (ReadUnsettledTarget() is not null)
                throw new InvalidOperationException("Silo retention must settle an outstanding delete before changing its controls.");
            return await AppendAsync([record], "retention-control:" + Guid.NewGuid().ToString("N"), cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    internal ValueTask<IReadOnlyList<SiloRetentionCandidate>> DryRunAsync(DateTimeOffset now, bool underPressure,
        IReadOnlyList<SiloRetentionHold> holds, CancellationToken cancellationToken) =>
        publisher.WithPublicationLockAsync(async () =>
        {
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
            var policy = await index.ReadRetentionPolicyAsync(cancellationToken).ConfigureAwait(false);
            return await PlanAsync(policy, SiloRetentionState.Read(root), now, underPressure, holds, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Age and pressure sweeps share this entry point with future schedule and sky-condition selectors.</summary>
    internal async ValueTask<string> SweepAsync(bool canStartDelete, Func<CancellationToken, ValueTask<IReadOnlyList<SiloRetentionHold>>> readHolds,
        ISiloRetentionArchive? archive, CancellationToken cancellationToken,
        Func<SiloExactLocator, bool>? select = null, Func<CancellationToken, ValueTask>? cleanExpiredCache = null,
        Func<SiloExactLocator, bool>? verifiedStoreExit = null, Func<bool>? canIssue = null,
        SemaphoreSlim? lifecycle = null, Func<IDisposable?>? enterMaintenance = null)
    {
        await publisher.RetentionSweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SweepLocalAsync(canStartDelete, readHolds, archive, select, cleanExpiredCache,
                verifiedStoreExit, canIssue, lifecycle, enterMaintenance, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            publisher.RetentionSweepGate.Release();
        }
    }

    private async ValueTask<string> SweepLocalAsync(bool canStartDelete,
        Func<CancellationToken, ValueTask<IReadOnlyList<SiloRetentionHold>>> readHolds, ISiloRetentionArchive? archive,
        Func<SiloExactLocator, bool>? select, Func<CancellationToken, ValueTask>? cleanExpiredCache,
        Func<SiloExactLocator, bool>? verifiedStoreExit, Func<bool>? canIssue, SemaphoreSlim? lifecycle,
        Func<IDisposable?>? enterMaintenance, CancellationToken cancellationToken)
    {
        if (SiloRetentionState.Read(root).Attempt is { } outstanding)
            return await ReconcileAsync(outstanding, verifiedStoreExit, cancellationToken).ConfigureAwait(false);

        if (!canStartDelete || local is not ISiloVersionRemoval)
            return "held";

        if (cleanExpiredCache is not null)
            await cleanExpiredCache(cancellationToken).ConfigureAwait(false);
        await publisher.CleanupRetentionSpoolAsync(enterMaintenance, cancellationToken).ConfigureAwait(false);

        var authorityReady = await publisher.WithPublicationLockAsync(async () =>
        {
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
            return (await index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false)).CompleteThroughPrefixEnd;
        }, cancellationToken).ConfigureAwait(false);
        if (!authorityReady) return "catalog-authority-incomplete";
        if ((await index.ReadRetentionPolicyAsync(cancellationToken).ConfigureAwait(false)).ExpiryHeld) return "held";

        await ResolveOrphansAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SiloRetentionHold> snapshot;
        if (lifecycle is not null) await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = await readHolds(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle?.Release();
        }

        if (budget.ObserveReclamation() is not { } physical) return "held";
        var (status, candidates) = await publisher.WithPublicationLockAsync<(string, IReadOnlyList<SiloRetentionCandidate>)>(async () =>
        {
            var saved = SiloRetentionState.Read(root);
            var reclaim = SiloReclaimLedger.Observe(saved.ReclaimLedger, physical, clock.GetUtcNow());
            if (reclaim != saved.Reclaim || saved.ReclaimPending is not null)
            {
                saved = saved with { Reclaim = reclaim, ReclaimPending = null };
                await saved.WriteAsync(root, cancellationToken).ConfigureAwait(false);
            }
            if (!SiloReclaimLedger.CanDelete(reclaim, physical, budget.ReclaimCaptureHourBytes, 0, clock.GetUtcNow()))
                return ("awaiting-physical-reclamation", []);

            var policy = await index.ReadRetentionPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (policy.ExpiryHeld) return ("held", []);
            if (!(await index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false)).CompleteThroughPrefixEnd)
                return ("catalog-authority-incomplete", []);
            return ("no-eligible-versions", await PlanAsync(policy, saved, clock.GetUtcNow(),
                budget.AdmissionRelease is not null, snapshot, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (candidate.Decision is not (Expire or Evict or Abandoned) || select?.Invoke(candidate.Locator) == false)
                continue;
            var before = budget.ObserveReclamation();
            if (before is null) return "held";
            var allocation = budget.ReadExactAllocationBytes(candidate.Locator);
            var pending = SiloRetentionState.Read(root);
            if (!SiloReclaimLedger.CanDelete(pending.Reclaim, before.Value, budget.ReclaimCaptureHourBytes, allocation, clock.GetUtcNow()))
            {
                status = "awaiting-physical-reclamation";
                continue;
            }
            var attempt = await PrepareAsync(candidate, before.Value, allocation,
                pending.SurvivorReleases.Count < MaximumPendingSurvivors ? archive : null, cancellationToken).ConfigureAwait(false);
            if (attempt is null) continue;
            var fenced = await TryFenceAsync(candidate, attempt, readHolds, canIssue, lifecycle, enterMaintenance, cancellationToken)
                .ConfigureAwait(false);
            if (fenced is null)
            {
                if (attempt.Survivors.Count > 0)
                    await ReleaseUnfencedSurvivorsAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return await IssueAsync(fenced, archive, canIssue, enterMaintenance, cancellationToken).ConfigureAwait(false);
        }
        return status;
    }

    private async ValueTask<SiloRetentionAttempt?> TryFenceAsync(SiloRetentionCandidate planned, SiloRetentionAttempt attempt,
        Func<CancellationToken, ValueTask<IReadOnlyList<SiloRetentionHold>>> readHolds, Func<bool>? canIssue,
        SemaphoreSlim? lifecycle, Func<IDisposable?>? enterMaintenance, CancellationToken cancellationToken)
    {
        if (lifecycle is not null) await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await publisher.TryWithPublicationLockAsync<SiloRetentionAttempt>(async () =>
            {
                using var maintenance = enterMaintenance?.Invoke();
                if ((enterMaintenance is not null && maintenance is null) || canIssue?.Invoke() == false) return null;

                // No object-store requests here. The latest hold snapshot and the durable fence share the lifecycle lock.
                var saved = SiloRetentionState.Read(root);
                var policy = await index.ReadRetentionPolicyAsync(cancellationToken).ConfigureAwait(false);
                if (policy.ExpiryHeld) return null;
                var holds = await readHolds(cancellationToken).ConfigureAwait(false);
                var fresh = (await PlanAsync(policy, saved, clock.GetUtcNow(), budget.AdmissionRelease is not null, holds,
                    cancellationToken, planned.Locator).ConfigureAwait(false)).SingleOrDefault();
                if (fresh is null || !SameDecision(planned, fresh)) return null;
                var (cutoff, sha) = await index.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
                var writer = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
                if (writer.Position != cutoff || writer.Sha256 != sha ||
                    await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is not null) return null;
                var fenced = attempt with { Intents = attempt.Intents.Select(intent => intent with { HoldEvaluationCutoff = cutoff }).ToArray() };
                await (saved with { Attempt = fenced }).WriteAsync(root, cancellationToken).ConfigureAwait(false);
                return fenced;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle?.Release();
        }
    }

    private async ValueTask ReleaseUnfencedSurvivorsAsync(SiloRetentionAttempt attempt, CancellationToken cancellationToken) =>
        await publisher.WithPublicationLockAsync(() => AppendAsync(
            [
                .. attempt.Survivors.Select(static survivor => survivor with { IntentId = null }),
                .. attempt.Survivors.Select(static survivor => new SiloReleaseSurvivorRecord(survivor.ReservationId, "local-delete-not-fenced"))
            ], "retention-unfenced:" + attempt.Id, cancellationToken), cancellationToken).ConfigureAwait(false);

    private static bool SameDecision(SiloRetentionCandidate planned, SiloRetentionCandidate fresh) =>
        planned.Decision == fresh.Decision && planned.AuthorizesRoleExpiry == fresh.AuthorizesRoleExpiry &&
        planned.References.Select(static reference => reference.Identity)
            .SequenceEqual(fresh.References.Select(static reference => reference.Identity)) &&
        planned.References.Zip(fresh.References).All(static pair => pair.First.Copies.SequenceEqual(pair.Second.Copies));

    private async ValueTask<IReadOnlyList<SiloRetentionCandidate>> PlanAsync(SiloRetentionPolicy policy,
        SiloRetentionState saved, DateTimeOffset now, bool pressure, IReadOnlyList<SiloRetentionHold> externalHolds,
        CancellationToken cancellationToken, SiloExactLocator? only = null)
    {
        var authority = await index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        var configured = policy.Roles.ToDictionary(static item => item.Role, StringComparer.Ordinal);
        var holds = externalHolds.Concat(await index.ReadRetentionPinsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(await index.ReadReopenRetentionHoldsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(await index.ReadViewRetentionHoldsAsync(externalHolds.Where(static hold => hold.Kind == SiloHoldKind.ViewManifest),
                cancellationToken).ConfigureAwait(false)).ToArray();
        var holdsByArtifact = holds.ToLookup(static hold => hold.ArtifactId, StringComparer.Ordinal);
        var archiveReferences = holds.Any(static hold => hold.Kind == SiloHoldKind.SatelliteArchiveReference)
            ? await index.ReadArchivedCaptureReferencesAsync(cancellationToken).ConfigureAwait(false) : null;
        var archiveHistoryUnknown = authority.UnknownInterval;
        var staged = await ReadStagedKeysAsync(cancellationToken).ConfigureAwait(false);
        var leased = saved.Leases.Where(lease => lease.ExpiresUtc > now).SelectMany(static lease => lease.Versions).ToHashSet();
        var result = new List<SiloRetentionCandidate>();
        foreach (var (locator, references) in await index.ReadRetentionReferencesAsync(cancellationToken, only).ConfigureAwait(false))
        {
            var decision = "within-policy";
            var allowRoleExpiry = references.All(reference => FindPolicy(reference, locator.Key, configured)?.AllowLastCopyExpiry == true);
            if (!authority.CompleteThroughPrefixEnd || references.Count is 0 or > MaximumSharedRoles ||
                references.Any(static reference => reference.Role is null || reference.PublishedUtc is null) || staged.Contains(locator.Key) ||
                leased.Contains(locator) || saved.Attempt?.Target == locator ||
                references.Any(reference => holdsByArtifact[reference.Identity.Id].Any(hold => hold.Matches(reference.Identity, now) &&
                    (hold.Kind != SiloHoldKind.SatelliteArchiveReference || archiveHistoryUnknown ||
                        archiveReferences?.Contains(hold.ArtifactId) == true))))
                decision = "held-reference";
            else if (references.All(reference => IsDue(FindPolicy(reference, locator.Key, configured), reference.PublishedUtc, now, pressure)))
            {
                if (references.All(static reference => reference.Copies.Count > 1))
                    decision = Evict;
                else if (allowRoleExpiry)
                    decision = Expire;
                else
                    decision = "last-copy-policy-or-survivor-required";
            }
            result.Add(new(locator, references, decision, allowRoleExpiry));
        }

        foreach (var locator in (await state.ListOrphansAsync(cancellationToken).ConfigureAwait(false))
            .Select(static orphan => orphan.Locator).Distinct())
        {
            if (!authority.CompleteThroughPrefixEnd || (only is not null && locator != only) ||
                locator.VersionId.Length == 0 || staged.Contains(locator.Key) ||
                leased.Contains(locator) || saved.Attempt?.Target == locator ||
                await index.HasRetentionLocatorAsync(locator, cancellationToken).ConfigureAwait(false))
                continue;
            result.Add(new(locator, [], Abandoned));
        }

        return result.OrderBy(static candidate => candidate.Decision switch { Abandoned => 0, Evict => 1, Expire => 2, _ => 3 })
            .ThenBy(static candidate => RetentionOrder(candidate.Locator.Key))
            .ThenBy(static candidate => candidate.References.Count > 0 ? candidate.References.Max(static item => item.PublishedUtc) : default)
            .ThenBy(static candidate => candidate.Locator.Key, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Locator.VersionId, StringComparer.Ordinal).ToArray();
    }

    private static SiloRoleRetention? FindPolicy(SiloRetentionReference reference, string key,
        Dictionary<string, SiloRoleRetention> configured) =>
        (reference.Role is { } role ? configured.GetValueOrDefault(role) : null) ?? configured.GetValueOrDefault(RetentionOrder(key) switch
        {
            0 => "full",
            1 => "display",
            _ => "metadata"
        });

    private static int RetentionOrder(string key) => key.StartsWith("p/full/", StringComparison.Ordinal) ? 0 :
        key.StartsWith("p/display/", StringComparison.Ordinal) ? 1 : 2;

    private static bool IsDue(SiloRoleRetention? policy, DateTimeOffset? publishedUtc, DateTimeOffset now, bool pressure) =>
        policy is not null && publishedUtc is { } published && published <= now && (pressure && policy.ExpireUnderPressure ||
            policy.MaximumAge is { } age && now - published >= age);

    private async ValueTask<HashSet<string>> ReadStagedKeysAsync(CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (groupId, revisionId) in await state.ListOpenAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            var group = (await state.ReadGroupAsync(groupId, revisionId, cancellationToken).ConfigureAwait(false))!;
            foreach (var member in group.Members)
                keys.Add(SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256));
            if (group.ManifestSha256 is { } sha)
                keys.Add(SiloObjectNamespace.ManifestKey(group.Draft.FromUtc, groupId, revisionId, sha));
        }
        return keys;
    }

    private async ValueTask ResolveOrphansAsync(CancellationToken cancellationToken)
    {
        const int batchSize = 32;
        var unresolved = (await state.ListOrphansAsync(cancellationToken).ConfigureAwait(false))
            .Where(orphan => orphan.Locator.VersionId.Length == 0 && orphan.Locator.StoreId == local.Descriptor.StoreId &&
                orphan.Locator.Bucket == local.Descriptor.Bucket).ToArray();
        if (unresolved.Length == 0) return;
        var offset = _orphanOffset % unresolved.Length;
        _orphanOffset = (offset + batchSize) % unresolved.Length;
        var staged = await ReadStagedKeysAsync(cancellationToken).ConfigureAwait(false);
        foreach (var orphan in unresolved.Skip(offset).Concat(unresolved.Take(offset)).Take(batchSize))
        {
            if (staged.Contains(orphan.Locator.Key) ||
                await index.HasRetentionKeyAsync(orphan.Locator, cancellationToken).ConfigureAwait(false)) continue;
            string? version;
            try
            {
                version = await local.LocateCurrentVersionAsync(orphan.Locator.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                continue;
            }
            if (string.IsNullOrEmpty(version)) continue;
            await publisher.WithPublicationLockAsync(async () =>
            {
                if (!(await index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false)).CompleteThroughPrefixEnd ||
                    (await ReadStagedKeysAsync(cancellationToken).ConfigureAwait(false)).Contains(orphan.Locator.Key) ||
                    await index.HasRetentionKeyAsync(orphan.Locator, cancellationToken).ConfigureAwait(false)) return false;
                await state.ResolveRetentionOrphanAsync(orphan, version, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SiloRetentionAttempt?> PrepareAsync(SiloRetentionCandidate candidate, SiloPhysicalHeadroom physical,
        long? measured, ISiloRetentionArchive? archive, CancellationToken cancellationToken)
    {
        if (candidate.Decision is not (Expire or Evict or Abandoned)) return null;
        using var open = await local.OpenVersionAsync(candidate.Locator.Key, candidate.Locator.VersionId, cancellationToken).ConfigureAwait(false);
        if (open.Status != SiloOpenStatus.Found) return null;
        var allocation = measured is { } allocated
            ? new SiloPhysicalAllocation(SiloHash.Sha256(SiloJson.Serialize(candidate.Locator)), allocated) : null;

        var id = Guid.NewGuid().ToString("N");
        var intents = new List<SiloExpiryIntentRecord>();
        var reservations = new List<SiloReserveSurvivorRecord>();
        var roleExpiry = candidate.Decision == Expire;
        if (candidate.Decision == Evict)
        {
            var first = candidate.References[0];
            var survivor = first.Copies.FirstOrDefault(copy => copy.StoreId != local.Descriptor.StoreId &&
                candidate.References.All(reference => reference.Copies.Contains(copy)));
            if (survivor is not null && archive is not null &&
                await archive.ProtectAsync(first.Identity, survivor, cancellationToken).ConfigureAwait(false))
                reservations.Add(new(Guid.NewGuid().ToString("N"), first.Identity, survivor, clock.GetUtcNow(),
                    HoldEvidence: SiloJson.SerializeToElement(new { legalHold = true, verification = SiloFormats.ExactGetSha256 })));
            else if (candidate.AuthorizesRoleExpiry)
                roleExpiry = true;
            else
                return null;
        }

        foreach (var reference in candidate.References)
        {
            var intentId = Guid.NewGuid().ToString("N");
            if (reservations.Count > 0 && reservations[0].IntentId is null)
                reservations[0] = reservations[0] with { IntentId = intentId };
            var targets = roleExpiry ? reference.Copies.Select(copy => new SiloExpiryTarget(copy,
                copy.StoreId == local.Descriptor.StoreId ? LocalCustody : "archive-owner")).ToArray()
                : [new(candidate.Locator, LocalCustody)];
            if (intents.Count == 0 && allocation is not null)
            {
                var exact = Array.FindIndex(targets, target => target.Locator == candidate.Locator);
                targets[exact] = targets[exact] with { AllocationId = allocation.Id, AllocatedBytes = allocation.Bytes };
            }
            intents.Add(new(intentId, roleExpiry ? SiloExpiryScope.Role : SiloExpiryScope.Copy, reference.Identity,
                SiloRetentionPolicy.PolicyId, targets, roleExpiry ? Expire : candidate.Decision, roleExpiry, null));
        }
        return new(id, candidate.Locator, intents, reservations, physical, Allocation: allocation);
    }

    private async ValueTask<string> IssueAsync(SiloRetentionAttempt attempt, ISiloRetentionArchive? archive,
        Func<bool>? canIssue, Func<IDisposable?>? enterMaintenance, CancellationToken cancellationToken)
    {
        await AppendIntentsAsync(attempt, cancellationToken).ConfigureAwait(false);
        foreach (var survivor in attempt.Survivors)
        {
            if (archive is null || !await archive.ConfirmProtectedAsync(survivor.Identity, survivor.Survivor, cancellationToken).ConfigureAwait(false))
                return await RecordOutcomeAsync(attempt, SiloExpiryOutcome.NotDeleted, "survivor-protection-unavailable",
                    cancellationToken).ConfigureAwait(false);
        }

        await publisher.WithPublicationLockAsync(async () =>
        {
            await AppendAsync([AttemptRecord(attempt, "issued")], "retention-issued:" + attempt.Id, cancellationToken).ConfigureAwait(false);
            attempt = attempt with { Issued = true };
            await (SiloRetentionState.Read(root) with { Attempt = attempt }).WriteAsync(root, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        // A pause can close the dispatch gate while protection or journal I/O is in flight.
        // An accepted request is covered by the durable issued fence until exact absence is proven.
        Task<bool>? request = null;
        using (var maintenance = enterMaintenance?.Invoke())
        {
            if ((enterMaintenance is null || maintenance is not null) && canIssue?.Invoke() != false)
                request = ((ISiloVersionRemoval)local).DeleteVersionAsync(attempt.Target, cancellationToken).AsTask();
        }
        if (request is null)
            return await RecordOutcomeAsync(attempt, SiloExpiryOutcome.NotDeleted, "maintenance-held", cancellationToken).ConfigureAwait(false);

        var accepted = await request.ConfigureAwait(false);
        return await ObserveAttemptAsync(attempt, accepted ? null : SiloExpiryOutcome.Refused,
            SiloExpiryOutcome.Deleted, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> ReconcileAsync(SiloRetentionAttempt attempt,
        Func<SiloExactLocator, bool>? verifiedStoreExit, CancellationToken cancellationToken)
    {
        if (attempt.Outcome is not null)
            return await FinishAsync(attempt, cancellationToken).ConfigureAwait(false);
        if (attempt.Issued)
            return await ObserveAttemptAsync(attempt, verifiedStoreExit?.Invoke(attempt.Target) == true
                ? SiloExpiryOutcome.NotDeleted : null, SiloExpiryOutcome.AlreadyAbsent, cancellationToken).ConfigureAwait(false);

        // Restart may have changed configured holds or fenced the installer. End the unsent attempt and replan.
        await AppendIntentsAsync(attempt, cancellationToken).ConfigureAwait(false);
        return await RecordOutcomeAsync(attempt, SiloExpiryOutcome.NotDeleted, "delete-not-issued", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> ObserveAttemptAsync(SiloRetentionAttempt attempt,
        SiloExpiryOutcome? presentOutcome, SiloExpiryOutcome absentOutcome, CancellationToken cancellationToken)
    {
        using var observed = await local.OpenVersionAsync(attempt.Target.Key, attempt.Target.VersionId, cancellationToken).ConfigureAwait(false);
        return observed.Status switch
        {
            SiloOpenStatus.NoSuchVersion => await RecordOutcomeAsync(attempt, absentOutcome, "exact-get-no-such-version",
                cancellationToken).ConfigureAwait(false),
            SiloOpenStatus.Found when presentOutcome is { } outcome =>
                await RecordOutcomeAsync(attempt, outcome, outcome == SiloExpiryOutcome.Refused ? "delete-refused" : "verified-store-exit",
                    cancellationToken).ConfigureAwait(false),
            _ => "unsettled-delete"
        };
    }

    private ValueTask<string> RecordOutcomeAsync(SiloRetentionAttempt attempt, SiloExpiryOutcome outcome,
        string evidence, CancellationToken cancellationToken) =>
        FinishAsync(attempt with
        {
            Outcome = outcome,
            ObservedUtc = clock.GetUtcNow(),
            Evidence = evidence,
            AfterDelete = outcome is SiloExpiryOutcome.Deleted or SiloExpiryOutcome.AlreadyAbsent
                ? budget.ObserveReclamation() : null
        }, cancellationToken, persistObservation: true);

    private ValueTask<string> FinishAsync(SiloRetentionAttempt attempt, CancellationToken cancellationToken,
        bool persistObservation = false) => publisher.WithPublicationLockAsync(async () =>
    {
        var saved = SiloRetentionState.Read(root);
        var outcome = attempt.Outcome!.Value;
        var absent = outcome is SiloExpiryOutcome.Deleted or SiloExpiryOutcome.AlreadyAbsent;
        var observed = attempt.ObservedUtc!.Value;
        var evidence = attempt.Evidence!;
        if (persistObservation)
            await (saved with { Attempt = attempt }).WriteAsync(root, cancellationToken).ConfigureAwait(false);

        var reclaim = saved.ReclaimLedger;
        if (!attempt.DispositionCommitted)
        {
            var records = new List<SiloJournalRecord> { AttemptRecord(attempt, "observed") };
            foreach (var intent in attempt.Intents)
            {
                records.Add(new SiloExpiryDispositionRecord(intent.IntentId, attempt.Target, outcome, observed, evidence));
                if (absent && intent.Scope == SiloExpiryScope.Role && intent.Targets.Count == 1)
                    records.Add(new SiloAvailabilityRecord(SiloAvailabilityScope.Role, intent.Role, SiloAvailabilityState.PolicyExpired,
                        observed, evidence, IntentId: intent.IntentId));
            }
            await AppendAsync(records, "retention-outcome:" + attempt.Id, cancellationToken).ConfigureAwait(false);
            if (absent)
                reclaim = SiloReclaimLedger.Add(reclaim, attempt, budget.Observe() ?? attempt.AfterDelete ?? attempt.Before);
        }

        if (absent && attempt.Intents.Count == 0)
            await state.RemoveRetentionOrphanAsync(attempt.Target, cancellationToken).ConfigureAwait(false);

        await (saved with
        {
            Attempt = null,
            LastSettledAttemptId = attempt.Id,
            Reclaim = reclaim,
            ReclaimPending = null,
            PendingSurvivorReleases = attempt.Survivors.Count == 0 ? saved.SurvivorReleases :
                [.. saved.SurvivorReleases, new(attempt.Id, attempt.Survivors, absent)]
        }).WriteAsync(root, cancellationToken).ConfigureAwait(false);
        return SiloJson.EnumName(outcome);
    }, cancellationToken);

    private async ValueTask AppendIntentsAsync(SiloRetentionAttempt attempt, CancellationToken cancellationToken)
    {
        await publisher.WithPublicationLockAsync(() => AppendAsync(
            [.. attempt.Survivors, .. attempt.Intents, AttemptRecord(attempt, "prepared")],
            "retention-intent:" + attempt.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static SiloExpiryAttemptRecord AttemptRecord(SiloRetentionAttempt attempt, string phase) =>
        new(attempt.Id, attempt.Target, phase, attempt.Intents.Select(static intent => intent.IntentId).ToArray(),
            phase == "observed" ? attempt.Outcome : null);

    private async ValueTask<SiloCommittedSegment> AppendAsync(IReadOnlyList<SiloJournalRecord> records, string operation,
        CancellationToken cancellationToken)
    {
        var committed = await journal.AppendAsync(records, operation, cancellationToken).ConfigureAwait(false);
        await CatchUpAsync(cancellationToken).ConfigureAwait(false);
        return committed;
    }

    private async ValueTask CatchUpAsync(CancellationToken cancellationToken)
    {
        await journal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
        var writer = await state.ReadWriterEvidenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await catalog.CatchUpAsync(writer, null, cancellationToken).ConfigureAwait(false);
        if (result.Last != writer.Head || result.Reason != SiloPrefixEnd.Tail)
            throw new SiloConflictException("silo-retention-authority-unavailable", "Retention requires the verified writer journal prefix.");
    }

    internal async ValueTask ReconcileArchiveAsync(ISiloRetentionArchive archive, CancellationToken cancellationToken)
    {
        foreach (var pending in SiloRetentionState.Read(root).SurvivorReleases)
        {
            var verified = true;
            foreach (var survivor in pending.Survivors)
            {
                if (!pending.RequiresVerification) continue;
                if (await archive.ConfirmProtectedAsync(survivor.Identity, survivor.Survivor, cancellationToken).ConfigureAwait(false))
                    continue;
                verified = false;
                break;
            }
            if (!verified) continue;

            await publisher.WithPublicationLockAsync(async () =>
            {
                var saved = SiloRetentionState.Read(root);
                if (!saved.SurvivorReleases.Any(item => item.AttemptId == pending.AttemptId)) return false;
                await AppendAsync(pending.Survivors.Select(static item =>
                    (SiloJournalRecord)new SiloReleaseSurvivorRecord(item.ReservationId, "local-delete-settled")).ToArray(),
                    "retention-release:" + pending.AttemptId, cancellationToken).ConfigureAwait(false);
                await (saved with { PendingSurvivorReleases = saved.SurvivorReleases.Where(item => item.AttemptId != pending.AttemptId).ToArray() })
                    .WriteAsync(root, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (intent, identity, target) in await index.ReadArchiveExpiryTargetsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await archive.IsAuthorityExpiredAsync(target, cancellationToken).ConfigureAwait(false)) continue;
            await publisher.WithPublicationLockAsync(async () =>
            {
                var suffix = SiloHash.Sha256(SiloJson.Serialize(target));
                var now = clock.GetUtcNow();
                const string evidence = "archive-custody-and-exact-absence";
                var records = new List<SiloJournalRecord> { new SiloExpiryDispositionRecord(intent, target, SiloExpiryOutcome.AlreadyAbsent, now, evidence) };
                if (await index.IsFinalExpiryTargetAsync(intent, target, cancellationToken).ConfigureAwait(false))
                    records.Add(new SiloAvailabilityRecord(SiloAvailabilityScope.Role, identity, SiloAvailabilityState.PolicyExpired,
                        now, evidence, IntentId: intent));
                await AppendAsync(records, $"retention-archive-absence:{intent}:{suffix}", cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<SiloObjectLease?> TryAcquireAsync(IReadOnlyList<SiloExactLocator> versions, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count is < 1 or > 128 || duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
            throw new ArgumentException("An object lease needs a bounded set of versions and a positive lease duration.");
        var exact = versions.Distinct().ToArray();
        foreach (var version in exact) version.Validate();
        return publisher.WithPublicationLockAsync<SiloObjectLease?>(async () =>
        {
            var saved = SiloRetentionState.Read(root);
            var now = clock.GetUtcNow();
            var active = saved.Leases.Where(lease => lease.ExpiresUtc > now).ToArray();
            if (active.Length >= MaximumLeases ||
                (saved.Attempt is { DispositionCommitted: false } attempt && exact.Contains(attempt.Target))) return null;
            foreach (var version in exact)
                if (!await index.HasActiveRetentionLocatorAsync(version, cancellationToken).ConfigureAwait(false)) return null;
            var lease = new SiloObjectLease(Guid.NewGuid().ToString("N"), exact, now + duration);
            await (saved with { Leases = [.. active, lease] }).WriteAsync(root, cancellationToken).ConfigureAwait(false);
            return lease;
        }, cancellationToken);
    }

    public ValueTask<bool> RevalidateAsync(SiloObjectLease lease, CancellationToken cancellationToken) =>
        publisher.WithPublicationLockAsync(async () =>
        {
            var saved = SiloRetentionState.Read(root);
            if (!saved.Leases.Any(active => active.Id == lease.Id && active.ExpiresUtc > clock.GetUtcNow() &&
                active.Versions.SequenceEqual(lease.Versions))) return false;
            foreach (var version in lease.Versions)
                if (!await index.HasActiveRetentionLocatorAsync(version, cancellationToken).ConfigureAwait(false)) return false;
            return true;
        }, cancellationToken);

    public async ValueTask ReleaseAsync(SiloObjectLease lease, CancellationToken cancellationToken) =>
        await publisher.WithPublicationLockAsync(async () =>
        {
            var saved = SiloRetentionState.Read(root);
            await (saved with { Leases = saved.Leases.Where(active => active.Id != lease.Id).ToArray() })
                .WriteAsync(root, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
}
