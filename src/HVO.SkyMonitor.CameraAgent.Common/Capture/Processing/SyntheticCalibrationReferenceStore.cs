using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record SyntheticCalibrationBundle(
    CalibrationLibraryBundleV1 LibraryBundle,
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
    CameraAgentClearReferenceLoader loader,
    CalibrationArtifactPublisher? publisher = null,
    SqliteCalibrationLibraryStore? calibrationLibrary = null)
{
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly CalibrationArtifactPublisher _publisher = publisher ??
        new CalibrationArtifactPublisher(options, NullCalibrationPublicationFaultInjector.Instance);
    private readonly SqliteCalibrationLibraryStore? _calibrationLibrary = calibrationLibrary;
    private readonly ConcurrentDictionary<string, Task<SyntheticCalibrationBundle>> _cache = new(StringComparer.Ordinal);

    internal async ValueTask<SyntheticCalibrationBundle> GetOrCreateAsync(
        ReconstructionDescriptor lightDescriptor,
        SyntheticCalibrationModelV1 model,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lightDescriptor);
        ArgumentNullException.ThrowIfNull(model);
        if (!CalibrationCaptureProcessingStep.MatchesSyntheticCalibrationModel(lightDescriptor, model))
        {
            throw new InvalidDataException("The synthetic calibration source model identity does not match the light capture.");
        }
        var layout = lightDescriptor.Layout;
        var modelElement = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            model,
            lightDescriptor.Capture.AgentId,
            lightDescriptor.Capture.RigId,
            InputLayout = layout,
            lightDescriptor.Profiles
        }));
        var publicationIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(modelElement);
        var sourceModelIdentity = SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model);
        var task = _cache.GetOrAdd(
            publicationIdentity,
            _ => CreateAsync(
                lightDescriptor, model, publicationIdentity, sourceModelIdentity, CancellationToken.None));
        try
        {
            var bundle = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await ValidateCachedBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            return bundle;
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            _cache.TryRemove(new KeyValuePair<string, Task<SyntheticCalibrationBundle>>(publicationIdentity, task));
            throw;
        }
    }

    private async Task<SyntheticCalibrationBundle> CreateAsync(
        ReconstructionDescriptor lightDescriptor,
        SyntheticCalibrationModelV1 model,
        string publicationIdentity,
        string sourceModelIdentity,
        CancellationToken cancellationToken)
    {
        var layout = lightDescriptor.Layout;
        var relativeDirectory = Path.Combine("calibration", "synthetic", publicationIdentity.ToUpperInvariant());
        var bundleRelativePath = Normalize(Path.Combine(relativeDirectory, CalibrationLibraryEvidenceNames.BundleEnvelope));
        var profileRelativePath = Normalize(Path.Combine(relativeDirectory, CalibrationLibraryEvidenceNames.ProfileMarker));
        EnsureCommittedBundleIsComplete(relativeDirectory, bundleRelativePath, profileRelativePath);
        var generated = SyntheticCalibrationReferenceGenerator.Generate(
            layout.Width, layout.Height, layout.PixelFormat, model);
        var referenceInputs = new Dictionary<string, ProcessingArtifact>(StringComparer.Ordinal);
        var referenceManifests = new Dictionary<string, ArtifactManifestV2>(StringComparer.Ordinal);
        var descriptors = new List<CalibrationReferenceDescriptorV1>(4);
        var libraryArtifacts = new List<CalibrationLibraryArtifactV1>(4);
        foreach (var reference in CreateReferences(generated, model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifactId = CreateGuid(publicationIdentity, reference.Kind);
            var captureId = CreateGuid(publicationIdentity, $"capture:{reference.Kind}");
            var payloadRelativePath = Normalize(Path.Combine(relativeDirectory, $"{reference.Kind}.bin"));
            var manifestRelativePath = Normalize(Path.Combine(relativeDirectory, $"{reference.Kind}.json"));
            var payload = reference.Frame.PixelData;
            var payloadSha256 = PayloadChecksum.ComputeSha256(payload.Span);
            var descriptor = CreateDescriptor(
                lightDescriptor,
                model,
                publicationIdentity,
                sourceModelIdentity,
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
            await _publisher.PublishPairAsync(
                payloadRelativePath,
                payload,
                manifestRelativePath,
                CaptureContractJson.Serialize(manifest),
                cancellationToken).ConfigureAwait(false);
            referenceInputs.Add(reference.Kind, CameraAgentClearReferenceLoader.CreateArtifact(descriptor, payload));
            referenceManifests.Add(reference.Kind, manifest);
            descriptors.Add(new CalibrationReferenceDescriptorV1(
                reference.Kind,
                artifactId,
                payloadSha256,
                reference.Exposure,
                model.Gain,
                model.TemperatureC));
            libraryArtifacts.Add(new CalibrationLibraryArtifactV1(
                reference.Kind,
                CalibrationLibraryArtifactRoles.Master,
                artifactId,
                manifestRelativePath,
                payloadSha256,
                reference.Exposure,
                model.Gain,
                null,
                model.TemperatureC,
                null,
                [],
                null));
        }

        var profile = new ReferenceCalibrationProfileV1(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            $"synthetic-{publicationIdentity[..16].ToUpperInvariant()}",
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
        var outputLayout = referenceManifests.Values.First().Descriptor.Layout;
        var bundle = new CalibrationLibraryBundleV1(
            CalibrationLibraryBundleV1.CurrentSchemaVersion,
            $"synthetic-{publicationIdentity[..32].ToUpperInvariant()}",
            CalibrationLibraryBundleSources.SyntheticReferencesV1,
            DateTimeOffset.UnixEpoch,
            profileRelativePath,
            profileIdentity,
            sourceModelIdentity,
            new CalibrationApplicabilityV1(
                lightDescriptor.Capture.AgentId,
                lightDescriptor.Capture.RigId,
                lightDescriptor.Profiles.Rig.Sha256,
                lightDescriptor.Profiles.Sensor.Sha256,
                layout,
                outputLayout,
                model.Gain,
                model.Gain,
                null,
                null,
                null,
                null,
                model.TemperatureC,
                model.TemperatureC,
                DateTimeOffset.UnixEpoch,
                null),
            libraryArtifacts);
        await _publisher.PublishBundleEnvelopeAsync(
            bundleRelativePath,
            CalibrationLibraryContractJson.Serialize(bundle),
            cancellationToken).ConfigureAwait(false);
        await _publisher.PublishProfileMarkerAsync(
            profileRelativePath,
            profileJson,
            cancellationToken).ConfigureAwait(false);
        foreach (var manifestRelativePath in libraryArtifacts.Select(static artifact => artifact.ManifestRelativePath))
        {
            loader.RegisterRetentionHold(manifestRelativePath);
        }
        if (_calibrationLibrary is not null)
        {
            _ = await _calibrationLibrary.AdoptPublishedBundleAsync(bundle, CancellationToken.None).ConfigureAwait(false);
            _publisher.InjectFault(CalibrationPublicationFaultPoint.AfterSqlitePublication, bundleRelativePath);
        }
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
            bundle,
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
        string publicationIdentity,
        string sourceModelIdentity,
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
            modelIdentitySha256 = sourceModelIdentity,
            referenceKind = kind,
            generator = SyntheticCalibrationReferenceGenerator.AlgorithmVersion
        });
        return new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(
                light.Capture.AgentId,
                light.Capture.RigId,
                CreateCaptureSequence(publicationIdentity, kind),
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
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model", model.SchemaVersion, sourceModelIdentity)
            },
            CreateOutputLayout(light.Layout, frame),
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

    private void EnsureCommittedBundleIsComplete(
        string relativeDirectory,
        string bundleRelativePath,
        string profileRelativePath)
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
            .Append(bundleRelativePath)
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
            .Append(Normalize(Path.Combine(relativeDirectory, CalibrationLibraryEvidenceNames.BundleEnvelope)))
            .Append(Normalize(Path.Combine(relativeDirectory, CalibrationLibraryEvidenceNames.ProfileMarker)))
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

    private static FrameLayoutDescriptor CreateOutputLayout(FrameLayoutDescriptor input, Linear16Frame frame)
        => input with
        {
            StrideBytes = frame.StrideBytes,
            SampleDepthBits = 16,
            ContainerDepthBits = 16,
            Packing = FrameSamplePacking.ByteAligned,
            BlackLevel = 0,
            WhiteLevel = ushort.MaxValue,
            ByteLength = frame.PixelData.Length,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };

    private static string Normalize(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');
}
