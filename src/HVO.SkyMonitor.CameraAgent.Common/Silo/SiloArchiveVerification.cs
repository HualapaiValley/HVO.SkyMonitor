using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed class SiloArchiveVerification(SiloArchiveStore archive, TimeProvider clock)
{
    internal async ValueTask<IReadOnlyList<SiloJournalRecord>> VerifyPublicationAsync(
        SiloPublicationRef reference, SiloCommittedSegment commit, SiloCatalogIndex index, CancellationToken cancellationToken)
    {
        var journal = await archive.ReadSegmentAsync(commit, cancellationToken).ConfigureAwait(false);
        var publish = GetPublication(journal, reference);
        if (publish.Manifest.Locator.StoreId != index.Binding.StoreId || publish.Manifest.Locator.Bucket != index.Binding.Bucket)
            throw new SiloArchiveException("failed", "archive-publication-source-mismatch");

        var manifestIdentity = new SiloObjectIdentity(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId,
            publish.Manifest.Sha256, publish.Manifest.Length);
        var manifestLocator = archive.ReplicaOf(publish.Manifest.Locator);
        var manifestProof = await RequireCopyAsync(manifestIdentity, manifestLocator, captureBytes: true, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManifest(manifestProof.Bytes!, journal, publish);

        var copies = new Dictionary<SiloRoleKey, SiloCopyRecord>
        {
            [manifestIdentity.Role] = await MakeCopyAsync(manifestIdentity, manifestProof, cancellationToken).ConfigureAwait(false)
        };
        var verified = new Dictionary<(SiloExactLocator, long, string), SiloProbe>();
        foreach (var member in publish.Members)
        {
            if (member.Locator.StoreId != index.Binding.StoreId || member.Locator.Bucket != index.Binding.Bucket)
                throw new SiloArchiveException("failed", "archive-publication-source-mismatch");
            var identity = new SiloObjectIdentity(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
            var locator = archive.ReplicaOf(member.Locator);
            var key = (locator, identity.Length, identity.Sha256);
            if (!verified.TryGetValue(key, out var proof))
            {
                proof = await RequireCopyAsync(identity, locator, captureBytes: false, cancellationToken).ConfigureAwait(false);
                verified.Add(key, proof);
            }
            copies.Add(identity.Role, await MakeCopyAsync(identity, proof, cancellationToken).ConfigureAwait(false));
        }

        foreach (var dependency in manifest.Dependencies)
        {
            var role = new SiloRoleKey(SiloIdentityKind.Member, dependency.ArtifactId, dependency.ArtifactRevision);
            if (copies.TryGetValue(role, out var existing))
            {
                if (existing.Identity.Sha256 != dependency.Sha256)
                    throw new SiloArchiveException("failed", "archive-dependency-identity-mismatch");
                continue;
            }
            var indexed = await index.ReadRoleAsync(role, cancellationToken).ConfigureAwait(false);
            if (indexed is null)
                throw new SiloArchiveException("pending", "archive-dependency-publication-pending");
            if (indexed.Sha256 != dependency.Sha256)
                throw new SiloArchiveException("failed", "archive-dependency-identity-mismatch");

            var identity = new SiloObjectIdentity(role.Kind, role.Id, role.Revision, indexed.Sha256, indexed.Length);
            var candidates = indexed.Locators.Where(row => row.Eligible &&
                ((row.Source is "named" or "remap" && row.Locator.StoreId == index.Binding.StoreId && row.Locator.Bucket == index.Binding.Bucket) ||
                (row.Source == "copy" && row.Locator.StoreId == archive.Marker.StoreId && row.Locator.Bucket == archive.Marker.Bucket &&
                    row.CopyState == "active")))
                .Select(row => archive.ReplicaOf(row.Locator)).Distinct().Take(32);
            SiloArchiveException? absence = null;
            foreach (var locator in candidates)
            {
                try
                {
                    var proof = await RequireCopyAsync(identity, locator, captureBytes: false, cancellationToken).ConfigureAwait(false);
                    copies.Add(role, await MakeCopyAsync(identity, proof, cancellationToken).ConfigureAwait(false));
                    break;
                }
                catch (SiloArchiveException exception) when (exception.State is "pending" || exception.Reason == "archive-copy-policy-expired")
                {
                    absence ??= exception;
                }
            }
            if (!copies.ContainsKey(role))
                throw absence ?? new SiloArchiveException("pending", "archive-dependency-version-pending");
        }

        var receipt = new SiloArchiveReceiptRecord(archive.Marker.StoreId, archive.Marker.Bucket, publish.GroupId, publish.RevisionId,
            commit.Position, reference.Index, commit.VersionId, commit.Sha256, clock.GetUtcNow());
        return [receipt, .. copies.Values];
    }

    internal async ValueTask<IReadOnlyList<SiloCopyRecord>> ReadReceiptAsync(SiloCommittedSegment commit, SiloPublicationRef reference,
        bool verifyCopies, CancellationToken cancellationToken)
    {
        var segment = await archive.ReadSegmentAsync(commit, cancellationToken).ConfigureAwait(false);
        if (segment.Records.Count == 0 || segment.Records[0] is not SiloArchiveReceiptRecord receipt ||
            receipt.ArchiveStoreId != archive.Marker.StoreId || receipt.ArchiveBucket != archive.Marker.Bucket ||
            receipt.GroupId != reference.GroupId || receipt.RevisionId != reference.RevisionId ||
            receipt.Publication != reference.Position || receipt.PublicationIndex != reference.Index || receipt.Publication >= commit.Position)
            throw new SiloArchiveException("failed", "archive-receipt-identity-mismatch");

        var journal = await archive.ReadSegmentAsync(new(receipt.Publication, receipt.PublicationSha256, receipt.PublicationVersionId),
            cancellationToken).ConfigureAwait(false);
        var publish = GetPublication(journal, reference);
        var copies = segment.Records.OfType<SiloCopyRecord>().ToArray();
        if (copies.Length + 1 != segment.Records.Count || copies.Length == 0 ||
            copies.Any(copy => copy.Locator.StoreId != receipt.ArchiveStoreId || copy.Locator.Bucket != receipt.ArchiveBucket ||
                copy.StoreClass != SiloStoreClass.Archive || copy.Retrieval != SiloRetrieval.Online ||
                copy.RetentionPolicy != SiloArchiveStore.RetentionPolicy || copy.ArchiveRetention is null || copy.Custody != SiloArchiveStore.Custody))
            throw new SiloArchiveException("failed", "archive-receipt-copy-mismatch");

        var manifestIdentity = new SiloObjectIdentity(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId,
            publish.Manifest.Sha256, publish.Manifest.Length);
        if (copies[0].Identity != manifestIdentity || copies[0].Locator != archive.ReplicaOf(publish.Manifest.Locator))
            throw new SiloArchiveException("failed", "archive-receipt-manifest-mismatch");
        var manifestProof = await RequireCopyAsync(manifestIdentity, copies[0].Locator, captureBytes: true, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManifest(manifestProof.Bytes!, journal, publish);
        var roles = new Dictionary<SiloRoleKey, SiloCopyRecord>();
        foreach (var copy in copies.Skip(1))
        {
            if (copy.Identity.Kind != SiloIdentityKind.Member || !roles.TryAdd(copy.Identity.Role, copy))
                throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");
        }
        foreach (var member in publish.Members)
        {
            if (!roles.TryGetValue(new(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision), out var copy) ||
                copy.Identity.Sha256 != member.Sha256 || copy.Identity.Length != member.Length ||
                copy.Locator != archive.ReplicaOf(member.Locator))
                throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");
        }
        foreach (var dependency in manifest.Dependencies)
        {
            if (!roles.TryGetValue(new(SiloIdentityKind.Member, dependency.ArtifactId, dependency.ArtifactRevision), out var copy) ||
                copy.Identity.Sha256 != dependency.Sha256)
                throw new SiloArchiveException("failed", "archive-receipt-dependencies-mismatch");
        }
        if (roles.Count != publish.Members.Select(static member => (member.ArtifactId, member.ArtifactRevision))
            .Concat(manifest.Dependencies.Select(static dependency => (dependency.ArtifactId, dependency.ArtifactRevision))).Distinct().Count())
            throw new SiloArchiveException("failed", "archive-receipt-members-mismatch");

        if (verifyCopies)
        {
            foreach (var copy in roles.Values.DistinctBy(static copy => (copy.Locator, copy.Identity.Length, copy.Identity.Sha256)))
                await RequireCopyAsync(copy.Identity, copy.Locator, captureBytes: false, cancellationToken).ConfigureAwait(false);
        }
        return copies;
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
        CancellationToken cancellationToken)
    {
        await RequireNotExpiredAsync(locator, cancellationToken).ConfigureAwait(false);
        var proof = await archive.ProbeAsync(identity, locator, captureBytes, cancellationToken).ConfigureAwait(false);
        await RequireVerifiedAsync(proof, cancellationToken).ConfigureAwait(false);
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
        if (await archive.WasExpiredAsync(locator, cancellationToken).ConfigureAwait(false))
            throw new SiloArchiveException("failed", "archive-copy-policy-expired");
    }

    private async ValueTask<SiloCopyRecord> MakeCopyAsync(SiloObjectIdentity identity, SiloProbe proof, CancellationToken cancellationToken) =>
        new(identity, proof.Locator, SiloStoreClass.Archive, SiloRetrieval.Online, SiloArchiveStore.Custody, proof.ObservedUtc,
            SiloFormats.ExactGetSha256, RetentionPolicy: SiloArchiveStore.RetentionPolicy,
            ArchiveRetention: await archive.ReadRetentionAsync(proof.Locator, cancellationToken).ConfigureAwait(false));

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
