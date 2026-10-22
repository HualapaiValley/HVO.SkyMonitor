using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use test values are clearer inline.")]
public sealed class FrameFactsTemplateTests
{
    private static readonly FrameFactsTokenCatalog Catalog = new([
        new("capture.camera", FrameFactsValueKind.Text),
        new("capture.sequence", FrameFactsValueKind.WholeNumber),
        new("capture.exposure", FrameFactsValueKind.Number),
        new("time.capture", FrameFactsValueKind.Timestamp)
    ]);

    [TestMethod]
    public void LiteralTokensAndAllStylesProduceStructuredPlainText()
    {
        var result = Evaluate("**Camera {{capture.camera}}** *{{capture.sequence}}* __{{capture.exposure|fixed2}}__",
            Context(("capture.camera", FrameFactsValue.FromText("AllSky")),
                ("capture.sequence", FrameFactsValue.FromWholeNumber(123)),
                ("capture.exposure", FrameFactsValue.FromNumber(1.25))));

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Camera AllSky 123 1.25", result.Text);
        CollectionAssert.AreEqual(new[] { FrameFactsTextStyle.Bold, FrameFactsTextStyle.Bold, FrameFactsTextStyle.None,
            FrameFactsTextStyle.Italic, FrameFactsTextStyle.None, FrameFactsTextStyle.Underline },
            result.Lines[0].Runs.Select(run => run.Style).ToArray());
    }

    [TestMethod]
    [DataRow("***sky***", 3)]
    [DataRow("*__**sky**__*", 7)]
    [DataRow("**__*sky*__**", 7)]
    [DataRow("**sky**", 1)]
    [DataRow("*sky*", 2)]
    [DataRow("__sky__", 4)]
    public void NestedStylesCombineWithoutMarkupInOutput(string source, int expectedStyle)
    {
        var result = Evaluate(source);
        Assert.AreEqual("sky", result.Text);
        Assert.AreEqual((FrameFactsTextStyle)expectedStyle, result.Lines[0].Runs[0].Style);
    }

    [TestMethod]
    [DataRow("*a **b** c*", 2, 3)]
    [DataRow("**a *b* c**", 1, 3)]
    public void EitherEmphasisCanNestInsideTheOther(string source, int outside, int inside)
    {
        var result = Evaluate(source);
        Assert.AreEqual("a b c", result.Text);
        Assert.AreEqual((FrameFactsTextStyle)outside, result.Lines[0].Runs[0].Style);
        Assert.AreEqual((FrameFactsTextStyle)inside, result.Lines[0].Runs[1].Style);
        Assert.AreEqual((FrameFactsTextStyle)outside, result.Lines[0].Runs[2].Style);
    }

    [TestMethod]
    public void EscapesRenderLiteralDelimitersAndAreNeverReparsed()
    {
        Assert.AreEqual("{{capture.camera}} **x** __y__ \\ | <b>",
            Evaluate(@"\{\{capture.camera\}\} \*\*x\*\* \_\_y\_\_ \\ \| \<b\>").Text);
        var result = Evaluate("{{capture.camera}}", Context(("capture.camera", FrameFactsValue.FromText("{{time.capture}} **x** <b>"))));
        Assert.AreEqual("{{time.capture}} **x** <b>", result.Text);
        Assert.AreEqual(FrameFactsTextStyle.None, result.Lines[0].Runs[0].Style);
    }

    [TestMethod]
    [DataRow("a\nb\n", "a\nb\n")]
    [DataRow("a\r\nb\r\n", "a\nb\n")]
    [DataRow("a\rb\r", "a\nb\n")]
    [DataRow("\n", "\n")]
    public void ActualNewlinesNormalizeAndPreserveBlankLines(string source, string expected)
    {
        Assert.AreEqual(expected, Evaluate(source).Text);
    }

    [TestMethod]
    public void StylesMayContinueAcrossLineBreaksAndEmptyCornerHasNoLines()
    {
        var result = Evaluate("**a\nb**");
        Assert.HasCount(2, result.Lines);
        Assert.IsTrue(result.Lines.All(line => line.Runs[0].Style == FrameFactsTextStyle.Bold));
        Assert.IsEmpty(Evaluate(string.Empty).Lines);
    }

    [TestMethod]
    [DataRow(null, "frame-facts.source-required", 0)]
    [DataRow("{{capture.camera", "frame-facts.malformed-token", 0)]
    [DataRow("{capture.camera}", "frame-facts.malformed-token", 0)]
    [DataRow("x}}", "frame-facts.malformed-token", 1)]
    [DataRow("{{}}", "frame-facts.malformed-token", 0)]
    [DataRow("{{capture..camera}}", "frame-facts.malformed-token", 0)]
    [DataRow("{{Capture.camera}}", "frame-facts.malformed-token", 0)]
    [DataRow("{{capture.camera }}", "frame-facts.malformed-token", 0)]
    [DataRow("{{if capture.camera}}", "frame-facts.malformed-token", 0)]
    [DataRow("{{capture.unknown}}", "frame-facts.unknown-token", 0)]
    [DataRow("{{spaceWeather.kp}}", "frame-facts.reserved-namespace", 0)]
    [DataRow("{{spaceWeather.kp.status}}", "frame-facts.reserved-namespace", 0)]
    [DataRow("{{capture.camera|}}", "frame-facts.unknown-format", 0)]
    [DataRow("{{capture.camera|fixed2}}", "frame-facts.unknown-format", 0)]
    [DataRow("{{capture.exposure|F2}}", "frame-facts.unknown-format", 0)]
    [DataRow("{{capture.exposure|fixed2|text}}", "frame-facts.unknown-format", 0)]
    [DataRow("{{time.capture|yyyy-MM-dd}}", "frame-facts.unknown-format", 0)]
    [DataRow("{{capture.camera.status|fixed1}}", "frame-facts.unknown-format", 0)]
    [DataRow("<script>x</script>", "frame-facts.unsupported-syntax", 0)]
    [DataRow("a\tb", "frame-facts.invalid-character", 1)]
    [DataRow("a\0b", "frame-facts.invalid-character", 1)]
    [DataRow("a\u2028b", "frame-facts.invalid-character", 1)]
    [DataRow("a\u2029b", "frame-facts.invalid-character", 1)]
    [DataRow("a\\n", "frame-facts.invalid-escape", 1)]
    [DataRow("x\\", "frame-facts.invalid-escape", 1)]
    [DataRow("**x", "frame-facts.unclosed-style", 0)]
    [DataRow("__x", "frame-facts.unclosed-style", 0)]
    [DataRow("*x", "frame-facts.unclosed-style", 0)]
    [DataRow("**__x**__", "frame-facts.mismatched-style", 5)]
    public void InvalidSourceHasStableCodeAndOffset(string? source, string code, int offset)
    {
        var result = FrameFactsTemplate.Parse(source, Catalog);
        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Template);
        Assert.AreEqual(new FrameFactsTemplateDiagnostic(code, offset), result.Error);
    }

    [TestMethod]
    public void InvalidUtf16IsRejectedInSourceAndExpansion()
    {
        Assert.AreEqual("frame-facts.invalid-character", FrameFactsTemplate.Parse("\ud800", Catalog).Error?.Code);
        var result = Evaluate("{{capture.camera}}", Context(("capture.camera", FrameFactsValue.FromText("\udfff"))));
        Assert.AreEqual("frame-facts.invalid-expansion", result.Error?.Code);
    }

    [TestMethod]
    public void SourceLengthBoundaryIs4096Utf16CodeUnitsIncludingMarkup()
    {
        Assert.IsTrue(FrameFactsTemplate.Parse(new string('*', 4096), Catalog).IsValid);
        var rejected = FrameFactsTemplate.Parse(new string('*', 4097), Catalog);
        Assert.AreEqual(new FrameFactsTemplateDiagnostic("frame-facts.source-too-long", 4096), rejected.Error);
    }

    [TestMethod]
    public void SourceAndOutputLineBoundariesAreEightLinesAnd64UnicodeScalars()
    {
        var source = string.Join('\n', Enumerable.Repeat(new string('x', 64), 8));
        var result = Evaluate(source);
        Assert.HasCount(8, result.Lines);
        Assert.AreEqual(source, result.Text);
        Assert.AreEqual("frame-facts.too-many-lines", FrameFactsTemplate.Parse(source + "\n", Catalog).Error?.Code);
        Assert.AreEqual("frame-facts.line-too-long", FrameFactsTemplate.Parse(new string('x', 65), Catalog).Error?.Code);
        Assert.AreEqual("frame-facts.line-too-long", FrameFactsTemplate.Parse(new string('x', 32) + "**" + new string('x', 33) + "**", Catalog).Error?.Code);
        var supplementary = string.Concat(Enumerable.Repeat("\U0001f30c", 64));
        Assert.AreEqual(supplementary, Evaluate(supplementary).Text);
        Assert.AreEqual(supplementary, Evaluate("{{capture.camera}}", Context(("capture.camera", FrameFactsValue.FromText(supplementary)))).Text);
    }

    [TestMethod]
    public void ExpandedBoundaryIncludesLiteralsAndAllStyledRunsAndFailsAtomically()
    {
        var context = Context(("capture.camera", FrameFactsValue.FromText(new string('x', 64))));
        Assert.AreEqual(64, Evaluate("{{capture.camera}}", context).Text.Length);
        var result = Evaluate("valid\n**{{capture.camera}}**!", context);
        Assert.AreEqual("frame-facts.expansion-too-long", result.Error?.Code);
        Assert.AreEqual("valid\n**{{capture.camera}}**!".IndexOf('!', StringComparison.Ordinal), result.Error?.SourceOffset);
        Assert.IsEmpty(result.Lines);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual("frame-facts.expansion-too-long", Evaluate("{{capture.camera}}{{capture.camera}}", context).Error?.Code);
        Assert.AreEqual("frame-facts.expansion-too-long", Evaluate("{{capture.exposure|fixed3}}",
            Context(("capture.exposure", FrameFactsValue.FromNumber(double.MaxValue)))).Error?.Code);
    }

    [TestMethod]
    [DataRow("x\ny")]
    [DataRow("x\ry")]
    [DataRow("x\ty")]
    [DataRow("x\u2028y")]
    [DataRow("x\u2029y")]
    public void FactTextCannotInjectLinesOrControls(string text)
    {
        var result = Evaluate("{{capture.camera}}", Context(("capture.camera", FrameFactsValue.FromText(text))));
        Assert.AreEqual("frame-facts.invalid-expansion", result.Error?.Code);
        Assert.IsEmpty(result.Lines);
    }

    [TestMethod]
    [DataRow(FrameFactsAvailability.Missing, "MISSING")]
    [DataRow(FrameFactsAvailability.Stale, "STALE")]
    [DataRow(FrameFactsAvailability.Conflicting, "CONFLICTING")]
    [DataRow(FrameFactsAvailability.Unsupported, "UNSUPPORTED")]
    public void UnavailableFactsUsePlaceholderAndPreserveStatusUnderEitherPolicy(FrameFactsAvailability availability, string status)
    {
        var context = new FrameFactsTokenContext(new Dictionary<string, FrameFactsFact>
        {
            ["capture.camera"] = FrameFactsFact.Unavailable(availability)
        });
        const string source = "Camera {{capture.camera}}\n{{capture.camera.status}}\n{{capture.camera}} {{capture.camera.status}}";
        Assert.AreEqual($"Camera UNAVAILABLE\n{status}\nUNAVAILABLE {status}", Evaluate(source, context).Text);
        var dropped = Evaluate(source, context, FrameFactsMissingPolicy.DropLine);
        Assert.AreEqual($"{status}\nUNAVAILABLE {status}", dropped.Text);
        Assert.HasCount(2, dropped.Lines);
    }

    [TestMethod]
    public void MissingKeysAreMissingAndWrongTypedValuesAreUnsupportedIncludingStatus()
    {
        Assert.AreEqual("UNAVAILABLE MISSING", Evaluate("{{capture.camera}} {{capture.camera.status}}").Text);
        var context = Context(("capture.camera", FrameFactsValue.FromNumber(0)));
        Assert.AreEqual("UNAVAILABLE UNSUPPORTED", Evaluate("{{capture.camera}} {{capture.camera.status}}", context).Text);
        Assert.IsEmpty(Evaluate("{{capture.camera}}", context, FrameFactsMissingPolicy.DropLine).Lines);
        Assert.AreEqual("AVAILABLE", Evaluate("{{capture.camera.status}}", Context(("capture.camera", FrameFactsValue.FromText("x")))).Text);
    }

    [TestMethod]
    public void DropLineIsDecidedBeforeOversizedExpansionAndDoesNotDropLiteralsOrEmptyAvailableText()
    {
        var context = Context(("capture.camera", FrameFactsValue.FromText(new string('x', 4096))));
        Assert.AreEqual("literal\n", Evaluate("literal\n{{capture.camera}}{{capture.exposure}}\n", context, FrameFactsMissingPolicy.DropLine).Text);
        Assert.AreEqual("camera=", Evaluate("camera={{capture.camera}}", Context(("capture.camera", FrameFactsValue.FromText(string.Empty))), FrameFactsMissingPolicy.DropLine).Text);
        Assert.AreEqual(string.Empty, Evaluate("{{capture.camera}}", null, FrameFactsMissingPolicy.DropLine).Text);
    }

    [TestMethod]
    [DataRow("text", "AllSky")]
    public void TextFormatIsExplicitOrDefault(string format, string expected)
    {
        var context = Context(("capture.camera", FrameFactsValue.FromText("AllSky")));
        Assert.AreEqual(expected, Evaluate("{{capture.camera|" + format + "}}", context).Text);
        Assert.AreEqual(expected, Evaluate("{{capture.camera}}", context).Text);
    }

    [TestMethod]
    [DataRow("number", "1.25")]
    [DataRow("fixed0", "1")]
    [DataRow("fixed1", "1.2")]
    [DataRow("fixed2", "1.25")]
    [DataRow("fixed3", "1.250")]
    public void NumberFormatsAreClosedAndInvariant(string format, string expected)
    {
        var context = Context(("capture.exposure", FrameFactsValue.FromNumber(1.25)));
        Assert.AreEqual(expected, Evaluate("{{capture.exposure|" + format + "}}", context).Text);
        Assert.AreEqual("1.25", Evaluate("{{capture.exposure}}", context).Text);
    }

    [TestMethod]
    [DataRow("iso8601", "2026-10-22T15:04:05.123-07:00")]
    [DataRow("date", "2026-10-22")]
    [DataRow("time", "15:04:05")]
    public void TimestampFormatsPreserveExplicitOffset(string format, string expected)
    {
        var context = Context(("time.capture", FrameFactsValue.FromTimestamp(new(2026, 10, 22, 15, 4, 5, 123, TimeSpan.FromHours(-7)))));
        Assert.AreEqual(expected, Evaluate("{{time.capture|" + format + "}}", context).Text);
        Assert.AreEqual("2026-10-22T15:04:05.123-07:00", Evaluate("{{time.capture}}", context).Text);
    }

    [TestMethod]
    public void FormattingDoesNotDependOnAmbientCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var context = Context(("capture.sequence", FrameFactsValue.FromWholeNumber(long.MinValue)),
                ("capture.exposure", FrameFactsValue.FromNumber(-1.25)),
                ("time.capture", FrameFactsValue.FromTimestamp(new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero))));
            foreach (var culture in new[] { "en-US", "fr-FR", "ar-SA", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
                Assert.AreEqual("-9223372036854775808 -1.250 2026-10-22",
                    Evaluate("{{capture.sequence|integer}} {{capture.exposure|fixed3}} {{time.capture|date}}", context).Text);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [TestMethod]
    public void CatalogueAndContextCopyInputsAndAreOrdinal()
    {
        var definitions = new List<FrameFactsTokenDefinition> { new("capture.camera", FrameFactsValueKind.Text) };
        var catalog = new FrameFactsTokenCatalog(definitions);
        var values = new Dictionary<string, FrameFactsFact> { ["capture.camera"] = FrameFactsFact.Available(FrameFactsValue.FromText("frozen")) };
        var context = new FrameFactsTokenContext(values);
        definitions.Clear();
        values["capture.camera"] = FrameFactsFact.Available(FrameFactsValue.FromText("changed"));
        var parsed = FrameFactsTemplate.Parse("{{capture.camera}}", catalog);
        Assert.AreEqual("frozen", FrameFactsEvaluator.Evaluate(parsed.Template!, context).Text);
        Assert.HasCount(2, catalog.Definitions);
        Assert.IsNull(catalog.Find("capture.Camera"));
        Assert.AreEqual(FrameFactsAvailability.Missing, context.Get("capture.Camera").Availability);
    }

    [TestMethod]
    public void CatalogueContextAndValueBoundsRejectAmbiguityAndReservedNamespace()
    {
        foreach (var key in new[] { "spaceWeather.kp", "capture.camera.status", "capture", "capture..camera", "Capture.camera", "capture.camera_", "capture." + new string('x', 89) })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenDefinition(key, FrameFactsValueKind.Text));
            Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenContext(new Dictionary<string, FrameFactsFact> { [key] = default }));
        }
        Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenCatalog([Catalog.Definitions[0], Catalog.Definitions[0]]));
        Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenCatalog([Catalog.Definitions[1]]));
        Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenContext([
            new("capture.camera", default), new("capture.camera", default)]));
        Assert.ThrowsExactly<ArgumentException>(() => FrameFactsFact.Available(default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameFactsFact.Unavailable(FrameFactsAvailability.Available));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameFactsValue.FromText(new string('x', 4097)));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameFactsValue.FromNumber(value));
        }
        var definitions = Enumerable.Range(0, 256).Select(i => new FrameFactsTokenDefinition($"capture.x{i}", FrameFactsValueKind.Text)).ToArray();
        var facts = definitions.Select(definition => KeyValuePair.Create(definition.Key, default(FrameFactsFact))).ToArray();
        Assert.HasCount(512, new FrameFactsTokenCatalog(definitions).Definitions);
        _ = new FrameFactsTokenContext(facts);
        Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenCatalog(definitions.Append(new("capture.overflow", FrameFactsValueKind.Text))));
        Assert.ThrowsExactly<ArgumentException>(() => new FrameFactsTokenContext(facts.Append(new("capture.overflow", default))));
        var longest = "capture." + new string('x', 88);
        var maxCatalog = new FrameFactsTokenCatalog([new(longest, FrameFactsValueKind.Text)]);
        Assert.IsTrue(FrameFactsTemplate.Parse("{{" + longest + ".status}}", maxCatalog).IsValid);
    }

    private static FrameFactsTokenContext Context(params (string Key, FrameFactsValue Value)[] facts)
        => new(facts.Select(fact => KeyValuePair.Create(fact.Key, FrameFactsFact.Available(fact.Value))));

    private static FrameFactsEvaluationResult Evaluate(string source, FrameFactsTokenContext? context = null,
        FrameFactsMissingPolicy policy = FrameFactsMissingPolicy.Placeholder)
    {
        var parsed = FrameFactsTemplate.Parse(source, Catalog);
        Assert.IsTrue(parsed.IsValid, parsed.Error?.ToString());
        return FrameFactsEvaluator.Evaluate(parsed.Template!, context ?? FrameFactsTokenContext.Empty, policy);
    }
}
