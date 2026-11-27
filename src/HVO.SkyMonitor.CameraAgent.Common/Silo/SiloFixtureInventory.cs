using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloFixtureSelection(
    string Format, string SourceId, long MaximumBytes, long MinimumFreeBytes,
    IReadOnlyList<SiloFixtureSelectionEntry?> Records, int? DisplayRetentionDays = null);

internal sealed record SiloFixtureSelectionEntry(string Stem, string ImageSha256, string SidecarSha256);

internal sealed record SiloFixtureSidecar(
    string Format, string SourceCaptureId, string SourceCameraId, string SourceArtifactId,
    DateTimeOffset CapturedFromUtc, DateTimeOffset CapturedToUtc, SiloFixtureSourceDependency? ReopenImage = null);

internal sealed record SiloFixtureSourceDependency(string SourceArtifactId, string Sha256);

internal sealed record SiloFixtureObject(string File, string Role, long Length, string Sha256,
    SiloRetentionClass RetentionClass)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? ImmediatelyAgeEligible { get; }
    public string AgeEligibilityReason { get; } = "target-retention-policy-unavailable";
}

internal sealed record SiloFixtureRecord(string? Stem, string Status, string? Reason = null,
    SiloFixtureSidecar? Source = null, string? GroupId = null, string? ArtifactId = null,
    IReadOnlyList<SiloFixtureObject>? Objects = null,
    SiloFixtureSourceDependency? UsableReopenImage = null);

internal sealed record SiloFixtureInventory(SiloFixtureSelection Selection, string SelectionSha256, DateTimeOffset AsOfUtc,
    IReadOnlyList<SiloFixtureRecord> Records)
{
    internal const string SelectionFormat = "hvo.cameraagent.silo.fixture-selection/1";
    internal const string SidecarFormat = "hvo.cameraagent.silo.fixture-image/1";
    internal const string ImageRevision = "image-1";
    internal const string SidecarRevision = "sidecar-1";
    internal const string GroupRevision = "1";
    internal const string GroupKind = "import";
    internal const int MaximumRecords = 8;
    internal const int MaximumImageBytes = 8 * 1024 * 1024;
    internal const int MaximumSidecarBytes = 16 * 1024;
    private const int MaximumImagePixels = 16 * 1024 * 1024;
    private const long MaximumSelectionBytes = 32 * 1024 * 1024;
    private const int MaximumSelectionFileBytes = 16 * 1024;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> PngTrailer => [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];

    private static readonly JsonSerializerOptions StrictJson = new(SiloJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static async ValueTask<SiloFixtureInventory> ReadAsync(ConfinedReadDirectory source, string selectionFile,
        DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        if (!IsSelectionFile(selectionFile))
            throw new ArgumentException("The selection filename must be a single '*.selection.json' name.", nameof(selectionFile));

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(source, selectionFile, MaximumSelectionFileBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new SiloConflictException("unsupported-source-size", "Fixture selection size is outside the supported bounds.");
        }
        var selection = Parse<SiloFixtureSelection>(bytes);
        if (selection.Format != SelectionFormat || !SiloText.IsToken(selection.SourceId, 96) ||
            selection.MaximumBytes is < 1 or > MaximumSelectionBytes || selection.MinimumFreeBytes < 1 ||
            selection.Records.Count is < 1 or > MaximumRecords)
            throw new InvalidDataException("Unsupported fixture selection format, source ID, budget or record count.");

        var duplicateStems = selection.Records.Where(static entry => entry is not null)
            .GroupBy(static entry => entry!.Stem, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = new List<SiloFixtureRecord>(selection.Records.Count);
        foreach (var entry in selection.Records)
        {
            if (entry is null || !SiloText.IsToken(entry.Stem, 80) || !SiloHash.IsValid(entry.ImageSha256) ||
                !SiloHash.IsValid(entry.SidecarSha256))
            {
                records.Add(new(null, "failed", "invalid-selection-entry"));
                continue;
            }
            if (duplicateStems.Contains(entry.Stem))
            {
                records.Add(new(entry.Stem, "failed", "duplicate-selection"));
                continue;
            }
            records.Add(await InspectAsync(source, selection, entry, asOfUtc, cancellationToken).ConfigureAwait(false));
        }

        ResolveLineage(records);
        return new(selection, SiloHash.Sha256(bytes), asOfUtc, records);
    }

    private static async ValueTask<SiloFixtureRecord> InspectAsync(ConfinedReadDirectory source, SiloFixtureSelection selection,
        SiloFixtureSelectionEntry entry, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        var stem = entry.Stem;
        try
        {
            var sidecar = await ReadBoundedAsync(source, stem + ".json", MaximumSidecarBytes, cancellationToken).ConfigureAwait(false);
            if (SiloHash.Sha256(sidecar) != entry.SidecarSha256)
                return new(stem, "failed", "source-checksum-mismatch");

            var facts = Parse<SiloFixtureSidecar>(sidecar);
            if (facts.Format != SidecarFormat || !SiloText.IsToken(facts.SourceCaptureId, 96) ||
                !SiloText.IsToken(facts.SourceArtifactId, 96) || !SiloText.IsToken(facts.SourceCameraId, 96) ||
                facts.CapturedFromUtc.Offset != TimeSpan.Zero || facts.CapturedToUtc.Offset != TimeSpan.Zero ||
                facts.CapturedFromUtc >= facts.CapturedToUtc || facts.CapturedToUtc > asOfUtc ||
                facts.ReopenImage is { } dependency &&
                    (!SiloText.IsToken(dependency.SourceArtifactId, 96) || !SiloHash.IsValid(dependency.Sha256)))
                return new(stem, "skipped", "unsupported-sidecar");

            var image = await ReadBoundedAsync(source, stem + ".png", MaximumImageBytes, cancellationToken).ConfigureAwait(false);
            if (SiloHash.Sha256(image) != entry.ImageSha256)
                return new(stem, "failed", "source-checksum-mismatch");
            if (image.Length < 45 || !image.AsSpan().StartsWith(PngSignature) ||
                !image.AsSpan(8, 8).SequenceEqual("\0\0\0\rIHDR"u8) || !image.AsSpan().EndsWith(PngTrailer) ||
                !PngImageCodec.IsDecodablePng(image, MaximumImagePixels))
                return new(stem, "skipped", "unsupported-image");

            var artifactId = ArtifactIdFor(selection.SourceId, facts.SourceArtifactId, entry.ImageSha256);
            return new(stem, "selected", Source: facts, GroupId: "fixture-" + artifactId.ToString("N"), ArtifactId: artifactId.ToString("D"),
                Objects: [new(stem + ".png", "fixture-image", image.LongLength, entry.ImageSha256, SiloRetentionClass.Display),
                          new(stem + ".json", "fixture-sidecar", sidecar.LongLength, entry.SidecarSha256, SiloRetentionClass.Metadata)]);
        }
        catch (JsonException)
        {
            return new(stem, "skipped", "unsupported-sidecar");
        }
        catch (InvalidDataException)
        {
            return new(stem, "skipped", "unsupported-source-size");
        }
        catch (IOException)
        {
            return new(stem, "failed", "source-unreadable-or-nonconfined");
        }
        catch (UnauthorizedAccessException)
        {
            return new(stem, "failed", "source-unreadable-or-nonconfined");
        }
    }

    private static void ResolveLineage(List<SiloFixtureRecord> records)
    {
        var duplicateIds = records.Where(static record => record.Source is not null)
            .GroupBy(static record => record.Source!.SourceArtifactId, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        var usable = new Dictionary<string, SiloFixtureRecord>(StringComparer.Ordinal);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Source is not { } facts)
                continue;
            if (duplicateIds.Contains(facts.SourceArtifactId))
            {
                records[index] = record with { Status = "failed", Reason = "duplicate-source-artifact" };
                continue;
            }
            if (facts.ReopenImage is { } dependency)
            {
                if (!usable.TryGetValue(dependency.SourceArtifactId, out var prior) ||
                    prior.Source!.CapturedToUtc > facts.CapturedFromUtc || prior.Objects![0].Sha256 != dependency.Sha256)
                {
                    records[index] = record with { Status = "skipped", Reason = "incomplete-reopen-lineage" };
                    continue;
                }
                records[index] = record with { UsableReopenImage = dependency };
            }
            usable.Add(facts.SourceArtifactId, records[index]);
        }
    }

    internal static Guid ArtifactIdFor(string sourceId, string artifactId, string imageSha256) =>
        new(Convert.FromHexString(SiloHash.Sha256(SiloJson.Serialize(new[] { SidecarFormat, sourceId, artifactId, imageSha256 })))
            .AsSpan(0, 16));

    private static bool IsSelectionFile(string name) =>
        SiloText.IsToken(name, 95) && name.EndsWith(".selection.json", StringComparison.Ordinal);

    private static T Parse<T>(byte[] bytes) where T : class =>
        JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new JsonException("Expected a JSON object.");

    internal static async ValueTask<byte[]> ReadBoundedAsync(ConfinedReadDirectory source, string name, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = source.OpenRead(name);
        var length = stream.Length;
        if (length < 1 || length > maximumBytes)
            throw new InvalidDataException("Fixture size is outside the supported bounds.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != length)
            throw new IOException("Fixture length changed while reading.");
        return bytes;
    }
}
