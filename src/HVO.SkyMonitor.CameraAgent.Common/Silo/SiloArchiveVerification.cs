using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloArchiveReceiptEvidence(IReadOnlyList<SiloCopyRecord> Copies, IReadOnlyDictionary<SiloRoleKey, string> Unverified);

internal sealed record SiloArchivePublicationEvidence(SiloCommittedSegment Publication, SiloGroupManifest Manifest,
    IReadOnlyList<SiloJournalRecord> Records, long LocalExpirySequence, IReadOnlyDictionary<SiloRoleKey, SiloCopyRecord> Copies);

internal sealed class SiloArchiveVerification(SiloArchiveStore archive, TimeProvider clock)
{
    private const int CandidateLimit = 32;

    internal async ValueTask<SiloArchivePublicationEvidence> VerifyPublicationAsync(
        SiloPublicationRef reference, SiloCommittedSegment commit, SiloCatalogIndex index, long prefix, CancellationToken cancellationToken)
    {
        var journal = await archive.ReadSegmentAsync(commit, cancellationToken).ConfigureAwait(false);
        var publish = GetPublication(journal, reference);
        if (publish.Manifest.Locator.StoreId != index.Binding.StoreId || publish.Manifest.Locator.Bucket != index.Binding.Bucket)
            throw new SiloArchiveException("failed", "archive-publication-source-mismatch");

        var manifestIdentity = new SiloObjectIdentity(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId,
            publish.Manifest.Sha256, publish.Manifest.Length);
        var copies = new Dictionary<SiloRoleKey, SiloCopyRecord>();
        var unverified = new Dictionary<SiloRoleKey, SiloArchiveMemberAvailability>();
        var verified = new Dictionary<(SiloExactLocator, long, string), SiloProbe>();
        var localExpirySequence = 0L;
        var manifestLocator = archive.ReplicaOf(publish.Manifest.Locator);
        byte[] manifestBytes;
        try
        {
            var proof = await RequireCopyAsync(manifestIdentity, manifestLocator, captureBytes: true, cancellationToken, verified).ConfigureAwait(false);
            manifestBytes = proof.Bytes!;
            copies.Add(manifestIdentity.Role, await MakeCopyAsync(manifestIdentity, proof, cancellationToken).ConfigureAwait(false));
        }
        catch (SiloArchiveException exception) when (exception.Reason == "archive-exact-version-pending")
        {
            if (!await IsLocallyExpiredAsync(manifestIdentity).ConfigureAwait(false) ||
                await index.ReadProjectedManifestAsync(reference, cancellationToken).ConfigureAwait(false) is not { } bytes ||
                bytes.LongLength != manifestIdentity.Length || SiloHash.Sha256(bytes) != manifestIdentity.Sha256)
                throw;
            manifestBytes = bytes;
        }
        var manifest = ReadManifest(manifestBytes, journal, publish);
        // Without a retained manifest, a replacement reader can authenticate only direct members
        // against the publication record. Dependency copies remain discoverable by their own receipts.
        var hasManifest = copies.ContainsKey(manifestIdentity.Role);
        var required = RequiredRoles(publish, hasManifest ? manifest : null);
        var members = new Dictionary<SiloRoleKey, SiloObjectIdentity>();

        async ValueTask<bool> IsLocallyExpiredAsync(SiloObjectIdentity identity)
        {
            if (await index.ReadRoleExpiryAsync(identity, cancellationToken).ConfigureAwait(false) is not { Generation: 1 } expiry ||
                expiry.Sequence > prefix)
                return false;
            localExpirySequence = Math.Max(localExpirySequence, expiry.Sequence);
            return true;
        }

        async ValueTask CheckRoleAsync(SiloObjectIdentity identity, IReadOnlyList<SiloExactLocator> candidates)
        {
            SiloArchiveException? unresolved = null;
            var sawExpiry = false;
            foreach (var locator in candidates.Take(CandidateLimit))
            {
                try
                {
                    var proof = await RequireCopyAsync(identity, locator, captureBytes: false, cancellationToken, verified).ConfigureAwait(false);
                    copies.Add(identity.Role, await MakeCopyAsync(identity, proof, cancellationToken).ConfigureAwait(false));
                    unverified.Remove(identity.Role);
                    return;
                }
                catch (SiloArchiveException exception) when (IsCopyFailure(exception))
                {
                    if (exception.Reason == "archive-copy-policy-expired")
                        sawExpiry = true;
                    else if (unresolved is null || unresolved.Reason == "archive-exact-version-pending" ||
                        (unresolved.State == "pending" && exception.State != "pending"))
                        unresolved = exception;
                }
            }

            if ((unresolved is null && sawExpiry && candidates.Count <= CandidateLimit) ||
                ((unresolved is null || unresolved.Reason == "archive-exact-version-pending") &&
                    await IsLocallyExpiredAsync(identity).ConfigureAwait(false)))
            {
                RecordUnavailable(identity.Role, "policy-expired");
                return;
            }
            if (required.Contains(identity.Role))
                throw unresolved ?? new SiloArchiveException("pending", "archive-dependency-version-pending");
            if (unresolved is { State: not "pending" })
                RecordUnavailable(identity.Role, unresolved.State);
        }

        void RecordUnavailable(SiloRoleKey role, string state) => unverified[role] = new(role.Id, role.Revision, state);

        foreach (var member in publish.Members)
        {
            if (member.Locator.StoreId != index.Binding.StoreId || member.Locator.Bucket != index.Binding.Bucket)
                throw new SiloArchiveException("failed", "archive-publication-source-mismatch");
            var identity = new SiloObjectIdentity(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
            members.Add(identity.Role, identity);
            await CheckRoleAsync(identity, [archive.ReplicaOf(member.Locator)]).ConfigureAwait(false);
        }

        foreach (var dependency in hasManifest ? manifest.Dependencies : [])
        {
            var role = DependencyRole(dependency);
            if (members.TryGetValue(role, out var existing))
            {
                if (existing.Sha256 != dependency.Sha256)
                    throw new SiloArchiveException("failed", "archive-dependency-identity-mismatch");
                continue;
            }

            var indexed = await index.ReadRoleAsync(role, cancellationToken).ConfigureAwait(false);
            if (indexed is null)
            {
                if (required.Contains(role))
                    throw new SiloArchiveException("pending", "archive-dependency-publication-pending");
                continue;
            }
            if (indexed.Sha256 != dependency.Sha256)
                throw new SiloArchiveException("failed", "archive-dependency-identity-mismatch");
            if (copies.ContainsKey(role)) continue;

            var identity = new SiloObjectIdentity(role.Kind, role.Id, role.Revision, indexed.Sha256, indexed.Length);
            var candidates = indexed.Locators.Where(row => row.Eligible &&
                ((row.Source is "named" or "remap" && row.Locator.StoreId == index.Binding.StoreId && row.Locator.Bucket == index.Binding.Bucket) ||
                (row.Source == "copy" && row.Locator.StoreId == archive.Marker.StoreId && row.Locator.Bucket == archive.Marker.Bucket &&
                    row.CopyState == "active")))
                .Select(row => archive.ReplicaOf(row.Locator)).Distinct().Take(CandidateLimit + 1).ToArray();
            await CheckRoleAsync(identity, candidates).ConfigureAwait(false);
        }

        if (copies.Count == 0)
            return new(commit, manifest, [], localExpirySequence, copies);

        var receipt = new SiloArchiveReceiptRecord(archive.Marker.StoreId, archive.Marker.Bucket, publish.GroupId, publish.RevisionId,
            commit.Position, reference.Index, commit.VersionId, commit.Sha256, clock.GetUtcNow(),
            unverified.Count == 0 ? null : unverified.Values.ToArray(), hasManifest ? null : "policy-expired");
        return new(commit, manifest, [receipt, .. copies.Values], localExpirySequence, copies);
    }

    internal async ValueTask<SiloArchiveReceiptEvidence> ReadReceiptAsync(SiloCommittedSegment commit, SiloPublicationRef reference,
        bool verifyCopies, CancellationToken cancellationToken, SiloArchivePublicationEvidence? current = null)
    {
        var segment = await archive.ReadSegmentAsync(commit, cancellationToken).ConfigureAwait(false);
        if (segment.Records.Count == 0 || segment.Records[0] is not SiloArchiveReceiptRecord receipt ||
            receipt.ArchiveStoreId != archive.Marker.StoreId || receipt.ArchiveBucket != archive.Marker.Bucket ||
            receipt.GroupId != reference.GroupId || receipt.RevisionId != reference.RevisionId ||
            receipt.Publication != reference.Position || receipt.PublicationIndex != reference.Index || receipt.Publication >= commit.Position)
            throw new SiloArchiveException("failed", "archive-receipt-identity-mismatch");

        var publication = new SiloCommittedSegment(receipt.Publication, receipt.PublicationSha256, receipt.PublicationVersionId);
        if (current is not null && publication != current.Publication)
            throw new SiloArchiveException("failed", "archive-receipt-publication-mismatch");
        var journal = await archive.ReadSegmentAsync(publication, cancellationToken).ConfigureAwait(false);
        var publish = GetPublication(journal, reference);
        var copies = segment.Records.OfType<SiloCopyRecord>().ToArray();
        if (copies.Length + 1 != segment.Records.Count || copies.Length == 0 ||
            copies.Any(copy => copy.Locator.StoreId != receipt.ArchiveStoreId || copy.Locator.Bucket != receipt.ArchiveBucket ||
                copy.StoreClass != SiloStoreClass.Archive || copy.Retrieval != SiloRetrieval.Online ||
                copy.RetentionPolicy != SiloArchiveStore.RetentionPolicy || copy.ArchiveRetention is null || copy.Custody != SiloArchiveStore.Custody))
            throw new SiloArchiveException("failed", "archive-receipt-copy-mismatch");

        var manifestIdentity = new SiloObjectIdentity(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId,
            publish.Manifest.Sha256, publish.Manifest.Length);
        var manifest = current?.Manifest;
        var hasManifest = copies[0].Identity.Kind == SiloIdentityKind.Manifest;
        if (hasManifest == (receipt.ManifestState == "policy-expired"))
            throw new SiloArchiveException("failed", "archive-receipt-manifest-mismatch");
        if (hasManifest)
        {
            if (copies[0].Identity != manifestIdentity || copies[0].Locator != archive.ReplicaOf(publish.Manifest.Locator))
                throw new SiloArchiveException("failed", "archive-receipt-manifest-mismatch");
            if (manifest is null)
            {
                var proof = await RequireCopyAsync(manifestIdentity, copies[0].Locator, captureBytes: true, cancellationToken)
                    .ConfigureAwait(false);
                manifest = ReadManifest(proof.Bytes!, journal, publish);
            }
        }
        else
        {
            // Use the same proof available on a replacement, even when refreshing on the writer.
            manifest = null;
        }
        var roles = new Dictionary<SiloRoleKey, SiloCopyRecord>();
        foreach (var copy in copies.Skip(hasManifest ? 1 : 0))
        {
            if (copy.Identity.Kind != SiloIdentityKind.Member || !roles.TryAdd(copy.Identity.Role, copy))
                throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");
        }
        var required = RequiredRoles(publish, manifest);
        var declared = new HashSet<SiloRoleKey>();
        foreach (var member in publish.Members)
        {
            var role = new SiloRoleKey(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision);
            declared.Add(role);
            if (roles.TryGetValue(role, out var copy) && (copy.Identity.Sha256 != member.Sha256 ||
                copy.Identity.Length != member.Length || copy.Locator != archive.ReplicaOf(member.Locator)))
                throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");
        }
        foreach (var dependency in manifest?.Dependencies ?? [])
        {
            var role = DependencyRole(dependency);
            declared.Add(role);
            if (roles.TryGetValue(role, out var copy) && copy.Identity.Sha256 != dependency.Sha256)
                throw new SiloArchiveException("failed", "archive-receipt-dependencies-mismatch");
        }
        if (manifest is not null && !declared.IsSupersetOf(roles.Keys))
            throw new SiloArchiveException("failed", "archive-receipt-dependencies-mismatch");
        var unverified = new Dictionary<SiloRoleKey, string>();
        foreach (var member in receipt.UnverifiedMembers ?? [])
        {
            if ((manifest is not null && !declared.Contains(member.Role)) ||
                (required.Contains(member.Role) && member.State != "policy-expired") ||
                roles.ContainsKey(member.Role) || !unverified.TryAdd(member.Role, member.State))
                throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");
        }
        if (required.Any(role => !roles.ContainsKey(role) && unverified.GetValueOrDefault(role) != "policy-expired"))
            throw new SiloArchiveException("failed", "archive-receipt-dependencies-mismatch");

        if (verifyCopies)
        {
            foreach (var group in roles.Values.GroupBy(static copy => (copy.Locator, copy.Identity.Length, copy.Identity.Sha256)))
            {
                var copy = group.First();
                try
                {
                    await RequireCopyAsync(copy.Identity, copy.Locator, captureBytes: false, cancellationToken).ConfigureAwait(false);
                }
                catch (SiloArchiveException exception) when (!group.Any(copy => required.Contains(copy.Identity.Role)) &&
                    IsCopyFailure(exception))
                {
                    // A historical receipt retains its recorded evidence. The worker compares it with current proof before publishing it.
                }
            }
        }
        // An archive-only read with an expired manifest can authenticate the directly published members.
        if (manifest is null)
            return new(copies.Where(copy => declared.Contains(copy.Identity.Role)).ToArray(),
                unverified.Where(member => declared.Contains(member.Key)).ToDictionary());
        return new(copies, unverified);
    }

    internal async ValueTask CopyAsync(SiloCopyRecord copy, Stream destination, CancellationToken cancellationToken)
    {
        if (copy.Identity.Length > SiloS3ObjectStore.MaximumPutBytes)
            throw new SiloArchiveException("failed", "archive-copy-identity-mismatch");
        await RequireNotExpiredAsync(copy.Locator, cancellationToken).ConfigureAwait(false);
        var proof = await SiloVerifier.CopyVerifiedAsync(archive.Objects, copy.Locator, copy.Identity.Length, copy.Identity.Sha256,
            clock, destination, cancellationToken).ConfigureAwait(false);
        await RequireVerifiedAsync(proof, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SiloProbe> RequireCopyAsync(SiloObjectIdentity identity, SiloExactLocator locator, bool captureBytes,
        CancellationToken cancellationToken, Dictionary<(SiloExactLocator, long, string), SiloProbe>? verified = null)
    {
        var key = (locator, identity.Length, identity.Sha256);
        if (verified is not null && verified.TryGetValue(key, out var previous) && (!captureBytes || previous.Bytes is not null))
            return previous;
        await RequireNotExpiredAsync(locator, cancellationToken).ConfigureAwait(false);
        var proof = await archive.ProbeAsync(identity, locator, captureBytes, cancellationToken).ConfigureAwait(false);
        await RequireVerifiedAsync(proof, cancellationToken).ConfigureAwait(false);
        if (verified is not null)
            verified[key] = proof;
        return proof;
    }

    private async ValueTask RequireVerifiedAsync(SiloProbe proof, CancellationToken cancellationToken)
    {
        if (proof.Status == SiloProbeStatus.Verified) return;
        if (proof.Status == SiloProbeStatus.Unavailable)
            throw new SiloArchiveException("blocked", "archive-version-unreachable");
        if (proof.Status == SiloProbeStatus.Damaged)
            throw new SiloArchiveException("failed", "archive-version-digest-mismatch");
        await RequireNotExpiredAsync(proof.Locator, cancellationToken).ConfigureAwait(false);
        throw new SiloArchiveException("pending", "archive-exact-version-pending");
    }

    private async ValueTask RequireNotExpiredAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        try
        {
            if (await archive.WasExpiredAsync(locator, cancellationToken).ConfigureAwait(false))
                throw new SiloArchiveException("failed", "archive-copy-policy-expired");
        }
        catch (SiloStoreUnavailableException)
        {
            throw new SiloArchiveException("blocked", "archive-version-unreachable");
        }
    }

    private async ValueTask<SiloCopyRecord> MakeCopyAsync(SiloObjectIdentity identity, SiloProbe proof, CancellationToken cancellationToken) =>
        new(identity, proof.Locator, SiloStoreClass.Archive, SiloRetrieval.Online, SiloArchiveStore.Custody, proof.ObservedUtc,
            SiloFormats.ExactGetSha256, RetentionPolicy: SiloArchiveStore.RetentionPolicy,
            ArchiveRetention: await archive.ReadRetentionAsync(proof.Locator, cancellationToken).ConfigureAwait(false));

    private static bool IsCopyFailure(SiloArchiveException exception) =>
        exception.Reason is "archive-exact-version-pending" or "archive-copy-policy-expired" or "archive-version-unreachable" or
            "archive-version-digest-mismatch" or "archive-retention-version-pending" or "archive-retention-unavailable";

    private static SiloRoleKey DependencyRole(SiloManifestDependency dependency) =>
        new(SiloIdentityKind.Member, dependency.ArtifactId, dependency.ArtifactRevision);

    private static HashSet<SiloRoleKey> RequiredRoles(SiloPublishRecord publish, SiloGroupManifest? manifest) =>
        [.. publish.Members.Where(static member => member.Required)
                .Select(static member => new SiloRoleKey(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision)),
            .. (manifest?.Dependencies ?? []).Where(static dependency => dependency.Kind == SiloDependencyKind.Reopen && dependency.Required)
                .Select(DependencyRole)];

    private static SiloPublishRecord GetPublication(SiloJournalSegment segment, SiloPublicationRef reference)
    {
        if (reference.Index < 0 || reference.Index >= segment.Records.Count ||
            segment.Records[reference.Index] is not SiloPublishRecord publish ||
            publish.GroupId != reference.GroupId || publish.RevisionId != reference.RevisionId)
            throw new SiloArchiveException("failed", "archive-publication-identity-mismatch");
        return publish;
    }

    private static SiloGroupManifest ReadManifest(byte[] bytes, SiloJournalSegment segment, SiloPublishRecord publish)
    {
        var manifest = SiloJson.ReadManifest(bytes, "archive manifest");
        if (manifest.InstanceId != segment.InstanceId || manifest.StorageBindingId != segment.StorageBindingId ||
            manifest.WriterGeneration != segment.Generation || manifest.GroupId != publish.GroupId ||
            manifest.RevisionId != publish.RevisionId || manifest.GroupKind != publish.GroupKind ||
            manifest.PreviousRevisionId != publish.PreviousRevisionId || publish.Manifest.Locator.Key !=
                SiloObjectNamespace.ManifestKey(manifest.FromUtc, publish.GroupId, publish.RevisionId, publish.Manifest.Sha256))
            throw new SiloArchiveException("failed", "archive-manifest-identity-mismatch");

        var expected = publish.Members.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            member.FactsSha256, member.Required, member.Locator)).ToHashSet();
        var actual = manifest.Members.Select(static member => (member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length,
            SiloMemberFacts.Of(member).Sha256(), member.Required, member.Locator));
        if (expected.Count != manifest.Members.Count || !expected.SetEquals(actual))
            throw new SiloArchiveException("failed", "archive-manifest-members-mismatch");
        return manifest;
    }
}
