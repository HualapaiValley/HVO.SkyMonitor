using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #1532 (slice A of #1386): typed option descriptors, the byte-preserving codec, validation reasons and the explicit
/// whole-draft upgrade contract.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use expected test values are clearer inline.")]
public sealed class ProcessingNodeOptionsTests
{
    private const string SceneVersion = "projected-scene-presentation-v13-plain-circle-bounded-expected-labels";
    private const string Color = "constellationLineColor";
    private const string Thickness = "constellationLineThickness";
    private static readonly ProcessingNodeOptionsDescriptor Scene = ProcessingNodeOptionsDescriptors.ScenePresentationLayer;
    private static readonly ProcessingGraphProductContract Raw = new(
        FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData);
    private static readonly ProcessingNodeOptionsDescriptor AllKinds = new(
        "all-kinds-options-v1",
        "AllKinds",
        "all-kinds-v1",
        "Every kind",
        "One option of every kind.",
        [
            ProcessingOptionDescriptor.Toggle("draw", "draw", "Draw", "Draws.", true),
            ProcessingOptionDescriptor.WholeNumber("count", "count", "Count", "Counts.", 3, -5, 5),
            ProcessingOptionDescriptor.RealNumber("opacity", "opacity", "Opacity", "Opacity.", 0.8, 0, 1),
            ProcessingOptionDescriptor.Text("caption", "caption", "Caption", "Caption.", "sky", 8),
            ProcessingOptionDescriptor.Choice("mode", "mode", "Mode", "Mode.", "fast", ["fast", "exact"]),
            ProcessingOptionDescriptor.Font("font", "font", "Font", "Font.", "dejavu-mono", ["dejavu-mono", "plex"]),
            ProcessingOptionDescriptor.Glyph("marker", "marker", "Marker", "Marker.", "circle", ["circle", "cross"]),
            ProcessingOptionDescriptor.Color("tint", "tintRed", "tintGreen", "tintBlue", "Tint", "Tint.", new(1, 2, 3))
        ]);

    [TestMethod]
    public void SceneDescriptorBindsTheShippedSceneVersionWithTodaysConstellationDefaults()
    {
        Assert.AreEqual("scene-presentation-layer-options-v2", Scene.DescriptorId);
        Assert.AreEqual("ScenePresentationLayer", Scene.StepAlias);
        // Pinned literally: a scene producer version change must decide its descriptor and upgrade explicitly.
        Assert.AreEqual(PresentationLayerProducers.SceneProducerVersion, Scene.StepVersion);
        var color = Scene.Find(Color)!;
        Assert.AreEqual(ProcessingOptionKind.Color, color.Kind);
        CollectionAssert.AreEqual(
            new[] { "constellationLineRed", "constellationLineGreen", "constellationLineBlue" }, color.Members.ToArray());
        Assert.AreEqual(new PresentationColor(96, 160, 255), color.DefaultValue.AsColor());
        var thickness = Scene.Find(Thickness)!;
        Assert.AreEqual(ProcessingOptionKind.WholeNumber, thickness.Kind);
        Assert.AreEqual(2, thickness.DefaultValue.AsWholeNumber());
        Assert.AreEqual(1, thickness.MinimumWholeNumber);
        Assert.AreEqual(8, thickness.MaximumWholeNumber);
        Assert.AreEqual("px", thickness.Unit);
        Assert.HasCount(3, ProcessingNodeOptionsCatalog.BuiltIn.Descriptors);
        Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Upgrades, "No upgrade ships, so no saved pipeline changes.");
    }

    [TestMethod]
    public void BothSceneVersionsRoundTripWithoutChangingSavedDrafts()
    {
        const string previousVersion = "projected-scene-presentation-v12-plain-circle-markers";
        var catalog = ProcessingNodeOptionsCatalog.BuiltIn;
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described,
            catalog.Resolve("ScenePresentationLayer", previousVersion).Status);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described,
            catalog.Resolve("ScenePresentationLayer", SceneVersion).Status);
        Assert.AreEqual("scene-presentation-layer-options-v1",
            catalog.Resolve("ScenePresentationLayer", previousVersion).Descriptor!.DescriptorId);
        var saved = Graph(
            Node("previous", "ScenePresentationLayer", previousVersion,
                """{"constellationLineThickness":2,"future":{"value":1e+02}}"""),
            Node("current", "ScenePresentationLayer", SceneVersion,
                """{"constellationLineThickness":3,"expectedPositionDiagnostics":true}"""));

        Assert.IsEmpty(catalog.Validate(saved));
        var roundTripped = RoundTrip(catalog, saved);

        CollectionAssert.AreEqual(ProcessingGraphJson.SerializeCanonical(saved),
            ProcessingGraphJson.SerializeCanonical(roundTripped));
        Assert.AreEqual(saved.Revision, roundTripped.Revision);
        for (var index = 0; index < saved.Nodes.Length; index++)
        {
            Assert.AreEqual(saved.Nodes[index].StepVersion, roundTripped.Nodes[index].StepVersion);
            Assert.AreEqual(saved.Nodes[index].EffectiveOptions.GetRawText(),
                roundTripped.Nodes[index].EffectiveOptions.GetRawText());
        }
        Assert.IsEmpty(catalog.Upgrades, "Describing both versions never upgrades a saved draft.");
    }

    [TestMethod]
    public void DescriptorsRejectInvalidShapes()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.WholeNumber("width", "width", "Width", "Width.", 9, 1, 8));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.WholeNumber("width", "width", "Width", "Width.", 2, 8, 1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.WholeNumber("Width", "width", "Width", "Width.", 2, 1, 8));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.Toggle("draw", "draw-it", "Draw", "Draws.", true));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.Choice("mode", "mode", "Mode", "Mode.", "slow", ["fast"]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.Choice("mode", "mode", "Mode", "Mode.", "fast", ["fast", "fast"]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.Text("caption", "caption", "Caption", "Caption.", "too long", 4));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingOptionDescriptor.Color("tint", "red", "Red", "blue", "Tint", "Tint.", default));
        var width = ProcessingOptionDescriptor.WholeNumber("width", "width", "Width", "Width.", 2, 1, 8);
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingNodeOptionsDescriptor(
            "example-options", "Example", "example-v1", "Example", "Example.", [width]));
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingNodeOptionsDescriptor(
            "example-options-v1", "Example", "example-v1", "Example", "Example.", [width, width]));
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingNodeOptionsDescriptor(
            "example-options-v1", "Example", "example-v1", "Example", "Example.",
            [width, ProcessingOptionDescriptor.Toggle("wide", "Width", "Wide", "Wide.", false)]));
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingNodeOptionsCatalog(
            [Scene, new ProcessingNodeOptionsDescriptor("other-options-v2", "scenepresentationlayer", SceneVersion,
                "Other", "Other.", [width])]));
    }

    [TestMethod]
    public void DecodeReportsCodeDefaultsForAbsentMembersAndReadsExplicitMembersIgnoringCase()
    {
        var defaults = ProcessingNodeOptionsCodec.Decode(Scene, Json("""{"unknown":[1,{"x":null}]}"""));
        Assert.IsTrue(defaults.IsValid);
        Assert.AreEqual(new PresentationColor(96, 160, 255), defaults.Get(Color).Value.AsColor());
        Assert.IsFalse(defaults.Get(Color).IsExplicit);
        Assert.AreEqual(2, defaults.Get(Thickness).Value.AsWholeNumber());
        Assert.IsFalse(defaults.Get(Thickness).IsExplicit);

        var explicitValues = ProcessingNodeOptionsCodec.Decode(
            Scene, Json("""{"ConstellationLineRed":10,"constellationlinethickness":8}"""));
        Assert.IsTrue(explicitValues.IsValid);
        Assert.AreEqual(new PresentationColor(10, 160, 255), explicitValues.Get(Color).Value.AsColor());
        Assert.IsTrue(explicitValues.Get(Color).IsExplicit);
        Assert.AreEqual(8, explicitValues.Get(Thickness).Value.AsWholeNumber());
        Assert.IsTrue(explicitValues.Get(Thickness).IsExplicit);
    }

    [TestMethod]
    [DataRow("""{"constellationLineThickness":0}""", "processing.options.out-of-range", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":9}""", "processing.options.out-of-range", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":99999999999999999999}""", "processing.options.out-of-range", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":2.5}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":2.0}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":2e0}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":"2"}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":null}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"constellationLineThickness":true}""", "processing.options.wrong-type", "constellationLineThickness")]
    [DataRow("""{"ConstellationLineThickness":0}""", "processing.options.out-of-range", "ConstellationLineThickness")]
    [DataRow("""{"constellationLineRed":256}""", "processing.options.out-of-range", "constellationLineRed")]
    [DataRow("""{"constellationLineGreen":-1}""", "processing.options.out-of-range", "constellationLineGreen")]
    [DataRow("""{"constellationLineRed":-0}""", "processing.options.out-of-range", "constellationLineRed")]
    [DataRow("""{"constellationLineGreen":-0}""", "processing.options.out-of-range", "constellationLineGreen")]
    [DataRow("""{"constellationLineBlue":-0}""", "processing.options.out-of-range", "constellationLineBlue")]
    [DataRow("""{"constellationLineBlue":1.5}""", "processing.options.wrong-type", "constellationLineBlue")]
    [DataRow("""{"constellationLineRed":"#60A0FF"}""", "processing.options.wrong-type", "constellationLineRed")]
    [DataRow("""{"constellationLineRed":1,"ConstellationLineRed":1}""", "processing.options.duplicate-member", "ConstellationLineRed")]
    public void DecodeRejectsWrongTypesAndOutOfRangeValuesWithStableReasons(string json, string code, string member)
    {
        var result = ProcessingNodeOptionsCodec.Decode(Scene, Json(json), "nodes[3].effectiveOptions");

        Assert.IsFalse(result.IsValid);
        Assert.IsEmpty(result.Options);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual(code, diagnostic.Code);
        Assert.AreEqual($"nodes[3].effectiveOptions.{member}", diagnostic.Path);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(255)]
    public void DecodeAcceptsEveryChannelAtItsUnsignedBounds(int channel)
    {
        var result = ProcessingNodeOptionsCodec.Decode(Scene, Json(string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"constellationLineRed":{{channel}},"constellationLineGreen":{{channel}},"constellationLineBlue":{{channel}}}""")));

        Assert.IsTrue(result.IsValid);
        var expected = (byte)channel;
        Assert.AreEqual(new PresentationColor(expected, expected, expected), result.Get(Color).Value.AsColor());
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("\"options\"")]
    public void DecodeAndEncodeRejectOptionsThatAreNotAnObject(string json)
    {
        var decoded = ProcessingNodeOptionsCodec.Decode(Scene, Json(json));
        var encoded = ProcessingNodeOptionsCodec.Encode(Scene, Json(json), new Dictionary<string, ProcessingOptionValue>());

        Assert.AreEqual("processing.options.not-an-object", decoded.Diagnostics.Single().Code);
        Assert.AreEqual("effectiveOptions", decoded.Diagnostics.Single().Path);
        Assert.IsNull(encoded.Options);
        Assert.AreEqual("processing.options.not-an-object", encoded.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void EveryKindDecodesItsJsonTypeAndRejectsOthers()
    {
        var decoded = ProcessingNodeOptionsCodec.Decode(AllKinds, Json("""
            {"draw":false,"count":-5,"opacity":0.25,"caption":"Café","mode":"exact","font":"plex",
             "marker":"cross","tintGreen":200}
            """));
        Assert.IsTrue(decoded.IsValid, string.Join("; ", decoded.Diagnostics));
        Assert.IsFalse(decoded.Get("draw").Value.AsToggle());
        Assert.AreEqual(-5, decoded.Get("count").Value.AsWholeNumber());
        Assert.AreEqual(0.25, decoded.Get("opacity").Value.AsRealNumber());
        Assert.AreEqual("Café", decoded.Get("caption").Value.AsText());
        Assert.AreEqual(ProcessingOptionValue.FromChoice("exact"), decoded.Get("mode").Value);
        Assert.AreEqual(ProcessingOptionValue.FromFont("plex"), decoded.Get("font").Value);
        Assert.AreEqual(ProcessingOptionValue.FromGlyph("cross"), decoded.Get("marker").Value);
        Assert.AreEqual(new PresentationColor(1, 200, 3), decoded.Get("tint").Value.AsColor());

        AssertRejected("""{"draw":1}""", "processing.options.wrong-type");
        AssertRejected("""{"draw":"true"}""", "processing.options.wrong-type");
        AssertRejected("""{"count":6}""", "processing.options.out-of-range");
        AssertRejected("""{"opacity":1.01}""", "processing.options.out-of-range");
        AssertRejected("""{"opacity":1e400}""", "processing.options.out-of-range");
        AssertRejected("""{"opacity":"0.5"}""", "processing.options.wrong-type");
        AssertRejected("""{"caption":"123456789"}""", "processing.options.too-long");
        AssertRejected("""{"caption":7}""", "processing.options.wrong-type");
        AssertRejected("""{"mode":"Fast"}""", "processing.options.not-allowed");
        AssertRejected("""{"font":"comic"}""", "processing.options.not-allowed");
        AssertRejected("""{"marker":null}""", "processing.options.wrong-type");

        static void AssertRejected(string json, string code)
            => Assert.AreEqual(code, ProcessingNodeOptionsCodec.Decode(AllKinds, Json(json)).Diagnostics.Single().Code, json);
    }

    [TestMethod]
    public void EncodeWithoutAnEffectiveChangeReturnsTheSourceByteForByte()
    {
        const string source = """
            { "zeta" : "Café",   "constellationLineThickness":2,
              // operator note
              "ConstellationLineRed" : 96, "nested": {"b":1.50,"a":[ ]}, }
            """;
        var element = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }).RootElement.Clone();

        foreach (var edits in new Dictionary<string, ProcessingOptionValue>[]
                 {
                     [],
                     new() { [Thickness] = ProcessingOptionValue.FromWholeNumber(2) },
                     // Green and blue are absent at their defaults: setting them again materializes nothing.
                     new() { [Color] = ProcessingOptionValue.FromColor(new(96, 160, 255)) }
                 })
        {
            var result = ProcessingNodeOptionsCodec.Encode(Scene, element, edits);

            Assert.IsTrue(result.IsValid, string.Join("; ", result.Diagnostics));
            Assert.AreEqual(element.GetRawText(), result.Options!.Value.GetRawText());
            Assert.AreEqual(source.Trim(), result.Options.Value.GetRawText());
        }
    }

    [TestMethod]
    public void EncodeRewritesOnlyTheEditedValuesKeepingEveryOtherByteAndMemberCase()
    {
        var source = Json("""{ "zeta" : "Café", "ConstellationLineRed" : 96 ,"constellationLineThickness": "2", "n":1.50 }""");

        var result = ProcessingNodeOptionsCodec.Encode(Scene, source, new Dictionary<string, ProcessingOptionValue>
        {
            [Color] = ProcessingOptionValue.FromColor(new(12, 160, 255)),
            [Thickness] = ProcessingOptionValue.FromWholeNumber(3)
        });

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.AreEqual(
            """{ "zeta" : "Café", "ConstellationLineRed" : 12 ,"constellationLineThickness": 3, "n":1.50 }""",
            result.Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("constellationLineRed", 0, 160, 255)]
    [DataRow("constellationLineGreen", 96, 0, 255)]
    [DataRow("constellationLineBlue", 96, 160, 0)]
    public void EncodeRewritesASignedZeroChannelThatTheEditSetsToZero(string member, int red, int green, int blue)
    {
        var result = ProcessingNodeOptionsCodec.Encode(Scene, Json($$"""{"{{member}}":-0,"unknown": [ 1.50 ]}"""),
            new Dictionary<string, ProcessingOptionValue>
            {
                [Color] = ProcessingOptionValue.FromColor(new((byte)red, (byte)green, (byte)blue))
            });

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.AreEqual($$"""{"{{member}}":0,"unknown": [ 1.50 ]}""", result.Options!.Value.GetRawText());
    }

    [TestMethod]
    public void EncodeAppendsOnlyMembersThatDifferFromTheirDefaults()
    {
        var withMembers = ProcessingNodeOptionsCodec.Encode(Scene, Json("""{"other": 1 }"""),
            new Dictionary<string, ProcessingOptionValue>
            {
                [Color] = ProcessingOptionValue.FromColor(new(96, 0, 7)),
                [Thickness] = ProcessingOptionValue.FromWholeNumber(5)
            });
        var empty = ProcessingNodeOptionsCodec.Encode(Scene, Json("{ }"),
            new Dictionary<string, ProcessingOptionValue> { [Thickness] = ProcessingOptionValue.FromWholeNumber(1) });

        Assert.AreEqual(
            """{"other": 1,"constellationLineGreen":0,"constellationLineBlue":7,"constellationLineThickness":5 }""",
            withMembers.Options!.Value.GetRawText());
        Assert.AreEqual("""{"constellationLineThickness":1 }""", empty.Options!.Value.GetRawText());
        var decoded = ProcessingNodeOptionsCodec.Decode(Scene, withMembers.Options.Value);
        Assert.AreEqual(new PresentationColor(96, 0, 7), decoded.Get(Color).Value.AsColor());
        Assert.AreEqual(5, decoded.Get(Thickness).Value.AsWholeNumber());
    }

    [TestMethod]
    public void EncodeRejectsUnknownOptionsWrongKindsOutOfRangeEditsAndInvalidResults()
    {
        var source = Json("{}");

        AssertRejected(new() { ["constellationLineOpacity"] = ProcessingOptionValue.FromRealNumber(1) },
            "processing.options.unknown-option", "effectiveOptions.constellationLineOpacity");
        AssertRejected(new() { [Thickness] = ProcessingOptionValue.FromText("2") },
            "processing.options.wrong-type", "effectiveOptions.constellationLineThickness");
        AssertRejected(new() { [Thickness] = ProcessingOptionValue.FromWholeNumber(9) },
            "processing.options.out-of-range", "effectiveOptions.constellationLineThickness");
        AssertRejected(new() { [Color] = ProcessingOptionValue.FromWholeNumber(255) },
            "processing.options.wrong-type", "effectiveOptions.constellationLineColor");
        // An edit never yields options that fail validation: the untouched invalid member still rejects the result.
        var invalidSource = ProcessingNodeOptionsCodec.Encode(Scene, Json("""{"constellationLineRed":300}"""),
            new Dictionary<string, ProcessingOptionValue> { [Thickness] = ProcessingOptionValue.FromWholeNumber(3) });
        Assert.IsNull(invalidSource.Options);
        Assert.AreEqual("processing.options.out-of-range", invalidSource.Diagnostics.Single().Code);

        void AssertRejected(Dictionary<string, ProcessingOptionValue> edits, string code, string path)
        {
            var result = ProcessingNodeOptionsCodec.Encode(Scene, source, edits);
            Assert.IsNull(result.Options);
            Assert.AreEqual(code, result.Diagnostics.Single().Code);
            Assert.AreEqual(path, result.Diagnostics.Single().Path);
        }
    }

    [TestMethod]
    public void ResolveSeparatesDescribedOpaqueUnsupportedAndUpgradeableSteps()
    {
        var builtIn = ProcessingNodeOptionsCatalog.BuiltIn;
        Assert.AreSame(Scene, builtIn.Resolve("ScenePresentationLayer", SceneVersion).Descriptor);
        Assert.AreSame(Scene, builtIn.Resolve("scenepresentationlayer", SceneVersion).Descriptor);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described,
            builtIn.Resolve("ScenePresentationLayer", SceneVersion).Status);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            builtIn.Resolve("ScenePresentationLayer", SceneVersion.ToUpperInvariant()).Status);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Opaque, builtIn.Resolve("annotation", SceneVersion).Status);

        var catalog = UpgradeCatalog(new WidthToThicknessUpgrade());
        var legacy = catalog.Resolve("Example", "example-v1");
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UpgradeRequired, legacy.Status);
        Assert.IsNull(legacy.Descriptor);
        Assert.AreEqual("example-v2", legacy.Upgrade!.ToStepVersion);
        Assert.ThrowsExactly<ArgumentException>(() => UpgradeCatalog(new WidthToThicknessUpgrade("example-v3")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            UpgradeCatalog(new WidthToThicknessUpgrade(), new WidthToThicknessUpgrade()));
    }

    [TestMethod]
    public void ValidateChecksDescribedNodesOnlyAndReportsGraphPaths()
    {
        var graph = Graph(
            Node("opaque", "annotation", "annotation-v1", """{"constellationLineThickness":"anything"}"""),
            Node("scene", "ScenePresentationLayer", SceneVersion, """{"constellationLineThickness":9}""", enabled: false),
            Node("older-scene", "ScenePresentationLayer", "projected-scene-presentation-v11", """{"constellationLineRed":-4}"""));

        var diagnostic = ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph).Single();

        Assert.AreEqual("processing.options.out-of-range", diagnostic.Code);
        Assert.AreEqual("nodes[1].effectiveOptions.constellationLineThickness", diagnostic.Path);
    }

    [TestMethod]
    public void TheCanonicalCentralGraphValidatesAndRoundTripsToItsShippedBytes()
    {
        // The fixture is the byte-exact DefinitionJson development/v1 b13f0d0e stored for the central graph.
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "canonical-central-graph-rev1-b13f0d0e.json"));
        var definition = ProcessingGraphJson.Parse(bytes).Definition!;

        Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(definition));
        var roundTripped = RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, definition);
        CollectionAssert.AreEqual(bytes, ProcessingGraphJson.SerializeCanonical(roundTripped));
        Assert.AreEqual(
            ProcessingGraphJson.ComputeDefinitionIdentity(definition),
            ProcessingGraphJson.ComputeDefinitionIdentity(roundTripped));
    }

    [TestMethod]
    public void UpgradeIsExplicitAndCoversTheWholeDraftInANewRevision()
    {
        var catalog = UpgradeCatalog(new WidthToThicknessUpgrade());
        var draft = Graph(
            Node("first", "Example", "example-v1", """{"width":4,"keep":"Café"}"""),
            Node("opaque", "Other", "other-v1", """{ "width" : 4 }"""),
            Node("second", "example", "example-v1", "{}", enabled: false));

        // Validation and decoding never upgrade: the legacy nodes stay opaque and the draft is untouched.
        Assert.IsEmpty(catalog.Validate(draft));
        var plan = catalog.PlanUpgrade(draft);
        CollectionAssert.AreEqual(
            new[] { "first", "second" }, plan.Steps.Select(static step => step.NodeId).ToArray());
        Assert.AreEqual(ProcessingGraphJson.ComputeDefinitionIdentity(draft), plan.SourceDefinitionIdentitySha256);

        Assert.AreEqual("processing.options.upgrade-revision-required",
            catalog.ApplyUpgrade(draft, plan, draft.Revision).Diagnostics.Single().Code);
        Assert.AreEqual("processing.options.upgrade-plan-stale",
            catalog.ApplyUpgrade(draft, plan with { Steps = [plan.Steps[0]] }, "2").Diagnostics.Single().Code);
        var edited = draft with { Name = "edited-graph" };
        Assert.AreEqual("processing.options.upgrade-plan-stale",
            catalog.ApplyUpgrade(edited, plan, "2").Diagnostics.Single().Code);

        var result = catalog.ApplyUpgrade(draft, plan, "2");
        Assert.IsTrue(result.IsValid, string.Join("; ", result.Diagnostics));
        var upgraded = result.Definition!;
        Assert.AreEqual("2", upgraded.Revision);
        Assert.AreEqual("""{"thickness":4,"keep":"Café"}""", upgraded.Nodes[0].EffectiveOptions.GetRawText());
        Assert.AreEqual("example-v2", upgraded.Nodes[0].StepVersion);
        Assert.AreEqual("example-v2", upgraded.Nodes[2].StepVersion);
        Assert.AreSame(draft.Nodes[1], upgraded.Nodes[1]);
        Assert.IsEmpty(catalog.Validate(upgraded));
        Assert.AreEqual("processing.options.upgrade-not-required",
            catalog.ApplyUpgrade(upgraded, catalog.PlanUpgrade(upgraded), "3").Diagnostics.Single().Code);
    }

    [TestMethod]
    public void AnUpgradeThatYieldsInvalidOptionsUpgradesNothing()
    {
        var catalog = UpgradeCatalog(new WidthToThicknessUpgrade());
        var draft = Graph(
            Node("first", "Example", "example-v1", """{"width":4}"""),
            Node("second", "Example", "example-v1", """{"width":40}"""));

        var result = catalog.ApplyUpgrade(draft, catalog.PlanUpgrade(draft), "2");

        Assert.IsNull(result.Definition);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual("processing.options.out-of-range", diagnostic.Code);
        Assert.AreEqual("nodes[1].effectiveOptions.thickness", diagnostic.Path);
    }

    private static ProcessingNodeOptionsCatalog UpgradeCatalog(params IProcessingNodeOptionsUpgrade[] upgrades) => new(
        [
            Scene,
            new ProcessingNodeOptionsDescriptor("example-options-v2", "Example", "example-v2", "Example", "Example.",
                [ProcessingOptionDescriptor.WholeNumber("thickness", "thickness", "Thickness", "Thickness.", 1, 1, 8)])
        ],
        upgrades);

    private static ProcessingGraphDefinition RoundTrip(ProcessingNodeOptionsCatalog catalog, ProcessingGraphDefinition definition)
        => definition with
        {
            Nodes = [.. definition.Nodes.Select(node => catalog.Resolve(node.StepAlias, node.StepVersion).Descriptor is { } descriptor
                ? WithOptions(node, node.StepVersion, ProcessingNodeOptionsCodec.Encode(
                    descriptor, node.EffectiveOptions, new Dictionary<string, ProcessingOptionValue>()).Options!.Value)
                : WithOptions(node, node.StepVersion, node.EffectiveOptions))]
        };

    private static ProcessingGraphDefinition Graph(params ProcessingGraphNodeDefinition[] nodes) => new(
        ProcessingGraphSchemaVersions.Current,
        "options-graph",
        "1",
        [new("$raw", [Raw])],
        [.. nodes]);

    private static ProcessingGraphNodeDefinition Node(
        string id,
        string stepAlias,
        string stepVersion,
        string options,
        bool enabled = true) => new(
            id,
            stepAlias,
            stepVersion,
            ProcessingOperationKind.Transform,
            enabled,
            ProcessingGraphNodeFailurePolicy.Required,
            0,
            Json(options),
            [new("$raw", ProcessingGraphDependencyKind.Artifact, true)],
            [new([FrameArtifactRole.Raw], [], [], [], [], true, "input", ProcessingGraphInputBindingKind.PrimaryArtifact)],
            [new(FrameArtifactRole.Preview, id, ProcessingProductKind.PixelData,
                new($"recipe-{id}", "1.0.0", "test-v1", ProcessingOperationKind.Transform))],
            null,
            [],
            []);

    private static ProcessingGraphNodeDefinition WithOptions(
        ProcessingGraphNodeDefinition node,
        string stepVersion,
        JsonElement options) => new(
            node.Id,
            node.StepAlias,
            stepVersion,
            node.OperationKind,
            node.Enabled,
            node.FailurePolicy,
            node.Order,
            options,
            node.Dependencies,
            node.Inputs,
            node.Outputs,
            node.Window,
            node.CapabilityLabels,
            node.HostApplicability);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    // Renames the legacy "width" member and keeps every other member's bytes.
    private sealed class WidthToThicknessUpgrade(string toStepVersion = "example-v2") : IProcessingNodeOptionsUpgrade
    {
        public string StepAlias => "Example";

        public string FromStepVersion => "example-v1";

        public string ToStepVersion => toStepVersion;

        public ProcessingGraphNodeDefinition Upgrade(ProcessingGraphNodeDefinition node)
        {
            var raw = node.EffectiveOptions.GetRawText().Replace("\"width\"", "\"thickness\"", StringComparison.Ordinal);
            return WithOptions(node, ToStepVersion, Json(raw));
        }
    }
}
