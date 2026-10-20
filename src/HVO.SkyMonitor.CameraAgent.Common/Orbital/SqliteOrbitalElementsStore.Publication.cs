using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

public sealed partial class SqliteOrbitalElementsStore
{
    private const string IntentHeader = "hvo-orbital-intent-v1";
    private const string IntentExtension = ".intent";
    private const int MaximumIntentBytes = 4096;
    private const int MaximumReconciledEntries = 1024;

    /// <inheritdoc />
    /// <remarks>
    /// A candidate whose bytes do not bind to its own manifest is rejected with <see cref="ArgumentException"/>
    /// before any storage is touched; the attempt stays InFlight so the caller can record it as quarantined.
    /// Every accepted file is written once under its content identity and never edited. A file named by an
    /// intent but bound to no published snapshot is a verified orphan and is removed on failure or on restart.
    /// </remarks>
    public async ValueTask<OrbitalPublication> PublishAndActivateAsync(
        string attemptId,
        OrbitalSnapshot candidate,
        ReadOnlyMemory<byte> rawBytes,
        CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        ArgumentNullException.ThrowIfNull(candidate);
        var prepared = Prepare(candidate, rawBytes);
        return await ExecuteGatedAsync(
            (connection, token) => PublishCoreAsync(connection, attemptId, prepared, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Supplied raw bytes are hashed; a supplied SHA-256 must agree with them. Only bytes within the provider
    /// bound are retained, and only the newest <see cref="OrbitalElementsStoreSettings.MaximumQuarantinedPayloads"/>
    /// candidates keep a file. Failing to retain evidence never prevents the quarantine from being recorded.
    /// The quarantine summary is cleared from provider state by the next successful activation.
    /// </remarks>
    public async ValueTask RecordQuarantineAsync(
        string attemptId,
        ReadOnlyMemory<byte> rawBytes,
        string? rawSha256,
        string reasonCode,
        string detail,
        CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        RequireReasonCode(reasonCode);
        ArgumentNullException.ThrowIfNull(detail);
        if (rawSha256 is not null)
        {
            RequireSha256(rawSha256, nameof(rawSha256));
        }
        var sha256 = rawSha256;
        long? length = null;
        if (!rawBytes.IsEmpty)
        {
            var computed = Convert.ToHexString(SHA256.HashData(rawBytes.Span));
            if (rawSha256 is not null && !string.Equals(rawSha256, computed, StringComparison.Ordinal))
            {
                throw new ArgumentException("The supplied raw SHA-256 does not match the raw bytes.", nameof(rawSha256));
            }
            sha256 = computed;
            length = rawBytes.Length;
        }
        var boundedDetail = BoundText(detail);
        var retain = sha256 is not null && rawBytes.Length is > 0 and <= MaximumRawBytes &&
            _settings.MaximumQuarantinedPayloads > 0;
        await ExecuteGatedAsync(async (connection, token) =>
        {
            using (var check = BeginImmediate(connection))
            {
                await RequireInFlightAsync(connection, check, attemptId, token).ConfigureAwait(false);
            }
            var retained = false;
            if (retain)
            {
                try
                {
                    retained = await RetainQuarantineFileAsync(connection, sha256!, rawBytes, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsCancellation(exception, token))
                {
                    await SettleFailedAttemptAsync(attemptId, OrbitalAttemptOutcome.Interrupted, "orbital.attempt-cancelled",
                        "The attempt was cancelled while retaining quarantine evidence.", null).ConfigureAwait(false);
                    throw new OperationCanceledException("The orbital quarantine was cancelled.", exception, token);
                }
                catch (Exception exception) when (IsStorageFault(exception))
                {
                    QuarantineFileFailed(_logger, sha256!, ClassifyFault(exception), exception);
                }
            }
            using var transaction = BeginImmediate(connection);
            await RequireInFlightAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
            var now = ToUnixMilliseconds(Truncate(Now()));
            await CompleteAttemptAsync(connection, transaction, attemptId, OrbitalAttemptOutcome.Quarantined, reasonCode,
                boundedDetail, now, token, rawSha256: sha256, rawLengthBytes: length, quarantineRetained: retained)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_provider_state
                SET quarantine_observed_unix_ms = $now, quarantine_raw_sha256 = $raw, quarantine_reason = $reason,
                    revision = revision + 1
                WHERE provider = 'celestrak';
                """, token, ("$now", now), ("$raw", (object?)sha256 ?? DBNull.Value), ("$reason", reasonCode))
                .ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE orbital_attempts SET quarantine_retained = 0
                WHERE quarantine_retained = 1 AND attempt_sequence NOT IN (
                    SELECT attempt_sequence FROM orbital_attempts WHERE quarantine_retained = 1
                    ORDER BY attempt_sequence DESC LIMIT $maximum);
                """, token, ("$maximum", _settings.MaximumQuarantinedPayloads)).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            CandidateQuarantined(_logger, reasonCode, attemptId, retained);
            await PruneQuarantineBestEffortAsync(connection).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PreparedPublication(
        OrbitalSnapshot Snapshot,
        OrbitalSnapshotSource Source,
        ReadOnlyMemory<byte> Raw,
        string RawSha256,
        byte[] Payload,
        string PayloadSha256,
        byte[] Manifest,
        string ManifestSha256,
        string ManifestFileSha256);

    /// <summary>Proves the candidate, its canonical bytes and the raw provider bytes bind to one identity.</summary>
    private static PreparedPublication Prepare(OrbitalSnapshot candidate, ReadOnlyMemory<byte> rawBytes)
    {
        var manifest = candidate.Manifest;
        if (manifest.Sources.Count != 1 ||
            !string.Equals(manifest.Sources[0].Provider, ProviderKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("An orbital candidate must carry exactly one CelesTrak source.", nameof(candidate));
        }
        var source = manifest.Sources[0];
        if (rawBytes.Length is 0 or > MaximumRawBytes || rawBytes.Length != source.RawLengthBytes)
        {
            throw new ArgumentException("The raw bytes do not match the candidate's recorded raw length.", nameof(rawBytes));
        }
        var rawSha256 = Convert.ToHexString(SHA256.HashData(rawBytes.Span));
        if (!string.Equals(rawSha256, source.RawSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The raw bytes do not match the candidate's recorded raw SHA-256.", nameof(rawBytes));
        }
        var payload = candidate.SerializePayload();
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
        if (payload.Length != manifest.NormalizedPayloadLengthBytes ||
            !string.Equals(payloadSha256, manifest.NormalizedPayloadSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The candidate payload does not match its manifest.", nameof(candidate));
        }
        var manifestBytes = candidate.SerializeManifest();
        // Parsing the exact bytes that will be stored proves the reader will accept them before they are committed.
        var parsed = OrbitalSnapshot.Parse(manifestBytes, payload);
        if (!parsed.IsValid ||
            !string.Equals(parsed.Snapshot!.Manifest.ManifestSha256, manifest.ManifestSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The candidate does not round-trip through the canonical orbital form.", nameof(candidate));
        }
        return new PreparedPublication(candidate, source, rawBytes, rawSha256, payload, payloadSha256, manifestBytes,
            manifest.ManifestSha256, Convert.ToHexString(SHA256.HashData(manifestBytes)));
    }

    private async ValueTask<OrbitalPublication> PublishCoreAsync(
        SqliteConnection connection,
        string attemptId,
        PreparedPublication prepared,
        CancellationToken cancellationToken)
    {
        string? existingState;
        using (var check = BeginImmediate(connection))
        {
            await RequireInFlightAsync(connection, check, attemptId, cancellationToken).ConfigureAwait(false);
            existingState = await ScalarStringAsync(connection, check,
                "SELECT state FROM orbital_snapshots WHERE manifest_sha256 = $sha;", cancellationToken,
                ("$sha", prepared.ManifestSha256)).ConfigureAwait(false);
        }
        if (string.Equals(existingState, "retired", StringComparison.Ordinal))
        {
            const string RetiredCode = "orbital.manifest-retired";
            await SettleFailedAttemptAsync(attemptId, OrbitalAttemptOutcome.StorageFailed, RetiredCode,
                "The candidate manifest identity was already retired; retirement is final.", prepared).ConfigureAwait(false);
            throw new OrbitalStorageException(RetiredCode, "The orbital candidate's manifest identity has been retired.", null);
        }
        var intentPath = Path.Combine(_rootPath, StagingDirectoryName, attemptId + IntentExtension);
        try
        {
            await WriteIntentAsync(attemptId, prepared, cancellationToken).ConfigureAwait(false);
            await EnsureContentFileAsync(connection, RawDirectoryName, prepared.RawSha256, prepared.Raw, prepared.RawSha256,
                cancellationToken).ConfigureAwait(false);
            _faults.Inject(OrbitalStorageFaultPoint.AfterRaw);
            await EnsureContentFileAsync(connection, PayloadDirectoryName, prepared.PayloadSha256, prepared.Payload,
                prepared.PayloadSha256, cancellationToken).ConfigureAwait(false);
            _faults.Inject(OrbitalStorageFaultPoint.AfterPayload);
            await EnsureContentFileAsync(connection, ManifestDirectoryName, prepared.ManifestSha256, prepared.Manifest,
                prepared.ManifestFileSha256, cancellationToken).ConfigureAwait(false);
            _faults.Inject(OrbitalStorageFaultPoint.AfterManifest);
            // Read back what is about to be committed: activation never binds to bytes it has not verified.
            foreach (var (path, length, sha256) in new[]
            {
                (RawPath(prepared.RawSha256), (long)prepared.Raw.Length, prepared.RawSha256),
                (PayloadPath(prepared.PayloadSha256), (long)prepared.Payload.Length, prepared.PayloadSha256),
                (ManifestPath(prepared.ManifestSha256), (long)prepared.Manifest.Length, prepared.ManifestFileSha256)
            })
            {
                if (!await FileMatchesAsync(path, length, sha256, cancellationToken).ConfigureAwait(false))
                {
                    throw new OrbitalStorageException("orbital.storage-integrity",
                        "A published orbital file did not read back with its identity.", null);
                }
            }
            var publication = await ActivateAsync(connection, attemptId, prepared, cancellationToken).ConfigureAwait(false);
            _activeManifestSha256 = prepared.ManifestSha256;
            TryDeleteIntent(intentPath);
            SnapshotActivated(_logger, prepared.ManifestSha256, publication.PayloadUnchanged, attemptId);
            return publication;
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            await SettleFailedAttemptAsync(attemptId, OrbitalAttemptOutcome.Interrupted, "orbital.attempt-cancelled",
                "The publication was cancelled before activation.", prepared, intentPath).ConfigureAwait(false);
            throw new OperationCanceledException("The orbital publication was cancelled.", exception, cancellationToken);
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            var code = ClassifyFault(exception);
            PublicationFailed(_logger, code, attemptId, exception);
            await SettleFailedAttemptAsync(attemptId, OrbitalAttemptOutcome.StorageFailed, code,
                "Orbital publication could not complete durably; the last-known-good snapshot remains active.",
                prepared, intentPath).ConfigureAwait(false);
            throw new OrbitalStorageException(code, "The orbital candidate could not be published durably.", exception);
        }
    }

    private async ValueTask<OrbitalPublication> ActivateAsync(
        SqliteConnection connection,
        string attemptId,
        PreparedPublication prepared,
        CancellationToken cancellationToken)
    {
        var manifest = prepared.Snapshot.Manifest;
        using var transaction = BeginImmediate(connection);
        await RequireInFlightAsync(connection, transaction, attemptId, cancellationToken).ConfigureAwait(false);
        string? active;
        string? previous;
        long snapshotSequence;
        bool unchanged;
        bool inserted;
        using (var command = CreateCommand(connection, transaction, """
            SELECT p.active_manifest_sha256, p.previous_manifest_sha256, p.snapshot_sequence, s.payload_sha256,
                (SELECT state FROM orbital_snapshots WHERE manifest_sha256 = $sha)
            FROM orbital_provider_state AS p
            LEFT JOIN orbital_snapshots AS s ON s.manifest_sha256 = p.active_manifest_sha256
            WHERE p.provider = 'celestrak';
            """, ("$sha", prepared.ManifestSha256)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The orbital provider state row is missing.");
            }
            active = NullableString(reader, 0);
            previous = NullableString(reader, 1);
            snapshotSequence = reader.GetInt64(2);
            var activePayload = NullableString(reader, 3);
            var state = NullableString(reader, 4);
            if (string.Equals(state, "retired", StringComparison.Ordinal))
            {
                throw new OrbitalStorageException("orbital.manifest-retired",
                    "The orbital candidate's manifest identity has been retired.", null);
            }
            inserted = state is null;
            if (inserted)
            {
                snapshotSequence++;
            }
            // Unchanged means the active normalized payload is byte-identical; a new manifest still activates.
            unchanged = string.Equals(activePayload, prepared.PayloadSha256, StringComparison.Ordinal);
        }
        var now = ToUnixMilliseconds(Truncate(Now()));
        if (inserted)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO orbital_snapshots(manifest_sha256, snapshot_sequence, attempt_id, manifest_file_sha256,
                    manifest_length_bytes, payload_sha256, payload_length_bytes, raw_sha256, raw_length_bytes,
                    record_count, object_count, minimum_epoch_utc_ticks, maximum_epoch_utc_ticks, retrieved_utc_ticks,
                    published_unix_ms, state, retired_unix_ms, files_pending)
                VALUES($sha, $sequence, $attempt, $manifestFile, $manifestLength, $payload, $payloadLength, $raw,
                    $rawLength, $records, $objects, $minimum, $maximum, $retrieved, $now, 'published', NULL, 0);
                """, cancellationToken,
                ("$sha", prepared.ManifestSha256), ("$sequence", snapshotSequence), ("$attempt", attemptId),
                ("$manifestFile", prepared.ManifestFileSha256), ("$manifestLength", prepared.Manifest.Length),
                ("$payload", prepared.PayloadSha256), ("$payloadLength", prepared.Payload.Length),
                ("$raw", prepared.RawSha256), ("$rawLength", prepared.Raw.Length),
                ("$records", manifest.RecordCount), ("$objects", manifest.ObjectCount),
                ("$minimum", manifest.MinimumEpochUtc.HasValue ? manifest.MinimumEpochUtc.Value.UtcTicks : DBNull.Value),
                ("$maximum", manifest.MaximumEpochUtc.HasValue ? manifest.MaximumEpochUtc.Value.UtcTicks : DBNull.Value),
                ("$retrieved", prepared.Source.RetrievedUtc.UtcTicks), ("$now", now)).ConfigureAwait(false);
        }
        var nextPrevious = active is not null && !string.Equals(active, prepared.ManifestSha256, StringComparison.Ordinal)
            ? active
            : previous;
        // A caller that did not record the retrieval first (the refresh job always does) records it with the activation.
        await ExecuteAsync(connection, transaction, """
            UPDATE orbital_attempts SET retrieved_utc_ticks = $retrieved
            WHERE attempt_id = $attempt AND retrieved_utc_ticks IS NULL;
            """, cancellationToken, ("$retrieved", prepared.Source.RetrievedUtc.UtcTicks), ("$attempt", attemptId))
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE orbital_provider_state
            SET active_manifest_sha256 = $sha, previous_manifest_sha256 = $previous, snapshot_sequence = $sequence,
                last_retrieval_success_utc_ticks = $retrieved, last_activation_unix_ms = $now, revision = revision + 1,
                quarantine_observed_unix_ms = NULL, quarantine_raw_sha256 = NULL, quarantine_reason = NULL
            WHERE provider = 'celestrak';
            """, cancellationToken,
            ("$sha", prepared.ManifestSha256), ("$previous", (object?)nextPrevious ?? DBNull.Value),
            ("$sequence", snapshotSequence), ("$retrieved", prepared.Source.RetrievedUtc.UtcTicks), ("$now", now))
            .ConfigureAwait(false);
        var kind = unchanged ? "unchanged-payload" : "activated";
        await ExecuteAsync(connection, transaction, """
            INSERT INTO orbital_activation_history(history_sequence, manifest_sha256, previous_manifest_sha256,
                payload_sha256, kind, attempt_id, activated_unix_ms, revision)
            VALUES((SELECT COALESCE(MAX(history_sequence), 0) + 1 FROM orbital_activation_history), $sha, $replaced,
                $payload, $kind, $attempt, $now,
                (SELECT revision FROM orbital_provider_state WHERE provider = 'celestrak'));
            """, cancellationToken,
            ("$sha", prepared.ManifestSha256), ("$replaced", (object?)active ?? DBNull.Value),
            ("$payload", prepared.PayloadSha256), ("$kind", kind), ("$attempt", attemptId), ("$now", now))
            .ConfigureAwait(false);
        await CompleteAttemptAsync(connection, transaction, attemptId,
            unchanged ? OrbitalAttemptOutcome.UnchangedPayload : OrbitalAttemptOutcome.Activated,
            unchanged ? "orbital.unchanged-payload" : "orbital.activated", null, now, cancellationToken,
            manifestSha256: prepared.ManifestSha256, rawSha256: prepared.RawSha256, rawLengthBytes: prepared.Raw.Length)
            .ConfigureAwait(false);
        _faults.Inject(OrbitalStorageFaultPoint.BeforeActivationCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OrbitalPublication(prepared.ManifestSha256, prepared.PayloadSha256, unchanged,
            manifest.RecordCount, manifest.ObjectCount);
    }
}
