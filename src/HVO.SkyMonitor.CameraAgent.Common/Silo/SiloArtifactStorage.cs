using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloArtifactInput(Guid ArtifactId, string Role, string MediaType, string Sha256,
    ReadOnlyMemory<byte> Payload, byte[] Evidence, string? ContentIdentitySha256, string OutputIdentitySha256,
    string? Producer = null, string? RecipeIdentitySha256 = null);

/// <summary>The logical member boundary used by CameraAgent writers and readers.</summary>
internal sealed class SiloArtifactStorage(SiloInstalledStorage storage, TimeProvider clock)
{
    public const string RawEvidenceRevision = "raw-evidence-1";
    public const string ProductEvidenceRevision = "product-evidence-1";

    internal bool BindingVerified => storage.BindingVerified;

    internal static bool IsReference(string path) => path.StartsWith("silo/", StringComparison.Ordinal);

    internal static string RawReference(Guid id, string extension) => $"silo/raw/{id:N}{extension}";

    public async ValueTask PublishProcessingAsync(string groupId, ReconstructionDescriptor raw, IReadOnlyList<SiloArtifactInput> outputs,
        CancellationToken cancellationToken)
    {
        if (outputs.Count == 0) return;
        var members = new List<SiloDraftMember>(outputs.Count * 2);
        var content = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            var id = output.ArtifactId.ToString("D");
            var sha = SiloCenteredViews.LowerHex(output.Sha256);
            var evidenceSha = SiloHash.Sha256(output.Evidence);
            var payloadToken = SiloFilePayloadSpool.TokenOf(sha);
            var evidenceToken = SiloFilePayloadSpool.TokenOf(evidenceSha);
            members.Add(new(id, output.OutputIdentitySha256, output.Role, RetentionClass(output.MediaType), output.MediaType,
                output.Payload.Length, sha, true, payloadToken, LowerHex(output.ContentIdentitySha256), LowerHex(output.OutputIdentitySha256),
                output.Producer, LowerHex(output.RecipeIdentitySha256)));
            members.Add(new(id, ProductEvidenceRevision, "product-evidence", SiloRetentionClass.Metadata, "application/json",
                output.Evidence.LongLength, evidenceSha, true, evidenceToken));
            content.TryAdd(payloadToken, output.Payload);
            content.TryAdd(evidenceToken, output.Evidence);
        }

        var draft = new SiloGroupDraft("processing-products", groupId, "1", raw.Timing.DurableIngressUtc.ToUniversalTime(),
            raw.Timing.ExposureStartedUtc.ToUniversalTime(), raw.Timing.ExposureEndedUtc.ToUniversalTime(), members,
            raw.Artifact.Role == FrameArtifactRole.Raw
                ? [new(raw.Artifact.ArtifactId.ToString("D"), CaptureContractJson.ComputeDescriptorSha256(raw),
                    SiloCenteredViews.LowerHex(raw.Artifact.ChecksumSha256), SiloDependencyKind.Compute, true)]
                : [], new([]), CameraId: raw.Capture.RigId);
        await PublishAsync(draft, (member, token) => OpenMemoryAsync(content[member.SourceToken], token), cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask ResumeProcessingAsync(string groupId, CancellationToken cancellationToken)
    {
        if (await storage.State.ReadGroupAsync(groupId, "1", cancellationToken).ConfigureAwait(false) is { } group)
        {
            await PublishAsync(group.Draft, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask PublishAsync(SiloGroupDraft draft, Func<SiloDraftMember, CancellationToken, ValueTask<Stream>>? open,
        CancellationToken cancellationToken)
    {
        if (!storage.BindingVerified)
            throw new SiloStoreUnavailableException(storage.Binding.StoreId, "verify writer binding", writeMayHaveLanded: false);
        await storage.Publisher.StageAsync(draft, open, cancellationToken).ConfigureAwait(false);
        var outcome = await storage.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        if (outcome.BlockedCode is { } conflict && conflict is not ("silo.store-unavailable" or "silo-retention-delete-unsettled"))
            throw new SiloConflictException(conflict, "The staged Silo artifact publication was refused.");
        if (outcome.State != SiloStagedState.Visible || outcome.CatalogState != "visible")
            throw new SiloStoreUnavailableException(storage.Binding.StoreId, "settle artifact publication", writeMayHaveLanded: true);
    }

    public static ValueTask<Stream> OpenMemoryAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false));
    }

    public static SiloDraftMember Member(Guid id, string revision, string role, SiloRetentionClass retentionClass, string media,
        long length, string sha256)
    {
        var sha = SiloCenteredViews.LowerHex(sha256);
        return new(id.ToString("D"), revision, role, retentionClass, media, length, sha, true, SiloFilePayloadSpool.TokenOf(sha));
    }

    internal ValueTask<bool> IsArtifactRetainedAsync(Guid artifactId, CancellationToken cancellationToken) =>
        storage.BindingVerified
            ? storage.Index.IsArtifactRetainedAsync(artifactId.ToString("D"), cancellationToken)
            : ValueTask.FromResult(true);

    internal async ValueTask<bool> VerifyRetainedLocalPayloadAsync(Guid artifactId, string sha256, CancellationToken cancellationToken)
    {
        if (!storage.BindingVerified) return false;
        var expected = SiloCenteredViews.LowerHex(sha256);
        var physical = await storage.Index.FindRetainedLocalPayloadAsync(artifactId.ToString("D"), expected, cancellationToken)
            .ConfigureAwait(false);
        return physical is { } found && storage.Retention.ReadUnsettledTarget() != found.Locator &&
            (await SiloVerifier.ProbeAsync(storage.Catalog.Stores, found.Locator, found.Length, expected, clock, false, cancellationToken)
                .ConfigureAwait(false)).Status == SiloProbeStatus.Verified;
    }

    internal async ValueTask<IReadOnlySet<string>?> ReadArchivedCaptureReferencesAsync(CancellationToken cancellationToken)
    {
        if (!storage.BindingVerified) return null;
        var authority = await storage.Index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        var head = await storage.State.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        return authority.UnknownInterval || authority.PrefixEnd != head.Position || authority.PrefixEnd is null
            ? null : await storage.Index.ReadArchivedCaptureReferencesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<bool> IsReferenceRetainedAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsReference(path) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            throw new InvalidDataException("The Silo retention reference is invalid.");
        return IsArtifactRetainedAsync(id, cancellationToken);
    }

    // Raw payload and sidecar readability must come from one local index snapshot.
    internal ValueTask<bool> HasRawEvidenceAsync(ReconstructionDescriptor descriptor, CancellationToken cancellationToken) =>
        HasReadableLocalMembersAsync(descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
            RawEvidenceRevision, cancellationToken);

    public ValueTask<bool> HasMemberAsync(Guid artifactId, string revision, CancellationToken cancellationToken) =>
        HasReadableLocalMembersAsync(artifactId, revision, null, cancellationToken);

    private ValueTask<bool> HasReadableLocalMembersAsync(Guid artifactId, string revision, string? evidenceRevision,
        CancellationToken cancellationToken) =>
        storage.BindingVerified
            ? storage.Index.HasReadableLocalMembersAsync(artifactId.ToString("D"), revision, evidenceRevision,
                storage.Retention.ReadUnsettledTarget(), cancellationToken)
            : ValueTask.FromResult(false);

    public async ValueTask<byte[]?> ReadRawAsync(ReconstructionDescriptor descriptor, CancellationToken cancellationToken) =>
        await ReadMemberAsync(descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
            descriptor.Layout.ByteLength, descriptor.Artifact.ChecksumSha256, cancellationToken).ConfigureAwait(false);

    internal ValueTask<byte[]?> ReadLiveRawAsync(ReconstructionDescriptor descriptor, string stagingPath,
        CancellationToken cancellationToken) =>
        ReadWithStagingAsync(token => ReadRawAsync(descriptor, token), stagingPath, cancellationToken);

    internal ValueTask<byte[]?> ReadRawEvidenceAsync(Guid artifactId, string sha256, string? stagingPath,
        CancellationToken cancellationToken) =>
        ReadWithStagingAsync(token => ReadMetadataAsync(artifactId, RawEvidenceRevision, SiloJson.MaximumMetadataObjectBytes,
            sha256, token), stagingPath, cancellationToken);

    private static async ValueTask<byte[]?> ReadWithStagingAsync(Func<CancellationToken, ValueTask<byte[]?>> read,
        string? stagingPath, CancellationToken cancellationToken)
    {
        var published = await read(cancellationToken).ConfigureAwait(false);
        if (published is not null || stagingPath is null) return published;

        try
        {
            return await File.ReadAllBytesAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // P4 may have committed and reclaimed the pair since the first catalog lookup.
            return await read(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> ReadMemberAsync(Guid artifactId, string revision, long length, string sha256,
        CancellationToken cancellationToken)
    {
        var role = await ResolveMemberAsync(artifactId, revision, length, sha256, cancellationToken).ConfigureAwait(false);
        if (role is null) return null;
        var probe = await ReadMemberCoreAsync(role, null, cancellationToken).ConfigureAwait(false);
        return probe.Bytes;
    }

    public async ValueTask<Stream?> OpenMemberAsync(Guid artifactId, string revision, long length, string sha256,
        CancellationToken cancellationToken)
    {
        var role = await ResolveMemberAsync(artifactId, revision, length, sha256, cancellationToken).ConfigureAwait(false);
        if (role is null) return null;
        var scratch = storage.OpenReadScratch();
        try
        {
            await ReadMemberCoreAsync(role, scratch, cancellationToken).ConfigureAwait(false);
            scratch.Position = 0;
            return scratch;
        }
        catch
        {
            await scratch.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<bool> VerifyMemberAsync(Guid artifactId, string revision, long length, string sha256,
        CancellationToken cancellationToken)
    {
        var role = await ResolveMemberAsync(artifactId, revision, length, sha256, cancellationToken).ConfigureAwait(false);
        if (role is null) return false;
        await ReadMemberCoreAsync(role, Stream.Null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async ValueTask<bool> VerifyRawPairIfPublishedAsync(ReconstructionDescriptor descriptor, byte[] evidence,
        CancellationToken cancellationToken)
    {
        var id = descriptor.Artifact.ArtifactId;
        if (!await VerifyMemberAsync(id, CaptureContractJson.ComputeDescriptorSha256(descriptor), descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256, cancellationToken).ConfigureAwait(false)) return false;
        if (!await VerifyMemberAsync(id, RawEvidenceRevision, evidence.LongLength, SiloHash.Sha256(evidence),
            cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The committed Silo raw frame has no settled source evidence.");
        return true;
    }

    internal async ValueTask<byte[]?> ReadMetadataAsync(Guid artifactId, string revision, int maximumBytes, string? sha256,
        CancellationToken cancellationToken)
    {
        var role = await FindMemberAsync(artifactId, revision, cancellationToken).ConfigureAwait(false);
        if (role is null) return null;
        if (role.Length > maximumBytes || sha256 is not null && role.Sha256 != SiloCenteredViews.LowerHex(sha256))
            throw new InvalidDataException("The committed Silo metadata differs from the expected size or identity.");
        return (await ReadMemberCoreAsync(role, null, cancellationToken).ConfigureAwait(false)).Bytes;
    }

    private async ValueTask<SiloRoleStatus?> ResolveMemberAsync(Guid artifactId, string revision, long length, string sha256,
        CancellationToken cancellationToken)
    {
        var role = await FindMemberAsync(artifactId, revision, cancellationToken).ConfigureAwait(false);
        if (role is not null && (role.Length != length || role.Sha256 != SiloCenteredViews.LowerHex(sha256)))
            throw new InvalidDataException("The committed Silo member differs from the requested identity.");
        return role;
    }

    private async ValueTask<SiloRoleStatus?> FindMemberAsync(Guid artifactId, string revision,
        CancellationToken cancellationToken)
    {
        if (!storage.BindingVerified)
            throw new SiloStoreUnavailableException(storage.Binding.StoreId, "verify writer binding", writeMayHaveLanded: false);

        var id = artifactId.ToString("D");
        if (!await storage.Index.HasVisibleMemberAsync(id, revision, cancellationToken).ConfigureAwait(false)) return null;
        return await storage.Index.ReadRoleAsync(new(SiloIdentityKind.Member, id, revision), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The visible Silo member is missing its exact role record.");
    }

    private ValueTask<SiloProbe> ReadMemberCoreAsync(SiloRoleStatus role, Stream? destination, CancellationToken cancellationToken) =>
        storage.Publisher.WithPublicationLockAsync(async () =>
        {
            var current = await storage.Index.ReadRoleAsync(role.Role, cancellationToken).ConfigureAwait(false);
            var fenced = storage.Retention.ReadUnsettledTarget();
            var hasFencedCopy = false;
            foreach (var candidate in current?.Locators ?? [])
            {
                if (!candidate.Eligible || candidate.CopyState != "active" ||
                    !storage.Catalog.Stores.TryResolve(candidate.Locator.StoreId, candidate.Locator.Bucket, out var store))
                    continue;
                if (candidate.Locator == fenced)
                {
                    hasFencedCopy = true;
                    continue;
                }
                var proof = destination is null
                    ? await SiloVerifier.ProbeAsync(store, candidate.Locator, role.Length, role.Sha256, clock,
                        captureBytes: true, cancellationToken).ConfigureAwait(false)
                    : await SiloVerifier.CopyVerifiedAsync(store, candidate.Locator, role.Length, role.Sha256, clock, destination,
                        cancellationToken).ConfigureAwait(false);
                switch (proof.Status)
                {
                    case SiloProbeStatus.Verified: return proof;
                    case SiloProbeStatus.Unavailable:
                        throw new SiloStoreUnavailableException(candidate.Locator.StoreId, "read committed member", writeMayHaveLanded: false);
                    case SiloProbeStatus.Damaged:
                        throw new InvalidDataException("The exact Silo member failed length or SHA-256 verification.");
                }
            }
            if (hasFencedCopy)
                throw new SiloStoreUnavailableException(fenced!.StoreId, "read while a version delete is unsettled",
                    writeMayHaveLanded: false);
            throw new FileNotFoundException("The exact Silo member version is missing.");
        }, cancellationToken);

    private static string? LowerHex(string? sha256) => sha256 is null ? null : SiloCenteredViews.LowerHex(sha256);

    private static SiloRetentionClass RetentionClass(string mediaType) => mediaType.StartsWith("image/", StringComparison.Ordinal)
        ? SiloRetentionClass.Display : mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal)
            ? SiloRetentionClass.Metadata : SiloRetentionClass.Full;
}
