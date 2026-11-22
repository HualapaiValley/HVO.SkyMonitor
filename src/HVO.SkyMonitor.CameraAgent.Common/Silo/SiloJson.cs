using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// The one serializer for every new-layout JSON format. Writing is canonical: declaration order, no whitespace, nulls
/// omitted, enums as kebab-case names. Reading checks the format identity before anything else, accepts additive
/// fields, and refuses an unknown format or record type read-only.
/// </summary>
internal static class SiloJson
{
    public const int MaximumMetadataObjectBytes = 64 * 1024 * 1024;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public const string CenteredViewBaseFormat = "cameraagent-centered-view-base-v1";
    public const string CenteredViewRevisionFormat = "cameraagent-centered-view-revision-v1";
    public const string CenteredViewGroupKind = "centered-view";

    private static readonly HashSet<string> LayerReadiness = new HashSet<string>(StringComparer.Ordinal)
    {
        "Waiting", "Ready", "Partial", "AssessedEmpty", "Incompatible", "NotConfigured"
    };

    private static readonly HashSet<string> LayerScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Reference", "Contributor", "Stack"
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowOutOfOrderMetadataProperties = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>The enum's name exactly as the formats write it.</summary>
    public static string EnumName<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value, Options).Trim('"');

    public static SiloGroupManifest ReadManifest(ReadOnlySpan<byte> bytes, string subject)
    {
        using var document = Parse(bytes, subject, SiloFormats.GroupManifest);
        var manifest = Deserialize<SiloGroupManifest>(document.RootElement, subject);
        Validate(subject, () => ValidateManifest(manifest, subject));
        return manifest;
    }

    public static SiloJournalSegment ReadSegment(ReadOnlySpan<byte> bytes, string subject)
    {
        using var document = Parse(bytes, subject, SiloFormats.JournalSegment);
        if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            throw new SiloInvalidObjectException(subject, "the segment has no record list");
        }
        foreach (var record in records.EnumerateArray())
        {
            RequireKnownRecordType(record, subject);
        }
        var segment = Deserialize<SiloJournalSegment>(document.RootElement, subject);
        Validate(subject, () => ValidateSegment(segment));
        return segment;
    }

    public static SiloJournalCheckpoint ReadCheckpoint(ReadOnlySpan<byte> bytes, string subject)
    {
        using var document = Parse(bytes, subject, SiloFormats.JournalCheckpoint);
        var checkpoint = Deserialize<SiloJournalCheckpoint>(document.RootElement, subject);
        // Every entry must be a valid record, so a malformed one disqualifies the checkpoint as an anchor instead of
        // stopping a rebuild after it was chosen.
        var published = new List<(string GroupId, string RevisionId)>();
        foreach (var entry in checkpoint.Entries)
        {
            if (ReadRecord(entry.Record, $"{subject} entry g{entry.Generation}/{entry.Sequence}#{entry.Index}") is SiloPublishRecord publish)
            {
                published.Add((publish.GroupId, publish.RevisionId));
            }
        }
        Validate(subject, () => ValidateCheckpoint(checkpoint, published));
        return checkpoint;
    }

    public static SiloJournalRecord ReadRecord(JsonElement record, string subject)
    {
        RequireKnownRecordType(record, subject);
        var value = Deserialize<SiloJournalRecord>(record, subject);
        Validate(subject, () => ValidateRecord(value));
        return value;
    }

    public static string RecordType(SiloJournalRecord record) => record switch
    {
        SiloGenerationOpenRecord => SiloRecordTypes.GenerationOpen,
        SiloPublishRecord => SiloRecordTypes.Publish,
        SiloLiveIssueRecord => SiloRecordTypes.LiveIssue,
        SiloCopyRecord => SiloRecordTypes.Copy,
        SiloReserveSurvivorRecord => SiloRecordTypes.ReserveSurvivor,
        SiloReleaseSurvivorRecord => SiloRecordTypes.ReleaseSurvivor,
        SiloRemapRecord => SiloRecordTypes.Remap,
        SiloPinRecord => SiloRecordTypes.Pin,
        SiloPolicyRecord => SiloRecordTypes.Policy,
        SiloExpiryIntentRecord => SiloRecordTypes.ExpiryIntent,
        SiloExpiryDispositionRecord => SiloRecordTypes.ExpiryDisposition,
        SiloAvailabilityRecord => SiloRecordTypes.Availability,
        SiloAnchorRecord => SiloRecordTypes.Anchor,
        _ => throw new ArgumentOutOfRangeException(nameof(record), "Unknown journal record type.")
    };

    private static JsonDocument Parse(ReadOnlySpan<byte> bytes, string subject, string expectedFormat)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException exception)
        {
            throw new SiloInvalidObjectException(subject, "the bytes are not well-formed JSON", exception);
        }
        string? format = null;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("format", out var formatElement) &&
            formatElement.ValueKind == JsonValueKind.String)
        {
            format = formatElement.GetString();
        }
        if (!string.Equals(format, expectedFormat, StringComparison.Ordinal))
        {
            document.Dispose();
            throw new SiloFormatRefusedException(subject, format, expectedFormat);
        }
        return document;
    }

    private static void RequireKnownRecordType(JsonElement record, string subject)
    {
        var type = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("type", out var element) &&
                   element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (type is null || !SiloRecordTypes.All.Contains(type))
        {
            throw new SiloFormatRefusedException(subject + " record", type, SiloFormats.JournalSegment + " record types");
        }
    }

    private static T Deserialize<T>(JsonElement element, string subject)
    {
        try
        {
            return element.Deserialize<T>(Options) ?? throw new SiloInvalidObjectException(subject, "the object is null");
        }
        catch (JsonException exception)
        {
            throw new SiloInvalidObjectException(subject, "the object does not match its format", exception);
        }
    }

    private static void Validate(string subject, Action validate)
    {
        try
        {
            validate();
        }
        catch (ArgumentException exception)
        {
            throw new SiloInvalidObjectException(subject, exception.Message, exception);
        }
    }

    internal static void ValidateManifest(SiloGroupManifest manifest, string subject)
    {
        SiloText.RequireText(manifest.InstanceId, nameof(manifest.InstanceId), 256);
        SiloHash.Require(manifest.StorageBindingId, nameof(manifest.StorageBindingId));
        ArgumentOutOfRangeException.ThrowIfLessThan(manifest.WriterGeneration, 1);
        RequireGroupKind(manifest.GroupKind);
        SiloText.RequireToken(manifest.GroupId, nameof(manifest.GroupId), 128);
        SiloText.RequireToken(manifest.RevisionId, nameof(manifest.RevisionId), 128);
        if (manifest.PreviousRevisionId is not null)
        {
            SiloText.RequireToken(manifest.PreviousRevisionId, nameof(manifest.PreviousRevisionId), 128);
        }
        RequireUtc(manifest.PublishedUtc, nameof(manifest.PublishedUtc));
        RequireUtc(manifest.FromUtc, nameof(manifest.FromUtc));
        RequireUtc(manifest.ToUtc, nameof(manifest.ToUtc));
        if (manifest.FromUtc > manifest.ToUtc)
        {
            throw new ArgumentException("The UTC bounds must be a half-open interval [from, to).", nameof(manifest));
        }
        if (manifest.CameraId is not null)
        {
            SiloText.RequireText(manifest.CameraId, nameof(manifest.CameraId), 256);
        }
        var members = new HashSet<(string, string)>();
        foreach (var member in manifest.Members)
        {
            SiloText.RequireText(member.ArtifactId, nameof(member.ArtifactId), 256);
            SiloText.RequireText(member.ArtifactRevision, nameof(member.ArtifactRevision), 256);
            SiloText.RequireText(member.Role, nameof(member.Role), 128);
            SiloText.RequireText(member.MediaType, nameof(member.MediaType), 256);
            SiloHash.Require(member.Sha256, nameof(member.Sha256));
            ArgumentOutOfRangeException.ThrowIfNegative(member.Length);
            member.Locator.Validate();
            if (!string.Equals(member.Locator.Key, SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256), StringComparison.Ordinal))
            {
                throw new ArgumentException("A member locator must name its content-addressed key.", nameof(manifest));
            }
            if (!members.Add((member.ArtifactId, member.ArtifactRevision)))
            {
                throw new ArgumentException("A manifest names each artifact revision once.", nameof(manifest));
            }
        }
        var dependencies = new HashSet<(string, string, SiloDependencyKind)>();
        foreach (var dependency in manifest.Dependencies)
        {
            SiloText.RequireText(dependency.ArtifactId, nameof(dependency.ArtifactId), 256);
            SiloText.RequireText(dependency.ArtifactRevision, nameof(dependency.ArtifactRevision), 256);
            SiloHash.Require(dependency.Sha256, nameof(dependency.Sha256));
            if (!dependencies.Add((dependency.ArtifactId, dependency.ArtifactRevision, dependency.Kind)))
            {
                throw new ArgumentException("A manifest names each dependency once per kind.", nameof(manifest));
            }
        }
        ArgumentNullException.ThrowIfNull(manifest.Provenance.RecipeInputIdentities);
        var isView = string.Equals(manifest.GroupKind, CenteredViewGroupKind, StringComparison.Ordinal);
        if (isView != manifest.View is not null)
        {
            throw new ArgumentException("A centered view revision, and only one, carries a view section.", nameof(manifest));
        }
        if (manifest.View is { } view)
        {
            if (!string.Equals(view.BaseFormat, CenteredViewBaseFormat, StringComparison.Ordinal))
            {
                throw new SiloFormatRefusedException(subject + " view base", view.BaseFormat, CenteredViewBaseFormat);
            }
            if (!string.Equals(view.RevisionFormat, CenteredViewRevisionFormat, StringComparison.Ordinal))
            {
                throw new SiloFormatRefusedException(subject + " view revision", view.RevisionFormat, CenteredViewRevisionFormat);
            }
            SiloText.RequireText(view.StreamId, nameof(view.StreamId), 512);
            SiloText.RequireText(view.ReferenceCaptureId, nameof(view.ReferenceCaptureId), 256);
            ArgumentOutOfRangeException.ThrowIfNegative(view.RevisionOrder);
            RequireEvidence(view.Preview);
            if (view.Base.ValueKind != JsonValueKind.Object || view.Revision.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The view base and revision are JSON objects.", nameof(manifest));
            }
            // The embedded typed documents are the view. The projection, preview and exact reopen set (the preview, the
            // closed window, displayed science and each evidence binding a Ready or Partial layer needs) must be exactly
            // what they derive, so none can be dropped while the embedded revision ID stays the same.
            SiloCenteredViews.RequireConformance(manifest, view);
            foreach (var layer in view.Layers)
            {
                SiloText.RequireText(layer.Kind, nameof(layer.Kind), 256);
                if (!LayerScopes.Contains(layer.Scope) || !LayerReadiness.Contains(layer.Readiness))
                {
                    throw new ArgumentException("A layer binding has an unknown scope or readiness.", nameof(manifest));
                }
                ArgumentOutOfRangeException.ThrowIfNegative(layer.EvidenceOrder);
                if (layer.ObservedUtc is { } observed)
                {
                    RequireUtc(observed, nameof(layer.ObservedUtc));
                }
                if (layer.Evidence is { } evidence)
                {
                    RequireEvidence(evidence);
                }
                // F17: an assessed layer binds a committed evidence product, its observation time and its order.
                if (layer.Readiness is "Ready" or "Partial" or "AssessedEmpty" &&
                    (layer.Evidence is null || layer.ObservedUtc is null || layer.EvidenceOrder == 0))
                {
                    throw new ArgumentException("An assessed layer binds its evidence, observation time and order.", nameof(manifest));
                }
            }
        }
    }

    private static void RequireEvidence(SiloViewEvidence evidence)
    {
        SiloText.RequireText(evidence.ArtifactId, nameof(evidence.ArtifactId), 256);
        SiloText.RequireText(evidence.IdentitySha256, nameof(evidence.IdentitySha256), 256);
        if (!SiloHash.IsValid(SiloCenteredViews.LowerHex(evidence.PayloadSha256)))
        {
            throw new ArgumentException("A view binding names a SHA-256 payload digest.", nameof(evidence));
        }
    }

    internal static void ValidateSegment(SiloJournalSegment segment)
    {
        SiloText.RequireText(segment.InstanceId, nameof(segment.InstanceId), 256);
        SiloHash.Require(segment.StorageBindingId, nameof(segment.StorageBindingId));
        ArgumentOutOfRangeException.ThrowIfLessThan(segment.Generation, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(segment.Sequence, 1);
        RequireUtc(segment.WrittenUtc, nameof(segment.WrittenUtc));
        if (segment.Records.Count == 0)
        {
            throw new ArgumentException("A segment holds at least one record.", nameof(segment));
        }
        for (var index = 0; index < segment.Records.Count; index++)
        {
            var record = segment.Records[index];
            var opens = record is SiloGenerationOpenRecord;
            if (opens != (index == 0 && segment.Sequence == 1))
            {
                throw new ArgumentException("Only the first record of a generation's first segment opens it.", nameof(segment));
            }
            if (record is SiloGenerationOpenRecord open && open.Generation != segment.Generation)
            {
                throw new ArgumentException("A generation-open record opens its own segment's generation.", nameof(segment));
            }
            ValidateRecord(record);
        }
        if (segment.Sequence == 1 && segment.Generation == 1)
        {
            if (segment.Previous is not null || segment.PreviousSha256 is not null)
            {
                throw new ArgumentException("The first segment of generation 1 has no predecessor.", nameof(segment));
            }
            return;
        }
        if (segment.Previous is not { } previous || !SiloHash.IsValid(segment.PreviousSha256))
        {
            throw new ArgumentException("Every later segment names its predecessor and its SHA-256.", nameof(segment));
        }
        var valid = segment.Sequence == 1
            ? previous.Generation < segment.Generation && previous.Sequence >= 1
            : previous == new SiloJournalPosition(segment.Generation, segment.Sequence - 1);
        if (!valid)
        {
            throw new ArgumentException("A segment's predecessor must be its immediate predecessor in (g, seq) order.", nameof(segment));
        }
    }

    internal static void ValidateCheckpoint(SiloJournalCheckpoint checkpoint, IReadOnlyList<(string GroupId, string RevisionId)> published)
    {
        SiloText.RequireText(checkpoint.InstanceId, nameof(checkpoint.InstanceId), 256);
        SiloHash.Require(checkpoint.StorageBindingId, nameof(checkpoint.StorageBindingId));
        SiloHash.Require(checkpoint.CoversSha256, nameof(checkpoint.CoversSha256));
        ArgumentOutOfRangeException.ThrowIfLessThan(checkpoint.Covers.Generation, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(checkpoint.Covers.Sequence, 1);
        (long, long, int)? previous = null;
        foreach (var entry in checkpoint.Entries)
        {
            var key = (entry.Generation, entry.Sequence, entry.Index);
            if (previous is { } last && key.CompareTo(last) <= 0 ||
                new SiloJournalPosition(entry.Generation, entry.Sequence) > checkpoint.Covers || entry.Index < 0)
            {
                throw new ArgumentException("Checkpoint entries are strictly ordered and covered.", nameof(checkpoint));
            }
            previous = key;
        }
        // Each P4 publication names a covered revision, once, in the order its first publish record was committed.
        ArgumentNullException.ThrowIfNull(checkpoint.ReachedP4);
        var first = new Dictionary<(string GroupId, string RevisionId), int>();
        foreach (var revision in published)
        {
            first.TryAdd(revision, first.Count);
        }
        var named = -1;
        foreach (var publication in checkpoint.ReachedP4)
        {
            ArgumentNullException.ThrowIfNull(publication);
            if (!first.TryGetValue((publication.GroupId, publication.RevisionId), out var at) || at <= named)
            {
                throw new ArgumentException("A checkpoint's P4 publications name covered revisions once, in journal order.", nameof(checkpoint));
            }
            named = at;
        }
    }

    internal static void ValidateRecord(SiloJournalRecord record)
    {
        switch (record)
        {
            case SiloGenerationOpenRecord open:
                ArgumentOutOfRangeException.ThrowIfLessThan(open.Generation, 1);
                SiloText.RequireToken(open.Reason, nameof(open.Reason), 64);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(open.HighestPriorGeneration, open.Generation);
                if (open.Generation > 1 && (open.PrefixEnd is null || !SiloHash.IsValid(open.PrefixEndSha256)))
                {
                    throw new ArgumentException("A later generation names the prefix end it was rebuilt from.", nameof(record));
                }
                foreach (var floor in open.IdentityFloors)
                {
                    SiloText.RequireToken(floor.Key, nameof(open.IdentityFloors), 128);
                    ArgumentOutOfRangeException.ThrowIfNegative(floor.Value);
                }
                break;
            case SiloPublishRecord publish:
                RequireGroupKind(publish.GroupKind);
                SiloText.RequireToken(publish.GroupId, nameof(publish.GroupId), 128);
                SiloText.RequireToken(publish.RevisionId, nameof(publish.RevisionId), 128);
                ValidatePublished(publish.Manifest.Locator, publish.Manifest.Sha256, publish.Manifest.Length, publish.Manifest.Qualification);
                var members = new HashSet<(string, string)>();
                foreach (var member in publish.Members)
                {
                    SiloText.RequireText(member.ArtifactId, nameof(member.ArtifactId), 256);
                    SiloText.RequireText(member.ArtifactRevision, nameof(member.ArtifactRevision), 256);
                    SiloHash.Require(member.FactsSha256, nameof(member.FactsSha256));
                    ValidatePublished(member.Locator, member.Sha256, member.Length, member.Qualification);
                    if (!members.Add((member.ArtifactId, member.ArtifactRevision)))
                    {
                        throw new ArgumentException("A publish record names each artifact revision once.", nameof(record));
                    }
                }
                break;
            case SiloLiveIssueRecord live:
                SiloText.RequireText(live.CameraId, nameof(live.CameraId), 256);
                SiloText.RequireText(live.StreamId, nameof(live.StreamId), 512);
                SiloText.RequireToken(live.GroupId, nameof(live.GroupId), 128);
                SiloText.RequireToken(live.RevisionId, nameof(live.RevisionId), 128);
                ArgumentOutOfRangeException.ThrowIfNegative(live.ChangeOrder);
                RequireUtc(live.StartedUtc, nameof(live.StartedUtc));
                break;
            case SiloCopyRecord copy:
                copy.Identity.Validate();
                copy.Locator.Validate();
                SiloText.RequireToken(copy.Custody, nameof(copy.Custody), 64);
                RequireMethod(copy.Method);
                RequireUtc(copy.VerifiedUtc, nameof(copy.VerifiedUtc));
                break;
            case SiloRemapRecord remap:
                remap.Identity.Validate();
                remap.From.Validate();
                remap.To.Validate();
                RequireMethod(remap.Method);
                RequireUtc(remap.VerifiedUtc, nameof(remap.VerifiedUtc));
                break;
            case SiloReserveSurvivorRecord reserve:
                SiloText.RequireToken(reserve.ReservationId, nameof(reserve.ReservationId), 128);
                reserve.Identity.Validate();
                reserve.Survivor.Validate();
                break;
            case SiloReleaseSurvivorRecord release:
                SiloText.RequireToken(release.ReservationId, nameof(release.ReservationId), 128);
                SiloText.RequireText(release.Reason, nameof(release.Reason), 512);
                break;
            case SiloPinRecord pin:
                SiloText.RequireToken(pin.PinId, nameof(pin.PinId), 128);
                SiloText.RequireToken(pin.TargetKind, nameof(pin.TargetKind), 32);
                SiloText.RequireText(pin.TargetId, nameof(pin.TargetId), 256);
                SiloText.RequireText(pin.TargetRevision, nameof(pin.TargetRevision), 256);
                SiloText.RequireText(pin.Reason, nameof(pin.Reason), 512);
                break;
            case SiloPolicyRecord policy:
                SiloText.RequireToken(policy.PolicyId, nameof(policy.PolicyId), 128);
                break;
            case SiloExpiryIntentRecord intent:
                SiloText.RequireToken(intent.IntentId, nameof(intent.IntentId), 128);
                intent.Role.Validate();
                SiloText.RequireText(intent.Authorization, nameof(intent.Authorization), 512);
                SiloText.RequireText(intent.PolicyReason, nameof(intent.PolicyReason), 512);
                if (intent.Targets.Count == 0)
                {
                    throw new ArgumentException("An expiry intent names at least one exact target copy.", nameof(record));
                }
                foreach (var target in intent.Targets)
                {
                    target.Locator.Validate();
                    SiloText.RequireToken(target.Custody, nameof(target.Custody), 64);
                }
                break;
            case SiloExpiryDispositionRecord disposition:
                SiloText.RequireToken(disposition.IntentId, nameof(disposition.IntentId), 128);
                disposition.Locator.Validate();
                RequireUtc(disposition.ObservedUtc, nameof(disposition.ObservedUtc));
                SiloText.RequireText(disposition.Evidence, nameof(disposition.Evidence), 1024);
                break;
            case SiloAvailabilityRecord availability:
                availability.Identity.Validate();
                availability.Locator?.Validate();
                var copyState = availability.State is SiloAvailabilityState.Evicted or SiloAvailabilityState.Missing
                    or SiloAvailabilityState.Quarantined or SiloAvailabilityState.Restored;
                var roleState = availability.State is SiloAvailabilityState.Missing or SiloAvailabilityState.Conflict
                    or SiloAvailabilityState.PolicyExpired;
                if (availability.Scope == SiloAvailabilityScope.Copy ? !copyState || availability.Locator is null : !roleState || availability.Locator is not null)
                {
                    throw new ArgumentException("An availability state must match its scope.", nameof(record));
                }
                SiloText.RequireText(availability.Evidence, nameof(availability.Evidence), 1024);
                break;
            case SiloAnchorRecord anchor:
                anchor.Checkpoint.Validate();
                SiloHash.Require(anchor.CheckpointSha256, nameof(anchor.CheckpointSha256));
                SiloHash.Require(anchor.CoversSha256, nameof(anchor.CoversSha256));
                ArgumentOutOfRangeException.ThrowIfNegative(anchor.CheckpointLength);
                break;
            default:
                throw new ArgumentException("Unknown journal record.", nameof(record));
        }
    }

    private static void ValidatePublished(SiloExactLocator locator, string sha256, long length, SiloQualification qualification)
    {
        locator.Validate();
        SiloHash.Require(sha256, nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RequireMethod(qualification.Method);
        RequireUtc(qualification.VerifiedUtc, nameof(qualification.VerifiedUtc));
        if (!qualification.ProfileQualified)
        {
            throw new ArgumentException("A committed record names only persistence-qualified locators.", nameof(qualification));
        }
    }

    private static void RequireMethod(string method)
    {
        if (!string.Equals(method, SiloFormats.ExactGetSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Physical proof is an exact-version GET with an independent SHA-256.", nameof(method));
        }
    }

    internal static void RequireGroupKind(string? kind)
    {
        if (kind is null || kind.Length is 0 or > 64 || kind[0] is < 'a' or > 'z' ||
            kind.Any(static character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new ArgumentException("A lowercase group kind token is required.", nameof(kind));
        }
    }

    internal static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Times are recorded in UTC.", name);
        }
    }
}

/// <summary>Bytes that claim a supported format but are malformed or violate it. An authenticated prefix ends here.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloInvalidObjectException : SiloDataException
{
    public SiloInvalidObjectException(string subject, string reason, Exception? inner = null)
        : base($"{subject} is invalid: {reason}.", inner)
    {
        Subject = subject;
    }

    public string Subject { get; }
}
