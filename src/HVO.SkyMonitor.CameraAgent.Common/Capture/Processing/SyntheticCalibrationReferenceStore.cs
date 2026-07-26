using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record SyntheticCalibrationBundle(
    ReferenceCalibrationProfileV1 Profile,
    ReadOnlyMemory<byte> ProfileJson,
    string ProfileIdentitySha256,
    IReadOnlyDictionary<string, ProcessingArtifact> References,
    IReadOnlyDictionary<string, ArtifactManifestV2> ReferenceManifests,
    IReadOnlyList<ProcessingAuxiliaryInput> AuxiliaryInputs,
    IReadOnlyList<SyntheticCalibrationEvidenceFile> EvidenceFiles);

internal sealed record SyntheticCalibrationEvidenceFile(
    string RelativePath,
    long Length,
    DateTime LastWriteUtc,
    string Sha256);

internal sealed class SyntheticCalibrationReferenceStore(
    IOptions<CameraAgentHostOptions> options,
    CameraAgentClearReferenceLoader loader)
{
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly ConcurrentDictionary<string, Task<SyntheticCalibrationBundle>> _cache = new(StringComparer.Ordinal);

    internal async ValueTask<SyntheticCalibrationBundle> GetOrCreateAsync(
        ReconstructionDescriptor lightDescriptor,
        SyntheticCalibrationModelV1 model,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lightDescriptor);
        ArgumentNullException.ThrowIfNull(model);
        var layout = lightDescriptor.Layout;
        var modelElement = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            model,
            layout.Width,
            layout.Height,
            layout.PixelFormat,
            layout.ByteOrder,
            layout.SampleDepthBits,
            layout.ContainerDepthBits,
            layout.Packing,
            layout.CfaPattern,
            RigProfileSha256 = lightDescriptor.Profiles.Rig.Sha256,
            SensorProfileSha256 = lightDescriptor.Profiles.Sensor.Sha256
        }));
        var modelIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(modelElement);
        var task = _cache.GetOrAdd(
            modelIdentity,
            _ => CreateAsync(lightDescriptor, model, modelIdentity, CancellationToken.None));
        try
        {
            var bundle = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await ValidateCachedBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            return bundle;
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            _cache.TryRemove(new KeyValuePair<string, Task<SyntheticCalibrationBundle>>(modelIdentity, task));
            throw;
        }
    }

    private async Task<SyntheticCalibrationBundle> CreateAsync(
        ReconstructionDescriptor lightDescriptor,
        SyntheticCalibrationModelV1 model,
        string modelIdentity,
        CancellationToken cancellationToken)
    {
        var layout = lightDescriptor.Layout;
        var relativeDirectory = Path.Combine("calibration", "synthetic", modelIdentity.ToUpperInvariant());
        var profileRelativePath = Normalize(Path.Combine(relativeDirectory, "calibration-profile.json"));
        EnsureCommittedBundleIsComplete(relativeDirectory, profileRelativePath);
        var generated = SyntheticCalibrationReferenceGenerator.Generate(
            layout.Width, layout.Height, layout.PixelFormat, model);
        var referenceInputs = new Dictionary<string, ProcessingArtifact>(StringComparer.Ordinal);
        var referenceManifests = new Dictionary<string, ArtifactManifestV2>(StringComparer.Ordinal);
        var descriptors = new List<CalibrationReferenceDescriptorV1>(4);
        foreach (var reference in CreateReferences(generated, model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifactId = CreateGuid(modelIdentity, reference.Kind);
            var captureId = CreateGuid(modelIdentity, $"capture:{reference.Kind}");
            var payloadRelativePath = Normalize(Path.Combine(relativeDirectory, $"{reference.Kind}.bin"));
            var manifestRelativePath = Normalize(Path.Combine(relativeDirectory, $"{reference.Kind}.json"));
            var payload = reference.Frame.PixelData;
            var payloadSha256 = PayloadChecksum.ComputeSha256(payload.Span);
            var descriptor = CreateDescriptor(
                lightDescriptor,
                model,
                modelIdentity,
                reference.Kind,
                reference.Exposure,
                artifactId,
                captureId,
                payloadRelativePath,
                payloadSha256,
                reference.Frame);
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion,
                descriptor,
                payloadRelativePath);
            await WriteImmutableAsync(payloadRelativePath, payload, cancellationToken).ConfigureAwait(false);
            await WriteImmutableAsync(
                manifestRelativePath,
                CaptureContractJson.Serialize(manifest),
                cancellationToken).ConfigureAwait(false);
            loader.RegisterRetentionHold(manifestRelativePath);
            referenceInputs.Add(reference.Kind, CameraAgentClearReferenceLoader.CreateArtifact(descriptor, payload));
            referenceManifests.Add(reference.Kind, manifest);
            descriptors.Add(new CalibrationReferenceDescriptorV1(
                reference.Kind,
                artifactId,
                payloadSha256,
                reference.Exposure,
                model.Gain,
                model.TemperatureC));
        }

        var profile = new ReferenceCalibrationProfileV1(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            $"synthetic-{modelIdentity[..16].ToUpperInvariant()}",
            model.SchemaVersion,
            "VirtualSky deterministic software reference generation; not a physical calibration",
            DateTimeOffset.UnixEpoch,
            null,
            layout.Width,
            layout.Height,
            layout.PixelFormat,
            generated.FlatNormalizationAdu,
            model.Gain,
            model.Gain,
            model.TemperatureC,
            model.TemperatureC,
            descriptors);
        var profileJson = ReferenceCalibrationProfileJson.Serialize(profile);
        var profileIdentity = ProcessingIdentity.ComputePayloadSha256(profileJson);
        await WriteImmutableAsync(
            profileRelativePath,
            profileJson,
            cancellationToken).ConfigureAwait(false);
        var auxiliaryInputs = new List<ProcessingAuxiliaryInput>
        {
            new(
                "calibration-profile",
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                IdentitySha256: profileIdentity,
                Payload: profileJson)
        };
        auxiliaryInputs.AddRange(referenceInputs.Select(pair => new ProcessingAuxiliaryInput(
            $"{pair.Key}-reference",
            ProcessingAuxiliaryInputKind.Artifact,
            ProcessingInputSelector.Raw(pair.Key),
            ArtifactId: pair.Value.ArtifactId)));
        return new SyntheticCalibrationBundle(
            profile,
            profileJson,
            profileIdentity,
            referenceInputs,
            referenceManifests,
            auxiliaryInputs,
            await CaptureEvidenceFilesAsync(relativeDirectory, cancellationToken).ConfigureAwait(false));
    }

    private static ReconstructionDescriptor CreateDescriptor(
        ReconstructionDescriptor light,
        SyntheticCalibrationModelV1 model,
        string modelIdentity,
        string kind,
        TimeSpan exposure,
        Guid artifactId,
        Guid captureId,
        string relativePath,
        string payloadSha256,
        Linear16Frame frame)
    {
        var created = DateTimeOffset.UnixEpoch;
        var recipeOptions = CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = model.SchemaVersion,
            modelIdentitySha256 = modelIdentity,
            referenceKind = kind,
            generator = SyntheticCalibrationReferenceGenerator.AlgorithmVersion
        });
        return new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(
                light.Capture.AgentId,
                light.Capture.RigId,
                CreateCaptureSequence(modelIdentity, kind),
                captureId),
            new CaptureTimingDescriptor(created, created, created.Add(exposure), created.Add(exposure), created.Add(exposure)),
            new CaptureControlDescriptor(
                exposure,
                exposure,
                model.Gain,
                model.Gain,
                null,
                null,
                model.TemperatureC,
                model.TemperatureC),
            light.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor("synthetic-calibration", model.SchemaVersion, modelIdentity)
            },
            light.Layout with
            {
                StrideBytes = frame.StrideBytes,
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = frame.PixelData.Length
            },
            new ArtifactDescriptor(
                artifactId,
                FrameArtifactRole.Raw,
                "SyntheticCalibrationReference",
                kind,
                created.Add(exposure),
                [],
                RecipeIdentityDescriptor.Create(
                    "synthetic-calibration-reference",
                    "1.0.0",
                    SyntheticCalibrationReferenceGenerator.AlgorithmVersion,
                    recipeOptions),
                "application/x-hvo-linear-frame",
                payloadSha256));
    }

    private async ValueTask WriteImmutableAsync(
        string relativePath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(relativePath);
        var directory = Path.GetDirectoryName(path)!;
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!directoryExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(_root, directory);
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var existing = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] existingHash;
            await using (existing.ConfigureAwait(false))
            {
                existingHash = await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            var expectedHash = SHA256.HashData(bytes.Span);
            if (info.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(existingHash, expectedHash))
            {
                throw new InvalidDataException("Synthetic calibration reference conflicts with immutable evidence.");
            }
            return;
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not guarantee durable filesystem publication.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporaryPath, path, overwrite: false);
            RawIngressFileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void EnsureCommittedBundleIsComplete(string relativeDirectory, string profileRelativePath)
    {
        var directory = ResolvePath(Normalize(relativeDirectory));
        var profileExists = File.Exists(ResolvePath(profileRelativePath));
        if (!profileExists)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new InvalidDataException("Synthetic calibration evidence exists without its commit marker.");
            }
            return;
        }
        var required = CalibrationReferenceKinds.All
            .SelectMany(kind => new[]
            {
                Normalize(Path.Combine(relativeDirectory, $"{kind}.bin")),
                Normalize(Path.Combine(relativeDirectory, $"{kind}.json"))
            })
            .Append(profileRelativePath);
        if (required.Any(path => !File.Exists(ResolvePath(path))))
        {
            throw new InvalidDataException("Committed synthetic calibration evidence is incomplete.");
        }
    }

    private async Task<SyntheticCalibrationEvidenceFile[]> CaptureEvidenceFilesAsync(
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        var paths = CalibrationReferenceKinds.All
            .SelectMany(kind => new[]
            {
                Normalize(Path.Combine(relativeDirectory, $"{kind}.bin")),
                Normalize(Path.Combine(relativeDirectory, $"{kind}.json"))
            })
            .Append(Normalize(Path.Combine(relativeDirectory, "calibration-profile.json")))
            .ToArray();
        var evidence = new SyntheticCalibrationEvidenceFile[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            var path = ResolvePath(paths[index]);
            var info = new FileInfo(path);
            evidence[index] = new(
                paths[index],
                info.Length,
                info.LastWriteTimeUtc,
                await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false));
        }
        return evidence;
    }

    private async Task ValidateCachedBundleAsync(
        SyntheticCalibrationBundle bundle,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in bundle.EvidenceFiles)
        {
            var path = ResolvePath(evidence.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != evidence.Length || info.LastWriteTimeUtc != evidence.LastWriteUtc ||
                !string.Equals(
                    await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false),
                    evidence.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Cached synthetic calibration evidence changed after publication.");
            }
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private string ResolvePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Synthetic calibration reference path escapes CameraAgent storage.");
        }
        return path;
    }

    private static IEnumerable<(string Kind, Linear16Frame Frame, TimeSpan Exposure)> CreateReferences(
        SyntheticCalibrationReferenceSet references,
        SyntheticCalibrationModelV1 model)
    {
        yield return (CalibrationReferenceKinds.Bias, references.Bias, model.BiasExposure);
        yield return (CalibrationReferenceKinds.Dark, references.Dark, model.DarkExposure);
        yield return (CalibrationReferenceKinds.Flat, references.Flat, model.FlatExposure);
        yield return (CalibrationReferenceKinds.Defect, references.DefectMask, model.BiasExposure);
    }

    private static Guid CreateGuid(string identity, string discriminator)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{discriminator}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static long CreateCaptureSequence(string identity, string kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:sequence:{kind}"));
        const ulong reservedStart = (ulong)long.MaxValue / 2;
        var offset = BinaryPrimitives.ReadUInt64BigEndian(hash) % reservedStart;
        return checked((long)(reservedStart + offset));
    }

    private static string Normalize(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');
}
