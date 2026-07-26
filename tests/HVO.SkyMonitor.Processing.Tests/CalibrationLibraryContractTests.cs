using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CalibrationLibraryContractTests
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    public void VirtualBundleRoundTripsCanonicalIdentityAndRejectsIncompleteMasterLineage()
    {
        var bundle = CreateVirtualBundle();

        var bytes = CalibrationLibraryContractJson.Serialize(bundle);
        var identity = CalibrationLibraryContractJson.ComputeIdentitySha256(bundle);
        var parsed = CalibrationLibraryContractJson.Parse(bytes);

        Assert.IsTrue(parsed.Validation.IsValid);
        Assert.IsNotNull(parsed.Value);
        CollectionAssert.AreEqual(bytes, CalibrationLibraryContractJson.Serialize(parsed.Value));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), identity);

        var master = bundle.Artifacts.Single(artifact =>
            artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == CalibrationReferenceKinds.Dark);
        var invalid = bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == master.ArtifactId
                ? artifact with { OrderedSourceArtifactIds = artifact.OrderedSourceArtifactIds.Take(2).ToArray() }
                : artifact).ToArray()
        };
        var validation = CalibrationLibraryContract.Validate(invalid);
        Assert.AreEqual(CalibrationLibraryReasonCodes.InvalidBundle, validation.ReasonCode);
        Assert.AreEqual("bundle.artifacts.dark", validation.FieldPath);
    }

    [TestMethod]
    public void LegacyBundlePreservesUnknownAcquisitionFactsWithoutInventingSources()
    {
        var virtualBundle = CreateVirtualBundle();
        var legacy = virtualBundle with
        {
            Source = CalibrationLibraryBundleSources.LegacySyntheticV1,
            Applicability = virtualBundle.Applicability with
            {
                MinimumOffset = null,
                MaximumOffset = null,
                MinimumLightExposure = null,
                MaximumLightExposure = null
            },
            Artifacts = virtualBundle.Artifacts
                .Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master)
                .Select(static artifact => artifact with
                {
                    Offset = null,
                    OrderedSourceArtifactIds = [],
                    MasterBuildRecipe = null
                })
                .ToArray()
        };

        Assert.IsTrue(CalibrationLibraryContract.Validate(legacy).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Artifacts = [.. legacy.Artifacts, virtualBundle.Artifacts[0]]
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Applicability = legacy.Applicability with { MinimumOffset = 1, MaximumOffset = 1 }
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Applicability = legacy.Applicability with
            {
                MinimumLightExposure = TimeSpan.FromSeconds(5),
                MaximumLightExposure = TimeSpan.FromSeconds(5)
            }
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Artifacts = legacy.Artifacts.Select((artifact, index) => index == 0
                ? artifact with { Offset = 1 }
                : artifact).ToArray()
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Applicability = legacy.Applicability with
            {
                MinimumTemperatureC = null,
                MaximumTemperatureC = null
            }
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Artifacts = legacy.Artifacts.Select(artifact => artifact with { TemperatureC = null }).ToArray(),
            Applicability = legacy.Applicability with
            {
                MinimumTemperatureC = null,
                MaximumTemperatureC = null
            }
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(legacy with
        {
            Applicability = legacy.Applicability with { MinimumGain = 1, MaximumGain = 1 }
        }).IsValid);
    }

    [TestMethod]
    public void VirtualBundleRejectsConditionRecipeAndByteOrderDrift()
    {
        var bundle = CreateVirtualBundle();
        var source = bundle.Artifacts[0];
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == source.ArtifactId
                ? artifact with { Gain = artifact.Gain + 1 }
                : artifact).ToArray()
        }).IsValid);

        var master = bundle.Artifacts.Single(static artifact =>
            artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == CalibrationReferenceKinds.Bias);
        Assert.IsNotNull(master.MasterBuildRecipe);
        var invalidRecipe = RecipeIdentityDescriptor.Create(
            master.MasterBuildRecipe.Name,
            master.MasterBuildRecipe.SemanticVersion,
            master.MasterBuildRecipe.ImplementationVersion,
            CaptureContractJson.SerializeToElement(new { referenceKind = master.Kind, sourceCount = 2 }));
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == master.ArtifactId
                ? artifact with { MasterBuildRecipe = invalidRecipe }
                : artifact).ToArray()
        }).IsValid);

        using var aliasedOptionsDocument = JsonDocument.Parse(
            $$"""{"ReferenceKind":"{{master.Kind}}","SourceCount":3}""");
        var aliasedRecipe = RecipeIdentityDescriptor.Create(
            master.MasterBuildRecipe.Name,
            master.MasterBuildRecipe.SemanticVersion,
            master.MasterBuildRecipe.ImplementationVersion,
            aliasedOptionsDocument.RootElement.Clone());
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == master.ArtifactId
                ? artifact with { MasterBuildRecipe = aliasedRecipe }
                : artifact).ToArray()
        }).IsValid);

        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Applicability = bundle.Applicability with
            {
                InputLayout = bundle.Applicability.InputLayout with { ByteOrder = FrameByteOrder.BigEndian }
            }
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Applicability = bundle.Applicability with
            {
                MinimumGain = 1,
                MaximumGain = 1,
                MinimumOffset = 2,
                MaximumOffset = 2,
                MinimumTemperatureC = 20,
                MaximumTemperatureC = 20
            }
        }).IsValid);
    }

    [TestMethod]
    public void ParseRejectsMalformedDuplicateOversizedAndUnsupportedJsonButNormalizesArtifactOrder()
    {
        var bundle = CreateVirtualBundle();
        var permuted = JsonSerializer.SerializeToUtf8Bytes(bundle with
        {
            Artifacts = bundle.Artifacts.Reverse().ToArray()
        }, ContractJsonOptions);

        var parsed = CalibrationLibraryContractJson.Parse(permuted);

        Assert.IsTrue(parsed.Validation.IsValid);
        Assert.IsNotNull(parsed.Value);
        Assert.AreEqual(CalibrationLibraryArtifactRoles.Source, parsed.Value.Artifacts[0].Role);
        var canonical = Encoding.UTF8.GetString(CalibrationLibraryContractJson.Serialize(bundle));
        Assert.AreEqual(
            CalibrationLibraryReasonCodes.InvalidBundle,
            CalibrationLibraryContractJson.Parse(Encoding.UTF8.GetBytes(canonical.Replace(
                "\"artifacts\":[{", "\"artifacts\":[null,{", StringComparison.Ordinal))).Validation.ReasonCode);
        Assert.AreEqual(
            CalibrationLibraryReasonCodes.InvalidJson,
            CalibrationLibraryContractJson.Parse(Encoding.UTF8.GetBytes(canonical.Insert(
                1, $"\"schemaVersion\":\"{CalibrationLibraryBundleV1.CurrentSchemaVersion}\","))).Validation.ReasonCode);
        Assert.AreEqual(
            CalibrationLibraryReasonCodes.PayloadTooLarge,
            CalibrationLibraryContractJson.Parse(new byte[CalibrationLibraryContractJson.MaximumBundleBytes + 1])
                .Validation.ReasonCode);
        Assert.AreEqual(
            CalibrationLibraryReasonCodes.UnsupportedSchema,
            CalibrationLibraryContractJson.Parse(Encoding.UTF8.GetBytes(canonical.Replace(
                CalibrationLibraryBundleV1.CurrentSchemaVersion,
                "calibration-library-bundle-v2",
                StringComparison.Ordinal))).Validation.ReasonCode);

        var biasMaster = bundle.Artifacts.Single(static artifact =>
            artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == CalibrationReferenceKinds.Bias);
        var nullOptions = JsonSerializer.SerializeToElement<object?>(null);
        var malformedRecipe = biasMaster.MasterBuildRecipe! with
        {
            Options = nullOptions,
            OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(nullOptions)
        };
        var malformedBundle = bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == biasMaster.ArtifactId
                ? artifact with { MasterBuildRecipe = malformedRecipe }
                : artifact).ToArray()
        };
        Assert.AreEqual(
            CalibrationLibraryReasonCodes.InvalidBundle,
            CalibrationLibraryContractJson.Parse(
                JsonSerializer.SerializeToUtf8Bytes(malformedBundle, ContractJsonOptions)).Validation.ReasonCode);

        var first = bundle.Artifacts[0];
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == first.ArtifactId
                ? artifact with { ManifestRelativePath = "C:/calibration/source.json" }
                : artifact).ToArray()
        }).IsValid);
        Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
        {
            Artifacts = bundle.Artifacts.Select((artifact, index) => index == 1
                ? artifact with { ManifestRelativePath = first.ManifestRelativePath.ToUpperInvariant() }
                : artifact).ToArray()
        }).IsValid);
        foreach (var invalidPath in new[]
                 {
                     "calibration/bad?.json",
                     "calibration/bad*.json",
                     "calibration/NUL.json",
                     "calibration/trailing.",
                     "calibration/trailing "
                 })
        {
            Assert.IsFalse(CalibrationLibraryContract.Validate(bundle with
            {
                Artifacts = bundle.Artifacts.Select(artifact => artifact.ArtifactId == first.ArtifactId
                    ? artifact with { ManifestRelativePath = invalidPath }
                    : artifact).ToArray()
            }).IsValid, invalidPath);
        }
    }

    private static CalibrationLibraryBundleV1 CreateVirtualBundle()
    {
        var layout = new FrameLayoutDescriptor(
            4, 2, 8, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb,
            64, 4095, 16)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(4, 2, 0, 0, 4, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0)
        };
        var output = layout with
        {
            SampleDepthBits = 16,
            BlackLevel = 0,
            WhiteLevel = ushort.MaxValue,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        var artifacts = new List<CalibrationLibraryArtifactV1>();
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var sourceIds = new List<Guid>();
            for (var sourceIndex = 0; sourceIndex < 3; sourceIndex++)
            {
                var id = DeterministicGuid($"{kind}:source:{sourceIndex}");
                sourceIds.Add(id);
                artifacts.Add(new(
                    kind,
                    CalibrationLibraryArtifactRoles.Source,
                    id,
                    $"calibration/virtual/bundle/{kind}-source-{sourceIndex}.json",
                    Hash($"{kind}:source:{sourceIndex}"),
                    TimeSpan.FromMilliseconds(1),
                    82,
                    1,
                    -10,
                    sourceIndex,
                    [],
                    null));
            }
            var algorithm = kind == CalibrationReferenceKinds.Defect
                ? CalibrationMasterBuildAlgorithms.BitwiseOrV1
                : CalibrationMasterBuildAlgorithms.MedianV1;
            artifacts.Add(new(
                kind,
                CalibrationLibraryArtifactRoles.Master,
                DeterministicGuid($"{kind}:master"),
                $"calibration/virtual/bundle/{kind}.json",
                Hash($"{kind}:master"),
                TimeSpan.FromMilliseconds(1),
                82,
                1,
                -10,
                null,
                sourceIds,
                RecipeIdentityDescriptor.Create(
                    "calibration-master-build",
                    "1.0.0",
                    algorithm,
                    CaptureContractJson.SerializeToElement(new { referenceKind = kind, sourceCount = 3 }))));
        }
        return new(
            CalibrationLibraryBundleV1.CurrentSchemaVersion,
            "virtual-asi676-bundle",
            CalibrationLibraryBundleSources.VirtualAcquisitionV1,
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
            "calibration/virtual/bundle/calibration-profile.json",
            Hash("profile"),
            Hash("model"),
            new CalibrationApplicabilityV1(
                "agent-1",
                "rig-1",
                Hash("rig"),
                Hash("sensor"),
                layout,
                output,
                82,
                82,
                1,
                1,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                -10,
                -10,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                null),
            artifacts);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Guid DeterministicGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
