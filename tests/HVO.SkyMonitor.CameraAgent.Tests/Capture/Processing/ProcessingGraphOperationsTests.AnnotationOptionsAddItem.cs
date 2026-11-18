using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private const string DuplicateCornerMessage = "items must be unique supported choices";
    private const string DuplicateEnvironmentMessage = "Select each environmental kind at most once.";

    [TestMethod]
    [DataRow("topLeftTokens", "schedule-profile")]
    [DataRow("bottomLeftTokens", "agent-identity")]
    [DataRow("bottomRightTokens", "agent-identity")]
    [DataRow("environmentalKinds", "WindDirection")]
    public void AnnotationOptionsAddItemOnADefaultListAddsAnUnusedAllowedValue(string key, string expected)
    {
        var form = new ProcessingNodeOptionsForm(AnnotationOptionDescriptors.Descriptor, null);
        var option = AnnotationOptionDescriptors.Descriptor.Find(key)!;
        var before = form.Collection(key).Items;
        if (key is "topLeftTokens" or "environmentalKinds")
        {
            // The audited case: the first allowed value is already selected, so repeating it was a duplicate.
            CollectionAssert.Contains(before.ToArray(), option.AllowedValues[0]);
        }

        form.AddCollectionItem(key);

        var after = form.Collection(key).Items;
        Assert.HasCount(before.Length + 1, after);
        CollectionAssert.AreEqual(before.ToArray(), after.Take(before.Length).ToArray(), "Existing items keep their order.");
        Assert.AreEqual(expected, after[^1]);
        Assert.HasCount(after.Length, after.Distinct(StringComparer.Ordinal));
        Assert.IsNull(form.AddCollectionItemUnavailableReason(key));
        Assert.IsTrue(form.TryBuild(out var options, out var diagnostics), string.Join(' ', diagnostics.Select(static item => item.Message)));
        CollectionAssert.AreEqual(after.ToArray(),
            options!.Value.GetProperty(key).EnumerateArray().Select(static value => value.GetString()).ToArray());
    }

    [TestMethod]
    [DataRow("topLeftTokens")]
    [DataRow("environmentalKinds")]
    public void AnnotationOptionsAddItemNeverRaisesADuplicateErrorAndStopsOnceEveryAllowedValueIsSelected(string key)
    {
        var form = new ProcessingNodeOptionsForm(AnnotationOptionDescriptors.Descriptor, null);
        var option = AnnotationOptionDescriptors.Descriptor.Find(key)!;

        // Past the eight-item limit the form reports the limit, but an added item is never a repeat.
        while (form.AddCollectionItemUnavailableReason(key) is null)
        {
            form.AddCollectionItem(key);
            Assert.IsFalse(form.Diagnostics.Any(static item =>
                item.Message.Contains(DuplicateCornerMessage, StringComparison.Ordinal) ||
                item.Message.Contains(DuplicateEnvironmentMessage, StringComparison.Ordinal)),
                string.Join(' ', form.Diagnostics.Select(static item => item.Message)));
        }

        CollectionAssert.AreEquivalent(option.AllowedValues.ToArray(), form.Collection(key).Items.ToArray());
        Assert.AreEqual(ProcessingNodeOptionsForm.EveryChoiceSelectedReason, form.AddCollectionItemUnavailableReason(key));
        var full = form.Collection(key);
        form.AddCollectionItem(key);
        Assert.AreSame(full, form.Collection(key), "With nothing left to add, the selection is left untouched.");

        // Removing any item makes exactly that value available again.
        form.RemoveCollectionItem(key, 0);
        Assert.IsNull(form.AddCollectionItemUnavailableReason(key));
        form.AddCollectionItem(key);
        Assert.AreEqual(full.Items[0], form.Collection(key).Items[^1]);
    }

    [TestMethod]
    public void AnnotationOptionsAddItemSkipsValuesARestoredSourceAlreadySelects()
    {
        var source = JsonSerializer.Deserialize<JsonElement>(
            """{ "topLeftTokens":["agent-identity","capture-sequence","utc","schedule-profile"], "environmentalKinds":[0,"relativeHumidity"] }""");
        var form = new ProcessingNodeOptionsForm(AnnotationOptionDescriptors.Descriptor, source);

        form.AddCollectionItem("topLeftTokens");
        form.AddCollectionItem("environmentalKinds");

        Assert.AreEqual("exposure", form.Collection("topLeftTokens").Items[^1]);
        // Numeric and differently cased source spellings still count as the kinds they name.
        Assert.AreEqual("AtmosphericPressure", form.Collection("environmentalKinds").Items[^1]);
        Assert.IsTrue(form.TryBuild(out _, out var diagnostics), string.Join(' ', diagnostics.Select(static item => item.Message)));
    }

    [TestMethod]
    public void AnnotationOptionsAddItemLeavesFreeTextCollectionsAddingAnEmptyItem()
    {
        var form = new ProcessingNodeOptionsForm(AnnotationOptionDescriptors.Descriptor, null);

        form.AddCollectionItem("constellationIds");
        form.AddCollectionItem("constellationIds");

        CollectionAssert.AreEqual(new[] { string.Empty, string.Empty }, form.Collection("constellationIds").Items.ToArray());
        Assert.IsNull(form.AddCollectionItemUnavailableReason("constellationIds"));
    }

    [TestMethod]
    public void SharedOptionsAddItemTreatsAnAliasOfASelectedChoiceAsSelected()
    {
        // The materializer compares layer kinds by canonical identity, so a legacy alias of a selected kind is a repeat.
        var form = new ProcessingNodeOptionsForm(PresentationMaterializerOptionDescriptors.Descriptor, null);
        const string key = "enabledLayerKinds";
        form.AddCollectionItem(key);
        form.SetCollectionItem(key, 0, "star-annotations");

        form.AddCollectionItem(key);

        Assert.AreEqual("scene-cardinals", form.Collection(key).Items[^1], "scene-annotation is the selected alias's own kind.");
        while (form.AddCollectionItemUnavailableReason(key) is null) form.AddCollectionItem(key);
        var kinds = form.Collection(key).Items;
        Assert.HasCount(kinds.Length, kinds.Select(PresentationLayerKindSelection.Canonicalize).Distinct(StringComparer.Ordinal));
        Assert.HasCount(PresentationLayerKindSelection.Supported.Select(PresentationLayerKindSelection.Canonicalize).Distinct(StringComparer.Ordinal).Count(), kinds);
        Assert.IsTrue(form.TryBuild(out _, out var diagnostics), string.Join(' ', diagnostics.Select(static item => item.Message)));
    }
}
