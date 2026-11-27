using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingIdentityUtf16Tests
{
    private const string MalformedHighSurrogate = "bad-\uD800";
    private const string MalformedLowSurrogate = "bad-\uDC00";
    private const string ValidAstralValue = "ok-\U0001F52D";

    private static readonly JsonElement EmptyOptions = JsonSerializer.SerializeToElement(new { });
    private static readonly string ValidSha256 = new('A', 64);
    private static readonly PixelPoint Origin = new(1, 2);
    private static readonly PreviewTransform IdentityTransform = new(1, 1);

    [TestMethod]
    public void CreateRecipeIdentityRejectsMalformedUtf16InDescriptorFields()
    {
        var badDescriptors = new[]
        {
            RecipeIdentityDescriptor.Create(MalformedHighSurrogate, "1.0.0", "impl-v1", EmptyOptions),
            RecipeIdentityDescriptor.Create("recipe", MalformedHighSurrogate, "impl-v1", EmptyOptions),
            RecipeIdentityDescriptor.Create("recipe", "1.0.0", MalformedLowSurrogate, EmptyOptions)
        };

        foreach (var descriptor in badDescriptors)
        {
            Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.CreateRecipeIdentity(descriptor));
        }

        var validDescriptor = RecipeIdentityDescriptor.Create(ValidAstralValue, "1.0.0", "impl-v1", EmptyOptions);
        var identity = ProcessingIdentity.CreateRecipeIdentity(validDescriptor);
        Assert.AreEqual(64, identity.IdentitySha256.Length);
    }

    [TestMethod]
    public void IsValidAnnotationUtf16ValidatesObjectsSegmentsAndMetadataCornerLines()
    {
        var validObject = new ProjectedAnnotationObject("star:1", ValidAstralValue, Origin);
        var validSegment = new ProjectedAnnotationSegment("ori", Origin, Origin);
        var validMetadata = new MetadataCornerOverlay(["tl"], ["tr"], ["bl"], ["br"]);

        Assert.IsTrue(ProcessingIdentity.IsValidAnnotationUtf16(new ProcessingAnnotationInput(
            null!,
            null!,
            IdentityTransform,
            null,
            ValidSha256,
            MetadataOverlay: null)));

        Assert.IsTrue(ProcessingIdentity.IsValidAnnotationUtf16(new ProcessingAnnotationInput(
            [null!, validObject],
            [null!, validSegment],
            IdentityTransform,
            null,
            ValidSha256,
            new MetadataCornerOverlay(null!, ["tr"], ["bl"], ["br"]))));

        Assert.IsTrue(ProcessingIdentity.IsValidAnnotationUtf16(new ProcessingAnnotationInput(
            [validObject],
            [validSegment],
            IdentityTransform,
            null,
            ValidSha256,
            validMetadata)));

        var invalidAnnotations = new[]
        {
            new ProcessingAnnotationInput(
                [new ProjectedAnnotationObject(MalformedHighSurrogate, "star", Origin)],
                [],
                IdentityTransform,
                null,
                ValidSha256),
            new ProcessingAnnotationInput(
                [new ProjectedAnnotationObject("star:1", MalformedLowSurrogate, Origin)],
                [],
                IdentityTransform,
                null,
                ValidSha256),
            new ProcessingAnnotationInput(
                [],
                [new ProjectedAnnotationSegment(MalformedHighSurrogate, Origin, Origin)],
                IdentityTransform,
                null,
                ValidSha256),
            new ProcessingAnnotationInput(
                [],
                [],
                IdentityTransform,
                null,
                ValidSha256,
                new MetadataCornerOverlay([MalformedHighSurrogate], ["tr"], ["bl"], ["br"])),
            new ProcessingAnnotationInput(
                [],
                [],
                IdentityTransform,
                null,
                ValidSha256,
                new MetadataCornerOverlay(["tl"], [MalformedHighSurrogate], ["bl"], ["br"])),
            new ProcessingAnnotationInput(
                [],
                [],
                IdentityTransform,
                null,
                ValidSha256,
                new MetadataCornerOverlay(["tl"], ["tr"], [MalformedLowSurrogate], ["br"])),
            new ProcessingAnnotationInput(
                [],
                [],
                IdentityTransform,
                null,
                ValidSha256,
                new MetadataCornerOverlay(["tl"], ["tr"], ["bl"], [MalformedLowSurrogate]))
        };

        foreach (var annotation in invalidAnnotations)
        {
            Assert.IsFalse(ProcessingIdentity.IsValidAnnotationUtf16(annotation));
            Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.BindExecutionInputs(
                EmptyOptions,
                ProcessingInputSelector.Raw("source"),
                annotation));
        }
    }

    [TestMethod]
    public void BindExecutionInputsRejectsMalformedUtf16InSelectorsAndAuxiliaryInputs()
    {
        var validSelector = ProcessingInputSelector.Raw("source");

        Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.BindExecutionInputs(
            EmptyOptions,
            ProcessingInputSelector.Raw(MalformedHighSurrogate),
            annotation: null));

        Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.BindExecutionInputs(
            EmptyOptions,
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Calibrated, "linear", MalformedLowSurrogate),
            annotation: null));

        var invalidAuxiliaries = new IReadOnlyList<ProcessingAuxiliaryInput>[]
        {
            [
                null!,
                new ProcessingAuxiliaryInput(
                    MalformedHighSurrogate,
                    ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: "schema-v1",
                    IdentitySha256: ValidSha256)
            ],
            [
                new ProcessingAuxiliaryInput(
                    "aux",
                    ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: MalformedLowSurrogate,
                    IdentitySha256: ValidSha256)
            ],
            [
                new ProcessingAuxiliaryInput(
                    "aux",
                    ProcessingAuxiliaryInputKind.Artifact,
                    Selector: ProcessingInputSelector.Raw(MalformedHighSurrogate),
                    ArtifactId: Guid.NewGuid())
            ],
            [
                new ProcessingAuxiliaryInput(
                    "aux",
                    ProcessingAuxiliaryInputKind.Artifact,
                    Selector: ProcessingInputSelector.RecipeResult(FrameArtifactRole.Calibrated, "linear", MalformedLowSurrogate),
                    ArtifactId: Guid.NewGuid())
            ]
        };

        foreach (var auxiliaries in invalidAuxiliaries)
        {
            Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.BindExecutionInputs(
                EmptyOptions,
                validSelector,
                annotation: null,
                auxiliaryInputs: auxiliaries));
        }

        var bound = ProcessingIdentity.BindExecutionInputs(
            EmptyOptions,
            ProcessingInputSelector.Raw(ValidAstralValue),
            annotation: new ProcessingAnnotationInput(
                [new ProjectedAnnotationObject("star:1", ValidAstralValue, Origin)],
                [new ProjectedAnnotationSegment("ori", Origin, Origin)],
                IdentityTransform,
                null,
                ValidSha256,
                new MetadataCornerOverlay(["tl"], ["tr"], ["bl"], ["br"])),
            auxiliaryInputs:
            [
                new ProcessingAuxiliaryInput(
                    "aux",
                    ProcessingAuxiliaryInputKind.Artifact,
                    Selector: ProcessingInputSelector.RecipeResult(FrameArtifactRole.Calibrated, ValidAstralValue, ValidSha256),
                    ArtifactId: Guid.NewGuid())
            ]);
        Assert.AreEqual(ProcessingIdentity.BoundInputSchemaVersion, bound.GetProperty("schema").GetString());
    }
}
