using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

/// <summary>
/// One checked master of a per-input family selection: its kind, library artifact, payload hash and library exposure.
/// </summary>
internal sealed record CalibrationFamilySelectedMaster(string Kind, Guid ArtifactId, string PayloadSha256, TimeSpan Exposure);

/// <summary>
/// The per-input calibration-family selection (#1475): the family identity, the member chosen for the input, and the
/// bias, dark, flat and defect masters it names, in that order, after the checks
/// <see cref="SqliteCalibrationLibraryStore.SelectFamilyMastersAsync"/> lists.
/// </summary>
internal sealed record CalibrationFamilyMasterSelection(
    string FamilyIdentitySha256,
    string MemberId,
    IReadOnlyList<CalibrationFamilySelectedMaster> Masters);

/// <summary>The selection for one input, or the stable reason the input is excluded.</summary>
internal sealed record CalibrationFamilySelectionResult(
    CalibrationFamilyMasterSelection? Selection,
    string ReasonCode)
{
    public bool IsSelected => Selection is not null;
}

public sealed partial class SqliteCalibrationLibraryStore
{
    private const int FamilyMasterHashBufferBytes = 64 * 1024;

    /// <summary>
    /// Selects the family member for one light and checks each master it names against the library before use.
    /// Each master must be a master of a published bundle whose stored kind, payload hash, agent, rig, sensor,
    /// acquisition model and input layout equal the member's, whose temperature and light-exposure applicability
    /// cover the member's ranges, and which is in effect when the light was observed. A bias, dark or flat must also
    /// have the member's gain and offset; a defect master may serve every gain, as the family contract allows. The
    /// payload must then hash to that SHA-256. Finally the committed evidence of each master's bundle must pass the
    /// check <see cref="SelectAsync"/> applies to the active bundle: the profile marker hashes to its recorded
    /// identity, each manifest matches its library record (artifact ID, role, variant equal to the kind, checksum,
    /// payload path, layout and conditions), each payload hashes to its record, and a virtual acquisition's sources
    /// reproduce from its durable plan. A failure there is <c>master-corrupt</c>. Any failure excludes the light with
    /// a stable reason and no other master is substituted. The library is read in one short read transaction; files
    /// are hashed afterwards, outside the store gate and any transaction.
    /// </summary>
    /// <remarks>
    /// The result is not pinned. The normalization plan freezes it with the input, and a later claim re-confirms it
    /// with <see cref="ConfirmFamilyMastersAsync"/>.
    /// </remarks>
    internal async Task<CalibrationFamilySelectionResult> SelectFamilyMastersAsync(
        CalibrationFamilyV1 family,
        ReconstructionDescriptor light,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(light);
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.family.select");
        try
        {
            var result = await SelectFamilyMastersCoreAsync(family, light, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("calibration.outcome", result.IsSelected ? "selected" : "rejected");
            activity?.SetTag("calibration.reason", result.ReasonCode);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private async Task<CalibrationFamilySelectionResult> SelectFamilyMastersCoreAsync(
        CalibrationFamilyV1 family,
        ReconstructionDescriptor light,
        CancellationToken cancellationToken)
    {
        var observed = light.Timing.ExposureStartedUtc;
        var resolution = CalibrationFamilyContract.Evaluate(family, new CalibrationFamilyInputV1(
            light.Capture.AgentId,
            light.Capture.RigId,
            light.Profiles.Rig.Sha256,
            light.Profiles.Sensor.Sha256,
            light.Profiles.Calibration,
            light.Profiles.Mask.Sha256,
            light.Layout,
            light.Controls.EffectiveGain,
            light.Controls.EffectiveOffset,
            light.Controls.EffectiveTemperatureC,
            light.Controls.EffectiveExposure,
            observed));
        if (resolution.Member is not { } member)
        {
            return FamilyExcluded(resolution.ReasonCode!);
        }

        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var masters = member.Masters
            .OrderBy(static master => MasterKindRank(master.Kind))
            .ToArray();
        var stored = new StoredFamilyMaster?[masters.Length];
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginRead(connection);
            var bundles = new Dictionary<string, PersistedBundle>(StringComparer.Ordinal);
            for (var index = 0; index < masters.Length; index++)
            {
                stored[index] = await ReadFamilyMasterAsync(
                    connection, transaction, masters[index].ArtifactId, bundles, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return FamilyExcluded(CalibrationFamilyReasonCodes.MasterCorrupt);
        }
        for (var index = 0; index < masters.Length; index++)
        {
            if (StoredMasterReason(family, member, masters[index], stored[index], observed) is { } reason)
            {
                return FamilyExcluded(reason);
            }
        }

        var selected = new CalibrationFamilySelectedMaster[masters.Length];
        for (var index = 0; index < masters.Length; index++)
        {
            var master = stored[index]!;
            if (await VerifyFamilyMasterPayloadAsync(master, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                return FamilyExcluded(reason);
            }
            selected[index] = new CalibrationFamilySelectedMaster(
                master.Artifact.Kind, master.Artifact.ArtifactId, master.Artifact.PayloadSha256, master.Artifact.Exposure);
        }
        if (await VerifyFamilyBundleEvidenceAsync(
            Array.ConvertAll(stored, static master => master!), cancellationToken).ConfigureAwait(false) is { } evidenceReason)
        {
            return FamilyExcluded(evidenceReason);
        }
        return new CalibrationFamilySelectionResult(
            new CalibrationFamilyMasterSelection(
                CalibrationFamilyContractJson.ComputeIdentitySha256(family), member.MemberId, Array.AsReadOnly(selected)),
            CalibrationFamilyReasonCodes.Selected);
    }

    /// <summary>
    /// Re-confirms the masters of a frozen selection when work that uses them is claimed: each is still a master of a
    /// published bundle with the frozen kind and payload hash, its payload hashes to it, and its bundle's committed
    /// evidence passes the check <see cref="SelectFamilyMastersAsync"/> applies. Returns null, or the stable reason
    /// the first master fails. Confirmation bypasses the file-metadata cache so unchanged length and timestamp
    /// cannot hide corrupt bytes. Conditions and effective windows were settled when the selection was frozen and
    /// are not evaluated again.
    /// </summary>
    internal async Task<string?> ConfirmFamilyMastersAsync(
        IReadOnlyList<CalibrationFamilyMasterV1> masters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masters);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var stored = new StoredFamilyMaster?[masters.Count];
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginRead(connection);
            var bundles = new Dictionary<string, PersistedBundle>(StringComparer.Ordinal);
            for (var index = 0; index < masters.Count; index++)
            {
                stored[index] = await ReadFamilyMasterAsync(
                    connection, transaction, masters[index].ArtifactId, bundles, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return CalibrationFamilyReasonCodes.MasterCorrupt;
        }
        for (var index = 0; index < masters.Count; index++)
        {
            var reason = stored[index] is not { } master
                ? CalibrationFamilyReasonCodes.MasterMissing
                : !string.Equals(master.Bundle.PublicationState, "published", StringComparison.Ordinal)
                    ? CalibrationFamilyReasonCodes.MasterUnpublished
                    : !string.Equals(master.Artifact.Kind, masters[index].Kind, StringComparison.Ordinal) ||
                      !string.Equals(
                          master.Artifact.PayloadSha256, masters[index].PayloadSha256, StringComparison.OrdinalIgnoreCase)
                        ? CalibrationFamilyReasonCodes.MasterMismatch
                        : null;
            if (reason is not null)
            {
                return reason;
            }
        }
        foreach (var master in stored)
        {
            if (await VerifyFamilyMasterPayloadAsync(master!, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                return reason;
            }
        }
        return await VerifyFamilyBundleEvidenceAsync(
            Array.ConvertAll(stored, static master => master!), cancellationToken, forceValidation: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the committed evidence of each distinct bundle the masters come from, as <see cref="SelectAsync"/>
    /// validates the active bundle. Selection may reuse the file-metadata cache; frozen-master confirmation always
    /// verifies the bytes again. Any failure is <c>master-corrupt</c>.
    /// </summary>
    private async Task<string?> VerifyFamilyBundleEvidenceAsync(
        IReadOnlyList<StoredFamilyMaster> masters,
        CancellationToken cancellationToken,
        bool forceValidation = false)
    {
        foreach (var bundle in masters.Select(static master => master.Bundle).DistinctBy(static bundle => bundle.BundleIdentitySha256))
        {
            try
            {
                await ValidateEvidenceCachedAsync(bundle, cancellationToken, forceValidation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return CalibrationFamilyReasonCodes.MasterCorrupt;
            }
        }
        return null;
    }

    /// <summary>Reads a library master and its validated bundle, or null when no master has that artifact ID.</summary>
    private static async Task<StoredFamilyMaster?> ReadFamilyMasterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid artifactId,
        Dictionary<string, PersistedBundle> bundles,
        CancellationToken cancellationToken)
    {
        string bundleId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT bundle_id FROM calibration_library_artifacts
                WHERE artifact_id = $artifact AND role = 'master';
                """;
            command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string id)
            {
                return null;
            }
            bundleId = id;
        }
        if (!bundles.TryGetValue(bundleId, out var bundle))
        {
            bundle = await ReadBundleAsync(connection, transaction, bundleId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A calibration artifact row names a missing bundle.");
            bundles.Add(bundleId, bundle);
        }
        return new StoredFamilyMaster(
            bundle.Snapshot,
            bundle.Snapshot.Bundle.Artifacts.Single(artifact => artifact.ArtifactId == artifactId));
    }

    private static string? StoredMasterReason(
        CalibrationFamilyV1 family,
        CalibrationFamilyMemberV1 member,
        CalibrationFamilyMasterV1 master,
        StoredFamilyMaster? stored,
        DateTimeOffset observed)
    {
        if (stored is null)
        {
            return CalibrationFamilyReasonCodes.MasterMissing;
        }
        if (!string.Equals(stored.Bundle.PublicationState, "published", StringComparison.Ordinal))
        {
            return CalibrationFamilyReasonCodes.MasterUnpublished;
        }
        var artifact = stored.Artifact;
        var applicability = stored.Bundle.Bundle.Applicability;
        var perGain = !string.Equals(master.Kind, CalibrationReferenceKinds.Defect, StringComparison.Ordinal);
        if (!string.Equals(artifact.Kind, master.Kind, StringComparison.Ordinal) ||
            !string.Equals(artifact.PayloadSha256, master.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(applicability.AgentId, family.AgentId, StringComparison.Ordinal) ||
            !string.Equals(applicability.RigId, family.RigId, StringComparison.Ordinal) ||
            !string.Equals(applicability.RigProfileSha256, family.RigProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(applicability.SensorProfileSha256, family.SensorProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !AcquisitionModelMatchesLight(stored.Bundle.Bundle, family.AcquisitionModel) ||
            applicability.InputLayout != member.InputLayout ||
            perGain && (artifact.Gain != member.Gain || artifact.Offset != member.Offset) ||
            applicability.MinimumTemperatureC is not { } minimumTemperature ||
            minimumTemperature > member.MinimumTemperatureC ||
            applicability.MaximumTemperatureC < member.MaximumTemperatureC ||
            applicability.MinimumLightExposure is { } minimumExposure &&
            (minimumExposure > member.MinimumLightExposure ||
             applicability.MaximumLightExposure < member.MaximumLightExposure))
        {
            return CalibrationFamilyReasonCodes.MasterMismatch;
        }
        return observed < applicability.EffectiveFromUtc ||
               applicability.EffectiveUntilUtc is { } until && observed >= until
            ? CalibrationFamilyReasonCodes.MasterStale
            : null;
    }

    /// <summary>
    /// Hashes exactly the master's published byte length with a fixed buffer. A payload of another length or hash is
    /// corrupt; one that cannot be opened or read is unreadable.
    /// </summary>
    private async Task<string?> VerifyFamilyMasterPayloadAsync(
        StoredFamilyMaster master,
        CancellationToken cancellationToken)
    {
        var expectedLength = master.Bundle.Bundle.Applicability.OutputLayout.ByteLength;
        var buffer = ArrayPool<byte>.Shared.Rent(FamilyMasterHashBufferBytes);
        try
        {
            var path = ResolveSafePath(PayloadPathForManifest(master.Artifact.ManifestRelativePath));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                if (stream.Length != expectedLength)
                {
                    return CalibrationFamilyReasonCodes.MasterCorrupt;
                }
                for (var remaining = expectedLength; remaining > 0;)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return CalibrationFamilyReasonCodes.MasterCorrupt;
                    }
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            return string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()),
                    master.Artifact.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase)
                ? null
                : CalibrationFamilyReasonCodes.MasterCorrupt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return CalibrationFamilyReasonCodes.MasterUnreadable;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int MasterKindRank(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => 0,
            CalibrationReferenceKinds.Dark => 1,
            CalibrationReferenceKinds.Flat => 2,
            _ => 3
        };

    private static CalibrationFamilySelectionResult FamilyExcluded(string reasonCode)
        => new(null, reasonCode);

    private sealed record StoredFamilyMaster(
        CalibrationLibraryBundleSnapshot Bundle,
        CalibrationLibraryArtifactV1 Artifact);
}
