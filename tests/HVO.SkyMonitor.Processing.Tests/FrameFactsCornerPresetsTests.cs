using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use fixture expectations are clearer inline.")]
public sealed class FrameFactsCornerPresetsTests
{
    [TestMethod]
    public async Task TokenBasedConversionReproducesEveryFixedLegacyCornerWithoutChangingItsSource()
    {
        var descriptor = FrameFactsRetainedContextTests.Descriptor();
        var identity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var context = FrameFactsRetainedContext.Create(new(descriptor,
            Stack: new(identity, new string('B', 64), [descriptor.Artifact.ArtifactId], TimeSpan.FromSeconds(2.5)),
            Astronomy: await FrameFactsRetainedContextTests.SceneAsync(descriptor).ConfigureAwait(false)));
        var legacy = new PresentationMetadataFactsV1(identity,
            ["CAPTURE 42", "1936 × 1216 PIXELS", "UTC 2026-10-22T23:30:00.123Z"],
            ["ACQUISITION", "SCHEDULE UNAVAILABLE", "EXPOSURE 2.500 S", "GAIN 7.250", "OFFSET 3.0", "SETPOINT -10.0 C"],
            ["ENVIRONMENT", "AIRTEMPERATURE 12.5 DEGREESCELSIUS FRESH GOOD AGE 10 S"],
            ["LINEAGE", "CATALOG fixture 1", "CALIBRATION cal-v1 / AAAAAAAAAAAA", "STACK 1 2.500 S", "PROFILE proc-v1 / AAAAAAAAAAAA"]);
        var before = CaptureContractJson.ComputeCanonicalJsonSha256(legacy);
        var tokenPresets = FrameFactsCornerPresets.FromRetained(context, legacy.BottomLeft.Skip(1).ToArray());
        var frozenPresets = FrameFactsCornerPresets.ConvertLegacy(legacy);
        Assert.IsTrue(tokenPresets.IsValid, tokenPresets.Error?.ToString());
        Assert.IsTrue(frozenPresets.IsValid);
        var corners = new[] { legacy.TopLeft, legacy.TopRight, legacy.BottomLeft, legacy.BottomRight };
        for (var index = 0; index < corners.Length; index++)
        {
            var expected = string.Join('\n', corners[index]);
            Assert.AreEqual(expected, FrameFactsRetainedContextTests.Evaluate(tokenPresets.Presets[index].Source, context));
            Assert.AreEqual(expected, FrameFactsRetainedContextTests.Evaluate(frozenPresets.Presets[index].Source, context));
        }
        Assert.AreEqual(before, CaptureContractJson.ComputeCanonicalJsonSha256(legacy));
    }

    [TestMethod]
    public void MissingLegacyStackAndEnvironmentAreExplicitAndNotInventedZeroes()
    {
        var context = FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor()));
        var result = FrameFactsCornerPresets.FromRetained(context);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("ENVIRONMENT\nNOT RETAINED", FrameFactsRetainedContextTests.Evaluate(result.Presets[2].Source, context));
        StringAssert.Contains(FrameFactsRetainedContextTests.Evaluate(result.Presets[3].Source, context), "STACK UNAVAILABLE", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AllLegacyCornerSyntaxIsEscapedAndEditableSourceRemainsPlacementIndependent()
    {
        const string literal = @"{{capture.sequence}} **bold** __under__ \ | <tag>";
        var facts = new PresentationMetadataFactsV1("frozen", [literal], [literal], [literal], [literal]);
        var context = FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor()));
        var converted = FrameFactsCornerPresets.ConvertLegacy(facts);
        Assert.IsTrue(converted.IsValid);
        foreach (var preset in converted.Presets.Reverse())
        {
            Assert.AreEqual(literal, FrameFactsRetainedContextTests.Evaluate(preset.Source, context));
            Assert.AreEqual("edited " + literal, FrameFactsRetainedContextTests.Evaluate("edited " + preset.Source, context));
        }
        CollectionAssert.AreEqual(new[] { "capture", "acquisition", "environment", "lineage" }, converted.Presets.Select(preset => preset.Id).ToArray());
    }

    [TestMethod]
    public void EmptyCornerAndOneEmptyRetainedLineRemainDistinct()
    {
        var context = FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor()));
        var result = FrameFactsCornerPresets.ConvertLegacy(new("frozen", [], [""], ["", ""], ["x", ""]));
        Assert.IsTrue(result.IsValid);
        var expectedLineCounts = new[] { 0, 1, 2, 2 };
        for (var index = 0; index < result.Presets.Length; index++)
        {
            var template = FrameFactsTemplate.Parse(result.Presets[index].Source, FrameFactsRetainedCatalog.Catalog).Template!;
            Assert.HasCount(expectedLineCounts[index], FrameFactsEvaluator.Evaluate(template, context.Tokens).Lines);
        }
    }

    [TestMethod]
    [DataRow(0, "capture")]
    [DataRow(1, "acquisition")]
    [DataRow(2, "environment")]
    [DataRow(3, "lineage")]
    public void OversizedLegacyLinesFailAtomicallyInEveryCorner(int corner, string expectedId)
    {
        var lines = new IReadOnlyList<string>[] { ["ok"], ["ok"], ["ok"], ["ok"] };
        lines[corner] = [new string('x', 65)];
        var result = FrameFactsCornerPresets.ConvertLegacy(new("frozen", lines[0], lines[1], lines[2], lines[3]));
        Assert.IsFalse(result.IsValid);
        Assert.IsEmpty(result.Presets);
        Assert.AreEqual(expectedId, result.FailedPresetId);
        Assert.AreEqual("frame-facts.line-too-long", result.Error?.Code);
    }

    [TestMethod]
    public void UnicodeLimitsAndOriginalLegacyClippingArePreservedWithoutAdditionalClipping()
    {
        var unicode = string.Concat(Enumerable.Repeat("\U0001f30c", 64));
        var context = FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor()));
        var result = FrameFactsCornerPresets.ConvertLegacy(new("frozen", [unicode], [new string('*', 64)], [], []));
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(unicode, FrameFactsRetainedContextTests.Evaluate(result.Presets[0].Source, context));
        Assert.AreEqual(new string('*', 64), FrameFactsRetainedContextTests.Evaluate(result.Presets[1].Source, context));
        var tooMany = FrameFactsCornerPresets.ConvertLegacy(new("frozen", Enumerable.Repeat("x", 9).ToArray(), [], [], []));
        Assert.AreEqual("frame-facts.too-many-lines", tooMany.Error?.Code);
    }

    [TestMethod]
    [DataRow("injected\nline")]
    [DataRow("injected\rline")]
    [DataRow("injected\tline")]
    [DataRow("injected\u2028line")]
    [DataRow("injected\u2029line")]
    public void LegacyLineStringsCannotInjectAdditionalLinesOrControls(string line)
    {
        var result = FrameFactsCornerPresets.ConvertLegacy(new("frozen", [line], [], [], []));
        Assert.IsFalse(result.IsValid);
        Assert.IsEmpty(result.Presets);
        Assert.AreEqual("frame-facts.invalid-character", result.Error?.Code);
    }

    [TestMethod]
    public void TokenPresetExpansionAndEnvironmentalLineCountsAreBoundedWithoutClipping()
    {
        var descriptor = FrameFactsRetainedContextTests.Descriptor();
        descriptor = descriptor with { Profiles = descriptor.Profiles with { Processing = descriptor.Profiles.Processing with { Version = new string('x', 64) } } };
        var context = FrameFactsRetainedContext.Create(new(descriptor));
        var result = FrameFactsCornerPresets.FromRetained(context);
        Assert.AreEqual("lineage", result.FailedPresetId);
        Assert.AreEqual("frame-facts.expansion-too-long", result.Error?.Code);
        Assert.IsEmpty(result.Presets);
        var tooMany = FrameFactsCornerPresets.FromRetained(context, Enumerable.Repeat("x", 8).ToArray());
        Assert.AreEqual("environment", tooMany.FailedPresetId);
        Assert.AreEqual("frame-facts.too-many-lines", tooMany.Error?.Code);
    }

    [TestMethod]
    public void ConversionIsDeterministicAcrossCulture()
    {
        var context = FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor()));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var first = FrameFactsCornerPresets.FromRetained(context);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = FrameFactsCornerPresets.FromRetained(context);
            CollectionAssert.AreEqual(first.Presets.ToArray(), second.Presets.ToArray());
            StringAssert.Contains(FrameFactsRetainedContextTests.Evaluate(second.Presets[1].Source, context), "2.500", StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }
}
