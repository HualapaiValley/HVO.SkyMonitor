using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

// ---------------------------------------------------------------------------------------------------------------
// Group manifest, hvo.cameraagent.silo.group-manifest/1 (#1965 4.4). One immutable object per group revision.
// ---------------------------------------------------------------------------------------------------------------

internal sealed record SiloGroupManifest(
    string Format,
    string InstanceId,
    string StorageBindingId,
    long WriterGeneration,
    string GroupKind,
    string GroupId,
    string RevisionId,
    DateTimeOffset PublishedUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<SiloManifestMember> Members,
    IReadOnlyList<SiloManifestDependency> Dependencies,
    SiloManifestProvenance Provenance,
    string? PreviousRevisionId = null,
    string? CameraId = null,
    SiloManifestView? View = null);

/// <summary>A member published in this group, with the persistence-qualified locator named at publication.</summary>
internal sealed record SiloManifestMember(
    string ArtifactId,
    string ArtifactRevision,
    string Role,
    SiloRetentionClass RetentionClass,
    string MediaType,
    long Length,
    string Sha256,
    bool Required,
    SiloExactLocator Locator,
    string? ContentIdentitySha256 = null,
    string? OutputIdentitySha256 = null,
    string? Producer = null,
    string? RecipeIdentitySha256 = null);

/// <summary>An exact dependency on an artifact revision, for reopen (R) or recompute (C) (#1965 6.1).</summary>
internal sealed record SiloManifestDependency(
    string ArtifactId,
    string ArtifactRevision,
    string Sha256,
    SiloDependencyKind Kind,
    bool Required);

internal sealed record SiloManifestProvenance(
    IReadOnlyList<string> RecipeInputIdentities,
    JsonElement? Capture = null,
    string? GraphIdentitySha256 = null,
    string? RigRevision = null,
    string? SiteRevision = null,
    JsonElement? ImportSource = null);

/// <summary>
/// A centered view revision. The base and revision documents are kept verbatim as their producer serialized them, so
/// every supplied binding, and therefore the content-derived revision ID, survives publication and rebuild exactly. The
/// layer list is the indexable projection of the same revision.
/// </summary>
internal sealed record SiloManifestView(
    string StreamId,
    string ReferenceCaptureId,
    long RevisionOrder,
    SiloViewEvidence Preview,
    string BaseFormat,
    JsonElement Base,
    string RevisionFormat,
    JsonElement Revision,
    IReadOnlyList<SiloViewLayerBinding> Layers);

internal sealed record SiloViewLayerBinding(
    string Kind,
    string Scope,
    long EvidenceOrder,
    string Readiness,
    string? SourceCaptureId = null,
    SiloViewEvidence? Evidence = null,
    DateTimeOffset? ObservedUtc = null,
    string? Reason = null);

/// <summary>An exact artifact binding as the view contract states it; its identity is the artifact revision.</summary>
internal sealed record SiloViewEvidence(string ArtifactId, string IdentitySha256, string PayloadSha256);

/// <summary>
/// The producer and sidecar facts of one artifact revision (#1965 4.2). Any change to them is a new revision, so the
/// revision is bound to their digest as well as to its bytes.
/// </summary>
internal sealed record SiloMemberFacts(
    string Role,
    SiloRetentionClass RetentionClass,
    string MediaType,
    string? ContentIdentitySha256,
    string? OutputIdentitySha256,
    string? Producer,
    string? RecipeIdentitySha256)
{
    public string Sha256() => SiloHash.Sha256(SiloJson.Serialize(this));

    public static SiloMemberFacts Of(SiloManifestMember member) => new(member.Role, member.RetentionClass, member.MediaType,
        member.ContentIdentitySha256, member.OutputIdentitySha256, member.Producer, member.RecipeIdentitySha256);
}

internal sealed record SiloCaptureSequence(string AgentId, long CaptureSequence);

// ---------------------------------------------------------------------------------------------------------------
// Journal, hvo.cameraagent.silo.journal-segment/1 and hvo.cameraagent.silo.journal-checkpoint/2 (#1965 4.5).
// ---------------------------------------------------------------------------------------------------------------

/// <summary>One create-only journal object. Its predecessor's SHA-256 chains it into the total (g, seq) order.</summary>
internal sealed record SiloJournalSegment(
    string Format,
    string InstanceId,
    string StorageBindingId,
    long Generation,
    long Sequence,
    DateTimeOffset WrittenUtc,
    IReadOnlyList<SiloJournalRecord> Records,
    SiloJournalPosition? Previous = null,
    string? PreviousSha256 = null)
{
    [JsonIgnore] public SiloJournalPosition Position => new(Generation, Sequence);
}

/// <summary>
/// A checkpoint covering every record at or before <see cref="Covers"/>. It carries each record exactly as committed,
/// so it carries forward every retained floor, pointer, copy, custody, remap, pin, policy, availability and expiry
/// fact. <see cref="ReachedP4"/> names, in journal order, each covered publication the catalog had proven to reach P4
/// when the checkpoint was written, whether it was visible then or closed by a later outage: the proof that it is
/// published history, which no journal record carries. It authenticates nothing until an anchor record and seal
/// evidence make it a trusted anchor.
/// </summary>
internal sealed record SiloJournalCheckpoint(
    string Format,
    string InstanceId,
    string StorageBindingId,
    SiloJournalPosition Covers,
    string CoversSha256,
    IReadOnlyList<SiloCheckpointEntry> Entries,
    IReadOnlyList<SiloCheckpointPublication> ReachedP4);

/// <summary>One covered publication proven to have reached P4 when its checkpoint was written.</summary>
internal sealed record SiloCheckpointPublication(string GroupId, string RevisionId);

internal sealed record SiloCheckpointEntry(long Generation, long Sequence, int Index, JsonElement Record);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SiloGenerationOpenRecord), SiloRecordTypes.GenerationOpen)]
[JsonDerivedType(typeof(SiloPublishRecord), SiloRecordTypes.Publish)]
[JsonDerivedType(typeof(SiloLiveIssueRecord), SiloRecordTypes.LiveIssue)]
[JsonDerivedType(typeof(SiloCopyRecord), SiloRecordTypes.Copy)]
[JsonDerivedType(typeof(SiloArchiveReceiptRecord), SiloRecordTypes.ArchiveReceipt)]
[JsonDerivedType(typeof(SiloReserveSurvivorRecord), SiloRecordTypes.ReserveSurvivor)]
[JsonDerivedType(typeof(SiloReleaseSurvivorRecord), SiloRecordTypes.ReleaseSurvivor)]
[JsonDerivedType(typeof(SiloRemapRecord), SiloRecordTypes.Remap)]
[JsonDerivedType(typeof(SiloPinRecord), SiloRecordTypes.Pin)]
[JsonDerivedType(typeof(SiloPolicyRecord), SiloRecordTypes.Policy)]
[JsonDerivedType(typeof(SiloExpiryIntentRecord), SiloRecordTypes.ExpiryIntent)]
[JsonDerivedType(typeof(SiloExpiryDispositionRecord), SiloRecordTypes.ExpiryDisposition)]
[JsonDerivedType(typeof(SiloAvailabilityRecord), SiloRecordTypes.Availability)]
[JsonDerivedType(typeof(SiloAnchorRecord), SiloRecordTypes.Anchor)]
internal abstract record SiloJournalRecord;

internal static class SiloRecordTypes
{
    public const string GenerationOpen = "generation-open";
    public const string Publish = "publish";
    public const string LiveIssue = "live-issue";
    public const string Copy = "copy";
    public const string ArchiveReceipt = "archive-receipt";
    public const string ReserveSurvivor = "reserve-survivor";
    public const string ReleaseSurvivor = "release-survivor";
    public const string Remap = "remap";
    public const string Pin = "pin";
    public const string Policy = "policy";
    public const string ExpiryIntent = "expiry-intent";
    public const string ExpiryDisposition = "expiry-disposition";
    public const string Availability = "availability";
    public const string Anchor = "anchor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        GenerationOpen, Publish, LiveIssue, Copy, ArchiveReceipt, ReserveSurvivor, ReleaseSurvivor, Remap, Pin, Policy,
        ExpiryIntent, ExpiryDisposition, Availability, Anchor
    };
}

/// <summary>
/// Starts generation g. Generation 1 is opened at install. A later generation names the prefix end it was rebuilt from,
/// the highest prior generation, identity floors and its witness evidence (#1965 5.4, 7.2).
/// </summary>
internal sealed record SiloGenerationOpenRecord(
    long Generation,
    string Reason,
    long HighestPriorGeneration,
    IReadOnlyDictionary<string, long> IdentityFloors,
    SiloJournalPosition? PrefixEnd = null,
    string? PrefixEndSha256 = null,
    SiloJournalPosition? RebuiltFromAnchor = null,
    JsonElement? Witnesses = null) : SiloJournalRecord;

/// <summary>The commit point P3 (#1965 5.3). Every locator it names was persistence-qualified before it was written.</summary>
internal sealed record SiloPublishRecord(
    string GroupKind,
    string GroupId,
    string RevisionId,
    SiloPublishedObject Manifest,
    IReadOnlyList<SiloPublishedMember> Members,
    string? PreviousRevisionId = null,
    SiloCaptureSequence? Capture = null) : SiloJournalRecord;

internal sealed record SiloPublishedObject(SiloExactLocator Locator, long Length, string Sha256, SiloQualification Qualification);

internal sealed record SiloPublishedMember(
    string ArtifactId,
    string ArtifactRevision,
    long Length,
    string Sha256,
    string FactsSha256,
    bool Required,
    SiloExactLocator Locator,
    SiloQualification Qualification);

/// <summary>The original persistence qualification of one exact named locator.</summary>
internal sealed record SiloQualification(DateTimeOffset VerifiedUtc, string Method, bool ProfileQualified);

/// <summary>Starts a camera's live interval on an exact revision and ends the camera's prior interval.</summary>
internal sealed record SiloLiveIssueRecord(
    string CameraId,
    string StreamId,
    string GroupId,
    string RevisionId,
    long ChangeOrder,
    DateTimeOffset StartedUtc) : SiloJournalRecord;

/// <summary>Verified custody of the same bytes at another exact version. It never qualifies a different locator.</summary>
internal sealed record SiloCopyRecord(
    SiloObjectIdentity Identity,
    SiloExactLocator Locator,
    SiloStoreClass StoreClass,
    SiloRetrieval Retrieval,
    string Custody,
    DateTimeOffset VerifiedUtc,
    string Method,
    string? Endpoint = null,
    string? RetentionPolicy = null,
    SiloArchiveRetention? ArchiveRetention = null) : SiloJournalRecord;

internal sealed record SiloArchiveRetention(bool LegalHold, string? Mode = null, DateTimeOffset? RetainUntilUtc = null);

/// <summary>Completes one archive verification. The exact copies are the other records in this same segment.</summary>
internal sealed record SiloArchiveReceiptRecord(
    string ArchiveStoreId,
    string ArchiveBucket,
    string GroupId,
    string RevisionId,
    SiloJournalPosition Publication,
    int PublicationIndex,
    string PublicationVersionId,
    string PublicationSha256,
    DateTimeOffset VerifiedUtc,
    IReadOnlyList<SiloArchiveMemberAvailability>? UnverifiedMembers = null) : SiloJournalRecord;

internal sealed record SiloArchiveMemberAvailability(string ArtifactId, string ArtifactRevision, string State)
{
    internal SiloRoleKey Role => new(SiloIdentityKind.Member, ArtifactId, ArtifactRevision);
}

internal sealed record SiloReserveSurvivorRecord(
    string ReservationId,
    SiloObjectIdentity Identity,
    SiloExactLocator Survivor,
    DateTimeOffset VerifiedUtc,
    string? IntentId = null,
    JsonElement? HoldEvidence = null) : SiloJournalRecord;

internal sealed record SiloReleaseSurvivorRecord(string ReservationId, string Reason) : SiloJournalRecord;

/// <summary>A verified restore or import moved the same bytes to a new version ID; identities never change.</summary>
internal sealed record SiloRemapRecord(
    SiloObjectIdentity Identity,
    SiloExactLocator From,
    SiloExactLocator To,
    DateTimeOffset VerifiedUtc,
    string Method,
    bool PersistenceQualified) : SiloJournalRecord;

internal sealed record SiloPinRecord(
    string PinId,
    string TargetKind,
    string TargetId,
    string TargetRevision,
    bool Active,
    string Reason) : SiloJournalRecord;

internal sealed record SiloPolicyRecord(string PolicyId, JsonElement Policy) : SiloJournalRecord;

internal sealed record SiloExpiryIntentRecord(
    string IntentId,
    SiloExpiryScope Scope,
    SiloObjectIdentity Role,
    string Authorization,
    IReadOnlyList<SiloExpiryTarget> Targets,
    string PolicyReason,
    bool LastCopy,
    SiloJournalPosition? HoldEvaluationCutoff = null) : SiloJournalRecord;

/// <summary>One exact target copy. A null allocation is an unknown allocation and is never charged.</summary>
internal sealed record SiloExpiryTarget(
    SiloExactLocator Locator,
    string Custody,
    string? AllocationId = null,
    long? AllocatedBytes = null);

internal sealed record SiloExpiryDispositionRecord(
    string IntentId,
    SiloExactLocator Locator,
    SiloExpiryOutcome Outcome,
    DateTimeOffset ObservedUtc,
    string Evidence) : SiloJournalRecord;

internal sealed record SiloAvailabilityRecord(
    SiloAvailabilityScope Scope,
    SiloObjectIdentity Identity,
    SiloAvailabilityState State,
    DateTimeOffset ObservedUtc,
    string Evidence,
    SiloExactLocator? Locator = null,
    string? IntentId = null) : SiloJournalRecord;

/// <summary>Names a checkpoint's exact version and SHA-256. With seal evidence it makes the checkpoint a trusted anchor.</summary>
internal sealed record SiloAnchorRecord(
    SiloExactLocator Checkpoint,
    long CheckpointLength,
    string CheckpointSha256,
    SiloJournalPosition Covers,
    string CoversSha256,
    JsonElement? SealEvidence = null) : SiloJournalRecord;
