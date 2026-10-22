using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>How the last refresh attempt ended. <see cref="InProgress"/> left behind by a stopped process reads as interrupted.</summary>
public enum CameraAgentTimeDataRefreshOutcome
{
    InProgress,
    Adopted,
    Unchanged,
    NotNewer,
    Failed
}

/// <summary>The durable record of the last refresh attempt. Its start time is the daily floor's origin.</summary>
public sealed record CameraAgentTimeDataRefreshState(
    DateTimeOffset LastAttemptUtc,
    bool Manual,
    CameraAgentTimeDataRefreshOutcome Outcome,
    string ReasonCode,
    string Detail,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? LastSuccessUtc);

/// <summary>One earlier adoption, kept so results computed with that pair stay attributable after its files are retired.</summary>
public sealed record CameraAgentManagedTimeAdoption(string Pair, DateTimeOffset AdoptedUtc, DateTimeOffset RetrievedUtc,
    string EarthOrientationSha256, string LeapSecondsSha256);

/// <summary>The adopted managed pair: its directory and the bounded adoption history, newest first.</summary>
public sealed record CameraAgentManagedTimePointer(string Pair, DateTimeOffset AdoptedUtc, string? Previous,
    IReadOnlyList<CameraAgentManagedTimeAdoption> History);

/// <summary>A downloaded, verified pair ready for adoption, with its original bytes and retrieval metadata.</summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "The verified bytes are written once and never mutated.")]
public sealed record CameraAgentTimeDataCandidate(
    byte[] EarthOrientation, DateTimeOffset? EarthOrientationLastModifiedUtc, string EarthOrientationSha512,
    byte[] LeapSeconds, DateTimeOffset? LeapSecondsLastModifiedUtc, string LeapSecondsHash,
    DateTimeOffset RetrievedUtc)
{
    public string EarthOrientationSha256 { get; } = Convert.ToHexString(SHA256.HashData(EarthOrientation));
    public string LeapSecondsSha256 { get; } = Convert.ToHexString(SHA256.HashData(LeapSeconds));
}

/// <summary>
/// The managed time-data pair under <c>{RawIngressRoot}/.time-data/</c>, which survives restarts and upgrades with the
/// rest of the raw-ingress state. Each adopted pair is an immutable directory <c>pairs/{id}/</c> laid out like the
/// bundled one (the two provider files byte for byte plus <c>provenance.json</c>). Adoption writes and flushes that
/// directory first and then atomically replaces <c>current.json</c>, which names it; that rename is the only commit
/// point, so a stop before it leaves the prior pair active and an unreferenced directory that the next adoption
/// removes. The current pair and an intact predecessor are kept when one exists; the predecessor can skip a damaged
/// pair or preserve the only intact pair covering the adoption time. <c>refresh.json</c> records the last attempt and is written before
/// any request, so the daily floor survives a restart. Only this process writes here.
/// </summary>
public sealed partial class CameraAgentManagedTimeDataStore
{
    public const string DirectoryName = ".time-data";
    public const string PointerSchema = "hvo-managed-time-data-pointer-v1";
    public const string ProvenanceSchema = "hvo-managed-time-data-v1";
    public const string StateSchema = "hvo-time-data-refresh-state-v1";
    public const int MaximumHistory = 32;
    internal const string PointerFile = "current.json";
    internal const string StateFile = "refresh.json";
    internal const string PairsDirectory = "pairs";
    private const int MaximumMetadataBytes = CameraAgentPinnedOrbitalTimeSource.MaximumProvenanceBytes;
    private const int MaximumRetiredPerAdoption = 16;

    private long verificationRevision;

    /// <summary>In-memory cache revision shared by the refresh job and time source; the revision resets on restart.</summary>
    internal long VerificationRevision => Interlocked.Read(ref verificationRevision);

    /// <summary>Recheck managed verification even when a refresh leaves the pointer unchanged.</summary>
    internal void InvalidateVerification() => Interlocked.Increment(ref verificationRevision);

    /// <summary>Test seam: called after the pair directory is durable and before <c>current.json</c> is replaced.</summary>
    internal Action? BeforeCommit { get; set; }

    public CameraAgentManagedTimeDataStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>The directory of an adopted pair named by the pointer.</summary>
    public string PairDirectory(string pair)
        => IsPairName(pair) ? Path.Combine(RootDirectory, PairsDirectory, pair) : throw new InvalidDataException();

    /// <summary>The adopted pair, or null before the first adoption. A malformed pointer throws <see cref="InvalidDataException"/>.</summary>
    public async ValueTask<CameraAgentManagedTimePointer?> ReadPointerAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalAsync(Path.Combine(RootDirectory, PointerFile), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : ParsePointer(bytes);
    }

    /// <summary>The last attempt, or null when none was recorded. A malformed record throws <see cref="InvalidDataException"/>.</summary>
    public async ValueTask<CameraAgentTimeDataRefreshState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalAsync(Path.Combine(RootDirectory, StateFile), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : ParseState(bytes);
    }

    /// <summary>Durably replaces the attempt record.</summary>
    public async ValueTask WriteStateAsync(CameraAgentTimeDataRefreshState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var root = OpenRoot();
        await PublishJsonAsync(root, StateFile, PublishMode.Replace, writer =>
        {
            writer.WriteString("schema", StateSchema);
            writer.WriteString("lastAttemptUtc", Utc(state.LastAttemptUtc));
            writer.WriteBoolean("manual", state.Manual);
            writer.WriteString("outcome", state.Outcome.ToString());
            writer.WriteString("reasonCode", state.ReasonCode);
            writer.WriteString("detail", state.Detail);
            WriteTime(writer, "completedUtc", state.CompletedUtc);
            WriteTime(writer, "lastSuccessUtc", state.LastSuccessUtc);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the candidate as a new immutable pair directory, then atomically makes it current. The prior pair stays
    /// current if anything before the pointer replacement fails. Returns the new pointer.
    /// </summary>
    public async ValueTask<CameraAgentManagedTimePointer> AdoptAsync(CameraAgentTimeDataCandidate candidate, DateTimeOffset adoptedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var root = OpenRoot();
        CameraAgentManagedTimePointer? current;
        try
        {
            current = await ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // A damaged pointer never blocks adopting a verified pair; its history is lost with it.
            current = null;
        }
        var pair = string.Concat(adoptedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), "-",
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        var relative = string.Concat(PairsDirectory, "/", pair, "/");
        await PublishBytesAsync(root, relative + CameraAgentTimeDataSources.EarthOrientationFile, candidate.EarthOrientation, cancellationToken).ConfigureAwait(false);
        await PublishBytesAsync(root, relative + CameraAgentTimeDataSources.LeapSecondsFile, candidate.LeapSeconds, cancellationToken).ConfigureAwait(false);
        await PublishJsonAsync(root, relative + CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName, PublishMode.CreateNew,
            writer => WriteProvenance(writer, candidate), cancellationToken).ConfigureAwait(false);
        BeforeCommit?.Invoke();

        var previous = await SelectPreviousAsync(current, candidate, adoptedUtc, cancellationToken).ConfigureAwait(false);
        var adoption = new CameraAgentManagedTimeAdoption(pair, adoptedUtc.ToUniversalTime(), candidate.RetrievedUtc.ToUniversalTime(),
            candidate.EarthOrientationSha256, candidate.LeapSecondsSha256);
        var pointer = new CameraAgentManagedTimePointer(pair, adoption.AdoptedUtc, previous,
            [adoption, .. (current?.History ?? []).Take(MaximumHistory - 1)]);
        await PublishJsonAsync(root, PointerFile, PublishMode.Replace, writer => WritePointer(writer, pointer), cancellationToken).ConfigureAwait(false);
        RetireUnreferenced(pointer);
        return pointer;
    }

    /// <summary>Retains a verified pair, preserving the predecessor when it alone covers the adoption time.</summary>
    private async Task<string?> SelectPreviousAsync(CameraAgentManagedTimePointer? current, CameraAgentTimeDataCandidate candidate,
        DateTimeOffset adoptedUtc, CancellationToken cancellationToken)
    {
        if (current is null) return null;
        var adopted = await CameraAgentPinnedOrbitalTimeSource.ReadProvenancedAsync(PairDirectory(current.Pair), ProvenanceSchema,
            cancellationToken).ConfigureAwait(false);
        var predecessor = current.Previous is { } previous
            ? await CameraAgentPinnedOrbitalTimeSource.ReadProvenancedAsync(PairDirectory(previous), ProvenanceSchema,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (adopted is null) return predecessor is null ? null : current.Previous;
        var instant = new OrbitalUtcInterval(adoptedUtc, adoptedUtc);
        if (predecessor is not null && CameraAgentPinnedOrbitalTimeSource.Covers(predecessor.Data, instant) &&
            !CameraAgentPinnedOrbitalTimeSource.Covers(adopted.Data, instant) &&
            !CameraAgentPinnedOrbitalTimeSource.Covers(OrbitalTimeData.FromFinals2000A(candidate.EarthOrientation, candidate.LeapSeconds), instant))
            return current.Previous;
        return current.Pair;
    }

    /// <summary>Best-effort removal of pair directories that are neither current nor previous, bounded per call.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Retirement never fails an adoption that already committed.")]
    internal int RetireUnreferenced(CameraAgentManagedTimePointer pointer)
    {
        var retired = 0;
        try
        {
            var pairs = Path.Combine(RootDirectory, PairsDirectory);
            foreach (var directory in Directory.EnumerateDirectories(pairs).Take(MaximumRetiredPerAdoption + 2))
            {
                var name = Path.GetFileName(directory);
                if (name == pointer.Pair || name == pointer.Previous || new DirectoryInfo(directory).LinkTarget is not null) continue;
                Directory.Delete(directory, recursive: true);
                retired++;
            }
            AtomicPublisher.CleanupTemporaries(OpenRoot(), RootDirectory, MaximumRetiredPerAdoption);
        }
        catch (Exception)
        {
            // The next adoption retries; unreferenced directories are never read.
        }
        return retired;
    }

    internal static bool IsPairName(string? name) => name is not null && PairName().IsMatch(name);

    private PhysicalRoot OpenRoot()
    {
        Directory.CreateDirectory(RootDirectory);
        return PhysicalRoot.Open(RootDirectory);
    }

    private static async Task PublishBytesAsync(PhysicalRoot root, string relativePath, byte[] bytes, CancellationToken cancellationToken)
        => await AtomicPublisher.PublishAsync(root, relativePath, PublishMode.CreateNew,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);

    private static async Task PublishJsonAsync(PhysicalRoot root, string relativePath, PublishMode mode, Action<Utf8JsonWriter> write,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        await AtomicPublisher.PublishAsync(root, relativePath, mode,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
    }

    private static void WriteProvenance(Utf8JsonWriter writer, CameraAgentTimeDataCandidate candidate)
    {
        writer.WriteString("schema", ProvenanceSchema);
        Entry("earthOrientation", CameraAgentTimeDataSources.EarthOrientationFile, CameraAgentTimeDataSources.EarthOrientationProduct,
            CameraAgentTimeDataSources.EarthOrientationUri, candidate.EarthOrientationLastModifiedUtc, candidate.EarthOrientation.Length,
            candidate.EarthOrientationSha256, CameraAgentTimeDataSources.EarthOrientationChecksumAlgorithm, candidate.EarthOrientationSha512,
            CameraAgentTimeDataSources.EarthOrientationChecksumsUri, CameraAgentTimeDataSources.EarthOrientationTerms,
            CameraAgentTimeDataSources.EarthOrientationTermsUri);
        Entry("leapSeconds", CameraAgentTimeDataSources.LeapSecondsFile, CameraAgentTimeDataSources.LeapSecondsProduct,
            CameraAgentTimeDataSources.LeapSecondsUri, candidate.LeapSecondsLastModifiedUtc, candidate.LeapSeconds.Length,
            candidate.LeapSecondsSha256, CameraAgentTimeDataSources.LeapSecondsChecksumAlgorithm, candidate.LeapSecondsHash,
            CameraAgentTimeDataSources.LeapSecondsUri, CameraAgentTimeDataSources.LeapSecondsTerms, CameraAgentTimeDataSources.LeapSecondsUri);

        void Entry(string name, string file, string product, Uri source, DateTimeOffset? modified, int length, string sha256,
            string algorithm, string checksum, Uri checksumSource, string terms, Uri termsSource)
        {
            writer.WriteStartObject(name);
            writer.WriteString("file", file);
            writer.WriteString("product", product);
            writer.WriteString("sourceUrl", source.AbsoluteUri);
            writer.WriteString("retrievedUtc", Utc(candidate.RetrievedUtc));
            WriteTime(writer, "lastModifiedUtc", modified);
            writer.WriteNumber("lengthBytes", length);
            writer.WriteString("sha256", sha256);
            writer.WriteStartObject("providerChecksum");
            writer.WriteString("algorithm", algorithm);
            writer.WriteString("value", checksum);
            writer.WriteString("sourceUrl", checksumSource.AbsoluteUri);
            writer.WriteEndObject();
            writer.WriteString("terms", terms);
            writer.WriteString("termsUrl", termsSource.AbsoluteUri);
            writer.WriteEndObject();
        }
    }

    private static void WritePointer(Utf8JsonWriter writer, CameraAgentManagedTimePointer pointer)
    {
        writer.WriteString("schema", PointerSchema);
        writer.WriteString("pair", pointer.Pair);
        writer.WriteString("adoptedUtc", Utc(pointer.AdoptedUtc));
        if (pointer.Previous is null) writer.WriteNull("previous");
        else writer.WriteString("previous", pointer.Previous);
        writer.WriteStartArray("history");
        foreach (var adoption in pointer.History)
        {
            writer.WriteStartObject();
            writer.WriteString("pair", adoption.Pair);
            writer.WriteString("adoptedUtc", Utc(adoption.AdoptedUtc));
            writer.WriteString("retrievedUtc", Utc(adoption.RetrievedUtc));
            writer.WriteString("earthOrientationSha256", adoption.EarthOrientationSha256);
            writer.WriteString("leapSecondsSha256", adoption.LeapSecondsSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static CameraAgentManagedTimePointer ParsePointer(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            // A non-object root or history item would make property reads throw InvalidOperationException instead.
            if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != PointerSchema || Text(root, "pair") is not { } pair ||
                !IsPairName(pair) || !root.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array ||
                history.GetArrayLength() is 0 or > MaximumHistory ||
                history.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException();
            var previous = Text(root, "previous");
            if (previous is not null && !IsPairName(previous)) throw new InvalidDataException();
            var adoptions = history.EnumerateArray().Select(static item => new CameraAgentManagedTimeAdoption(
                Text(item, "pair") is { } name && IsPairName(name) ? name : throw new InvalidDataException(),
                Time(item, "adoptedUtc") ?? throw new InvalidDataException(), Time(item, "retrievedUtc") ?? throw new InvalidDataException(),
                Hash(item, "earthOrientationSha256"), Hash(item, "leapSecondsSha256"))).ToArray();
            if (adoptions[0].Pair != pair) throw new InvalidDataException();
            return new(pair, Time(root, "adoptedUtc") ?? throw new InvalidDataException(), previous, adoptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The managed time-data pointer is malformed.", exception);
        }
    }

    private static CameraAgentTimeDataRefreshState ParseState(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != StateSchema ||
                !Enum.TryParse<CameraAgentTimeDataRefreshOutcome>(Text(root, "outcome"), out var outcome) ||
                !Enum.IsDefined(outcome) || !root.TryGetProperty("manual", out var manual) || manual.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException();
            return new(Time(root, "lastAttemptUtc") ?? throw new InvalidDataException(), manual.GetBoolean(), outcome,
                Text(root, "reasonCode") ?? throw new InvalidDataException(), Text(root, "detail") ?? string.Empty,
                Time(root, "completedUtc"), Time(root, "lastSuccessUtc"));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The time-data refresh record is malformed.", exception);
        }
    }

    private static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumMetadataBytes) throw new InvalidDataException();
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time) writer.WriteString(name, Utc(time));
        else writer.WriteNull(name);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name)
        => Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) && value.Offset == TimeSpan.Zero ? value : null;

    private static string Hash(JsonElement element, string name)
        => Text(element, name) is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit) ? hash : throw new InvalidDataException();

    [GeneratedRegex("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PairName();
}
