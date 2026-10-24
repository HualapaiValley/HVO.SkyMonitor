using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{ \"MaximumDimension\":null, \"JpegQuality\":90 }")]
    [DataRow("{ \"MaximumDimension\":4, \"JpegQuality\":90 }")]
    public async Task NullableDimensionEditsChangeOnlyJpegSizeAndReconstructRetainedOutput(string json)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("JpegEncoding", "packed-jpeg-v1").Descriptor!;
        var retained = JsonSerializer.Deserialize<JsonElement>(json);
        var packed = await Execute(Source(CameraPixelFormat.Mono16), BuiltInProcessingRecipes.EncodedPreview,
            new EncodedPreviewOptions(OutputEncoding: "Packed"));
        var input = AsArtifact(packed) with { Role = FrameArtifactRole.AnnotatedPreview };
        static JpegEncodingOptions Parameters(ProcessingNodeOptionsDescriptor descriptor, JsonElement json)
        {
            var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, json);
            return new((int)decoded.Get("jpegQuality").Value.AsWholeNumber(), (int?)decoded.Get("maximumDimension").Value.AsNullableWholeNumber());
        }
        var before = await Execute(input, BuiltInProcessingRecipes.JpegEncoding, Parameters(descriptor, retained));
        foreach (var limit in new long?[] { 2, null, 16384 })
        {
            var edited = ProcessingNodeOptionsCodec.Encode(descriptor, retained,
                new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromNullableWholeNumber(limit) });
            Assert.IsTrue(edited.IsValid);
            var after = await Execute(input, BuiltInProcessingRecipes.JpegEncoding, Parameters(descriptor, edited.Options!.Value));
            var dimensions = JpegImageCodec.InspectJpeg(after.Payload);
            Assert.AreEqual(limit is null ? 8 : (int)Math.Min(8, limit.Value), dimensions.Width);
            Assert.AreEqual(dimensions.Width, dimensions.Height);
            CollectionAssert.AreEqual(before.SourceArtifactIds.ToArray(), after.SourceArtifactIds.ToArray());
            if (limit == 2)
            {
                Assert.AreNotEqual(before.ChecksumSha256, after.ChecksumSha256);
                Assert.AreNotEqual(before.OutputIdentitySha256, after.OutputIdentitySha256);
                Assert.AreNotEqual(before.Recipe.IdentitySha256, after.Recipe.IdentitySha256);
            }
            var restored = await Execute(input, BuiltInProcessingRecipes.JpegEncoding, Parameters(descriptor, retained));
            Assert.AreEqual(before.OutputIdentitySha256, restored.OutputIdentitySha256);
            CollectionAssert.AreEqual(before.Payload.ToArray(), restored.Payload.ToArray());
            Console.WriteLine($"JPEG dimensions limit={limit}: original={before.ChecksumSha256}, edited={after.ChecksumSha256}, restored={restored.ChecksumSha256}");
        }
    }

    [TestMethod]
    public async Task EditedPreviewAndJpegOptionsChangeRetainedOutputsAndReconstructOldProductsAcrossHosts()
    {
        var source = Source(CameraPixelFormat.Mono16);
        var originalBytes = source.Payload.ToArray();
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("Preview", "encoded-preview-v1").Descriptor!;
        var retained = JsonSerializer.Deserialize<JsonElement>("""{ "FixedTransfer":null, "future":[1e+02] }""");
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, retained, new Dictionary<string, ProcessingOptionValue>
        {
            ["blackPercentile"] = ProcessingOptionValue.FromRealNumber(0.1),
            ["whitePercentile"] = ProcessingOptionValue.FromRealNumber(0.95),
            ["asinhStrength"] = ProcessingOptionValue.FromRealNumber(12)
        }).Options!.Value;
        static EncodedPreviewOptions Parameters(JsonElement json)
        {
            var values = ProcessingNodeOptionsCodec.Decode(PreviewOptionDescriptors.All[0], json);
            return new(values.Get("blackPercentile").Value.AsRealNumber(), values.Get("whitePercentile").Value.AsRealNumber(),
                values.Get("asinhStrength").Value.AsRealNumber(), OutputEncoding: "Packed");
        }
        var oldPreview = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, Parameters(retained));
        var newPreview = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, Parameters(edited));
        Assert.AreNotEqual(oldPreview.ChecksumSha256, newPreview.ChecksumSha256);
        Assert.AreNotEqual(oldPreview.Recipe.IdentitySha256, newPreview.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(oldPreview.SourceArtifactIds.ToArray(), newPreview.SourceArtifactIds.ToArray());
        Assert.AreEqual(oldPreview.Layout, newPreview.Layout);
        var restored = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, Parameters(retained));
        Assert.AreEqual(oldPreview.OutputIdentitySha256, restored.OutputIdentitySha256);
        CollectionAssert.AreEqual(oldPreview.Payload.ToArray(), restored.Payload.ToArray());

        var jpegDescriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("JpegEncoding", "packed-jpeg-v1").Descriptor!;
        foreach (var maximum in new int?[] { null, 4 })
        {
            var jpegOptions = JsonSerializer.SerializeToElement(new { JpegQuality = 90, MaximumDimension = maximum, future = new[] { 100 } });
            var changed = ProcessingNodeOptionsCodec.Encode(jpegDescriptor, jpegOptions,
                new Dictionary<string, ProcessingOptionValue> { ["jpegQuality"] = ProcessingOptionValue.FromWholeNumber(35) });
            Assert.IsTrue(changed.IsValid);
            Assert.AreEqual(jpegOptions.GetProperty("MaximumDimension").GetRawText(), changed.Options!.Value.GetProperty("MaximumDimension").GetRawText());
            var input = AsArtifact(oldPreview) with { Role = FrameArtifactRole.AnnotatedPreview };
            var oldJpeg = await Execute(input, BuiltInProcessingRecipes.JpegEncoding, new JpegEncodingOptions(90, maximum));
            var newJpeg = await Execute(input, BuiltInProcessingRecipes.JpegEncoding,
                new JpegEncodingOptions((int)ProcessingNodeOptionsCodec.Decode(jpegDescriptor, changed.Options.Value).Get("jpegQuality").Value.AsWholeNumber(), maximum));
            Assert.AreNotEqual(oldJpeg.ChecksumSha256, newJpeg.ChecksumSha256);
            Assert.AreNotEqual(oldJpeg.OutputIdentitySha256, newJpeg.OutputIdentitySha256);
            var dimensions = JpegImageCodec.InspectJpeg(newJpeg.Payload);
            Assert.AreEqual(maximum ?? 8, dimensions.Width);
            Assert.AreEqual(maximum ?? 8, dimensions.Height);
            var reconstructed = await Execute(input, BuiltInProcessingRecipes.JpegEncoding, new JpegEncodingOptions(90, maximum));
            Assert.AreEqual(oldJpeg.OutputIdentitySha256, reconstructed.OutputIdentitySha256);
            CollectionAssert.AreEqual(oldJpeg.Payload.ToArray(), reconstructed.Payload.ToArray());
            Console.WriteLine($"JPEG max={maximum}: old={oldJpeg.ChecksumSha256}, edited={newJpeg.ChecksumSha256}, restored={reconstructed.ChecksumSha256}");
        }
        var fixedOptions = new FixedDisplayTransferOptions(0, ushort.MaxValue, 1);
        var fixedSource = JsonSerializer.SerializeToElement(new { FixedTransfer = fixedOptions, BlackPercentile = 0.25, future = new[] { 100 } });
        var fixedDescriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("Preview", PreviewOptionDescriptors.VersionFor(fixedSource)).Descriptor!;
        var fixedEdited = ProcessingNodeOptionsCodec.Encode(fixedDescriptor, fixedSource,
            new Dictionary<string, ProcessingOptionValue> { ["recipeVersion"] = ProcessingOptionValue.FromText("custom-retained-label") });
        Assert.AreEqual(fixedSource.GetProperty("FixedTransfer").GetRawText(), fixedEdited.Options!.Value.GetProperty("FixedTransfer").GetRawText());
        var fixedBefore = await Execute(source, BuiltInProcessingRecipes.FixedPreview, fixedOptions);
        var fixedAfter = await Execute(source, BuiltInProcessingRecipes.FixedPreview,
            fixedEdited.Options.Value.GetProperty("FixedTransfer").Deserialize<FixedDisplayTransferOptions>()!);
        Assert.AreEqual(fixedBefore.OutputIdentitySha256, fixedAfter.OutputIdentitySha256);
        CollectionAssert.AreEqual(originalBytes, source.Payload.ToArray());
        Console.WriteLine($"Preview old={oldPreview.ChecksumSha256}, edited={newPreview.ChecksumSha256}, restored={restored.ChecksumSha256}; fixed={fixedBefore.ChecksumSha256}");
    }
}
